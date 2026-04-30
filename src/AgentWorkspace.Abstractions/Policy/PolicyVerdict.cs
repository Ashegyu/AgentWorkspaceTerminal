namespace AgentWorkspace.Abstractions.Policy;

/// <summary>The three possible outcomes of evaluating a <see cref="ProposedAction"/> against a policy.</summary>
public enum PolicyVerdict
{
    /// <summary>Action is permitted without user prompt.</summary>
    Allow,

    /// <summary>Action requires user confirmation before execution.</summary>
    AskUser,

    /// <summary>Action is forbidden; the workflow must abort.</summary>
    Deny,
}
