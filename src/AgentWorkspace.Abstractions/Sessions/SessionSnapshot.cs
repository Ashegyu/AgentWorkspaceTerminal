using System.Collections.Generic;
using AgentWorkspace.Abstractions.Layout;

namespace AgentWorkspace.Abstractions.Sessions;

/// <summary>
/// Full restorable view of a session: its meta, the layout tree as last saved, and the
/// pane specs (one per leaf in the layout). Exposed by <c>ISessionStore.AttachAsync</c>.
/// </summary>
public sealed record SessionSnapshot(
    SessionInfo Info,
    LayoutSnapshot Layout,
    IReadOnlyList<PaneSpec> Panes);
