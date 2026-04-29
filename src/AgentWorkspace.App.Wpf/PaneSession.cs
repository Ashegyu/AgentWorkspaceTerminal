using System;
using System.Buffers;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
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
    private static readonly JsonWriterOptions JsonOpts = new() { Indented = false };

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

        // Send the renderer its init signal once the PTY is alive — the renderer will respond
        // with its own resize message, which we relay back to ConPTY.
        await PostInitAsync().ConfigureAwait(false);

        _readPump = Task.Run(() => RunReadLoopAsync(_cts.Token));
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
                    string envelope = BuildOutputEnvelope(Id, chunk.Data.Span);
                    await _postToWeb(envelope).ConfigureAwait(false);
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
        await _postToWeb(BuildSimpleEnvelope("init", Id)).ConfigureAwait(false);
    }

    private void OnExited(object? sender, int exitCode)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await _postToWeb(BuildExitEnvelope(Id, exitCode)).ConfigureAwait(false);
            }
            catch { /* renderer may already be gone */ }
        });
    }

    private static string BuildSimpleEnvelope(string type, PaneId id)
    {
        using var ms = new System.IO.MemoryStream();
        using (var w = new Utf8JsonWriter(ms, JsonOpts))
        {
            w.WriteStartObject();
            w.WriteString("type", type);
            w.WriteString("paneId", id.ToString());
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private static string BuildOutputEnvelope(PaneId id, ReadOnlySpan<byte> data)
    {
        using var ms = new System.IO.MemoryStream(data.Length + 64);
        using (var w = new Utf8JsonWriter(ms, JsonOpts))
        {
            w.WriteStartObject();
            w.WriteString("type", "output");
            w.WriteString("paneId", id.ToString());
            w.WriteString("b64", Convert.ToBase64String(data));
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private static string BuildExitEnvelope(PaneId id, int code)
    {
        using var ms = new System.IO.MemoryStream();
        using (var w = new Utf8JsonWriter(ms, JsonOpts))
        {
            w.WriteStartObject();
            w.WriteString("type", "exit");
            w.WriteString("paneId", id.ToString());
            w.WriteNumber("code", code);
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
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
