using System.Text.RegularExpressions;

namespace AgentWorkspace.Core.Policy;

/// <summary>
/// One regex-based auto-allow rule applied to <c>ExecuteCommand.CommandLine</c>.
/// Used by <c>PolicyLevel.TrustedLocal</c> to skip the approval prompt for known-safe
/// developer commands (`git status`, `ls`, …). Whitelist evaluation runs AFTER the blacklist,
/// so a whitelisted command is still hard-denied if it also matches a blacklist rule.
/// </summary>
public sealed class WhitelistRule
{
    private readonly Regex _regex;

    public WhitelistRule(string pattern, string reason)
    {
        Pattern = pattern;
        Reason  = reason;
        _regex  = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.Compiled);
    }

    public string Pattern { get; }
    public string Reason  { get; }

    public bool IsMatch(string input) => _regex.IsMatch(input);
}
