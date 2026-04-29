using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AgentWorkspace.Daemon;
using AgentWorkspace.Daemon.Auth;
using AgentWorkspace.Daemon.Channels;

namespace AgentWorkspace.Tests.Daemon;

public sealed class DaemonHostTests : IDisposable
{
    private readonly string _root;

    public DaemonHostTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "awt-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch { /* best effort */ }
    }

    private DaemonHostOptions BuildOptions() => new()
    {
        TokenPath = Path.Combine(_root, "session.token"),
        Channel = new ControlChannelOptions
        {
            PipeName = $"awt.test.host.{Guid.NewGuid():N}",
            HandshakeTimeout = TimeSpan.FromSeconds(2),
        },
        DeleteTokenOnShutdown = true,
    };

    [Fact]
    public async Task StartAsync_WritesTokenAndAcceptsClient()
    {
        var options = BuildOptions();

        await using (var host = new DaemonHost(options))
        {
            await host.StartAsync();

            Assert.True(File.Exists(options.TokenPath));
            var saved = SessionTokenStore.Load(options.TokenPath);

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await using var client = new NamedPipeClientStream(
                ".", host.ResolvedPipeName!, PipeDirection.InOut, PipeOptions.Asynchronous);
            await client.ConnectAsync(cts.Token);

            await HandshakeProtocol.WriteStringFrameAsync(
                client, HandshakeProtocol.OpHello, saved.Value, cts.Token);

            var welcome = await HandshakeProtocol.ReadFrameAsync(client, cts.Token);
            Assert.Equal(HandshakeProtocol.OpWelcome, welcome.Op);
        }

        // Token should be cleaned up on shutdown (DeleteTokenOnShutdown=true).
        Assert.False(File.Exists(options.TokenPath));
    }

    [Fact]
    public async Task StartAsync_RegeneratesTokenEachStart()
    {
        var options = BuildOptions();

        string firstValue;
        await using (var host = new DaemonHost(options))
        {
            await host.StartAsync();
            firstValue = SessionTokenStore.Load(options.TokenPath).Value;
        }

        // Different host instance → fresh token.
        var options2 = new DaemonHostOptions
        {
            TokenPath = options.TokenPath,
            Channel = options.Channel with { PipeName = $"awt.test.host.{Guid.NewGuid():N}" },
            DeleteTokenOnShutdown = true,
        };

        string secondValue;
        await using (var host2 = new DaemonHost(options2))
        {
            await host2.StartAsync();
            secondValue = SessionTokenStore.Load(options2.TokenPath).Value;
        }

        Assert.NotEqual(firstValue, secondValue);
    }
}
