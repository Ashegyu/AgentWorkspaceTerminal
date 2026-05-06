using AgentWorkspace.App.Wpf;

namespace AgentWorkspace.Tests.App;

public sealed class RendererShortcutDispatcherTests
{
    [Fact]
    public void Plan_PaletteToggle_DoesNotRequireWorkspace()
    {
        var action = RendererShortcutDispatcher.Plan(
            RendererShortcutCommand.PaletteToggle,
            new RendererShortcutHostState(HasWorkspace: false, HasOpenPanes: false));

        Assert.Equal(RendererShortcutAction.TogglePalette, action);
    }

    [Theory]
    [InlineData(nameof(RendererShortcutCommand.SplitRight))]
    [InlineData(nameof(RendererShortcutCommand.SplitDown))]
    [InlineData(nameof(RendererShortcutCommand.FocusNext))]
    [InlineData(nameof(RendererShortcutCommand.FocusPrevious))]
    public void Plan_WorkspaceCommands_NoOpWithoutWorkspace(string commandName)
    {
        var command = Enum.Parse<RendererShortcutCommand>(commandName);

        var action = RendererShortcutDispatcher.Plan(
            command,
            new RendererShortcutHostState(HasWorkspace: false, HasOpenPanes: false));

        Assert.Equal(RendererShortcutAction.None, action);
    }

    [Theory]
    [InlineData(nameof(RendererShortcutCommand.SplitRight), nameof(RendererShortcutAction.SplitRight))]
    [InlineData(nameof(RendererShortcutCommand.SplitDown), nameof(RendererShortcutAction.SplitDown))]
    [InlineData(nameof(RendererShortcutCommand.FocusNext), nameof(RendererShortcutAction.FocusNext))]
    [InlineData(nameof(RendererShortcutCommand.FocusPrevious), nameof(RendererShortcutAction.FocusPrevious))]
    public void Plan_WorkspaceCommands_RequireAtLeastOneOpenPane(
        string commandName,
        string expectedActionName)
    {
        var command = Enum.Parse<RendererShortcutCommand>(commandName);
        var expected = Enum.Parse<RendererShortcutAction>(expectedActionName);

        var noPaneAction = RendererShortcutDispatcher.Plan(
            command,
            new RendererShortcutHostState(HasWorkspace: true, HasOpenPanes: false));
        var readyAction = RendererShortcutDispatcher.Plan(
            command,
            new RendererShortcutHostState(HasWorkspace: true, HasOpenPanes: true));

        Assert.Equal(RendererShortcutAction.None, noPaneAction);
        Assert.Equal(expected, readyAction);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    public void Plan_SendToPane_ShowsNoOpenPanesUntilPaneExists(
        bool hasWorkspace,
        bool hasOpenPanes)
    {
        var action = RendererShortcutDispatcher.Plan(
            RendererShortcutCommand.SendToPane,
            new RendererShortcutHostState(hasWorkspace, hasOpenPanes));

        Assert.Equal(RendererShortcutAction.ShowNoOpenPanes, action);
    }

    [Fact]
    public void Plan_SendToPane_OpensDialogWhenPaneExists()
    {
        var action = RendererShortcutDispatcher.Plan(
            RendererShortcutCommand.SendToPane,
            new RendererShortcutHostState(HasWorkspace: true, HasOpenPanes: true));

        Assert.Equal(RendererShortcutAction.SendToPane, action);
    }
}
