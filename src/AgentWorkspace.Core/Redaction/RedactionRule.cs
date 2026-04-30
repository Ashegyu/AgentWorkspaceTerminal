using System.Text.RegularExpressions;

namespace AgentWorkspace.Core.Redaction;

/// <summary>
/// One regex → placeholder substitution applied by <see cref="RegexRedactionEngine"/>.
/// </summary>
public sealed class RedactionRule
{
    private readonly Regex _regex;

    public RedactionRule(string pattern, string placeholder, RegexOptions options = RegexOptions.None)
    {
        Pattern     = pattern;
        Placeholder = placeholder;
        _regex      = new Regex(pattern, options | RegexOptions.Compiled);
    }

    public string Pattern     { get; }
    public string Placeholder { get; }

    public string Apply(string input) => _regex.Replace(input, Placeholder);
}
