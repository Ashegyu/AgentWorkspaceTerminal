using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AgentWorkspace.Abstractions.Ids;
using AgentWorkspace.Abstractions.Layout;

namespace AgentWorkspace.Abstractions.Sessions;

/// <summary>
/// Persistence boundary for workspace sessions. The store owns the on-disk schema and is the
/// only place that talks to SQLite. Implementations are required to be thread-safe.
/// </summary>
public interface ISessionStore
{
    /// <summary>
    /// Initialises the on-disk schema if necessary. Safe to call repeatedly.
    /// </summary>
    ValueTask InitializeAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Creates a fresh session row with the supplied meta. Returns the new session id.
    /// </summary>
    ValueTask<SessionId> CreateAsync(string name, string? workspaceRoot, CancellationToken cancellationToken);

    /// <summary>
    /// Lists all known sessions ordered by <c>LastAttachedAtUtc</c> descending so the caller
    /// can pick the most recent. Cheap to load — does not include layouts or pane specs.
    /// </summary>
    ValueTask<IReadOnlyList<SessionInfo>> ListAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Loads the full snapshot for the given session, or null if the session id is unknown.
    /// Updates <c>LastAttachedAtUtc</c> as a side effect.
    /// </summary>
    ValueTask<SessionSnapshot?> AttachAsync(SessionId id, CancellationToken cancellationToken);

    /// <summary>
    /// Persists or updates a single pane spec. Idempotent on (sessionId, paneId).
    /// </summary>
    ValueTask UpsertPaneAsync(SessionId id, PaneSpec pane, CancellationToken cancellationToken);

    /// <summary>
    /// Removes a pane spec (e.g. after the user closes the pane).
    /// </summary>
    ValueTask DeletePaneAsync(SessionId id, PaneId pane, CancellationToken cancellationToken);

    /// <summary>
    /// Atomically saves the layout tree and focused pane id for the session.
    /// </summary>
    ValueTask SaveLayoutAsync(SessionId id, LayoutSnapshot layout, CancellationToken cancellationToken);

    /// <summary>
    /// Permanently deletes a session (cascade-removes its panes and layout).
    /// </summary>
    ValueTask DeleteAsync(SessionId id, CancellationToken cancellationToken);
}
