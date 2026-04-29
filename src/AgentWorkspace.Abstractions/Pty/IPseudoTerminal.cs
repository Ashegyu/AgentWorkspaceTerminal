using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AgentWorkspace.Abstractions.Ids;

namespace AgentWorkspace.Abstractions.Pty;

/// <summary>
/// A handle to a single pseudo-console pane plus its child process.
/// </summary>
/// <remarks>
/// <para>
/// Lifecycle: instances start in <see cref="PaneState.Created"/>; the caller invokes
/// <see cref="StartAsync"/> exactly once. After start, <see cref="WriteAsync"/>,
/// <see cref="ResizeAsync"/>, and <see cref="SignalAsync"/> may be called concurrently with one
/// another from any thread; the implementation serializes them onto a single actor channel to
/// honour the ConPTY API's threading constraints.
/// </para>
/// <para>
/// <see cref="ReadAsync"/> returns at most one active subscriber per pane; multi-fan-out belongs
/// to <c>IPaneOutputBroadcaster</c> rather than the raw terminal.
/// </para>
/// </remarks>
public interface IPseudoTerminal : IAsyncDisposable
{
    PaneId Id { get; }

    PaneState State { get; }

    /// <summary>
    /// Fires when the child process exits, regardless of success.
    /// The integer payload is the OS exit code (or -1 if the runtime forced termination).
    /// </summary>
    event EventHandler<int>? Exited;

    ValueTask StartAsync(PaneStartOptions options, CancellationToken cancellationToken);

    ValueTask WriteAsync(ReadOnlyMemory<byte> input, CancellationToken cancellationToken);

    ValueTask ResizeAsync(short columns, short rows, CancellationToken cancellationToken);

    IAsyncEnumerable<PtyChunk> ReadAsync(CancellationToken cancellationToken);

    ValueTask SignalAsync(PtySignal signal, CancellationToken cancellationToken);

    ValueTask KillAsync(KillMode mode, CancellationToken cancellationToken);
}
