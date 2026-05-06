using AgentWorkspace.Abstractions.Ids;
using AgentWorkspace.Abstractions.Layout;
using AgentWorkspace.Abstractions.Sessions;
using AgentWorkspace.App.Wpf.Sessions;

namespace AgentWorkspace.Tests.App;

public sealed class SessionRestorePlanTests
{
    [Fact]
    public void FromSnapshot_PreservesFocusedPaneFromStoredLayout()
    {
        var focused = PaneId.Parse("11111111222233334444555555555555");
        var other = PaneId.Parse("aaaaaaaa222233334444555555555555");
        var snapshot = Snapshot(focused, other, focused);

        var plan = SessionRestorePlan.FromSnapshot(snapshot);

        Assert.Equal(focused, plan.Layout.Focused);
        Assert.Same(snapshot.Layout, plan.Layout);
    }

    [Fact]
    public void FromSnapshot_ReissuesDefaultTitleForEachRestoredPane()
    {
        var first = PaneId.Parse("11111111222233334444555555555555");
        var second = PaneId.Parse("aaaaaaaa222233334444555555555555");
        var snapshot = Snapshot(first, second, first);

        var plan = SessionRestorePlan.FromSnapshot(snapshot);

        Assert.Collection(
            plan.Panes,
            pane =>
            {
                Assert.Equal(first, pane.Pane.Pane);
                Assert.Equal("pane 111111", pane.Title);
            },
            pane =>
            {
                Assert.Equal(second, pane.Pane.Pane);
                Assert.Equal("pane aaaaaa", pane.Title);
            });
    }

    [Fact]
    public void FromSnapshot_UsesPersistedPaneTitleWhenPresent()
    {
        var paneId = PaneId.Parse("11111111222233334444555555555555");
        var snapshot = Snapshot(
            paneId,
            PaneId.Parse("aaaaaaaa222233334444555555555555"),
            paneId,
            includeSecondPane: false,
            firstTitle: "api server");

        var plan = SessionRestorePlan.FromSnapshot(snapshot);

        var restoreItem = Assert.Single(plan.Panes);
        Assert.Equal("api server", restoreItem.Title);
    }

    [Fact]
    public void FromSnapshot_OnlyPlansPanesFromRestoredSnapshot()
    {
        var restored = PaneId.Parse("11111111222233334444555555555555");
        var stalePreviousSessionPane = PaneId.Parse("ffffffff222233334444555555555555");
        var snapshot = Snapshot(restored, stalePreviousSessionPane, restored, includeSecondPane: false);

        var plan = SessionRestorePlan.FromSnapshot(snapshot);

        var restoreItem = Assert.Single(plan.Panes);
        Assert.Equal(restored, restoreItem.Pane.Pane);
    }

    [Fact]
    public void FromSnapshot_RunningPanePlansReattach()
    {
        var paneId = PaneId.Parse("11111111222233334444555555555555");
        var snapshot = Snapshot(
            paneId,
            PaneId.Parse("aaaaaaaa222233334444555555555555"),
            paneId,
            includeSecondPane: false,
            firstLiveState: "Running");

        var plan = SessionRestorePlan.FromSnapshot(snapshot);

        var restoreItem = Assert.Single(plan.Panes);
        Assert.Equal(SessionRestoreMode.Reattach, restoreItem.Mode);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Exited")]
    [InlineData("running")]
    public void FromSnapshot_NonRunningPanePlansStart(string? liveState)
    {
        var paneId = PaneId.Parse("11111111222233334444555555555555");
        var snapshot = Snapshot(
            paneId,
            PaneId.Parse("aaaaaaaa222233334444555555555555"),
            paneId,
            includeSecondPane: false,
            firstLiveState: liveState);

        var plan = SessionRestorePlan.FromSnapshot(snapshot);

        var restoreItem = Assert.Single(plan.Panes);
        Assert.Equal(SessionRestoreMode.Start, restoreItem.Mode);
    }

    private static SessionSnapshot Snapshot(
        PaneId first,
        PaneId second,
        PaneId focused,
        bool includeSecondPane = true,
        string? firstLiveState = null,
        string? secondLiveState = null,
        string? firstTitle = null,
        string? secondTitle = null)
    {
        LayoutNode root = includeSecondPane
            ? new SplitNode(
                LayoutId.Parse("99999999222233334444555555555555"),
                SplitDirection.Horizontal,
                0.5,
                new PaneNode(LayoutId.Parse("aaaaaaaa111133334444555555555555"), first),
                new PaneNode(LayoutId.Parse("bbbbbbbb111133334444555555555555"), second))
            : new PaneNode(LayoutId.Parse("aaaaaaaa111133334444555555555555"), first);

        var panes = new List<PaneSpec>
        {
            Pane(first, firstLiveState, firstTitle),
        };
        if (includeSecondPane)
        {
            panes.Add(Pane(second, secondLiveState, secondTitle));
        }

        return new SessionSnapshot(
            new SessionInfo(
                new SessionId(Guid.Parse("12345678-1234-1234-1234-123456789abc")),
                "restored",
                @"C:\Work",
                DateTimeOffset.Parse("2026-05-06T00:00:00Z"),
                DateTimeOffset.Parse("2026-05-06T01:00:00Z")),
            new LayoutSnapshot(root, focused),
            panes);
    }

    private static PaneSpec Pane(PaneId id, string? liveState = null, string? title = null) => new(
        id,
        "pwsh.exe",
        Array.Empty<string>(),
        @"C:\Work",
        null,
        liveState,
        title);
}
