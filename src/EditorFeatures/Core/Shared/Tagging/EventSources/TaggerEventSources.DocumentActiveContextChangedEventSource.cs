// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using Microsoft.CodeAnalysis.Editor.Tagging;
using Microsoft.CodeAnalysis.Text;
using Microsoft.VisualStudio.Text;

namespace Microsoft.CodeAnalysis.Editor.Shared.Tagging
{
    internal partial class TaggerEventSources
    {
        private class DocumentActiveContextChangedEventSource : AbstractWorkspaceTrackingTaggerEventSource
        {
            public DocumentActiveContextChangedEventSource(ITextBuffer subjectBuffer, TaggerDelay delay)
                : base(subjectBuffer, delay)
            {
            }

            public override string EventKind
            {
                get
                {
                    return PredefinedChangedEventKinds.DocumentActiveContextChanged;
                }
            }

            protected override void ConnectToWorkspace(Workspace workspace)
            {
                workspace.DocumentActiveContextChanged += OnDocumentActiveContextChanged;
            }

            protected override void DisconnectFromWorkspace(Workspace workspace)
            {
                workspace.DocumentActiveContextChanged -= OnDocumentActiveContextChanged;
            }

            private void OnDocumentActiveContextChanged(object sender, DocumentEventArgs e)
            {
                var document = SubjectBuffer.AsTextContainer().GetOpenDocumentInCurrentContext();

                if (document != null && document.Id == e.Document.Id)
                {
                    this.RaiseChanged();
                }
            }
        }
    }
}
