using System;
using System.Collections.Generic;
using System.Linq;
using AgentWorkspace.Abstractions.Ids;
using AgentWorkspace.Abstractions.Layout;
using AgentWorkspace.Abstractions.Sessions;

namespace AgentWorkspace.App.Wpf.Sessions;

internal sealed record SessionRestorePane(
    PaneSpec Pane,
    string Title);

internal sealed record SessionRestorePlan(
    LayoutSnapshot Layout,
    IReadOnlyList<SessionRestorePane> Panes)
{
    public static SessionRestorePlan FromSnapshot(SessionSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        return new SessionRestorePlan(
            snapshot.Layout,
            snapshot.Panes
                .Select(pane => new SessionRestorePane(pane, DefaultPaneTitle(pane.Pane)))
                .ToList());
    }

    private static string DefaultPaneTitle(PaneId paneId) => $"pane {paneId.ToString()[..6]}";
}
