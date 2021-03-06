// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using Microsoft.VisualStudio.Debugger.Clr;
using Microsoft.VisualStudio.Debugger.Evaluation;
using Microsoft.VisualStudio.Debugger.Evaluation.ClrCompilation;
using Microsoft.VisualStudio.Debugger.Metadata;
using Roslyn.Utilities;
using MethodAttributes = System.Reflection.MethodAttributes;
using Type = Microsoft.VisualStudio.Debugger.Metadata.Type;
using TypeCode = Microsoft.VisualStudio.Debugger.Metadata.TypeCode;

namespace Microsoft.CodeAnalysis.ExpressionEvaluator
{
    internal static class TypeHelpers
    {
        internal const BindingFlags MemberBindingFlags = BindingFlags.Public |
                                                         BindingFlags.NonPublic |
                                                         BindingFlags.Instance |
                                                         BindingFlags.Static |
                                                         BindingFlags.DeclaredOnly;

        internal static void AppendTypeMembers(
            this Type type,
            ArrayBuilder<MemberAndDeclarationInfo> includedMembers,
            Predicate<MemberInfo> predicate,
            Type declaredType,
            DkmClrAppDomain appDomain,
            bool includeInherited,
            bool hideNonPublic)
        {
            Debug.Assert(!type.IsInterface);

            var memberLocation = DeclarationInfo.FromSubTypeOfDeclaredType;
            var previousDeclarationMap = includeInherited ? new Dictionary<string, DeclarationInfo>() : null;

            int inheritanceLevel = 0;
            while (!type.IsObject())
            {
                if (type.Equals(declaredType))
                {
                    Debug.Assert(memberLocation == DeclarationInfo.FromSubTypeOfDeclaredType);
                    memberLocation = DeclarationInfo.FromDeclaredTypeOrBase;
                }

                // Get the state from DebuggerBrowsableAttributes for the members of the current type.
                var browsableState = DkmClrType.Create(appDomain, type).GetDebuggerBrowsableAttributeState();

                // Hide non-public members if hideNonPublic is specified (intended to reflect the
                // DkmInspectionContext's DkmEvaluationFlags), and the type is from an assembly
                // with no symbols.
                var hideNonPublicBehavior = DeclarationInfo.None;
                if (hideNonPublic)
                {
                    var moduleInstance = appDomain.FindClrModuleInstance(type.Module.ModuleVersionId);
                    if (moduleInstance == null || moduleInstance.Module == null)
                    {
                        // Synthetic module or no symbols loaded.
                        hideNonPublicBehavior = DeclarationInfo.HideNonPublic;
                    }
                }

                foreach (var member in type.GetMembers(MemberBindingFlags))
                {
                    if (!predicate(member))
                    {
                        continue;
                    }

                    var memberName = member.Name;
                    // This represents information about the immediately preceding (more derived)
                    // declaration with the same name as the current member.
                    var previousDeclaration = DeclarationInfo.None;
                    var memberNameAlreadySeen = false;
                    if (includeInherited)
                    {
                        memberNameAlreadySeen = previousDeclarationMap.TryGetValue(memberName, out previousDeclaration);
                        if (memberNameAlreadySeen)
                        {
                            // There was a name conflict, so we'll need to include the declaring
                            // type of the member to disambiguate.
                            previousDeclaration |= DeclarationInfo.IncludeTypeInMemberName;
                        }

                        // Update previous member with name hiding (casting) and declared location information for next time.
                        previousDeclarationMap[memberName] =
                            (previousDeclaration & ~(DeclarationInfo.RequiresExplicitCast |
                                DeclarationInfo.FromSubTypeOfDeclaredType)) |
                            member.AccessingBaseMemberWithSameNameRequiresExplicitCast() |
                            memberLocation;
                    }

                    Debug.Assert(memberNameAlreadySeen != (previousDeclaration == DeclarationInfo.None));

                    // Decide whether to include this member in the list of members to display.
                    if (!memberNameAlreadySeen || previousDeclaration.IsSet(DeclarationInfo.RequiresExplicitCast))
                    {
                        DkmClrDebuggerBrowsableAttributeState? browsableStateValue = null;
                        if (browsableState != null)
                        {
                            DkmClrDebuggerBrowsableAttributeState value;
                            if (browsableState.TryGetValue(memberName, out value))
                            {
                                browsableStateValue = value;
                            }
                        }

                        if (memberLocation.IsSet(DeclarationInfo.FromSubTypeOfDeclaredType))
                        {
                            // If the current type is a sub-type of the declared type, then
                            // we always need to insert a cast to access the member
                            previousDeclaration |= DeclarationInfo.RequiresExplicitCast;
                        }
                        else if (previousDeclaration.IsSet(DeclarationInfo.FromSubTypeOfDeclaredType))
                        {
                            // If the immediately preceding member (less derived) was
                            // declared on a sub-type of the declared type, then we'll
                            // ignore the casting bit.  Accessing a member through the
                            // declared type is the same as casting to that type, so
                            // the cast would be redundant.
                            previousDeclaration &= ~DeclarationInfo.RequiresExplicitCast;
                        }

                        previousDeclaration |= hideNonPublicBehavior;

                        includedMembers.Add(new MemberAndDeclarationInfo(member, browsableStateValue, previousDeclaration, inheritanceLevel));
                    }
                }

                if (!includeInherited)
                {
                    break;
                }

                type = type.BaseType;
                inheritanceLevel++;
            }

            includedMembers.Sort(MemberAndDeclarationInfo.Comparer);
        }

