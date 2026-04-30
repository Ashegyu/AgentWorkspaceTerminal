using System.Text.RegularExpressions;
using AgentWorkspace.Abstractions.Policy;

namespace AgentWorkspace.Core.Policy;

/// <summary>
/// One regex-based hard-deny rule applied to <see cref="ExecuteCommand.CommandLine"/>.
/// Carries the risk classification and the reason returned in the resulting <see cref="PolicyDecision"/>.
/// </summary>
public sealed class BlacklistRule
{
    private readonly Regex _regex;

    public BlacklistRule(string pattern, Risk risk, string reason)
    {
        Pattern = pattern;
        Risk    = risk;
        Reason  = reason;
        _regex  = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.Compiled);
    }

    public string Pattern { get; }
    public Risk   Risk    { get; }
    public string Reason  { get; }

    public bool IsMatch(string input) => _regex.IsMatch(input);
}
