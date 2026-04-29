using System;
using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AgentWorkspace.Client.Wire;
using AgentWorkspace.Daemon.Auth;
using AgentWorkspace.Daemon.Channels;

namespace AgentWorkspace.Tests.Daemon;

[SupportedOSPlatform("windows")]
public sealed class ControlChannelServerTests
{
    private static ControlChannelOptions UniqueOptions() => new()
    {
        // Keep ScopePipeNameToUser=true so we exercise the real server resolution path.
        PipeName = $"awt.test.{Guid.NewGuid():N}",
        MaxConcurrentClients = 2,
        HandshakeTimeout = TimeSpan.FromSeconds(2),
    };

    [Fact]
    public async Task Handshake_AcceptsCorrectToken_AndReturnsWelcome()
    {
        var token = SessionToken.Generate();
        var options = UniqueOptions();

        var authenticated = new TaskCompletionSource<ControlClientAuthenticatedEventArgs>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        await using var server = new ControlChannelServer(token, options);
        server.ClientAuthenticated += (_, args) => authenticated.TrySetResult(args);

        await server.Start();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var client = new NamedPipeClientStream(
            ".", server.ResolvedPipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await client.ConnectAsync(cts.Token);

        await RpcProtocol.WriteStringFrameAsync(
            client, RpcProtocol.OpHello, requestId: 0, token.Value, cts.Token);

        var welcome = await RpcProtocol.ReadFrameAsync(client, cts.Token);
        Assert.Equal(RpcProtocol.OpWelcome, welcome.Op);
        Assert.Equal(RpcProtocol.ServerVersion, Encoding.UTF8.GetString(welcome.Payload));

        var args = await authenticated.Task.WaitAsync(cts.Token);
        Assert.NotNull(args.Pipe);
    }

    [Fact]
    public async Task Handshake_RejectsBadToken_WithReason()
    {
        var serverToken = SessionToken.Generate();
        var attackerToken = SessionToken.Generate();
        var options = UniqueOptions();

        var rejected = new TaskCompletionSource<ControlClientRejectedEventArgs>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        await using var server = new ControlChannelServer(serverToken, options);
        server.ClientRejected += (_, args) => rejected.TrySetResult(args);

        await server.Start();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var client = new NamedPipeClientStream(
            ".", server.ResolvedPipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await client.ConnectAsync(cts.Token);

        await RpcProtocol.WriteStringFrameAsync(
            client, RpcProtocol.OpHello, requestId: 0, attackerToken.Value, cts.Token);

        var reject = await RpcProtocol.ReadFrameAsync(client, cts.Token);
        Assert.Equal(RpcProtocol.OpReject, reject.Op);
        Assert.Equal(RpcProtocol.RejectReasonBadToken, Encoding.UTF8.GetString(reject.Payload));

        var args = await rejected.Task.WaitAsync(cts.Token);
        Assert.Equal(RpcProtocol.RejectReasonBadToken, args.Reason);
    }

    [Fact]
    public async Task Handshake_RejectsBadFrameMagic()
    {
        var token = SessionToken.Generate();
        var options = UniqueOptions();

        var rejected = new TaskCompletionSource<ControlClientRejectedEventArgs>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        await using var server = new ControlChannelServer(token, options);
        server.ClientRejected += (_, args) => rejected.TrySetResult(args);

        await server.Start();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var client = new NamedPipeClientStream(
            ".", server.ResolvedPipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await client.ConnectAsync(cts.Token);

        // Send a full bad-magic frame header so the server can read past header parsing.
        // Layout: [4B bad-magic] [op 1B] [requestId 4B] [payloadLen 4B]
        await client.WriteAsync("XXXX\x01\x00\x00\x00\x00\x00\x00\x00\x00"u8.ToArray(), cts.Token);
        await client.FlushAsync(cts.Token);

        var args = await rejected.Task.WaitAsync(cts.Token);
        Assert.Equal(RpcProtocol.RejectReasonBadFrame, args.Reason);
    }

    [Fact]
    public async Task PipeName_IsScopedToCurrentUserSid_ByDefault()
    {
        var token = SessionToken.Generate();
        var options = UniqueOptions();

        await using var server = new ControlChannelServer(token, options);
        await server.Start();

        var sid = WindowsIdentity.GetCurrent().User?.Value
            ?? throw new InvalidOperationException("no user SID");
        Assert.EndsWith(sid, server.ResolvedPipeName, StringComparison.Ordinal);
        Assert.StartsWith(options.PipeName, server.ResolvedPipeName, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DisposeAsync_StopsAcceptLoop_AndUnblocksFurtherConnects()
    {
        var token = SessionToken.Generate();
        var options = UniqueOptions();
        var server = new ControlChannelServer(token, options);
        await server.Start();
        var pipeName = server.ResolvedPipeName;

        await server.DisposeAsync();

        // Subsequent connect attempts should fail (timeout) since no pipe instance exists.
        await using var client = new NamedPipeClientStream(
            ".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);

        await Assert.ThrowsAnyAsync<Exception>(() =>
            client.ConnectAsync(timeout: 500));
    }
}
