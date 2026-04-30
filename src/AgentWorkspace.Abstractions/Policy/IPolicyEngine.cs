using System.Threading;
using System.Threading.Tasks;

namespace AgentWorkspace.Abstractions.Policy;

/// <summary>
/// Decides whether a <see cref="ProposedAction"/> is permitted under the current
/// <see cref="PolicyContext"/>. Implementations are pure (no side effects) and fast —
/// the engine sits in the per-action hot path between agent output and approval UI.
/// </summary>
public interface IPolicyEngine
{
    ValueTask<PolicyDecision> EvaluateAsync(
        ProposedAction action,
        PolicyContext context,
        CancellationToken cancellationToken = default);
}
