using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AgentWorkspace.Abstractions.Channels;
using AgentWorkspace.Abstractions.Ids;
using AgentWorkspace.Abstractions.Layout;
using AgentWorkspace.Abstractions.Pty;
using AgentWorkspace.Abstractions.Sessions;
using AgentWorkspace.Client.Wire;
using AgentWorkspace.Core.Sessions;

namespace AgentWorkspace.Daemon.Channels;

/// <summary>
/// Per-connection daemon-side RPC dispatcher. Reads frames off the authenticated pipe, routes
/// them to the shared <see cref="PtyControlChannel"/> / <see cref="ISessionStore"/>, and pushes
/// pane events back to the client over the same pipe.
/// </summary>
/// <remarks>
/// One instance per accepted client connection. The dispatcher does not own the pane channel —
/// multiple clients (in theory; today: one) share a single <see cref="PtyControlChannel"/> so
/// the pane lifecycle is decoupled from any individual client. Day 18 will replace the JSON
/// codec with gRPC; the request handlers below are codec-agnostic.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class RpcDispatcher : IAsyncDisposable
{
    private readonly NamedPipeServerStream _pipe;
    private readonly Stream _stream;
    private readonly PtyControlChannel _panes;
    private readonly ISessionStore _store;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly ConcurrentDictionary<PaneId, FrameSubscription> _subscriptions = new();
    private readonly CancellationTokenSource _cts = new();
    private bool _disposed;

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public RpcDispatcher(NamedPipeServerStream pipe, PtyControlChannel panes, ISessionStore store)
    {
        ArgumentNullException.ThrowIfNull(pipe);
        ArgumentNullException.ThrowIfNull(panes);
        ArgumentNullException.ThrowIfNull(store);

        _pipe = pipe;
        _stream = pipe;
        _panes = panes;
        _store = store;
        _panes.PaneExited += OnPaneExited;
    }

    /// <summary>
    /// Pumps frames off the pipe until the connection drops or <see cref="DisposeAsync"/> runs.
    /// </summary>
    public async Task RunAsync(CancellationToken externalToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(externalToken, _cts.Token);
        var token = linked.Token;

        try
        {
            while (!token.IsCancellationRequested)
            {
                RpcFrame frame;
                try
                {
                    frame = await RpcProtocol.ReadFrameAsync(_stream, token).ConfigureAwait(false);
                }
                catch (EndOfStreamException) { return; }
                catch (IOException) { return; }
                catch (OperationCanceledException) { return; }

                if (frame.Op != RpcProtocol.OpRequest)
                {
                    // Day 17 only the client sends requests; ignore other ops to stay forward-compatible.
                    continue;
                }

                _ = Task.Run(() => HandleRequestAsync(frame, token), token);
            }
        }
        finally
        {
            await CleanupSubscriptionsAsync().ConfigureAwait(false);
        }
    }

    private async Task HandleRequestAsync(RpcFrame frame, CancellationToken ct)
    {
        try
        {
            var req = JsonSerializer.Deserialize<RpcRequest>(frame.Payload, JsonOpts)
                ?? throw new InvalidDataException("RPC request decoded to null.");

            object? result;
            try
            {
                result = await DispatchAsync(req, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                await SendErrorAsync(frame.RequestId, code: 500, message: ex.Message, ct).ConfigureAwait(false);
                return;
            }

            await SendResultAsync(frame.RequestId, result, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { /* shutdown */ }
        catch (Exception ex)
        {
            try { await SendErrorAsync(frame.RequestId, 500, ex.Message, ct).ConfigureAwait(false); }
            catch { /* best effort */ }
        }
    }

    private async Task<object?> DispatchAsync(RpcRequest req, CancellationToken ct)
    {
        switch (req.Method)
        {
            case RpcMethods.StartPane:
                {
                    var p = req.Params.Deserialize<StartPaneRequest>(JsonOpts)
                        ?? throw new ArgumentException("StartPane payload missing.");
                    var paneId = PaneId.Parse(p.PaneId);
                    var opts = new PaneStartOptions(
                        Command: p.Command,
                        Arguments: p.Arguments,
                        WorkingDirectory: p.WorkingDirectory,
                        Environment: p.Environment,
                        InitialColumns: p.Cols,
                        InitialRows: p.Rows);
                    var state = await _panes.StartPaneAsync(paneId, opts, ct).ConfigureAwait(false);
                    return new StartPaneResult(state.ToString());
                }
            case RpcMethods.WriteInput:
                {
                    var p = req.Params.Deserialize<WriteInputRequest>(JsonOpts)
                        ?? throw new ArgumentException("WriteInput payload missing.");
                    var paneId = PaneId.Parse(p.PaneId);
                    var bytes = Convert.FromBase64String(p.BytesBase64);
                    await _panes.WriteInputAsync(paneId, bytes, ct).ConfigureAwait(false);
                    return new EmptyResult();
                }
            case RpcMethods.ResizePane:
                {
                    var p = req.Params.Deserialize<ResizePaneRequest>(JsonOpts)
                        ?? throw new ArgumentException("ResizePane payload missing.");
                    var paneId = PaneId.Parse(p.PaneId);
                    await _panes.ResizePaneAsync(paneId, p.Cols, p.Rows, ct).ConfigureAwait(false);
                    return new EmptyResult();
                }
            case RpcMethods.SignalPane:
                {
                    var p = req.Params.Deserialize<SignalPaneRequest>(JsonOpts)
                        ?? throw new ArgumentException("SignalPane payload missing.");
                    var paneId = PaneId.Parse(p.PaneId);
                    var sig = Enum.Parse<PtySignal>(p.Signal, ignoreCase: false);
                    await _panes.SignalPaneAsync(paneId, sig, ct).ConfigureAwait(false);
                    return new EmptyResult();
                }
            case RpcMethods.ClosePane:
                {
                    var p = req.Params.Deserialize<ClosePaneRequest>(JsonOpts)
                        ?? throw new ArgumentException("ClosePane payload missing.");
                    var paneId = PaneId.Parse(p.PaneId);
                    var mode = Enum.Parse<KillMode>(p.Mode, ignoreCase: false);

                    // Stop pushing frames before killing — avoids races where a final frame races the exit push.
                    if (_subscriptions.TryRemove(paneId, out var sub))
                    {
                        await sub.StopAsync().ConfigureAwait(false);
                    }

                    var exit = await _panes.ClosePaneAsync(paneId, mode, ct).ConfigureAwait(false);
                    return new ClosePaneResult(exit);
                }
            case RpcMethods.SubscribeFrames:
                {
                    var p = req.Params.Deserialize<PaneScopeRequest>(JsonOpts)
                        ?? throw new ArgumentException("SubscribeFrames payload missing.");
                    var paneId = PaneId.Parse(p.PaneId);
                    if (_subscriptions.ContainsKey(paneId))
                    {
                        return new EmptyResult();
                    }
                    var sub = new FrameSubscription(paneId, _panes, this);
                    if (!_subscriptions.TryAdd(paneId, sub))
                    {
                        await sub.StopAsync().ConfigureAwait(false);
                        return new EmptyResult();
                    }
                    sub.Start(ct);
                    return new EmptyResult();
                }
            case RpcMethods.UnsubscribeFrames:
                {
                    var p = req.Params.Deserialize<PaneScopeRequest>(JsonOpts)
                        ?? throw new ArgumentException("UnsubscribeFrames payload missing.");
                    var paneId = PaneId.Parse(p.PaneId);
                    if (_subscriptions.TryRemove(paneId, out var sub))
                    {
                        await sub.StopAsync().ConfigureAwait(false);
                    }
                    return new EmptyResult();
                }
            case RpcMethods.StoreInitialize:
                await _store.InitializeAsync(ct).ConfigureAwait(false);
                return new EmptyResult();
            case RpcMethods.StoreCreateSession:
                {
                    var p = req.Params.Deserialize<CreateSessionRequest>(JsonOpts)
                        ?? throw new ArgumentException("CreateSession payload missing.");
                    var id = await _store.CreateAsync(p.Name, p.WorkspaceRoot, ct).ConfigureAwait(false);
                    return new CreateSessionResult(id.ToString());
                }
            case RpcMethods.StoreListSessions:
                {
                    var infos = await _store.ListAsync(ct).ConfigureAwait(false);
                    var dtos = infos.Select(i => new SessionInfoDto(
                        i.Id.ToString(), i.Name, i.WorkspaceRoot,
                        i.CreatedAtUtc, i.LastAttachedAtUtc)).ToList();
                    return new ListSessionsResult(dtos);
                }
            case RpcMethods.StoreAttachSession:
                {
                    var p = req.Params.Deserialize<AttachSessionRequest>(JsonOpts)
                        ?? throw new ArgumentException("AttachSession payload missing.");
                    var sid = SessionId.Parse(p.SessionId);
                    var snapshot = await _store.AttachAsync(sid, ct).ConfigureAwait(false);
                    if (snapshot is null)
                    {
                        return new AttachSessionResult(false, null, null, null, null);
                    }
                    var info = new SessionInfoDto(
                        snapshot.Info.Id.ToString(),
                        snapshot.Info.Name,
                        snapshot.Info.WorkspaceRoot,
                        snapshot.Info.CreatedAtUtc,
                        snapshot.Info.LastAttachedAtUtc);
                    var layoutJson = LayoutJson.Serialize(snapshot.Layout.Root);
                    var panes = snapshot.Panes.Select(s => new PaneSpecDto(
                        s.Pane.ToString(),
                        s.Command,
                        s.Arguments,
                        s.WorkingDirectory,
                        s.Environment is null ? null : new Dictionary<string, string>(s.Environment),
                        _panes.IsKnown(s.Pane) ? "Running" : null)).ToList();
                    return new AttachSessionResult(
                        true, info, layoutJson, snapshot.Layout.Focused.ToString(), panes);
                }
            case RpcMethods.StoreUpsertPane:
                {
                    var p = req.Params.Deserialize<UpsertPaneRequest>(JsonOpts)
                        ?? throw new ArgumentException("UpsertPane payload missing.");
                    var sid = SessionId.Parse(p.SessionId);
                    var spec = new PaneSpec(
                        PaneId.Parse(p.Pane.PaneId),
                        p.Pane.Command,
                        p.Pane.Arguments,
                        p.Pane.WorkingDirectory,
                        p.Pane.Environment);
                    await _store.UpsertPaneAsync(sid, spec, ct).ConfigureAwait(false);
                    return new EmptyResult();
                }
            case RpcMethods.StoreDeletePane:
                {
                    var p = req.Params.Deserialize<DeletePaneRequest>(JsonOpts)
                        ?? throw new ArgumentException("DeletePane payload missing.");
                    var sid = SessionId.Parse(p.SessionId);
                    var paneId = PaneId.Parse(p.PaneId);
                    await _store.DeletePaneAsync(sid, paneId, ct).ConfigureAwait(false);
                    return new EmptyResult();
                }
            case RpcMethods.StoreSaveLayout:
                {
                    var p = req.Params.Deserialize<SaveLayoutRequest>(JsonOpts)
                        ?? throw new ArgumentException("SaveLayout payload missing.");
                    var sid = SessionId.Parse(p.SessionId);
                    var root = LayoutJson.Deserialize(p.LayoutJson);
                    var focused = PaneId.Parse(p.FocusedPaneId);
                    await _store.SaveLayoutAsync(sid, new LayoutSnapshot(root, focused), ct).ConfigureAwait(false);
                    return new EmptyResult();
                }
            case RpcMethods.StoreDeleteSession:
                {
                    var p = req.Params.Deserialize<DeleteSessionRequest>(JsonOpts)
                        ?? throw new ArgumentException("DeleteSession payload missing.");
                    var sid = SessionId.Parse(p.SessionId);
                    await _store.DeleteAsync(sid, ct).ConfigureAwait(false);
                    return new EmptyResult();
                }
            case RpcMethods.StartAgentSession:
                {
                    var p = req.Params.Deserialize<StartAgentSessionRequest>(JsonOpts)
                        ?? throw new ArgumentException("StartAgentSession payload missing.");
                    // MVP-5: daemon acknowledges registration; client manages Claude process lifecycle.
                    return new StartAgentSessionResult(p.AgentSessionId);
                }
            case RpcMethods.SpawnSubagent:
                {
                    var p = req.Params.Deserialize<SpawnSubagentRequest>(JsonOpts)
                        ?? throw new ArgumentException("SpawnSubagent payload missing.");
                    // Mesh-P3: daemon acknowledges; App.Wpf AgentMesh manages the actual spawn.
                    var childId = Guid.NewGuid().ToString("N");
                    return new SpawnSubagentResponse(ChildSessionId: childId, ChildPaneId: null);
                }
            case RpcMethods.MergeSubagent:
                {
                    var p = req.Params.Deserialize<MergeSubagentRequest>(JsonOpts)
                        ?? throw new ArgumentException("MergeSubagent payload missing.");
                    // Mesh-P3: daemon acknowledges; App.Wpf AgentMesh handles the merge protocol.
                    _ = p;
                    return new MergeSubagentResponse(Summary: null, ParentResumed: true);
                }
            case RpcMethods.WorkflowStart:
                {
                    var p = req.Params.Deserialize<WorkflowStartRequest>(JsonOpts)
                        ?? throw new ArgumentException("WorkflowStart payload missing.");
                    // MVP-6: daemon acknowledges; App.Wpf WorkflowEngine executes the workflow.
                    var execId = Guid.NewGuid().ToString("N");
                    return new WorkflowStartResult(execId);
                }
            case RpcMethods.WorkflowCancel:
                {
                    // MVP-6: cancellation is app-local; daemon records but does not act.
                    _ = req.Params.Deserialize<WorkflowCancelRequest>(JsonOpts);
                    return new EmptyResult();
                }
            case RpcMethods.Ping:
                return new EmptyResult();
            default:
                throw new InvalidOperationException($"Unknown RPC method '{req.Method}'.");
        }
    }

    private async Task SendResultAsync(uint requestId, object? result, CancellationToken ct)
    {
        var resJson = result is null
            ? JsonSerializer.SerializeToElement(new EmptyResult(), JsonOpts)
            : JsonSerializer.SerializeToElement(result, result.GetType(), JsonOpts);

        var resp = new RpcResponse(resJson, null);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(resp, JsonOpts);
        await SendFrameAsync(RpcProtocol.OpResponse, requestId, bytes, ct).ConfigureAwait(false);
    }

    private async Task SendErrorAsync(uint requestId, int code, string message, CancellationToken ct)
    {
        var resp = new RpcResponse(null, new RpcError(code, message));
        var bytes = JsonSerializer.SerializeToUtf8Bytes(resp, JsonOpts);
        await SendFrameAsync(RpcProtocol.OpResponse, requestId, bytes, ct).ConfigureAwait(false);
    }

    internal async Task SendFrameAsync(byte op, uint requestId, ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await RpcProtocol.WriteFrameAsync(_stream, op, requestId, payload, ct).ConfigureAwait(false);
        }
        catch (IOException) { /* peer dropped */ }
        catch (ObjectDisposedException) { /* pipe closed */ }
        finally
        {
            _writeLock.Release();
        }
    }

    private void OnPaneExited(object? sender, PaneExitedEventArgs e)
    {
        var payload = new PaneExitedPushPayload(e.Pane.ToString(), e.ExitCode);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOpts);
        // Fire and forget — write lock serialises with concurrent RPC responses.
        _ = SendFrameAsync(RpcProtocol.OpPaneExitedPush, requestId: 0, bytes, _cts.Token);
    }

    private async Task CleanupSubscriptionsAsync()
    {
        foreach (var (_, sub) in _subscriptions)
        {
            await sub.StopAsync().ConfigureAwait(false);
        }
        _subscriptions.Clear();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        _panes.PaneExited -= OnPaneExited;

        try { await _cts.CancelAsync().ConfigureAwait(false); }
        catch (ObjectDisposedException) { /* already cancelled */ }

        await CleanupSubscriptionsAsync().ConfigureAwait(false);

        try { await _pipe.DisposeAsync().ConfigureAwait(false); }
        catch { /* swallow */ }

        _writeLock.Dispose();
        _cts.Dispose();
    }

    /// <summary>
    /// Pumps frames from the daemon-side <see cref="IDataChannel"/> into
    /// <see cref="RpcProtocol.OpPaneFramePush"/> frames on the wire. Exists because
    /// <see cref="IDataChannel.SubscribeAsync"/> returns an IAsyncEnumerable and we need a
    /// fire-and-forget loop owning that subscription.
    /// </summary>
    private sealed class FrameSubscription
    {
        private readonly PaneId _pane;
        private readonly IDataChannel _data;
        private readonly RpcDispatcher _owner;
        private readonly CancellationTokenSource _stopCts = new();
        private Task? _pump;

        public FrameSubscription(PaneId pane, IDataChannel data, RpcDispatcher owner)
        {
            _pane = pane;
            _data = data;
            _owner = owner;
        }

        public void Start(CancellationToken external)
        {
            var linked = CancellationTokenSource.CreateLinkedTokenSource(external, _stopCts.Token);
            _pump = Task.Run(() => PumpAsync(linked.Token));
        }

        private async Task PumpAsync(CancellationToken ct)
        {
            try
            {
                await foreach (var frame in _data.SubscribeAsync(_pane, ct).ConfigureAwait(false))
                {
                    var payload = new PaneFramePushPayload(
                        _pane.ToString(),
                        frame.Sequence,
                        Convert.ToBase64String(frame.Bytes.Span));
                    var bytes = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOpts);

                    try
                    {
                        await _owner.SendFrameAsync(RpcProtocol.OpPaneFramePush, 0, bytes, ct).ConfigureAwait(false);
                    }
                    finally
                    {
                        // Return the rented buffer that PtyControlChannel handed us.
                        if (MemoryMarshal.TryGetArray(frame.Bytes, out var seg) && seg.Array is { } arr)
                        {
                            ArrayPool<byte>.Shared.Return(arr);
                        }
                    }
                }
            }
            catch (OperationCanceledException) { /* stopping */ }
            catch (InvalidOperationException) { /* pane already gone */ }
        }

        public async Task StopAsync()
        {
            try { await _stopCts.CancelAsync().ConfigureAwait(false); }
            catch (ObjectDisposedException) { /* already disposed */ }
            if (_pump is not null)
            {
                try { await _pump.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
                catch { /* best effort */ }
            }
            _stopCts.Dispose();
        }
    }
}
