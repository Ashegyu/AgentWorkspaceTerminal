namespace AgentWorkspace.Core.Policy;

/// <summary>
/// How a <see cref="WhitelistRule"/> or <see cref="BlacklistRule"/> pattern is matched
/// against the input command line. Designed for future yaml-driven user policies where
/// regex authoring is a usability burden — see retro §3.5.
/// </summary>
public enum MatchMode
{
    /// <summary>Treat <c>Pattern</c> as an ECMAScript regex (default; preserves existing behaviour).</summary>
    Regex,

    /// <summary>Match if input starts with <c>Pattern</c> (case-insensitive). Useful for "all <c>git status …</c>".</summary>
    Prefix,

    /// <summary>Anchored glob: <c>*</c> matches any run of characters, <c>?</c> matches one. Other characters are literal.</summary>
    Glob,
}