        private static DeclarationInfo AccessingBaseMemberWithSameNameRequiresExplicitCast(this MemberInfo member)
        {
            switch (member.MemberType)
            {
                case MemberTypes.Field:
                    return DeclarationInfo.RequiresExplicitCast;
                case MemberTypes.Property:
                    var getMethod = GetNonIndexerGetMethod((PropertyInfo)member);
                    if ((getMethod != null) &&
                        (!getMethod.IsVirtual || ((getMethod.Attributes & MethodAttributes.VtableLayoutMask) == MethodAttributes.NewSlot)))
                    {
                        return DeclarationInfo.RequiresExplicitCast;
                    }
                    return DeclarationInfo.None;
                default:
                    throw ExceptionUtilities.UnexpectedValue(member.MemberType);
            }
        }

        internal static bool IsVisibleMember(MemberInfo member)
        {
            switch (member.MemberType)
            {
                case MemberTypes.Field:
                    return true;
                case MemberTypes.Property:
                    return GetNonIndexerGetMethod((PropertyInfo)member) != null;
            }
            return false;
        }

        /// <summary>
        /// Returns true if the member is public or protected.
        /// </summary>
        internal static bool IsPublic(this MemberInfo member)
        {
            // Matches native EE which includes protected members.
            switch (member.MemberType)
            {
                case MemberTypes.Field:
                    {
                        var field = (FieldInfo)member;
                        var attributes = field.Attributes;
                        return ((attributes & System.Reflection.FieldAttributes.Public) == System.Reflection.FieldAttributes.Public) ||
                            ((attributes & System.Reflection.FieldAttributes.Family) == System.Reflection.FieldAttributes.Family);
                    }
                case MemberTypes.Property:
                    {
                        // Native EE uses the accessibility of the property rather than getter
                        // so "public object P { private get; set; }" is treated as public.
                        // Instead, we drop properties if the getter is inaccessible.
                        var getMethod = GetNonIndexerGetMethod((PropertyInfo)member);
                        if (getMethod == null)
                        {
                            return false;
                        }
                        var attributes = getMethod.Attributes;
                        return ((attributes & System.Reflection.MethodAttributes.Public) == System.Reflection.MethodAttributes.Public) ||
                            ((attributes & System.Reflection.MethodAttributes.Family) == System.Reflection.MethodAttributes.Family);
                    }
                default:
                    return false;
            }
        }

        private static MethodInfo GetNonIndexerGetMethod(PropertyInfo property)
        {
            return (property.GetIndexParameters().Length == 0) ?
                property.GetGetMethod(nonPublic: true) :
                null;
        }

        internal static bool IsBoolean(this Type type)
        {
            return Type.GetTypeCode(type) == TypeCode.Boolean;
        }

        internal static bool IsCharacter(this Type type)
        {
            return Type.GetTypeCode(type) == TypeCode.Char;
        }

        internal static bool IsDecimal(this Type type)
        {
            return Type.GetTypeCode(type) == TypeCode.Decimal;
        }

