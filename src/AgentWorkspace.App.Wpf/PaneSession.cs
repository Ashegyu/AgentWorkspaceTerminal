using System;
using System.Buffers;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using AgentWorkspace.Abstractions.Ids;
using AgentWorkspace.Abstractions.Pty;
using AgentWorkspace.ConPTY;

namespace AgentWorkspace.App.Wpf;

/// <summary>
/// Glue between a single <see cref="PseudoConsoleProcess"/> and the WebView2-hosted xterm.js
/// instance for one pane. Owns the read loop that pumps PTY bytes to the renderer and the
/// helpers that handle inbound keystrokes and resize requests.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class PaneSession : IAsyncDisposable
{
    private readonly Func<string, ValueTask> _postToWeb;
    private readonly CancellationTokenSource _cts = new();
    private PseudoConsoleProcess? _pty;
    private Task? _readPump;

    public PaneSession(PaneId id, Func<string, ValueTask> postToWeb)
    {
        Id = id;
        _postToWeb = postToWeb;
    }

    public PaneId Id { get; }

    /// <summary>
    /// OS process id of the currently running child, or 0 if no child is running.
    /// Exposed for diagnostics and integration tests.
    /// </summary>
    public int ProcessId => _pty?.ProcessId ?? 0;

    /// <summary>
    /// Last successfully started options. Captured so <see cref="RestartAsync"/> can reuse them
    /// without the caller having to remember.
    /// </summary>
    public PaneStartOptions? LastStartOptions { get; private set; }

    /// <summary>
    /// Starts the child process, then begins pumping PTY output to the renderer.
    /// </summary>
    public async ValueTask StartAsync(PaneStartOptions options, CancellationToken cancellationToken)
    {
        if (_pty is not null)
        {
            throw new InvalidOperationException("Session already started.");
        }

        _pty = new PseudoConsoleProcess(Id);
        _pty.Exited += OnExited;

        await _pty.StartAsync(options, cancellationToken).ConfigureAwait(false);
        LastStartOptions = options;

        // Send the renderer its init signal once the PTY is alive — the renderer will respond
        // with its own resize message, which we relay back to ConPTY.
        await PostInitAsync().ConfigureAwait(false);

        _readPump = Task.Run(() => RunReadLoopAsync(_cts.Token));
    }

    /// <summary>
    /// Tears down the current child process and pump, then starts a fresh one with the same
    /// options. Used by the Command Palette's "Restart Shell" entry.
    /// </summary>
    public async ValueTask RestartAsync(CancellationToken cancellationToken)
    {
        var options = LastStartOptions
            ?? throw new InvalidOperationException("Session has not been started yet.");

        if (_pty is not null)
        {
            try { await _pty.KillAsync(KillMode.Force, cancellationToken).ConfigureAwait(false); }
            catch { /* swallow */ }
            try { await _pty.DisposeAsync().ConfigureAwait(false); }
            catch { /* swallow */ }
            _pty = null;
        }

        if (_readPump is not null)
        {
            try { await _readPump.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false); }
            catch { /* swallow */ }
            _readPump = null;
        }

        _pty = new PseudoConsoleProcess(Id);
        _pty.Exited += OnExited;

        await _pty.StartAsync(options, cancellationToken).ConfigureAwait(false);
        // No re-init: the renderer's xterm instance is still alive; we just need bytes flowing
        // again. Send a status hint so the user sees something happened.
        await _postToWeb(Envelope.Status($"shell restarted ({options.Command})")).ConfigureAwait(false);

        _readPump = Task.Run(() => RunReadLoopAsync(_cts.Token));
    }

    /// <summary>
    /// Sends a Ctrl+C to the foreground program in the pane.
    /// </summary>
    public ValueTask SendInterruptAsync(CancellationToken cancellationToken)
    {
        if (_pty is null) return ValueTask.CompletedTask;
        return _pty.SignalAsync(PtySignal.Interrupt, cancellationToken);
    }

    /// <summary>
    /// Forwards user keystrokes (already UTF-8) into the PTY.
    /// </summary>
    public ValueTask WriteInputAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken)
    {
        if (_pty is null)
        {
            return ValueTask.CompletedTask;
        }
        return _pty.WriteAsync(bytes, cancellationToken);
    }

    /// <summary>
    /// Propagates a renderer-side resize to ConPTY.
    /// </summary>
    public ValueTask ResizeAsync(short cols, short rows, CancellationToken cancellationToken)
    {
        if (_pty is null)
        {
            return ValueTask.CompletedTask;
        }
        return _pty.ResizeAsync(cols, rows, cancellationToken);
    }

    private async Task RunReadLoopAsync(CancellationToken ct)
    {
        if (_pty is null) return;

        try
        {
            await foreach (var chunk in _pty.ReadAsync(ct).ConfigureAwait(false))
            {
                try
                {
                    await _postToWeb(Envelope.Output(Id, chunk.Data.Span)).ConfigureAwait(false);
                }
                finally
                {
                    if (MemoryMarshal.TryGetArray(chunk.Data, out var seg) && seg.Array is { } arr)
                    {
                        ArrayPool<byte>.Shared.Return(arr);
                    }
                }
            }
        }
        catch (OperationCanceledException) { /* shutting down */ }
    }

    private async ValueTask PostInitAsync()
    {
        await _postToWeb(Envelope.Init(Id)).ConfigureAwait(false);
    }

    private void OnExited(object? sender, int exitCode)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await _postToWeb(Envelope.Exit(Id, exitCode)).ConfigureAwait(false);
            }
            catch { /* renderer may already be gone */ }
        });
    }

    public async ValueTask DisposeAsync()
    {
        if (!_cts.IsCancellationRequested)
        {
            _cts.Cancel();
        }
        if (_pty is not null)
        {
            try { await _pty.KillAsync(KillMode.Force, CancellationToken.None).ConfigureAwait(false); }
            catch { /* swallow */ }
            await _pty.DisposeAsync().ConfigureAwait(false);
        }
        if (_readPump is not null)
        {
            try { await _readPump.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
            catch { /* swallow */ }
        }
        _cts.Dispose();
    }
}
