using System.Collections.Generic;

namespace AgentWorkspace.Abstractions.Templates;

/// <summary>
/// Fully-parsed, validated workspace template. Produced by
/// <see cref="IWorkspaceTemplateLoader"/> and consumed by <c>TemplateRunner</c>.
/// </summary>
public sealed record WorkspaceTemplate(
    string Name,
    string? Description,
    IReadOnlyList<PaneTemplate> Panes,
    LayoutNodeTemplate Layout,
    string? Focus);