        internal static bool IsDateTime(this Type type)
        {
            return Type.GetTypeCode(type) == TypeCode.DateTime;
        }

        internal static bool IsObject(this Type type)
        {
            bool result = type.IsClass && (type.BaseType == null) && !type.IsPointer;
            Debug.Assert(result == type.IsMscorlibType("System", "Object"));
            return result;
        }

        internal static bool IsValueType(this Type type)
        {
            return type.IsMscorlibType("System", "ValueType");
        }

        internal static bool IsString(this Type type)
        {
            return Type.GetTypeCode(type) == TypeCode.String;
        }

        internal static bool IsVoid(this Type type)
        {
            return type.IsMscorlibType("System", "Void") && !type.IsGenericType;
        }

        internal static bool IsIEnumerable(this Type type)
        {
            return type.IsMscorlibType("System.Collections", "IEnumerable");
        }

        internal static bool IsIEnumerableOfT(this Type type)
        {
            return type.IsMscorlibType("System.Collections.Generic", "IEnumerable`1");
        }

        internal static bool IsTypeVariables(this Type type)
        {
            return type.IsType(null, "<>c__TypeVariables");
        }

        /// <summary>
        /// Returns type argument if the type is
        /// Nullable&lt;T&gt;, otherwise null.
        /// </summary>
        internal static Type GetNullableTypeArgument(this Type type)
        {
            if (type.IsMscorlibType("System", "Nullable`1"))
            {
                var typeArgs = type.GetGenericArguments();
                if (typeArgs.Length == 1)
                {
                    return typeArgs[0];
                }
            }
            return null;
        }

        internal static bool IsNullable(this Type type)
        {
            return type.GetNullableTypeArgument() != null;
        }

        internal static DkmClrValue GetFieldValue(this DkmClrValue value, string name, DkmInspectionContext inspectionContext)
        {
            return value.GetMemberValue(name, (int)MemberTypes.Field, ParentTypeName: null, InspectionContext: inspectionContext);
        }

        internal static DkmClrValue GetNullableValue(this DkmClrValue value, DkmInspectionContext inspectionContext)
        {
            Debug.Assert(value.Type.GetLmrType().IsNullable());

            var hasValue = value.GetFieldValue(InternalWellKnownMemberNames.NullableHasValue, inspectionContext);
            if (object.Equals(hasValue.HostObjectValue, false))
            {
                return null;
            }

            return value.GetFieldValue(InternalWellKnownMemberNames.NullableValue, inspectionContext);
        }

        internal static Type GetBaseTypeOrNull(this Type underlyingType, DkmClrAppDomain appDomain, out DkmClrType type)
        {
            Debug.Assert((underlyingType.BaseType != null) || underlyingType.IsPointer || underlyingType.IsArray, "BaseType should only return null if the underlyingType is a pointer or array.");

            underlyingType = underlyingType.BaseType;
            type = (underlyingType != null) ? DkmClrType.Create(appDomain, underlyingType) : null;

            return underlyingType;
        }

        /// <summary>
        /// Get the first attribute from <see cref="DkmClrType.GetEvalAttributes"/> (including inherited attributes)
        /// that is of type T, as well as the type that it targeted.
        /// </summary>
        internal static bool TryGetEvalAttribute<T>(this DkmClrType type, out DkmClrType attributeTarget, out T evalAttribute)
            where T : DkmClrEvalAttribute
        {
            attributeTarget = null;
            evalAttribute = null;

            var appDomain = type.AppDomain;
            var underlyingType = type.GetLmrType();
            while ((underlyingType != null) && !underlyingType.IsObject())
            {
                foreach (var attribute in type.GetEvalAttributes())
                {
                    evalAttribute = attribute as T;
                    if (evalAttribute != null)
                    {
                        attributeTarget = type;
                        return true;
                    }
                }

                underlyingType = underlyingType.GetBaseTypeOrNull(appDomain, out type);
            }

            return false;
        }

