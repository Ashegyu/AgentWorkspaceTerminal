namespace AgentWorkspace.Abstractions.Pty;

/// <summary>
/// Console control signals deliverable to the foreground process group of a pane.
/// </summary>
public enum PtySignal
{
    /// <summary>Ctrl+C — CTRL_C_EVENT.</summary>
    Interrupt = 0,

    /// <summary>Ctrl+Break — CTRL_BREAK_EVENT.</summary>
    Break = 1,
}
