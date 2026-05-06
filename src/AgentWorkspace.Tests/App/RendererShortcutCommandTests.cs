using AgentWorkspace.App.Wpf;

namespace AgentWorkspace.Tests.App;

public sealed class RendererShortcutCommandTests
{
    [Theory]
    [InlineData("paletteToggle", nameof(RendererShortcutCommand.PaletteToggle))]
    [InlineData("splitRight", nameof(RendererShortcutCommand.SplitRight))]
    [InlineData("splitDown", nameof(RendererShortcutCommand.SplitDown))]
    [InlineData("focusNext", nameof(RendererShortcutCommand.FocusNext))]
    [InlineData("focusPrevious", nameof(RendererShortcutCommand.FocusPrevious))]
    [InlineData("sendToPane", nameof(RendererShortcutCommand.SendToPane))]
    public void TryParse_KnownRendererShortcutTypes_ReturnsHostCommand(
        string type,
        string expected)
    {
        bool parsed = RendererShortcutCommands.TryParse(type, out var actual);

        Assert.True(parsed);
        Assert.Equal(expected, actual.ToString());
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
