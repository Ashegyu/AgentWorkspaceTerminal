using System.Collections.Generic;
using AgentWorkspace.Abstractions.Ids;

namespace AgentWorkspace.Abstractions.Sessions;

/// <summary>
/// Persisted spec for one pane: how to (re)start its child process. Distinct from a live
/// <c>PaneStartOptions</c> in that it is an immutable record we serialize as JSON in SQLite,
/// and it does not carry the initial cell-grid size (which is renderer-decided at restore time).
/// </summary>
public sealed record PaneSpec(
    PaneId Pane,
    string Command,
    IReadOnlyList<string> Arguments,
    string? WorkingDirectory,
    IReadOnlyDictionary<string, string>? Environment);