        /// <summary>
        /// Returns the set of DebuggerBrowsableAttribute state for the
        /// members of the type, indexed by member name, or null if there
        /// are no DebuggerBrowsableAttributes on members of the type.
        /// </summary>
        private static Dictionary<string, DkmClrDebuggerBrowsableAttributeState> GetDebuggerBrowsableAttributeState(this DkmClrType type)
        {
            Dictionary<string, DkmClrDebuggerBrowsableAttributeState> result = null;
            foreach (var attribute in type.GetEvalAttributes())
            {
                var browsableAttribute = attribute as DkmClrDebuggerBrowsableAttribute;
                if (browsableAttribute == null)
                {
                    continue;
                }
                if (result == null)
                {
                    result = new Dictionary<string, DkmClrDebuggerBrowsableAttributeState>();
                }
                result.Add(browsableAttribute.TargetMember, browsableAttribute.State);
            }
            return result;
        }

        /// <summary>
        /// Extracts information from the first <see cref="DebuggerDisplayAttribute"/> on the runtime type of <paramref name="value"/>, if there is one.
        /// </summary>
        internal static bool TryGetDebuggerDisplayInfo(this DkmClrValue value, out DebuggerDisplayInfo displayInfo)
        {
            displayInfo = default(DebuggerDisplayInfo);

            // The native EE does not consider DebuggerDisplayAttribute
            // on null or error instances.
            if (value.IsError() || value.IsNull)
            {
                return false;
            }

            var clrType = value.Type;

            DkmClrType attributeTarget;
            DkmClrDebuggerDisplayAttribute attribute;
            if (clrType.TryGetEvalAttribute(out attributeTarget, out attribute)) // First, as in dev12.
            {
                displayInfo = new DebuggerDisplayInfo(attributeTarget, attribute);
                return true;
            }

            return false;
        }

        /// <summary>
        /// Returns the array of <see cref="DkmCustomUIVisualizerInfo"/> objects of the type from its <see cref="DkmClrDebuggerVisualizerAttribute"/> attributes,
        /// or null if the type has no [DebuggerVisualizer] attributes associated with it.
        /// </summary>
        internal static DkmCustomUIVisualizerInfo[] GetDebuggerCustomUIVisualizerInfo(this DkmClrType type)
        {
            var builder = ArrayBuilder<DkmCustomUIVisualizerInfo>.GetInstance();

            var appDomain = type.AppDomain;
            var underlyingType = type.GetLmrType();
            while ((underlyingType != null) && !underlyingType.IsObject())
            {
                foreach (var attribute in type.GetEvalAttributes())
                {
                    var visualizerAttribute = attribute as DkmClrDebuggerVisualizerAttribute;
                    if (visualizerAttribute == null)
                    {
                        continue;
                    }

                    builder.Add(DkmCustomUIVisualizerInfo.Create((uint)builder.Count,
                        visualizerAttribute.VisualizerDescription,
                        visualizerAttribute.VisualizerDescription,
                        // ClrCustomVisualizerVSHost is a registry entry that specifies the CLSID of the
                        // IDebugCustomViewer class that will be instantiated to display the custom visualizer.
                        "ClrCustomVisualizerVSHost",
                        visualizerAttribute.UISideVisualizerTypeName,
                        visualizerAttribute.UISideVisualizerAssemblyName,
                        visualizerAttribute.UISideVisualizerAssemblyLocation,
                        visualizerAttribute.DebuggeeSideVisualizerTypeName,
                        visualizerAttribute.DebuggeeSideVisualizerAssemblyName));
                }

                underlyingType = underlyingType.GetBaseTypeOrNull(appDomain, out type);
            }

            var result = (builder.Count > 0) ? builder.ToArray() : null;
            builder.Free();
            return result;
        }

        internal static DkmClrType GetProxyType(this DkmClrType type)
        {
            DkmClrType attributeTarget;
            DkmClrDebuggerTypeProxyAttribute attribute;
            if (type.TryGetEvalAttribute(out attributeTarget, out attribute))
            {
                var targetedType = attributeTarget.GetLmrType();
                var proxyType = attribute.ProxyType;
                var underlyingProxy = proxyType.GetLmrType();
                if (underlyingProxy.IsGenericType && targetedType.IsGenericType)
                {
                    var typeArgs = targetedType.GetGenericArguments();

                    // Drop the proxy type if the arity does not match.
                    if (typeArgs.Length != underlyingProxy.GetGenericArguments().Length)
                    {
                        return null;
                    }

                    // Substitute target type arguments for proxy type arguments.
                    var constructedProxy = underlyingProxy.Substitute(underlyingProxy, typeArgs);
                    proxyType = DkmClrType.Create(type.AppDomain, constructedProxy);
                }

                return proxyType;
            }

            return null;
        }

