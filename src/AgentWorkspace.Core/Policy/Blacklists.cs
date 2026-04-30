using System.Collections.Generic;

namespace AgentWorkspace.Core.Policy;

/// <summary>
/// Pre-canned blacklist rule sets for the built-in <see cref="Abstractions.Policy.PolicyLevel"/>s.
/// Day 46 fills in <see cref="SafeDev"/>; <see cref="Empty"/> is the no-rule default used by tests
/// that don't care about hard-deny matches.
/// </summary>
public static class Blacklists
{
    /// <summary>The empty rule set — every command falls through to level-based evaluation.</summary>
    public static readonly IReadOnlyList<BlacklistRule> Empty = [];

    /// <summary>The rule set used by <see cref="Abstractions.Policy.PolicyLevel.SafeDev"/>.
    /// Populated on Day 46 with patterns drawn from DESIGN.md §9.2 and CWE-78 / OWASP guidance.</summary>
    public static readonly IReadOnlyList<BlacklistRule> SafeDev = [];
}
