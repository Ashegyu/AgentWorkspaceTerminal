using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using AgentWorkspace.Client.Channels;

namespace AgentWorkspace.Client.Discovery;

/// <summary>
/// Day-17 daemon discovery: locate a running daemon via the bearer token at
/// <c>%LOCALAPPDATA%\AgentWorkspace\session.token</c>; if none is alive, spawn <c>awtd.exe</c>
/// next to the client binary and poll until the token shows up.
/// </summary>
/// <remarks>
/// <para>This deliberately does <b>not</b> kill the daemon when the client exits — ADR-010 Day 20
/// expects the daemon to outlive client crashes so the client can re-attach. Orphan daemons
/// during dev are acceptable until Day 20 wires up an idle-timeout.</para>
/// <para>Test harnesses can run a <c>DaemonHost</c> in-process and call
/// <see cref="ConnectAsync"/> with a custom token + pipe name to skip the spawn path entirely.
/// Set <see cref="DaemonDiscoveryOptions.AllowSpawn"/> to <c>false</c> to force connect-only.</para>
/// </remarks>
[SupportedOSPlatform("windows")]
public static class DaemonDiscovery
{
    /// <summary>Default token path used by the daemon.</summary>
    public static string DefaultTokenPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AgentWorkspace",
        "session.token");

    /// <summary>Default control-pipe prefix (the daemon appends the user SID).</summary>
    public const string DefaultPipeNamePrefix = "agentworkspace.control";

    /// <summary>
    /// Attempts to connect to a running daemon. If none can be reached, optionally spawns one
    /// and retries until <paramref name="options"/>.SpawnTimeout elapses.
    /// </summary>
    public static async Task<ClientConnection> ConnectAsync(
        DaemonDiscoveryOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);

        // 1) First attempt: assume daemon already running.
        var first = await TryConnectFromTokenAsync(options, cancellationToken).ConfigureAwait(false);
        if (first is not null) return first;

        if (!options.AllowSpawn)
        {
            throw new IOException("Daemon is not running and AllowSpawn=false.");
        }

        // 2) Spawn if we can find awtd.exe; then poll for token + retry.
        SpawnDaemon(options);

        var deadline = DateTimeOffset.UtcNow + options.SpawnTimeout;
        Exception? lastFailure = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var c = await TryConnectFromTokenAsync(options, cancellationToken).ConfigureAwait(false);
                if (c is not null) return c;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                lastFailure = ex;
            }

            try { await Task.Delay(options.PollInterval, cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
        }

        throw new IOException(
            $"Daemon spawn at '{options.DaemonExecutablePath}' did not become reachable within {options.SpawnTimeout}.",
            lastFailure);
    }

    private static async Task<ClientConnection?> TryConnectFromTokenAsync(
        DaemonDiscoveryOptions options,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(options.TokenPath)) return null;

        string token;
        try
        {
            token = File.ReadAllText(options.TokenPath).Trim();
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }

        if (string.IsNullOrEmpty(token)) return null;

        string pipeName = ResolvePipeName(options);

        using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        connectCts.CancelAfter(options.ConnectTimeout);

        var pipe = new NamedPipeClientStream(
            ".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            await pipe.ConnectAsync(connectCts.Token).ConfigureAwait(false);
        }
        catch (Exception)
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            return null;
        }

        var connection = new ClientConnection(pipe);
        try
        {
            await connection.PerformHandshakeAsync(token, connectCts.Token).ConfigureAwait(false);
            connection.StartReader();
            return connection;
        }
        catch (Exception)
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            return null;
        }
    }

    private static void SpawnDaemon(DaemonDiscoveryOptions options)
    {
        string exe = options.DaemonExecutablePath;
        if (!File.Exists(exe))
        {
            throw new FileNotFoundException(
                $"Cannot find daemon executable to spawn at '{exe}'.", exe);
        }

        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(exe) ?? Environment.CurrentDirectory,
        };
        // Detach: not waited on, not redirected, parent-independent. Day 20 will refine lifecycle.
        var proc = Process.Start(psi);
        if (proc is null)
        {
            throw new IOException($"Failed to spawn daemon at '{exe}'.");
        }
    }

    /// <summary>
    /// Resolves the pipe name the daemon listens on. Mirrors the daemon's logic in
    /// <c>ControlChannelOptions.ScopePipeNameToUser</c>.
    /// </summary>
    public static string ResolvePipeName(DaemonDiscoveryOptions options)
    {
        if (options.ExplicitPipeName is { } explicitName) return explicitName;
        var sid = WindowsIdentity.GetCurrent().User?.Value ?? "anonymous";
        return $"{options.PipeNamePrefix}.{sid}";
    }

    /// <summary>
    /// Default daemon executable path: <c>awtd.exe</c> next to the calling assembly.
    /// </summary>
    public static string DefaultDaemonExecutable() =>
        Path.Combine(AppContext.BaseDirectory, "awtd.exe");
}

/// <summary>
/// Configuration for <see cref="DaemonDiscovery.ConnectAsync"/>.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed record DaemonDiscoveryOptions
{
    /// <summary>Path to the daemon's bearer token. Default: <c>%LOCALAPPDATA%\AgentWorkspace\session.token</c>.</summary>
    public string TokenPath { get; init; } = DaemonDiscovery.DefaultTokenPath;

    /// <summary>Pipe-name prefix that the daemon scopes to the user SID. Default matches the daemon.</summary>
    public string PipeNamePrefix { get; init; } = DaemonDiscovery.DefaultPipeNamePrefix;

    /// <summary>If non-null, used verbatim instead of <see cref="PipeNamePrefix"/> + user SID.</summary>
    public string? ExplicitPipeName { get; init; }

    /// <summary>Path to the daemon executable for spawn fallback. Default: <c>awtd.exe</c> in <c>AppContext.BaseDirectory</c>.</summary>
    public string DaemonExecutablePath { get; init; } = DaemonDiscovery.DefaultDaemonExecutable();

    /// <summary>If false, never spawn — used by tests that own daemon lifecycle.</summary>
    public bool AllowSpawn { get; init; } = true;

    /// <summary>How long to wait for a single NamedPipe connect attempt.</summary>
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromMilliseconds(500);

    /// <summary>How long to keep retrying after spawning the daemon.</summary>
    public TimeSpan SpawnTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Sleep between retry attempts.</summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromMilliseconds(100);
}
