using AgentWorkspace.Abstractions.Agents;
using AgentWorkspace.Abstractions.Policy;

namespace AgentWorkspace.Abstractions.Workflows;

/// <summary>
/// One row submitted to <see cref="IApprovalGateway.RequestApprovalAsync"/>.
/// Pairs the agent's raw <see cref="ActionRequestEvent"/> with the upstream
/// <see cref="PolicyDecision"/> so the approval UI can show Risk badges and the
/// human-readable Reason returned by <see cref="IPolicyEngine"/>.
/// </summary>
public sealed record ApprovalRequestItem(
    ActionRequestEvent Action,
    PolicyDecision Decision);
