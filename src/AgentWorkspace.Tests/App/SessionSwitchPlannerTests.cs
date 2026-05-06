using AgentWorkspace.Abstractions.Ids;
using AgentWorkspace.Abstractions.Sessions;
using AgentWorkspace.App.Wpf.Sessions;

namespace AgentWorkspace.Tests.App;

public sealed class SessionSwitchPlannerTests
{
    [Fact]
    public void Decide_ExistingSession_AttachesSelectedSessionId()
    {
        var currentId = new SessionId(Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"));
        var selectedId = new SessionId(Guid.Parse("11111111-2222-3333-4444-555555555555"));
        var choice = SessionChoiceItem.FromSession(
            new SessionInfo(
                selectedId,
                "feature-work",
                @"C:\Work\AgentWorkspaceTerminal",
                DateTimeOffset.Parse("2026-05-06T00:00:00Z"),
                DateTimeOffset.Parse("2026-05-06T01:00:00Z")),
            isCurrent: false);

        var decision = SessionSwitchPlanner.Decide(choice, currentId);

        Assert.Equal(SessionSwitchAction.AttachExisting, decision.Action);
        Assert.Equal(selectedId, decision.SessionId);
    }

    [Fact]
    public void Decide_NewSessionChoice_StartsFreshSession()
    {
        var currentId = new SessionId(Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"));

        var decision = SessionSwitchPlanner.Decide(SessionChoiceItem.NewSession(), currentId);

        Assert.Equal(SessionSwitchAction.StartNew, decision.Action);
        Assert.Null(decision.SessionId);
    }

    [Fact]
    public void Decide_CurrentSessionChoice_DoesNotReattach()
    {
        var currentId = new SessionId(Guid.Parse("11111111-2222-3333-4444-555555555555"));
        var choice = SessionChoiceItem.FromSession(
            new SessionInfo(
                currentId,
                "current",
                @"C:\Work",
                DateTimeOffset.Parse("2026-05-06T00:00:00Z"),
                DateTimeOffset.Parse("2026-05-06T01:00:00Z")),
            isCurrent: true);

        var decision = SessionSwitchPlanner.Decide(choice, currentId);

        Assert.Equal(SessionSwitchAction.AlreadyAttached, decision.Action);
        Assert.Equal(currentId, decision.SessionId);
    }
}
