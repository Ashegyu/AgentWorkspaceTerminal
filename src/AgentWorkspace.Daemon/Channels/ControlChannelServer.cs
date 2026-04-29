using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using AgentWorkspace.Daemon.Auth;

namespace AgentWorkspace.Daemon.Channels;

/// <summary>
/// Day-15 NamedPipe listener. Accepts a connection, performs the bearer-token handshake, and
/// raises <see cref="ClientAuthenticated"/> when a peer is verified. The actual gRPC service is
/// wired in Day 18; until then the pipe stays open in Established state for follow-up Day 16/17
/// integration.
/// </summary>
public sealed class ControlChannelServer : IAsyncDisposable
{
    private readonly SessionToken _token;
    private readonly ControlChannelOptions _options;
    private readonly CancellationTokenSource _cts = new();
    private readonly List<Task> _connectionTasks = new();
    private readonly object _gate = new();
    private Task? _acceptLoop;
    private bool _started;
    private bool _disposed;

    public ControlChannelServer(SessionToken token, ControlChannelOptions? options = null)
    {
        _token = token ?? throw new ArgumentNullException(nameof(token));
        _options = options ?? ControlChannelOptions.Default;
        ResolvedPipeName = ResolvePipeName(_options);
    }

    /// <summary>The absolute pipe name the server listens on.</summary>
    public string ResolvedPipeName { get; }

    /// <summary>
    /// Raised on the accept loop's task pool thread when a client completes the handshake.
    /// Day 15 just records the event; Day 18 will replace this with a gRPC call dispatcher.
    /// </summary>
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
        lock (_gate)
        {
            pending = _connectionTasks.ToArray();
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
                // Server stream broke (often during shutdown). Loop and retry until cancelled.
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

        // Owner-only access. Day 18 may extend with explicit Authority\Service rules if needed.
        security.AddAccessRule(new PipeAccessRule(
            currentUser,
            PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance,
            AccessControlType.Allow));
        security.SetOwner(currentUser);
        return security;
    }

    private async Task HandleConnectionAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        await using var _ = pipe.ConfigureAwait(false);

        try
        {
            using var handshakeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            handshakeCts.CancelAfter(_options.HandshakeTimeout);

            HandshakeFrame frame;
            try
            {
                frame = await HandshakeProtocol.ReadFrameAsync(pipe, handshakeCts.Token)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException)
            {
                await TryRejectAsync(pipe, HandshakeProtocol.RejectReasonBadFrame, ct).ConfigureAwait(false);
                ClientRejected?.Invoke(this, new ControlClientRejectedEventArgs(HandshakeProtocol.RejectReasonBadFrame));
                return;
            }

            if (frame.Op != HandshakeProtocol.OpHello)
            {
                await TryRejectAsync(pipe, HandshakeProtocol.RejectReasonBadFrame, ct).ConfigureAwait(false);
                ClientRejected?.Invoke(this, new ControlClientRejectedEventArgs(HandshakeProtocol.RejectReasonBadFrame));
                return;
            }

            var presented = frame.PayloadAsAscii();
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
                await TryRejectAsync(pipe, HandshakeProtocol.RejectReasonBadToken, ct).ConfigureAwait(false);
                ClientRejected?.Invoke(this, new ControlClientRejectedEventArgs(HandshakeProtocol.RejectReasonBadToken));
                return;
            }

            await HandshakeProtocol
                .WriteStringFrameAsync(pipe, HandshakeProtocol.OpWelcome, HandshakeProtocol.ServerVersion, ct)
                .ConfigureAwait(false);

            ClientAuthenticated?.Invoke(this, new ControlClientAuthenticatedEventArgs(pipe));

            // Day 15 keeps the connection idle until the client closes or daemon shuts down.
            await WaitForCloseAsync(pipe, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { /* shutdown / handshake timeout */ }
        catch (IOException) { /* peer dropped */ }
    }

    private static async Task TryRejectAsync(NamedPipeServerStream pipe, string reason, CancellationToken ct)
    {
        try
        {
            await HandshakeProtocol
                .WriteStringFrameAsync(pipe, HandshakeProtocol.OpReject, reason, ct)
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
            // Discard bytes — Day 15 has no command surface yet.
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
