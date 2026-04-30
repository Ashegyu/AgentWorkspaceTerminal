using System.Threading.Tasks;
using AgentWorkspace.Abstractions.Policy;
using AgentWorkspace.Core.Policy;

namespace AgentWorkspace.Tests.Policy;

/// <summary>
/// Maintenance slot — verifies <see cref="PolicyEngineFactory.WithUserConfig"/> appends
/// user rules in order *after* built-ins, preserving the defense-in-depth ordering
/// (blacklist still beats whitelist; built-ins still come before user additions).
/// </summary>
public sealed class PolicyEngineFactoryTests
{
    private static readonly PolicyContext SafeDevCtx =
        new(WorkspaceRoot: @"C:\workspace", Level: PolicyLevel.SafeDev,    AgentName: "test");
    private static readonly PolicyContext TrustedCtx =
        new(WorkspaceRoot: @"C:\workspace", Level: PolicyLevel.TrustedLocal, AgentName: "test");

    [Fact]
    public async Task WithUserConfig_Empty_BehavesLikeDefault()
    {
        var engine = PolicyEngineFactory.WithUserConfig(UserPolicyConfig.Empty);

        // rm -rf / still hits a built-in blacklist rule.
        var decision = await engine.EvaluateAsync(
            new ExecuteCommand("rm", new[] { "-rf", "/" }), SafeDevCtx);

        Assert.Equal(PolicyVerdict.Deny, decision.Verdict);
        Assert.Equal(Risk.Critical, decision.Risk);
    }

    [Fact]
    public async Task WithUserConfig_AppendedBlacklist_BlocksMatching()
    {
        var user = new UserPolicyConfig(1,
            Blacklist: new[]
            {
                new UserBlacklistRule(
                    Pattern: "deploy-prod",
                    Risk:    Risk.High,
                    Reason:  "Production deploy requires manual confirmation.",
                    Mode:    MatchMode.Prefix),
            },
            Whitelist: []);

        var engine = PolicyEngineFactory.WithUserConfig(user);
        var decision = await engine.EvaluateAsync(
            new ExecuteCommand("deploy-prod", new[] { "--region", "us-east-1" }), SafeDevCtx);

        Assert.Equal(PolicyVerdict.Deny, decision.Verdict);
        Assert.Equal(Risk.High,          decision.Risk);
        Assert.Contains("Production deploy", decision.Reason);
    }

    [Fact]
    public async Task WithUserConfig_AppendedWhitelist_AllowsTrustedLocalCommand()
    {
        var user = new UserPolicyConfig(1,
            Blacklist: [],
            Whitelist: new[]
            {
                new UserWhitelistRule(
                    Pattern: "kubectl get",
                    Reason:  "Read-only kubernetes lookup.",
                    Mode:    MatchMode.Prefix),
            });

        var engine = PolicyEngineFactory.WithUserConfig(user);

        // Same command under SafeDev still asks for confirmation (whitelist only applies under TrustedLocal).
        var safeDev = await engine.EvaluateAsync(
            new ExecuteCommand("kubectl", new[] { "get", "pods" }), SafeDevCtx);
        Assert.Equal(PolicyVerdict.AskUser, safeDev.Verdict);

        // Under TrustedLocal the user's whitelist auto-allows it.
        var trusted = await engine.EvaluateAsync(
            new ExecuteCommand("kubectl", new[] { "get", "pods" }), TrustedCtx);
        Assert.Equal(PolicyVerdict.Allow, trusted.Verdict);
        Assert.Contains("Read-only kubernetes lookup", trusted.Reason);
    }

    [Fact]
    public async Task BuiltinBlacklist_BeatsUserWhitelist()
    {
        // User accidentally whitelists "rm" — the built-in blacklist must still win.
        var user = new UserPolicyConfig(1,
            Blacklist: [],
            Whitelist: new[]
            {
                new UserWhitelistRule(
                    Pattern: "^rm",
                    Reason:  "I really mean it.",
                    Mode:    MatchMode.Regex),
            });

        var engine = PolicyEngineFactory.WithUserConfig(user);
        var decision = await engine.EvaluateAsync(
            new ExecuteCommand("rm", new[] { "-rf", "/" }), TrustedCtx);

        Assert.Equal(PolicyVerdict.Deny, decision.Verdict);
        Assert.Equal(Risk.Critical,      decision.Risk);
    }
}
