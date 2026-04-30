using System;
using System.Text;
using System.Text.RegularExpressions;

namespace AgentWorkspace.Core.Policy;

/// <summary>
/// Compiles a <see cref="MatchMode"/> + pattern pair into a single
/// <see cref="Func{String, Boolean}"/> predicate. Shared by
/// <see cref="WhitelistRule"/> and <see cref="BlacklistRule"/> so both rule types
/// support the same modes uniformly.
/// </summary>
internal static class PatternMatcher
{
    public static Func<string, bool> Compile(string pattern, MatchMode mode) => mode switch
    {
        MatchMode.Prefix => CompilePrefix(pattern),
        MatchMode.Glob   => CompileGlob(pattern),
        _                => CompileRegex(pattern),
    };

    private static Func<string, bool> CompileRegex(string pattern)
    {
        var rx = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.Compiled);
        return input => rx.IsMatch(input);
    }

    private static Func<string, bool> CompilePrefix(string pattern)
        => input => input.StartsWith(pattern, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Anchored glob: <c>*</c> → <c>.*</c>, <c>?</c> → <c>.</c>, every other character is escaped.
    /// Anchoring matches the existing whitelist semantic (the rule has to cover the whole command line).
    /// </summary>
    private static Func<string, bool> CompileGlob(string pattern)
    {
        var sb = new StringBuilder("^");
        foreach (var c in pattern)
        {
            switch (c)
            {
                case '*': sb.Append(".*"); break;
                case '?': sb.Append('.');  break;
                default:  sb.Append(Regex.Escape(c.ToString())); break;
            }
        }
        sb.Append('$');
        var rx = new Regex(sb.ToString(), RegexOptions.IgnoreCase | RegexOptions.Compiled);
        return input => rx.IsMatch(input);
    }
}
