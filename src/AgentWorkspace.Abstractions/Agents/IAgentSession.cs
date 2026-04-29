using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace AgentWorkspace.Abstractions.Agents;

/// <summary>
/// Represents a running agent session. Callers enumerate <see cref="Events"/> to receive
/// structured output and call <see cref="CancelAsync"/> to request termination.
/// </summary>
public interface IAgentSession : IAsyncDisposable
{
    AgentSessionId Id { get; }

    /// <summary>
    /// Yields events until the session ends (<see cref="AgentDoneEvent"/> or
    /// <see cref="AgentErrorEvent"/>). Enumeration must not be started more than once.
    /// </summary>
    IAsyncEnumerable<AgentEvent> Events { get; }

    /// <summary>Sends a follow-up message to the running agent.</summary>
    ValueTask SendAsync(AgentMessage msg, CancellationToken cancellationToken = default);

    /// <summary>
    /// Requests cancellation (SIGINT to the agent process). The agent should emit
    /// <see cref="AgentDoneEvent"/> shortly after; callers should still drain <see cref="Events"/>.
    /// </summary>
    ValueTask CancelAsync(CancellationToken cancellationToken = default);
}
