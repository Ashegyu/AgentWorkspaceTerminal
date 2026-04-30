using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AgentWorkspace.Abstractions.Agents;

namespace AgentWorkspace.Abstractions.Workflows;

/// <summary>
/// Seam between workflow logic and any UI that collects approval from the user.
/// Tests inject <c>AutoApproveGateway</c>; WPF injects <c>DialogApprovalGateway</c>.
/// </summary>
public interface IApprovalGateway
{
    /// <summary>
    /// Present <paramref name="actions"/> to the user and return their decision.
    /// Must be called on the UI thread (or marshalled there by the implementation).
    /// </summary>
    ValueTask<ApprovalDecision> RequestApprovalAsync(
        IReadOnlyList<ActionRequestEvent> actions,
        CancellationToken cancellationToken = default);
}
