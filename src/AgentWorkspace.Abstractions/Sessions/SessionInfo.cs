using System;
using AgentWorkspace.Abstractions.Ids;

namespace AgentWorkspace.Abstractions.Sessions;

/// <summary>
/// Lightweight session listing entry. Cheap to load; does not include layout or pane specs.
/// </summary>
public sealed record SessionInfo(
    SessionId Id,
    string Name,
    string? WorkspaceRoot,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset LastAttachedAtUtc);
