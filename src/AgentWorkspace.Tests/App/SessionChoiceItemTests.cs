using AgentWorkspace.Abstractions.Ids;
using AgentWorkspace.Abstractions.Sessions;
using AgentWorkspace.App.Wpf.Sessions;

namespace AgentWorkspace.Tests.App;

public sealed class SessionChoiceItemTests
{
    [Fact]
    public void FromSession_IncludesNameShortIdAndCurrentMarker()
    {
        var id = new SessionId(Guid.Parse("11111111-2222-3333-4444-555555555555"));
        var info = new SessionInfo(
            id,
            "main-dev",
            @"C:\Work\AgentWorkspaceTerminal",
            new DateTimeOffset(2026, 5, 6, 1, 2, 3, TimeSpan.Zero),
            new DateTimeOffset(2026, 5, 6, 4, 5, 6, TimeSpan.Zero));

        var item = SessionChoiceItem.FromSession(info, isCurrent: true);

        Assert.False(item.CreatesNewSession);
        Assert.Equal(id, item.SessionId);
        Assert.Contains("main-dev", item.Label);
        Assert.Contains("111111", item.Label);
        Assert.Contains("현재", item.Label);
        Assert.Contains(@"C:\Work\AgentWorkspaceTerminal", item.Description);
    }

    [Fact]
    public void NewSessionChoice_IsExplicitAndHasNoSessionId()
    {
        var item = SessionChoiceItem.NewSession();

        Assert.True(item.CreatesNewSession);
        Assert.Null(item.SessionId);
        Assert.Contains("새 세션", item.Label);
    }
}
