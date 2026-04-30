using AgentWorkspace.Abstractions.Policy;
using AgentWorkspace.Core.Policy;

namespace AgentWorkspace.Tests.Policy;

/// <summary>
/// Maintenance slot — yaml-driven user policy add-ons. Verifies the loader's
/// shape contract (versioning, mode/risk parsing, missing-section tolerance,
/// strict required fields) without touching disk.
/// </summary>
public sealed class UserPolicyConfigLoaderTests
{
    [Fact]
    public void EmptySections_ProducesEmptyConfig()
    {
        const string yaml = "version: 1\n";
        var cfg = UserPolicyConfigLoader.ParseAndValidate(yaml);

        Assert.Equal(1, cfg.Version);
        Assert.Empty(cfg.Blacklist);
        Assert.Empty(cfg.Whitelist);
        Assert.True(cfg.IsEmpty);
    }

    [Fact]
    public void Version_Mismatch_Throws()
    {
        const string yaml = "version: 99\n";
        var ex = Assert.Throws<UserPolicyConfigException>(
            () => UserPolicyConfigLoader.ParseAndValidate(yaml));
        Assert.Contains("unsupported version 99", ex.Message);
    }

    [Fact]
    public void BlacklistEntry_AllFields_Parsed()
    {
        const string yaml = """
            version: 1
            blacklist:
              - pattern: "^rm -rf /"
                mode: regex
                risk: critical
                reason: "Recursive root delete."
            """;
        var cfg = UserPolicyConfigLoader.ParseAndValidate(yaml);

        var rule = Assert.Single(cfg.Blacklist);
        Assert.Equal("^rm -rf /",            rule.Pattern);
        Assert.Equal(MatchMode.Regex,        rule.Mode);
        Assert.Equal(Risk.Critical,          rule.Risk);
        Assert.Equal("Recursive root delete.", rule.Reason);
    }

    [Fact]
    public void WhitelistEntry_PrefixMode_Parsed()
    {
        const string yaml = """
            version: 1
            whitelist:
              - pattern: "git status"
                mode: prefix
                reason: "Read-only status check."
            """;
        var cfg = UserPolicyConfigLoader.ParseAndValidate(yaml);

        var rule = Assert.Single(cfg.Whitelist);
        Assert.Equal("git status",            rule.Pattern);
        Assert.Equal(MatchMode.Prefix,        rule.Mode);
        Assert.Equal("Read-only status check.", rule.Reason);
    }

    [Fact]
    public void MissingMode_DefaultsToRegex_AndMissingRisk_DefaultsToHigh()
    {
        const string yaml = """
            version: 1
            blacklist:
              - pattern: "secret"
                reason: "Block secret leaks."
            """;
        var cfg  = UserPolicyConfigLoader.ParseAndValidate(yaml);
        var rule = Assert.Single(cfg.Blacklist);

        Assert.Equal(MatchMode.Regex, rule.Mode);
        Assert.Equal(Risk.High,       rule.Risk);  // safer default per loader contract
    }

    [Fact]
    public void MissingPattern_Throws()
    {
        const string yaml = """
            version: 1
            blacklist:
              - reason: "missing pattern"
            """;
        var ex = Assert.Throws<UserPolicyConfigException>(
            () => UserPolicyConfigLoader.ParseAndValidate(yaml));
        Assert.Contains("blacklist[0].pattern is required", ex.Message);
    }

    [Fact]
    public void MissingReason_Throws()
    {
        const string yaml = """
            version: 1
            whitelist:
              - pattern: "git log"
                mode: prefix
            """;
        var ex = Assert.Throws<UserPolicyConfigException>(
            () => UserPolicyConfigLoader.ParseAndValidate(yaml));
        Assert.Contains("whitelist[0].reason is required", ex.Message);
    }

    [Fact]
    public void UnknownMode_Throws()
    {
        const string yaml = """
            version: 1
            blacklist:
              - pattern: "rm"
                mode: literal
                reason: "x"
            """;
        var ex = Assert.Throws<UserPolicyConfigException>(
            () => UserPolicyConfigLoader.ParseAndValidate(yaml));
        Assert.Contains("blacklist[0].mode 'literal'", ex.Message);
    }

    [Fact]
    public void UnknownRisk_Throws()
    {
        const string yaml = """
            version: 1
            blacklist:
              - pattern: "rm"
                risk: extreme
                reason: "x"
            """;
        var ex = Assert.Throws<UserPolicyConfigException>(
            () => UserPolicyConfigLoader.ParseAndValidate(yaml));
        Assert.Contains("blacklist[0].risk 'extreme'", ex.Message);
    }

    [Fact]
    public void GarbageYaml_WrappedInPolicyException()
    {
        const string yaml = "this is not yaml: : :\n  - bad";
        Assert.Throws<UserPolicyConfigException>(
            () => UserPolicyConfigLoader.ParseAndValidate(yaml));
    }
}
