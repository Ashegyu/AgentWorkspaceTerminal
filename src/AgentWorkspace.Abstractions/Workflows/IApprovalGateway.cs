using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace AgentWorkspace.Abstractions.Workflows;

/// <summary>
/// Seam between workflow logic and any UI that collects approval from the user.
/// Tests inject <c>AutoApproveGateway</c>; WPF injects <c>DialogApprovalGateway</c>.
/// </summary>
public interface IApprovalGateway
{
    /// <summary>
    /// Present <paramref name="items"/> to the user and return their decision.
    /// Each item carries the raw <c>ActionRequestEvent</c> alongside the upstream
    /// <c>PolicyDecision</c> so the UI can show Risk and Reason.
    /// Must be called on the UI thread (or marshalled there by the implementation).
    /// </summary>
    ValueTask<ApprovalDecision> RequestApprovalAsync(
        IReadOnlyList<ApprovalRequestItem> items,
        CancellationToken cancellationToken = default);
}
