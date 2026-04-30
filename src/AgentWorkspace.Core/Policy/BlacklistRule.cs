using System;
using AgentWorkspace.Abstractions.Policy;

namespace AgentWorkspace.Core.Policy;

/// <summary>
/// One hard-deny rule applied to <see cref="ExecuteCommand.CommandLine"/>.
/// Carries the risk classification and the reason returned in the resulting <see cref="PolicyDecision"/>.
/// Mode defaults to <see cref="MatchMode.Regex"/> for backwards compatibility; <see cref="MatchMode.Prefix"/>
/// or <see cref="MatchMode.Glob"/> are exposed for future yaml-driven user policies.
/// </summary>
public sealed class BlacklistRule
{
    private readonly Func<string, bool> _matcher;

    public BlacklistRule(string pattern, Risk risk, string reason, MatchMode mode = MatchMode.Regex)
    {
        Pattern = pattern;
        Risk    = risk;
        Reason  = reason;
        Mode    = mode;
        _matcher = PatternMatcher.Compile(pattern, mode);
    }

    public string Pattern { get; }
    public Risk   Risk    { get; }
    public string Reason  { get; }
    public MatchMode Mode { get; }

    public bool IsMatch(string input) => _matcher(input);
}
