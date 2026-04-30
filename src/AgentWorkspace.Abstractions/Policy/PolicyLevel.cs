namespace AgentWorkspace.Abstractions.Policy;

/// <summary>
/// Permission profile selecting how strict the policy engine is.
/// Mirrors the `permissionProfiles` table in DESIGN.md §9.1.
/// </summary>
public enum PolicyLevel
{
    /// <summary>
    /// Reads allowed; writes / exec / deletes / network all denied.
    /// Use for code review, documentation tours, and audit-only workflows.
    /// Even MCP tools are denied — caller has to escalate to a higher profile.
    /// </summary>
    ReadOnly,

    /// <summary>
    /// Default for development. Reads allowed, writes/exec/network ask the user,
    /// and a hard-coded blacklist of dangerous commands is denied outright.
    /// </summary>
    SafeDev,

    /// <summary>Reads/writes/network allowed; execution still asks the user.</summary>
    TrustedLocal,
}
