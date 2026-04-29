using System;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using AgentWorkspace.Abstractions.Channels;
using AgentWorkspace.Abstractions.Ids;
using AgentWorkspace.Abstractions.Pty;

namespace AgentWorkspace.App.Wpf;

/// <summary>
/// Glue between a single pane (managed by the active <see cref="IControlChannel"/> +
/// <see cref="IDataChannel"/> pair) and the WebView2-hosted xterm.js instance. Owns the read
/// pump that forwards data-channel frames to the renderer, plus the helpers that translate
/// inbound keystrokes / resize requests into control-channel calls.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class PaneSession : IAsyncDisposable
{
    private readonly Func<string, ValueTask> _postToWeb;
    private readonly IControlChannel _control;
    private readonly IDataChannel _data;
    private readonly CancellationTokenSource _cts = new();
    private readonly ChannelExitForwarder _exitForwarder;
    private Task? _readPump;
    private bool _started;
    private bool _disposed;

    public PaneSession(
        PaneId id,
        Func<string, ValueTask> postToWeb,
        IControlChannel control,
        IDataChannel data)
    {
        Id = id;
        _postToWeb = postToWeb;
        _control = control;
        _data = data;

        _exitForwarder = new ChannelExitForwarder(this);
        _control.PaneExited += _exitForwarder.OnPaneExited;
    }

    public PaneId Id { get; }

    /// <summary>
    /// Last successfully started options. Captured so <see cref="RestartAsync"/> can reuse them
    /// without the caller having to remember.
    /// </summary>
    public PaneStartOptions? LastStartOptions { get; private set; }

    /// <summary>
    /// Starts the child process via the control channel, then begins pumping data frames to the
    /// renderer.
    /// </summary>
    public async ValueTask StartAsync(PaneStartOptions options, CancellationToken cancellationToken)
    {
        if (_started)
        {
            throw new InvalidOperationException("Session already started.");
        }

        await _control.StartPaneAsync(Id, options, cancellationToken).ConfigureAwait(false);
        LastStartOptions = options;
        _started = true;

        await PostInitAsync().ConfigureAwait(false);
        _readPump = Task.Run(() => RunReadLoopAsync(_cts.Token));
    }

    /// <summary>
    /// Attaches to an already-live pane owned by the daemon without spawning a new child process.
    /// Used during session restore when the daemon reports <c>LiveState == "Running"</c>.
    /// </summary>
    public async ValueTask ReattachAsync(CancellationToken cancellationToken)
    {
        if (_started)
        {
            throw new InvalidOperationException("Session already started.");
        }

        _started = true;

        await PostInitAsync().ConfigureAwait(false);
        _readPump = Task.Run(() => RunReadLoopAsync(_cts.Token));
    }

    /// <summary>
    /// Tears down the current child via the control channel and starts a fresh one with the
    /// same options. Used by the Command Palette's "Restart Shell" entry.
    /// </summary>
    public async ValueTask RestartAsync(CancellationToken cancellationToken)
    {
        var options = LastStartOptions
            ?? throw new InvalidOperationException("Session has not been started yet.");

        try { await _control.ClosePaneAsync(Id, KillMode.Force, cancellationToken).ConfigureAwait(false); }
        catch { /* swallow */ }

        if (_readPump is not null)
        {
            try { await _readPump.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false); }
            catch { /* swallow */ }
            _readPump = null;
        }

        await _control.StartPaneAsync(Id, options, cancellationToken).ConfigureAwait(false);
        await _postToWeb(Envelope.Status($"shell restarted ({options.Command})")).ConfigureAwait(false);

        _readPump = Task.Run(() => RunReadLoopAsync(_cts.Token));
    }

    public ValueTask SendInterruptAsync(CancellationToken cancellationToken) =>
        _started
            ? _control.SignalPaneAsync(Id, PtySignal.Interrupt, cancellationToken)
            : ValueTask.CompletedTask;

    public ValueTask WriteInputAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken) =>
        _started ? _control.WriteInputAsync(Id, bytes, cancellationToken) : ValueTask.CompletedTask;

    public ValueTask ResizeAsync(short cols, short rows, CancellationToken cancellationToken) =>
        _started
            ? _control.ResizePaneAsync(Id, cols, rows, cancellationToken)
            : ValueTask.CompletedTask;

    private async Task RunReadLoopAsync(CancellationToken ct)
    {
        // Frames arrive over the NamedPipe wire as Convert.FromBase64String byte[] — heap-allocated,
        // not pooled. Returning these to ArrayPool would corrupt the pool. Just let GC reclaim them.
        try
        {
            await foreach (var frame in _data.SubscribeAsync(Id, ct).ConfigureAwait(false))
            {
                await _postToWeb(Envelope.Output(Id, frame.Bytes.Span)).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { /* shutting down */ }
    }

    private async ValueTask PostInitAsync()
    {
        await _postToWeb(Envelope.Init(Id)).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        _control.PaneExited -= _exitForwarder.OnPaneExited;

        if (!_cts.IsCancellationRequested)
        {
            _cts.Cancel();
        }

        if (_started)
        {
            try { await _control.ClosePaneAsync(Id, KillMode.Force, CancellationToken.None).ConfigureAwait(false); }
            catch { /* swallow */ }
        }

        if (_readPump is not null)
        {
            try { await _readPump.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
            catch { /* swallow */ }
        }
        _cts.Dispose();
    }

    /// <summary>
    /// Bridges <see cref="IControlChannel.PaneExited"/> to the renderer envelope. Filters by
    /// <see cref="Id"/> so each session only reacts to its own pane.
    /// </summary>
    private sealed class ChannelExitForwarder
    {
        private readonly PaneSession _owner;
        public ChannelExitForwarder(PaneSession owner) => _owner = owner;

        public void OnPaneExited(object? sender, PaneExitedEventArgs e)
        {
            if (!e.Pane.Equals(_owner.Id)) return;
            _ = Task.Run(async () =>
            {
                try
                {
                    await _owner._postToWeb(Envelope.Exit(_owner.Id, e.ExitCode)).ConfigureAwait(false);
                }
                catch { /* renderer may already be gone */ }
            });
        }
    }
}
