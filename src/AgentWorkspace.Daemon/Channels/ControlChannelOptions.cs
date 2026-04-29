using System;

namespace AgentWorkspace.Daemon.Channels;

/// <summary>
/// Options for <see cref="ControlChannelServer"/>. Mostly intended for tests; the daemon
/// uses <see cref="Default"/> at startup.
/// </summary>
public sealed record ControlChannelOptions
{
    public const string ControlPipePrefix = "agentworkspace.control";

    public string PipeName { get; init; } = ControlPipePrefix;

    /// <summary>Maximum simultaneous connections. The control channel is single-client by design.</summary>
    public int MaxConcurrentClients { get; init; } = 4;

    /// <summary>Time the server waits for a token before disconnecting a client.</summary>
    public TimeSpan HandshakeTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>If true, the server appends the current user SID to the pipe name.</summary>
    public bool ScopePipeNameToUser { get; init; } = true;

    public static ControlChannelOptions Default { get; } = new();
}
