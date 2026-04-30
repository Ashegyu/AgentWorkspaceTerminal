using AgentWorkspace.Abstractions.Policy;
using AgentWorkspace.Core.Policy;

namespace AgentWorkspace.Tests.Policy;

/// <summary>
/// Polish 6 — verifies <see cref="MatchMode"/> behaviour for both
/// <see cref="WhitelistRule"/> and <see cref="BlacklistRule"/>: regex (default),
/// prefix, and glob.
/// </summary>
public sealed class PatternMatcherTests
{
    // ── Regex (default) ──────────────────────────────────────────────────────

    [Fact]
    public void Whitelist_RegexMode_DefaultPreservesExistingBehavior()
    {
        var rule = new WhitelistRule(@"^git status\b", "git status");
        Assert.Equal(MatchMode.Regex, rule.Mode);
        Assert.True(rule.IsMatch("git status"));
        Assert.True(rule.IsMatch("git status -sb"));
        Assert.False(rule.IsMatch("git statussssss"));   // \b boundary respected
        Assert.False(rule.IsMatch("not git status"));
    }

    [Fact]
    public void Blacklist_RegexMode_DefaultPreservesExistingBehavior()
    {
        var rule = new BlacklistRule(@"^rm\s+-rf\s+/", Risk.Critical, "wipe");
        Assert.Equal(MatchMode.Regex, rule.Mode);
        Assert.True(rule.IsMatch("rm -rf /"));
        Assert.False(rule.IsMatch("rm /tmp/file"));
    }

    // ── Prefix ───────────────────────────────────────────────────────────────

    [Fact]
    public void Whitelist_PrefixMode_MatchesAnyTrailingArgs()
    {
        var rule = new WhitelistRule("git status", "all status variants", MatchMode.Prefix);
        Assert.True(rule.IsMatch("git status"));
        Assert.True(rule.IsMatch("git status -sb"));
        Assert.True(rule.IsMatch("git status --porcelain"));
        Assert.False(rule.IsMatch("not-git status"));
    }

    [Fact]
    public void Whitelist_PrefixMode_IsCaseInsensitive()
    {
        var rule = new WhitelistRule("GIT STATUS", "case-insensitive", MatchMode.Prefix);
        Assert.True(rule.IsMatch("git status"));
        Assert.True(rule.IsMatch("GIT Status"));
    }

    [Fact]
    public void Blacklist_PrefixMode_Works()
    {
        var rule = new BlacklistRule("sudo ", Risk.High, "no sudo", MatchMode.Prefix);
        Assert.True(rule.IsMatch("sudo apt update"));
        Assert.False(rule.IsMatch("not sudo"));
    }

    // ── Glob ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Whitelist_GlobMode_StarMatchesAnyRun()
    {
        var rule = new WhitelistRule("git *", "any git", MatchMode.Glob);
        Assert.True(rule.IsMatch("git status"));
        Assert.True(rule.IsMatch("git log --oneline"));
        Assert.False(rule.IsMatch("git"));            // trailing space + content required
    }

    [Fact]
    public void Whitelist_GlobMode_QuestionMatchesOneChar()
    {
        var rule = new WhitelistRule("ls -?", "ls with single flag", MatchMode.Glob);
        Assert.True(rule.IsMatch("ls -a"));
        Assert.True(rule.IsMatch("ls -l"));
        Assert.False(rule.IsMatch("ls -la"));         // two chars after dash → no
        Assert.False(rule.IsMatch("ls"));
    }

    [Fact]
    public void Glob_AnchoredEnd_NoTrailingGarbage()
    {
        // Glob is anchored — no trailing characters allowed unless pattern ends with *.
        var rule = new WhitelistRule("git status", "exact", MatchMode.Glob);
        Assert.True(rule.IsMatch("git status"));
        Assert.False(rule.IsMatch("git status -sb"));
    }

    [Fact]
    public void Glob_EscapesRegexMetaChars()
    {
        // Dots and parens in the pattern must be treated as literals, not regex meta.
        var rule = new WhitelistRule("dotnet --info.txt", "literal", MatchMode.Glob);
        Assert.True(rule.IsMatch("dotnet --info.txt"));
        Assert.False(rule.IsMatch("dotnet --infoXtxt"));
    }

    [Fact]
    public void Blacklist_GlobMode_Works()
    {
        var rule = new BlacklistRule("rm -rf *", Risk.Critical, "wipe glob", MatchMode.Glob);
        Assert.True(rule.IsMatch("rm -rf /"));
        Assert.True(rule.IsMatch("rm -rf /home/user"));
        Assert.False(rule.IsMatch("rm /tmp"));
    }

    // ── Defense-in-depth check: blacklist still beats whitelist regardless of mode ──

    [Fact]
    public void Whitelist_PrefixMode_StillBeatenByBlacklist_InEngine()
    {
        // Sanity: engine evaluation order is independent of mode — blacklist > whitelist.
        // Here we just verify that prefix-matched whitelist doesn't auto-allow on its own.
        var allow = new WhitelistRule("git ", "any git via prefix", MatchMode.Prefix);
        var deny  = new BlacklistRule(@"git\s+push\s+--force", Risk.High, "no force-push");

        const string cmd = "git push --force origin main";
        Assert.True(allow.IsMatch(cmd));
        Assert.True(deny.IsMatch(cmd));
        // Real PolicyEngine ordering is asserted in PolicyEngineTests; this is the rule-level guard.
    }
}
