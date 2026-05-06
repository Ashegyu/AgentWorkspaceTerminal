namespace AgentWorkspace.App.Wpf;

internal enum RendererShortcutCommand
{
    PaletteToggle,
    SplitRight,
    SplitDown,
    FocusNext,
    FocusPrevious,
    SendToPane,
}

internal static class RendererShortcutCommands
{
    public static bool TryParse(string? type, out RendererShortcutCommand command)
    {
        switch (type)
        {
            case "paletteToggle":
                command = RendererShortcutCommand.PaletteToggle;
                return true;
            case "splitRight":
                command = RendererShortcutCommand.SplitRight;
                return true;
            case "splitDown":
                command = RendererShortcutCommand.SplitDown;
                return true;
            case "focusNext":
                command = RendererShortcutCommand.FocusNext;
                return true;
            case "focusPrevious":
                command = RendererShortcutCommand.FocusPrevious;
                return true;
            case "sendToPane":
                command = RendererShortcutCommand.SendToPane;
                return true;
            default:
                command = default;
                return false;
        }
    }
}
