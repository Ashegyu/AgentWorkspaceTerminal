using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using AgentWorkspace.Abstractions.Channels;
using AgentWorkspace.Abstractions.Ids;
using AgentWorkspace.Abstractions.Pty;

namespace AgentWorkspace.ConPTY.Channels;

/// <summary>
/// Day-16 in-process implementation of the control + data channel pair. Internally stores one
/// <see cref="PseudoConsoleProcess"/> per pane and forwards every method directly. Day 17 will
/// swap a NamedPipe-backed implementation in without touching the consumer.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class InProcessControlChannel : IControlChannel, IDataChannel
{
    private readonly ConcurrentDictionary<PaneId, Entry> _panes = new();
    private bool _disposed;

    public event EventHandler<PaneExitedEventArgs>? PaneExited;

    public ValueTask<PaneState> StartPaneAsync(
        PaneId id,
        PaneStartOptions options,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var entry = new Entry(new PseudoConsoleProcess(id));
        if (!_panes.TryAdd(id, entry))
        {
            entry.Pty.DisposeAsync().AsTask().GetAwaiter().GetResult();
            throw new InvalidOperationException($"Pane {id} is already started.");
        }

        entry.Pty.Exited += (_, code) =>
        {
            PaneExited?.Invoke(this, new PaneExitedEventArgs(id, code));
            entry.Reader.Writer.TryComplete();
        };

        return StartCoreAsync(entry, options, cancellationToken);
    }

    private static async ValueTask<PaneState> StartCoreAsync(
        Entry entry,
        PaneStartOptions options,
        CancellationToken ct)
    {
        await entry.Pty.StartAsync(options, ct).ConfigureAwait(false);

        // Spin up the consume-and-forward loop. Each subscriber gets a private channel; the
        // pump fans output out to all current subscribers, dropping bytes for sinks that fall
        // behind. (Day 16 has at most one subscriber per pane.)
        _ = Task.Run(() => entry.PumpAsync());
        return entry.Pty.State;
    }

    public ValueTask WriteInputAsync(
        PaneId id,
        ReadOnlyMemory<byte> bytes,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return GetEntry(id).Pty.WriteAsync(bytes, cancellationToken);
    }

    public ValueTask ResizePaneAsync(
        PaneId id,
        short columns,
        short rows,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return GetEntry(id).Pty.ResizeAsync(columns, rows, cancellationToken);
    }

    public ValueTask SignalPaneAsync(
        PaneId id,
        PtySignal signal,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return GetEntry(id).Pty.SignalAsync(signal, cancellationToken);
    }

    public async ValueTask<int> ClosePaneAsync(
        PaneId id,
        KillMode mode,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_panes.TryRemove(id, out var entry))
        {
            return -1;
        }

        try
        {
            await entry.Pty.KillAsync(mode, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Best-effort kill — Dispose still runs.
        }

        try
        {
            await entry.Pty.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Swallow per-pane teardown errors; we still want to drain remaining panes on shutdown.
        }

        entry.Reader.Writer.TryComplete();
        return entry.LastExitCode;
    }

    public async IAsyncEnumerable<PaneFrame> SubscribeAsync(
        PaneId pane,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var entry = GetEntry(pane);

        var sub = entry.AddSubscriber();
        try
        {
            while (await sub.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                while (sub.Reader.TryRead(out var frame))
                {
                    yield return frame;
                }
            }
        }
        finally
        {
            entry.RemoveSubscriber(sub);
        }
    }

    private Entry GetEntry(PaneId id) =>
        _panes.TryGetValue(id, out var e)
            ? e
            : throw new InvalidOperationException($"Pane {id} is not started.");

    /// <summary>
    /// Returns the OS pid of the child process backing <paramref name="pane"/>, or 0 if the
    /// pane is unknown or has not yet started. Diagnostics only — Day 17 daemon transport will
    /// expose pid through a control RPC instead.
    /// </summary>
    public int TryGetProcessId(PaneId pane) =>
        _panes.TryGetValue(pane, out var entry) ? entry.Pty.ProcessId : 0;

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        foreach (var (_, entry) in _panes)
        {
            try { await entry.Pty.KillAsync(KillMode.Force, CancellationToken.None).ConfigureAwait(false); }
            catch { /* best effort */ }
            try { await entry.Pty.DisposeAsync().ConfigureAwait(false); }
            catch { /* best effort */ }
            entry.Reader.Writer.TryComplete();
        }

        _panes.Clear();
    }

    /// <summary>
    /// Per-pane bookkeeping. <see cref="PumpAsync"/> reads from the underlying PTY and fans frames
    /// out to all current subscribers.
    /// </summary>
    private sealed class Entry
    {
        public Entry(PseudoConsoleProcess pty)
        {
            Pty = pty;
            Reader = Channel.CreateUnbounded<PaneFrame>(new UnboundedChannelOptions
            {
                SingleReader = false,
                SingleWriter = true,
            });
            pty.Exited += (_, code) => LastExitCode = code;
        }

        public PseudoConsoleProcess Pty { get; }
        public Channel<PaneFrame> Reader { get; }
        public int LastExitCode { get; private set; } = -1;

        private readonly object _gate = new();
        private readonly List<Channel<PaneFrame>> _subscribers = new();
        private long _sequence;

        public Channel<PaneFrame> AddSubscriber()
        {
            var ch = Channel.CreateUnbounded<PaneFrame>(new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = true,
            });
            lock (_gate) _subscribers.Add(ch);
            return ch;
        }

        public void RemoveSubscriber(Channel<PaneFrame> ch)
        {
            lock (_gate) _subscribers.Remove(ch);
            ch.Writer.TryComplete();
        }

        public async Task PumpAsync()
        {
            try
            {
                await foreach (var chunk in Pty.ReadAsync(default).ConfigureAwait(false))
                {
                    var seq = Interlocked.Increment(ref _sequence);
                    var frame = new PaneFrame(Pty.Id, chunk.Data, seq);

                    Channel<PaneFrame>[] snapshot;
                    lock (_gate) snapshot = _subscribers.ToArray();

                    foreach (var sub in snapshot)
                    {
                        sub.Writer.TryWrite(frame);
                    }
                }
            }
            catch (OperationCanceledException) { /* shutdown */ }
            catch (Exception)
            {
                // The PTY's own Exited event will fire and close subscribers.
            }
            finally
            {
                Channel<PaneFrame>[] snapshot;
                lock (_gate) snapshot = _subscribers.ToArray();
                foreach (var sub in snapshot) sub.Writer.TryComplete();
            }
        }
    }
}
