using AgentWorkspace.App.Wpf;

namespace AgentWorkspace.Tests.App;

public sealed class RendererShortcutCommandTests
{
    [Theory]
    [InlineData("paletteToggle", RendererShortcutCommand.PaletteToggle)]
    [InlineData("splitRight", RendererShortcutCommand.SplitRight)]
    [InlineData("splitDown", RendererShortcutCommand.SplitDown)]
    [InlineData("focusNext", RendererShortcutCommand.FocusNext)]
    [InlineData("focusPrevious", RendererShortcutCommand.FocusPrevious)]
    [InlineData("sendToPane", RendererShortcutCommand.SendToPane)]
    public void TryParse_KnownRendererShortcutTypes_ReturnsHostCommand(
        string type,
        RendererShortcutCommand expected)
    {
        bool parsed = RendererShortcutCommands.TryParse(type, out var actual);

        Assert.True(parsed);
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("input")]
    [InlineData("focusPane")]
    [InlineData("openPane")]
    public void TryParse_NonShortcutTypes_ReturnsFalse(string? type)
    {
        bool parsed = RendererShortcutCommands.TryParse(type, out _);

        Assert.False(parsed);
    }
}
