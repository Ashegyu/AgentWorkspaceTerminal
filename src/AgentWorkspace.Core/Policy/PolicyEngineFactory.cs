using System.Collections.Generic;

namespace AgentWorkspace.Core.Policy;

/// <summary>
/// Builds a <see cref="PolicyEngine"/> from the built-in defaults plus an optional
/// <see cref="UserPolicyConfig"/>. User rules are appended *after* the built-ins so
/// the defense-in-depth order (blacklist → whitelist → level) is preserved and a
/// user-supplied whitelist cannot shadow a built-in blacklist hit.
/// </summary>
public static class PolicyEngineFactory
{
    /// <summary>Built-ins only — equivalent to <c>new PolicyEngine()</c>.</summary>
    public static PolicyEngine Default() => new();

    /// <summary>Built-ins + user-supplied rules. Order is built-ins first, user rules second.</summary>
    public static PolicyEngine WithUserConfig(UserPolicyConfig user)
    {
        if (user.IsEmpty) return Default();

        var blacklist = new List<BlacklistRule>(Blacklists.SafeDev.Count + user.Blacklist.Count);
        blacklist.AddRange(Blacklists.SafeDev);
        foreach (var r in user.Blacklist)
        {
            blacklist.Add(new BlacklistRule(r.Pattern, r.Risk, r.Reason, r.Mode));
        }

        var whitelist = new List<WhitelistRule>(Whitelists.TrustedLocal.Count + user.Whitelist.Count);
        whitelist.AddRange(Whitelists.TrustedLocal);
        foreach (var r in user.Whitelist)
        {
            whitelist.Add(new WhitelistRule(r.Pattern, r.Reason, r.Mode));
        }

        return new PolicyEngine(blacklist, whitelist);
    }
}
