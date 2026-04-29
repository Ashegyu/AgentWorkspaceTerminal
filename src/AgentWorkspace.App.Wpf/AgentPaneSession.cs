using System;
using System.Collections.Generic;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using AgentWorkspace.Abstractions.Agents;
using AgentWorkspace.Abstractions.Ids;

namespace AgentWorkspace.App.Wpf;

/// <summary>
/// Composite that binds a terminal pane (<see cref="PaneSession"/>) to a structured agent
/// session (<see cref="IAgentSession"/>). Lifecycle management is unified: cancelling this
/// session sends SIGINT to the terminal pane and cancels the agent process.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class AgentPaneSession : IAsyncDisposable
{
    private readonly PaneSession _pane;
    private readonly IAgentSession _agent;

    public AgentPaneSession(PaneSession pane, IAgentSession agent)
    {
        _pane = pane;
        _agent = agent;
        PaneId = pane.Id;
        AgentSessionId = agent.Id;
    }

    public PaneId PaneId { get; }
    public AgentSessionId AgentSessionId { get; }

    /// <summary>Structured event stream from the agent (forwarded from <see cref="IAgentSession.Events"/>).</summary>
    public IAsyncEnumerable<AgentEvent> Events => _agent.Events;

    /// <summary>Sends a follow-up message to the agent.</summary>
    public ValueTask SendMessageAsync(AgentMessage msg, CancellationToken cancellationToken = default) =>
        _agent.SendAsync(msg, cancellationToken);

    /// <summary>Sends SIGINT to the terminal pane and cancels the agent session.</summary>
    public async ValueTask CancelAsync(CancellationToken cancellationToken = default)
    {
        await _pane.SendInterruptAsync(cancellationToken).ConfigureAwait(false);
        await _agent.CancelAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await _agent.DisposeAsync().ConfigureAwait(false);
        await _pane.DisposeAsync().ConfigureAwait(false);
    }
}
