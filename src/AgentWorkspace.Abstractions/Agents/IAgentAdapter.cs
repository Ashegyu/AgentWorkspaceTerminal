using System.Threading;
using System.Threading.Tasks;

namespace AgentWorkspace.Abstractions.Agents;

/// <summary>
/// Factory interface for launching agent sessions. Each concrete adapter targets one
/// agent CLI (e.g. Claude Code, Codex). The adapter is responsible for spawning the
/// process and wrapping its output as <see cref="AgentEvent"/> instances.
/// </summary>
public interface IAgentAdapter
{
    /// <summary>Display name shown in the UI (e.g. "Claude Code").</summary>
    string Name { get; }

    /// <summary>Declares which features this adapter supports.</summary>
    AgentCapabilities Capabilities { get; }

    /// <summary>Spawns the agent process and returns a live session handle.</summary>
    ValueTask<IAgentSession> StartSessionAsync(
        AgentSessionOptions options,
        CancellationToken cancellationToken = default);
}
