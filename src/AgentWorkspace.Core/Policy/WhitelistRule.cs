using System;

namespace AgentWorkspace.Core.Policy;

/// <summary>
/// One auto-allow rule applied to <c>ExecuteCommand.CommandLine</c>.
/// Used by <c>PolicyLevel.TrustedLocal</c> to skip the approval prompt for known-safe
/// developer commands (`git status`, `ls`, …). Whitelist evaluation runs AFTER the blacklist,
/// so a whitelisted command is still hard-denied if it also matches a blacklist rule.
/// Mode defaults to <see cref="MatchMode.Regex"/> for backwards compatibility; <see cref="MatchMode.Prefix"/>
/// or <see cref="MatchMode.Glob"/> are exposed for future yaml-driven user policies.
/// </summary>
public sealed class WhitelistRule
{
    private readonly Func<string, bool> _matcher;

    public WhitelistRule(string pattern, string reason, MatchMode mode = MatchMode.Regex)
    {
        Pattern = pattern;
        Reason  = reason;
        Mode    = mode;
        _matcher = PatternMatcher.Compile(pattern, mode);
    }

    public string Pattern { get; }
    public string Reason  { get; }
    public MatchMode Mode { get; }

    public bool IsMatch(string input) => _matcher(input);
}
