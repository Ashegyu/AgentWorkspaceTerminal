using AgentWorkspace.Abstractions.Agents;
using AgentWorkspace.Abstractions.Policy;

namespace AgentWorkspace.Abstractions.Workflows;

/// <summary>
/// Infrastructure seams a workflow needs that rarely change between runs.
/// Bundled into one record so <see cref="WorkflowContext"/> stays small even as more
/// dependencies (telemetry, redaction, transcripts) come online — see retro §3.6.
/// </summary>
public sealed record WorkflowDependencies(
    IAgentAdapter AgentAdapter,
    IApprovalGateway ApprovalGateway,
    IPolicyEngine PolicyEngine);
