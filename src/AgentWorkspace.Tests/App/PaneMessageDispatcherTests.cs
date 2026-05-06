using AgentWorkspace.Abstractions.Ids;
using AgentWorkspace.App.Wpf.PaneMessage;

namespace AgentWorkspace.Tests.App;

public sealed class PaneMessageDispatcherTests
{
    [Fact]
    public void TryCreateSendMessage_ReturnsFalse_WhenTargetPaneIsNotOpen()
    {
        var openPane = PaneId.New();
        var stalePane = PaneId.New();

        var created = PaneMessageDispatcher.TryCreateSendMessage(
            new[] { openPane },
            stalePane,
            "hello",
            out var message);

        Assert.False(created);
        Assert.Null(message);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void TryCreateSendMessage_ReturnsFalse_WhenTextIsEmpty(string? text)
    {
        var pane = PaneId.New();

        var created = PaneMessageDispatcher.TryCreateSendMessage(
            new[] { pane },
            pane,
            text,
            out var message);

        Assert.False(created);
        Assert.Null(message);
    }

    [Fact]
    public void TryCreateSendMessage_CreatesPaneSendMeshMessage_ForOpenPane()
    {
        var pane = PaneId.New();

        var created = PaneMessageDispatcher.TryCreateSendMessage(
            new[] { pane },
            pane,
            "hello\n",
            out var message);

        Assert.True(created);
        Assert.NotNull(message);
        Assert.Equal($"pane.{pane}.send", message!.Topic);
        Assert.Equal("send", message.Kind);
        Assert.Equal("hello\n", message.Payload);
    }
}
