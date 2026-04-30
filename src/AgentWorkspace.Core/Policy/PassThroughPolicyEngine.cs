using System.Threading;
using System.Threading.Tasks;
using AgentWorkspace.Abstractions.Policy;

namespace AgentWorkspace.Core.Policy;

/// <summary>
/// Default <see cref="IPolicyEngine"/> that always returns <see cref="PolicyVerdict.AskUser"/>.
/// Used as the no-op default when callers haven't wired a real engine yet — preserves
/// pre-MVP-7 behaviour where every action goes to the approval gateway.
/// </summary>
public sealed class PassThroughPolicyEngine : IPolicyEngine
{
    public static readonly PassThroughPolicyEngine Instance = new();

    public ValueTask<PolicyDecision> EvaluateAsync(
        ProposedAction action,
        PolicyContext context,
        CancellationToken cancellationToken = default)
        => ValueTask.FromResult(new PolicyDecision(
            PolicyVerdict.AskUser,
            "Pass-through engine — every action requires user confirmation.",
            Risk.Medium));
}