        /// <summary>
        /// Substitute references to type parameters from 'typeDef'
        /// with type arguments from 'typeArgs' in type 'type'.
        /// </summary>
        internal static Type Substitute(this Type type, Type typeDef, Type[] typeArgs)
        {
            Debug.Assert(typeDef.IsGenericTypeDefinition);
            Debug.Assert(typeDef.GetGenericArguments().Length == typeArgs.Length);

            if (type.IsGenericType)
            {
                var builder = ArrayBuilder<Type>.GetInstance();
                foreach (var t in type.GetGenericArguments())
                {
                    builder.Add(t.Substitute(typeDef, typeArgs));
                }
                var typeDefinition = type.GetGenericTypeDefinition();
                return typeDefinition.MakeGenericType(builder.ToArrayAndFree());
            }
            else if (type.IsArray)
            {
                var elementType = type.GetElementType();
                elementType = elementType.Substitute(typeDef, typeArgs);
                var n = type.GetArrayRank();
                return (n == 1) ? elementType.MakeArrayType() : elementType.MakeArrayType(n);
            }
            else if (type.IsPointer)
            {
                var elementType = type.GetElementType();
                elementType = elementType.Substitute(typeDef, typeArgs);
                return elementType.MakePointerType();
            }
            else if (type.IsGenericParameter)
            {
                if (type.DeclaringType.Equals(typeDef))
                {
                    var ordinal = type.GenericParameterPosition;
                    return typeArgs[ordinal];
                }
            }

            return type;
        }

        // Returns the IEnumerable interface implemented by the given type,
        // preferring System.Collections.Generic.IEnumerable<T> over
        // System.Collections.IEnumerable. If there are multiple implementations
        // of IEnumerable<T> on base and derived types, the implementation on
        // the most derived type is returned. If there are multiple implementations
        // of IEnumerable<T> on the same type, it is undefined which is returned.
        internal static Type GetIEnumerableImplementationIfAny(this Type type)
        {
            var t = type;
            do
            {
                foreach (var @interface in t.GetInterfacesOnType())
                {
                    if (@interface.IsIEnumerableOfT())
                    {
                        // Return the first implementation of IEnumerable<T>.
                        return @interface;
                    }
                }
                t = t.BaseType;
            } while (t != null);

            foreach (var @interface in type.GetInterfaces())
            {
                if (@interface.IsIEnumerable())
                {
                    return @interface;
                }
            }

            return null;
        }

        internal static bool IsEmptyResultsViewException(this Type type)
        {
            return type.IsType("System.Linq", "SystemCore_EnumerableDebugViewEmptyException");
        }

        internal static bool IsOrInheritsFrom(this Type type, Type baseType)
        {
            Debug.Assert(type != null);
            Debug.Assert(baseType != null);
            Debug.Assert(!baseType.IsInterface);

            if (type.IsInterface)
            {
                return false;
            }

            do
            {
                if (type.Equals(baseType))
                {
                    return true;
                }
                type = type.BaseType;
            }
            while (type != null);

            return false;
        }

        private static bool IsMscorlib(this Assembly assembly)
        {
            return assembly.GetReferencedAssemblies().Length == 0;
        }

        private static bool IsMscorlibType(this Type type, string @namespace, string name)
        {
            // Ignore IsMscorlib for now since type.Assembly returns
            // System.Runtime.dll for some types in mscorlib.dll.
            // TODO: Re-enable commented out check.
            return type.IsType(@namespace, name) /*&& type.Assembly.IsMscorlib()*/;
        }

        internal static bool IsOrInheritsFrom(this Type type, string @namespace, string name)
        {
            do
            {
                if (type.IsType(@namespace, name))
                {
                    return true;
                }
                type = type.BaseType;
            }
            while (type != null);
            return false;
        }

        internal static bool IsType(this Type type, string @namespace, string name)
        {
            Debug.Assert((@namespace == null) || (@namespace.Length > 0)); // Type.Namespace is null not empty.
            Debug.Assert(!string.IsNullOrEmpty(name));
            return string.Equals(type.Namespace, @namespace, StringComparison.Ordinal) &&
                string.Equals(type.Name, name, StringComparison.Ordinal);
        }
    }
}
