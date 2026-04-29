using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.IO;
using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using AgentWorkspace.Abstractions.Ids;
using AgentWorkspace.Client.Wire;

namespace AgentWorkspace.Client.Channels;

/// <summary>
/// Owns a single authenticated NamedPipe connection to the daemon and provides:
/// <list type="bullet">
///   <item>request/response correlation by <see cref="RpcProtocol.OpRequest"/> requestId</item>
///   <item>per-pane <see cref="System.Threading.Channels.Channel{PaneFramePushPayload}"/> queues
///   for incoming <see cref="RpcProtocol.OpPaneFramePush"/> frames</item>
///   <item>fan-out of <see cref="RpcProtocol.OpPaneExitedPush"/> events to subscribers</item>
/// </list>
/// One <see cref="ClientConnection"/> per process. <see cref="NamedPipeControlChannel"/> and
/// <see cref="NamedPipeDataChannel"/> wrap the same instance.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ClientConnection : IAsyncDisposable
{
    private readonly NamedPipeClientStream _pipe;
    private readonly Stream _stream;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly ConcurrentDictionary<uint, TaskCompletionSource<RpcResponse>> _inflight = new();
    private readonly ConcurrentDictionary<PaneId, Channel<PaneFramePushPayload>> _frameSinks = new();
    private readonly CancellationTokenSource _shutdown = new();
    private long _nextRequestId;
    private Task? _readerTask;
    private bool _disposed;

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public ClientConnection(NamedPipeClientStream pipe)
    {
        ArgumentNullException.ThrowIfNull(pipe);
        if (!pipe.IsConnected)
        {
            throw new ArgumentException("Pipe must be connected before constructing ClientConnection.", nameof(pipe));
        }
        _pipe = pipe;
        _stream = pipe;
    }

    /// <summary>Raised when the daemon pushes a <c>PaneExited</c> notification.</summary>
    public event EventHandler<PaneExitedPushPayload>? PaneExitedReceived;

    /// <summary>True until the connection has been disposed or the pipe has broken.</summary>
    public bool IsConnected => !_disposed && _pipe.IsConnected;

    /// <summary>
    /// Starts the reader loop. Must be called exactly once after handshake completes.
    /// </summary>
    public void StartReader()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_readerTask is not null)
        {
            throw new InvalidOperationException("Reader already started.");
        }
        _readerTask = Task.Run(() => RunReaderAsync(_shutdown.Token));
    }

    /// <summary>
    /// Issues an RPC and awaits the response. Throws <see cref="RpcException"/> on error
    /// envelope, <see cref="OperationCanceledException"/> if the connection drops.
    /// </summary>
    public async Task<TResult> InvokeAsync<TParams, TResult>(
        string method,
        TParams parameters,
        CancellationToken cancellationToken)
        where TParams : class
        where TResult : class
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        uint requestId = (uint)Interlocked.Increment(ref _nextRequestId);
        var paramsJson = JsonSerializer.SerializeToElement(parameters, JsonOpts);
        var envelope = new RpcRequest(method, paramsJson);
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(envelope, JsonOpts);

        var tcs = new TaskCompletionSource<RpcResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_inflight.TryAdd(requestId, tcs))
        {
            throw new InvalidOperationException("requestId collision (should be unreachable).");
        }

        using var reg = cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken));

        try
        {
            await SendFrameAsync(RpcProtocol.OpRequest, requestId, payload, cancellationToken).ConfigureAwait(false);
            var response = await tcs.Task.ConfigureAwait(false);

            if (response.Error is { } err)
            {
                throw new RpcException(err.Code, err.Message);
            }
            if (response.Result is not { } resultEl)
            {
                throw new InvalidDataException("RPC response missing both result and error.");
            }
            return resultEl.Deserialize<TResult>(JsonOpts)
                ?? throw new InvalidDataException("RPC result deserialised to null.");
        }
        finally
        {
            _inflight.TryRemove(requestId, out _);
        }
    }

    /// <summary>
    /// Subscribes to incoming pane-frame pushes for <paramref name="pane"/>. The returned
    /// channel reader yields the daemon's pushes until the pane is unsubscribed or the
    /// connection drops; the corresponding <see cref="RpcMethods.SubscribeFrames"/> RPC must
    /// be issued separately by the caller.
    /// </summary>
    public ChannelReader<PaneFramePushPayload> RegisterFrameSink(PaneId pane)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var ch = Channel.CreateUnbounded<PaneFramePushPayload>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = true,
        });
        if (!_frameSinks.TryAdd(pane, ch))
        {
            throw new InvalidOperationException($"Pane {pane} already has a subscriber on this connection.");
        }
        return ch.Reader;
    }

    /// <summary>Removes the frame sink installed by <see cref="RegisterFrameSink"/>.</summary>
    public void UnregisterFrameSink(PaneId pane)
    {
        if (_frameSinks.TryRemove(pane, out var ch))
        {
            ch.Writer.TryComplete();
        }
    }

    private async Task SendFrameAsync(byte op, uint requestId, ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await RpcProtocol.WriteFrameAsync(_stream, op, requestId, payload, ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task RunReaderAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var frame = await RpcProtocol.ReadFrameAsync(_stream, ct).ConfigureAwait(false);
                DispatchFrame(frame);
            }
        }
        catch (OperationCanceledException) { /* shutdown */ }
        catch (EndOfStreamException) { /* peer closed */ }
        catch (IOException) { /* pipe broken */ }
        catch (Exception ex)
        {
            FailAllInflight(ex);
        }
        finally
        {
            FailAllInflight(new IOException("Connection closed."));
            CompleteAllSinks();
        }
    }

    private void DispatchFrame(RpcFrame frame)
    {
        switch (frame.Op)
        {
            case RpcProtocol.OpResponse:
                {
                    if (_inflight.TryRemove(frame.RequestId, out var tcs))
                    {
                        try
                        {
                            var resp = JsonSerializer.Deserialize<RpcResponse>(frame.Payload, JsonOpts)
                                ?? throw new InvalidDataException("RPC response decoded to null.");
                            tcs.TrySetResult(resp);
                        }
                        catch (Exception ex)
                        {
                            tcs.TrySetException(ex);
                        }
                    }
                    break;
                }
            case RpcProtocol.OpPaneFramePush:
                {
                    var payload = JsonSerializer.Deserialize<PaneFramePushPayload>(frame.Payload, JsonOpts);
                    if (payload is null) return;

                    var paneId = PaneId.Parse(payload.PaneId);
                    if (_frameSinks.TryGetValue(paneId, out var ch))
                    {
                        ch.Writer.TryWrite(payload);
                    }
                    break;
                }
            case RpcProtocol.OpPaneExitedPush:
                {
                    var payload = JsonSerializer.Deserialize<PaneExitedPushPayload>(frame.Payload, JsonOpts);
                    if (payload is null) return;
                    PaneExitedReceived?.Invoke(this, payload);
                    break;
                }
            default:
                // Unknown push frame: log via debug write, drop. Daemon and client must agree on op set.
                System.Diagnostics.Debug.WriteLine($"[awtc] unknown op 0x{frame.Op:X2} (req {frame.RequestId}); dropped {frame.Payload.Length}B.");
                break;
        }
    }

    private void FailAllInflight(Exception cause)
    {
        foreach (var (_, tcs) in _inflight)
        {
            tcs.TrySetException(cause);
        }
        _inflight.Clear();
    }

    private void CompleteAllSinks()
    {
        foreach (var (_, ch) in _frameSinks)
        {
            ch.Writer.TryComplete();
        }
        _frameSinks.Clear();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        try { await _shutdown.CancelAsync().ConfigureAwait(false); }
        catch (ObjectDisposedException) { /* already cancelled */ }

        if (_readerTask is not null)
        {
            try { await _readerTask.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
            catch { /* swallow */ }
        }

        try { await _pipe.DisposeAsync().ConfigureAwait(false); }
        catch { /* swallow */ }

        _writeLock.Dispose();
        _shutdown.Dispose();
    }

    /// <summary>
    /// Performs the Day-15 handshake: writes <see cref="RpcProtocol.OpHello"/> with the bearer
    /// token, expects <see cref="RpcProtocol.OpWelcome"/>. Throws on any other response.
    /// </summary>
    public async Task PerformHandshakeAsync(string token, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(token);
        ObjectDisposedException.ThrowIf(_disposed, this);

        await SendFrameAsync(RpcProtocol.OpHello, 0, Encoding.UTF8.GetBytes(token), cancellationToken)
            .ConfigureAwait(false);

        var frame = await RpcProtocol.ReadFrameAsync(_stream, cancellationToken).ConfigureAwait(false);
        switch (frame.Op)
        {
            case RpcProtocol.OpWelcome:
                return;
            case RpcProtocol.OpReject:
                throw new IOException(
                    $"Daemon rejected handshake: {frame.PayloadAsUtf8()}");
            default:
                throw new IOException(
                    $"Daemon returned unexpected op 0x{frame.Op:X2} during handshake.");
        }
    }
}
