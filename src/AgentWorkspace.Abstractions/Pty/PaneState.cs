namespace AgentWorkspace.Abstractions.Pty;

/// <summary>
/// Lifecycle states a pseudo-terminal pane can be in.
/// Transitions are monotonic: Created → Running → (Exited | Faulted).
/// </summary>
public enum PaneState
{
    Created = 0,
    Starting = 1,
    Running = 2,
    Exited = 3,
    Faulted = 4,
}
