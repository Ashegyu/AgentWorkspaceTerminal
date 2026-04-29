using System;
using System.Threading;
using System.Threading.Tasks;
using AgentWorkspace.Abstractions.Ids;
using AgentWorkspace.Abstractions.Pty;

namespace AgentWorkspace.Abstractions.Channels;

/// <summary>
/// Request/response surface that a client uses to manage panes hosted by the daemon.
/// MVP-1~2 wires this in-process directly to <c>PseudoConsoleProcess</c>; MVP-3 swaps the
/// implementation for one that talks gRPC over a Named Pipe (ADR-003).
/// </summary>
/// <remarks>
/// All methods are safe to call concurrently from multiple threads. Exit notifications are
/// pushed by the server via <see cref="PaneExited"/>; the client is expected to forward those
/// to the renderer.
/// </remarks>
public interface IControlChannel : IAsyncDisposable
{
    /// <summary>
    /// Allocates a fresh pane with id <paramref name="id"/> and starts the child process.
    /// Returns the pane state observed at the end of the start sequence.
    /// </summary>
    ValueTask<PaneState> StartPaneAsync(
        PaneId id,
        PaneStartOptions options,
        CancellationToken cancellationToken);

    /// <summary>Forwards user keystrokes (UTF-8) to the pane.</summary>
    ValueTask WriteInputAsync(
        PaneId id,
        ReadOnlyMemory<byte> bytes,
        CancellationToken cancellationToken);

    /// <summary>Resizes the pseudo console grid for the pane.</summary>
    ValueTask ResizePaneAsync(
        PaneId id,
        short columns,
        short rows,
        CancellationToken cancellationToken);

    /// <summary>Sends Ctrl+C / Ctrl+Break to the pane's foreground process.</summary>
    ValueTask SignalPaneAsync(
        PaneId id,
        PtySignal signal,
        CancellationToken cancellationToken);

    /// <summary>
    /// Closes the pane. <paramref name="mode"/> selects between graceful and forced kill.
    /// Returns the OS exit code (or -1 if the runtime forced termination).
    /// </summary>
    ValueTask<int> ClosePaneAsync(
        PaneId id,
        KillMode mode,
        CancellationToken cancellationToken);

    /// <summary>Raised when a pane's child process exits, regardless of cause.</summary>
    event EventHandler<PaneExitedEventArgs>? PaneExited;
}

public sealed class PaneExitedEventArgs : EventArgs
{
    public PaneExitedEventArgs(PaneId pane, int exitCode)
    {
        Pane = pane;
        ExitCode = exitCode;
    }

    public PaneId Pane { get; }
    public int ExitCode { get; }
}
