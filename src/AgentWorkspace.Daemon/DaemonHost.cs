using System;
using System.Threading;
using System.Threading.Tasks;
using AgentWorkspace.Daemon.Auth;
using AgentWorkspace.Daemon.Channels;

namespace AgentWorkspace.Daemon;

/// <summary>
/// Day-15 daemon scaffold. Generates the session token, persists it with an owner-only ACL,
/// and starts the control-channel listener. Day 16 brings <c>IControlChannel</c> wiring,
/// Day 17 moves <c>Workspace</c> into this process.
/// </summary>
public sealed class DaemonHost : IAsyncDisposable
{
    private readonly DaemonHostOptions _options;
    private SessionToken? _token;
    private ControlChannelServer? _server;
    private bool _disposed;

    public DaemonHost(DaemonHostOptions? options = null)
    {
        _options = options ?? new DaemonHostOptions();
    }

    public string TokenPath => _options.TokenPath;
    public string? ResolvedPipeName => _server?.ResolvedPipeName;

    public async Task StartAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        _token = SessionToken.Generate();
        SessionTokenStore.Save(_token, _options.TokenPath);

        _server = new ControlChannelServer(_token, _options.Channel);
        _server.ClientAuthenticated += (_, args) =>
            _options.OnClientAuthenticated?.Invoke(args);
        _server.ClientRejected += (_, args) =>
            _options.OnClientRejected?.Invoke(args);

        await _server.Start().ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        if (_server is not null)
        {
            await _server.DisposeAsync().ConfigureAwait(false);
            _server = null;
        }

        if (_options.DeleteTokenOnShutdown)
        {
            try
            {
                if (System.IO.File.Exists(_options.TokenPath))
                {
                    System.IO.File.Delete(_options.TokenPath);
                }
            }
            catch (System.IO.IOException) { /* best effort */ }
            catch (UnauthorizedAccessException) { /* best effort */ }
        }
    }
}

public sealed class DaemonHostOptions
{
    public string TokenPath { get; init; } = SessionTokenStore.DefaultPath;
    public ControlChannelOptions Channel { get; init; } = ControlChannelOptions.Default;
    public bool DeleteTokenOnShutdown { get; init; } = true;
    public Action<ControlClientAuthenticatedEventArgs>? OnClientAuthenticated { get; init; }
    public Action<ControlClientRejectedEventArgs>? OnClientRejected { get; init; }
}
