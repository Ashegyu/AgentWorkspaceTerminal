using System;
using System.Collections.Generic;
using System.Linq;

namespace AgentWorkspace.Abstractions.Templates;

/// <summary>
/// Thrown by <see cref="IWorkspaceTemplateLoader"/> when a template file fails to parse
/// or fails cross-ref validation.
/// </summary>
public sealed class WorkspaceTemplateException : Exception
{
    public IReadOnlyList<string> Errors { get; }

    public WorkspaceTemplateException(IReadOnlyList<string> errors)
        : base("Workspace template validation failed:\n" + string.Join("\n", errors.Select(e => "  - " + e)))
    {
        Errors = errors;
    }

    public WorkspaceTemplateException(string error)
        : this([error]) { }
}
