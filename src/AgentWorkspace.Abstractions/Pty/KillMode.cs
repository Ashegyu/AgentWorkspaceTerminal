namespace AgentWorkspace.Abstractions.Pty;

/// <summary>
/// Termination strategy for a pane and its descendant process tree.
/// </summary>
public enum KillMode
{
    /// <summary>
    /// Send Ctrl+C to the foreground process group, then wait briefly.
    /// </summary>
    Graceful = 0,

    /// <summary>
    /// Terminate the entire Job Object (pane + descendants) immediately.
    /// </summary>
    Force = 1,
}
