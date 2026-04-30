namespace AgentWorkspace.Abstractions.Policy;

/// <summary>
/// Per-evaluation context provided to <see cref="IPolicyEngine.EvaluateAsync"/>.
/// Carries information that affects policy decisions but isn't part of the action itself
/// (workspace boundary, current profile, agent identity).
/// </summary>
/// <param name="WorkspaceRoot">
/// Absolute path of the project / workspace root. Used to classify file writes inside
/// versus outside the workspace.
/// </param>
/// <param name="Level">The active permission profile.</param>
/// <param name="AgentName">Display name of the agent requesting the action (e.g. "Claude Code").</param>
public sealed record PolicyContext(
    string? WorkspaceRoot,
    PolicyLevel Level,
    string AgentName);
