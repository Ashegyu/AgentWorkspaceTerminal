using System.Collections.Generic;

namespace AgentWorkspace.Core.Policy;

/// <summary>
/// Parsed user-supplied policy add-ons read from <c>~/.agentworkspace/policies.yaml</c>.
/// Closes the maintenance slot from MVP-7 retro §3.5 / ADR-016: lets users append
/// blacklist + whitelist rules without recompiling. User rules are evaluated *after*
/// the built-in <see cref="Blacklists.SafeDev"/> / <see cref="Whitelists.TrustedLocal"/>
/// sets, so they cannot weaken the defense-in-depth ordering (a rule the user adds
/// to the whitelist still loses to a built-in blacklist hit).
/// </summary>
/// <param name="Version">Schema version. Currently 1; loaders refuse anything else.</param>
/// <param name="Blacklist">Additional hard-deny rules.</param>
/// <param name="Whitelist">Additional auto-allow rules (TrustedLocal only).</param>
public sealed record UserPolicyConfig(
    int                                   Version,
    IReadOnlyList<UserBlacklistRule>      Blacklist,
    IReadOnlyList<UserWhitelistRule>      Whitelist)
{
    /// <summary>Empty config — used when no policies.yaml is present.</summary>
    public static readonly UserPolicyConfig Empty = new(1, [], []);

    public bool IsEmpty => Blacklist.Count == 0 && Whitelist.Count == 0;
}

/// <param name="Pattern">Regex / prefix / glob — see <paramref name="Mode"/>.</param>
/// <param name="Mode">How <paramref name="Pattern"/> is matched. Default Regex.</param>
/// <param name="Risk">Risk classification surfaced to the approval dialog.</param>
/// <param name="Reason">Human-readable explanation shown to the user when this rule fires.</param>
public sealed record UserBlacklistRule(
    string                                Pattern,
    Abstractions.Policy.Risk              Risk,
    string                                Reason,
    MatchMode                             Mode = MatchMode.Regex);

/// <param name="Pattern">Regex / prefix / glob — see <paramref name="Mode"/>.</param>
/// <param name="Mode">How <paramref name="Pattern"/> is matched. Default Regex.</param>
/// <param name="Reason">Human-readable explanation shown when the rule auto-allows a command.</param>
public sealed record UserWhitelistRule(
    string                                Pattern,
    string                                Reason,
    MatchMode                             Mode = MatchMode.Regex);
