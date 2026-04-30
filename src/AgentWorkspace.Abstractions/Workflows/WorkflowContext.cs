using System.Threading;
using AgentWorkspace.Abstractions.Agents;
using AgentWorkspace.Abstractions.Policy;

namespace AgentWorkspace.Abstractions.Workflows;

/// <summary>
/// Runtime context passed to every <see cref="IWorkflow"/> execution.
/// Provides the infrastructure seams the workflow needs without coupling to concrete types.
/// </summary>
public sealed record WorkflowContext(
    WorkflowExecutionId ExecutionId,
    WorkflowTrigger Trigger,
    IAgentAdapter AgentAdapter,
    IApprovalGateway ApprovalGateway,
    IPolicyEngine PolicyEngine,
    PolicyContext PolicyContext,
    CancellationToken CancellationToken);
