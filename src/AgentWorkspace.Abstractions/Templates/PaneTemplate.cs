using System.Collections.Generic;

namespace AgentWorkspace.Abstractions.Templates;

/// <summary>
/// Immutable definition of one pane inside a <see cref="WorkspaceTemplate"/>.
/// <see cref="Id"/> is the symbolic slot name used in layout refs — it is local to the
/// template and never equals a live <c>PaneId</c>.
/// </summary>
public sealed record PaneTemplate(
    string Id,
    string Command,
    IReadOnlyList<string> Args,
    string? Cwd,
    IReadOnlyDictionary<string, string>? Env);
