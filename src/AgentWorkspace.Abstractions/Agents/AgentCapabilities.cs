namespace AgentWorkspace.Abstractions.Agents;

/// <summary>
/// Describes what an agent adapter can do. Adapters without structured output emit only
/// <see cref="AgentMessageEvent"/>s; plan/action events require <see cref="StructuredOutput"/>.
/// </summary>
public sealed record AgentCapabilities(
    bool StructuredOutput,
    bool SupportsPlanProposal,
    bool SupportsCancel);
