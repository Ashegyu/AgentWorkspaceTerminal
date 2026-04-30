using System.Threading;
using AgentWorkspace.Abstractions.Agents;
using AgentWorkspace.Abstractions.Policy;

namespace AgentWorkspace.Abstractions.Workflows;

/// <summary>
/// Runtime context passed to every <see cref="IWorkflow"/> execution.
/// Holds per-run state (id, trigger, cancellation) plus the bundled
/// <see cref="WorkflowDependencies"/> seam.
/// Workflow code accesses dependencies through forwarding properties
/// (e.g. <see cref="AgentAdapter"/>) so callsites stay terse.
/// </summary>
public sealed record WorkflowContext(
    WorkflowExecutionId ExecutionId,
    WorkflowTrigger Trigger,
    WorkflowDependencies Dependencies,
    PolicyContext PolicyContext,
    CancellationToken CancellationToken)
{
    /// <summary>Convenience accessor for <see cref="WorkflowDependencies.AgentAdapter"/>.</summary>
    public IAgentAdapter AgentAdapter => Dependencies.AgentAdapter;

    /// <summary>Convenience accessor for <see cref="WorkflowDependencies.ApprovalGateway"/>.</summary>
    public IApprovalGateway ApprovalGateway => Dependencies.ApprovalGateway;

    /// <summary>Convenience accessor for <see cref="WorkflowDependencies.PolicyEngine"/>.</summary>
    public IPolicyEngine PolicyEngine => Dependencies.PolicyEngine;

    /// <summary>
    /// Convenience overload: build a context directly from individual seams
    /// (used by tests and the workflow engine boot path).
    /// </summary>
    public WorkflowContext(
        WorkflowExecutionId executionId,
        WorkflowTrigger trigger,
        IAgentAdapter agentAdapter,
        IApprovalGateway approvalGateway,
        IPolicyEngine policyEngine,
        PolicyContext policyContext,
        CancellationToken cancellationToken)
        : this(executionId,
               trigger,
               new WorkflowDependencies(agentAdapter, approvalGateway, policyEngine),
               policyContext,
               cancellationToken)
    {
    }
}
