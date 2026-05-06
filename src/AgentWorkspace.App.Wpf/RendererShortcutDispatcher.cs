namespace AgentWorkspace.App.Wpf;

internal sealed record RendererShortcutHostState(
    bool HasWorkspace,
    bool HasOpenPanes);

internal enum RendererShortcutAction
{
    None,
    TogglePalette,
    SplitRight,
    SplitDown,
    FocusNext,
    FocusPrevious,
    SendToPane,
    ShowNoOpenPanes,
}

internal static class RendererShortcutDispatcher
{
    public static RendererShortcutAction Plan(
        RendererShortcutCommand command,
        RendererShortcutHostState state)
    {
        return command switch
        {
            RendererShortcutCommand.PaletteToggle => RendererShortcutAction.TogglePalette,
            RendererShortcutCommand.SplitRight => state.HasWorkspace && state.HasOpenPanes
                ? RendererShortcutAction.SplitRight
                : RendererShortcutAction.None,
            RendererShortcutCommand.SplitDown => state.HasWorkspace && state.HasOpenPanes
                ? RendererShortcutAction.SplitDown
                : RendererShortcutAction.None,
            RendererShortcutCommand.FocusNext => state.HasWorkspace && state.HasOpenPanes
                ? RendererShortcutAction.FocusNext
                : RendererShortcutAction.None,
            RendererShortcutCommand.FocusPrevious => state.HasWorkspace && state.HasOpenPanes
                ? RendererShortcutAction.FocusPrevious
                : RendererShortcutAction.None,
            RendererShortcutCommand.SendToPane => state.HasWorkspace && state.HasOpenPanes
                ? RendererShortcutAction.SendToPane
                : RendererShortcutAction.ShowNoOpenPanes,
            _ => RendererShortcutAction.None,
        };
    }
}
