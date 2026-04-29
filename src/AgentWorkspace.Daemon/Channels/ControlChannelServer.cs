using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using AgentWorkspace.Abstractions.Sessions;
using AgentWorkspace.Client.Wire;
using AgentWorkspace.Daemon.Auth;

namespace AgentWorkspace.Daemon.Channels;

/// <summary>
/// Day-15 NamedPipe listener, evolved on Day 17 into a full RPC server. Accepts connections,
/// performs the bearer-token handshake (now over <see cref="RpcProtocol"/> magic), then hands
/// the authenticated pipe off to a per-connection <see cref="RpcDispatcher"/> that routes
/// requests at the shared <see cref="PtyControlChannel"/> + <see cref="ISessionStore"/>.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ControlChannelServer : IAsyncDisposable
{
    private readonly SessionToken _token;
    private readonly ControlChannelOptions _options;
    private readonly PtyControlChannel? _panes;
    private readonly ISessionStore? _store;
    private readonly CancellationTokenSource _cts = new();
    private readonly List<Task> _connectionTasks = new();
    private readonly List<RpcDispatcher> _dispatchers = new();
    private readonly object _gate = new();
    private Task? _acceptLoop;
    private bool _started;
    private bool _disposed;

    public ControlChannelServer(SessionToken token, ControlChannelOptions? options = null)
        : this(token, options, panes: null, store: null) { }

    /// <summary>
    /// Day-17 ctor that wires the pane channel + session store the dispatcher will route at.
    /// Day 15/16 callers that pass nulls still get the legacy "accept and idle" behaviour, used
    /// by the handshake-only smoke tests.
    /// </summary>
    public ControlChannelServer(
        SessionToken token,
        ControlChannelOptions? options,
        PtyControlChannel? panes,
        ISessionStore? store)
    {
        _token = token ?? throw new ArgumentNullException(nameof(token));
        _options = options ?? ControlChannelOptions.Default;
        _panes = panes;
        _store = store;
        ResolvedPipeName = ResolvePipeName(_options);
    }

    /// <summary>The absolute pipe name the server listens on.</summary>
    public string ResolvedPipeName { get; }

    /// <summary>Raised on the accept loop's task pool thread when a client completes the handshake.</summary>
    public event EventHandler<ControlClientAuthenticatedEventArgs>? ClientAuthenticated;

    public event EventHandler<ControlClientRejectedEventArgs>? ClientRejected;

    public Task Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_gate)
        {
            if (_started)
            {
                throw new InvalidOperationException("ControlChannelServer already started.");
            }
            _started = true;
            _acceptLoop = Task.Run(() => AcceptLoopAsync(_cts.Token));
        }
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        try { await _cts.CancelAsync().ConfigureAwait(false); }
        catch (ObjectDisposedException) { /* already disposed */ }

        Task[] pending;
        RpcDispatcher[] dispatchers;
        lock (_gate)
        {
            pending = _connectionTasks.ToArray();
            dispatchers = _dispatchers.ToArray();
            _dispatchers.Clear();
        }

        foreach (var d in dispatchers)
        {
            try { await d.DisposeAsync().ConfigureAwait(false); }
            catch { /* swallow */ }
        }

        if (_acceptLoop is not null)
        {
            try { await _acceptLoop.ConfigureAwait(false); }
            catch (OperationCanceledException) { /* expected */ }
            catch (IOException) { /* pipe broken on shutdown */ }
        }

        try { await Task.WhenAll(pending).ConfigureAwait(false); }
        catch { /* swallow per-connection errors during shutdown */ }

        _cts.Dispose();
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                pipe = CreateServerPipe();
                await pipe.WaitForConnectionAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                pipe?.Dispose();
                return;
            }
            catch (IOException)
            {
                pipe?.Dispose();
                if (ct.IsCancellationRequested) return;
                continue;
            }

            var connectionPipe = pipe;
            Task task = Task.Run(() => HandleConnectionAsync(connectionPipe, ct), ct);
            lock (_gate)
            {
                _connectionTasks.Add(task);
                _connectionTasks.RemoveAll(static t => t.IsCompleted);
            }
        }
    }

    private NamedPipeServerStream CreateServerPipe()
    {
        var security = BuildPipeSecurity();

        return NamedPipeServerStreamAcl.Create(
            pipeName: ResolvedPipeName,
            direction: PipeDirection.InOut,
            maxNumberOfServerInstances: _options.MaxConcurrentClients,
            transmissionMode: PipeTransmissionMode.Byte,
            options: PipeOptions.Asynchronous,
            inBufferSize: 64 * 1024,
            outBufferSize: 64 * 1024,
            pipeSecurity: security);
    }

    private static PipeSecurity BuildPipeSecurity()
    {
        var security = new PipeSecurity();
        var currentUser = WindowsIdentity.GetCurrent().User
            ?? throw new InvalidOperationException("Cannot resolve current Windows user SID.");

        security.AddAccessRule(new PipeAccessRule(
            currentUser,
            PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance,
            AccessControlType.Allow));
        security.SetOwner(currentUser);
        return security;
    }

    private async Task HandleConnectionAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        bool dispatcherTookOwnership = false;
        try
        {
            using var handshakeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            handshakeCts.CancelAfter(_options.HandshakeTimeout);

            RpcFrame frame;
            try
            {
                frame = await RpcProtocol.ReadFrameAsync(pipe, handshakeCts.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException)
            {
                await TryRejectAsync(pipe, RpcProtocol.RejectReasonBadFrame, ct).ConfigureAwait(false);
                ClientRejected?.Invoke(this, new ControlClientRejectedEventArgs(RpcProtocol.RejectReasonBadFrame));
                return;
            }

            if (frame.Op != RpcProtocol.OpHello)
            {
                await TryRejectAsync(pipe, RpcProtocol.RejectReasonBadFrame, ct).ConfigureAwait(false);
                ClientRejected?.Invoke(this, new ControlClientRejectedEventArgs(RpcProtocol.RejectReasonBadFrame));
                return;
            }

            var presented = frame.PayloadAsUtf8();
            SessionToken? presentedToken;
            try
            {
                presentedToken = SessionToken.FromValue(presented);
            }
            catch (ArgumentException)
            {
                presentedToken = null;
            }

            if (presentedToken is null || !_token.Equals(presentedToken))
            {
                await TryRejectAsync(pipe, RpcProtocol.RejectReasonBadToken, ct).ConfigureAwait(false);
                ClientRejected?.Invoke(this, new ControlClientRejectedEventArgs(RpcProtocol.RejectReasonBadToken));
                return;
            }

            await RpcProtocol
                .WriteStringFrameAsync(pipe, RpcProtocol.OpWelcome, requestId: 0, RpcProtocol.ServerVersion, ct)
                .ConfigureAwait(false);

            ClientAuthenticated?.Invoke(this, new ControlClientAuthenticatedEventArgs(pipe));

            // Day 17: hand the authenticated pipe to a per-connection dispatcher if the host
            // wired pane + store. Day 15-style "no dispatcher" servers fall back to the legacy
            // idle wait so the existing handshake-only tests keep working.
            if (_panes is not null && _store is not null)
            {
                var dispatcher = new RpcDispatcher(pipe, _panes, _store);
                lock (_gate) _dispatchers.Add(dispatcher);
                dispatcherTookOwnership = true;
                try
                {
                    await dispatcher.RunAsync(ct).ConfigureAwait(false);
                }
                finally
                {
                    lock (_gate) _dispatchers.Remove(dispatcher);
                    await dispatcher.DisposeAsync().ConfigureAwait(false);
                }
            }
            else
            {
                await WaitForCloseAsync(pipe, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { /* shutdown / handshake timeout */ }
        catch (IOException) { /* peer dropped */ }
        finally
        {
            if (!dispatcherTookOwnership)
            {
                try { await pipe.DisposeAsync().ConfigureAwait(false); }
                catch { /* swallow */ }
            }
        }
    }

    private static async Task TryRejectAsync(NamedPipeServerStream pipe, string reason, CancellationToken ct)
    {
        try
        {
            await RpcProtocol
                .WriteStringFrameAsync(pipe, RpcProtocol.OpReject, requestId: 0, reason, ct)
                .ConfigureAwait(false);
        }
        catch
        {
            // Best effort — peer may already be gone.
        }
    }

    private static async Task WaitForCloseAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        var buffer = new byte[256];
        while (!ct.IsCancellationRequested && pipe.IsConnected)
        {
            var read = await pipe.ReadAsync(buffer, ct).ConfigureAwait(false);
            if (read == 0) return;
        }
    }

    private static string ResolvePipeName(ControlChannelOptions options)
    {
        if (!options.ScopePipeNameToUser)
        {
            return options.PipeName;
        }
        var sid = WindowsIdentity.GetCurrent().User?.Value ?? "anonymous";
        return $"{options.PipeName}.{sid}";
    }
}

public sealed class ControlClientAuthenticatedEventArgs : EventArgs
{
    public ControlClientAuthenticatedEventArgs(NamedPipeServerStream pipe)
    {
        Pipe = pipe ?? throw new ArgumentNullException(nameof(pipe));
    }

    public NamedPipeServerStream Pipe { get; }
}

public sealed class ControlClientRejectedEventArgs : EventArgs
{
    public ControlClientRejectedEventArgs(string reason)
    {
        Reason = reason ?? throw new ArgumentNullException(nameof(reason));
    }

    public string Reason { get; }
}
