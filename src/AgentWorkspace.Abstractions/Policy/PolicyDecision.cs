namespace AgentWorkspace.Abstractions.Policy;

/// <summary>
/// Result of evaluating a <see cref="ProposedAction"/> against an <see cref="IPolicyEngine"/>.
/// </summary>
/// <param name="Verdict">Allow / AskUser / Deny.</param>
/// <param name="Reason">Human-readable justification, surfaced in approval UI and logs.</param>
/// <param name="Risk">Severity classification.</param>
/// <param name="RequireIndividualApproval">
/// When <see langword="true"/>, the action must not be batch-approved with sibling actions —
/// the user must confirm it on its own. Used for Critical-risk actions.
/// </param>
public sealed record PolicyDecision(
    PolicyVerdict Verdict,
    string Reason,
    Risk Risk,
    bool RequireIndividualApproval = false);
