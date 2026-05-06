using AgentWorkspace.Abstractions.Ids;
using AgentWorkspace.App.Wpf.PaneMessage;

namespace AgentWorkspace.Tests.App;

public sealed class PaneMessageDispatcherTests
{
    [Fact]
    public void BuildChoices_UsesLayoutPaneOrder_ForKeyboardScanning()
    {
        var first = PaneId.New();
        var second = PaneId.New();
        var third = PaneId.New();

        var choices = PaneMessageDispatcher.BuildChoices(
            layoutPaneOrder: new[] { first, second, third },
            openPanes: new[] { third, first, second },
            focusedPane: second,
            titleProvider: id => id == first ? "api" : id == second ? "worker" : "logs");

        Assert.Collection(
            choices,
            item =>
            {
                Assert.Equal(first, item.PaneId);
                Assert.Equal("api", item.Label);
            },
            item =>
            {
                Assert.Equal(second, item.PaneId);
                Assert.Contains("worker", item.Label);
                Assert.Contains("현재 포커스", item.Label);
            },
            item =>
            {
                Assert.Equal(third, item.PaneId);
                Assert.Equal("logs", item.Label);
            });
    }

    [Fact]
    public void BuildChoices_SkipsLayoutPanesWithoutOpenSession_AndIgnoresExtraOpenPanes()
    {
        var first = PaneId.New();
        var missingSession = PaneId.New();
        var extraStaleSession = PaneId.New();

        var choices = PaneMessageDispatcher.BuildChoices(
            layoutPaneOrder: new[] { first, missingSession },
            openPanes: new[] { extraStaleSession, first },
            focusedPane: first,
            titleProvider: _ => null);

        var choice = Assert.Single(choices);
        Assert.Equal(first, choice.PaneId);
        Assert.Contains("패널 1", choice.Label);
    }

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
