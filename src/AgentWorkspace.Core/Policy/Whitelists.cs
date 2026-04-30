using System.Collections.Generic;

namespace AgentWorkspace.Core.Policy;

/// <summary>
/// Pre-canned whitelist rule sets used by <see cref="PolicyEngine"/> under
/// <c>PolicyLevel.TrustedLocal</c>. Whitelisted commands skip the approval prompt,
/// enabling smoother developer flow on a trusted local machine.
/// </summary>
public static class Whitelists
{
    /// <summary>The empty rule set — no commands are auto-allowed.</summary>
    public static readonly IReadOnlyList<WhitelistRule> Empty = [];

    /// <summary>
    /// Read-only / inspection commands that are safe to auto-allow on a trusted local machine.
    /// Build / test commands are NOT included — they may run user-supplied code.
    /// </summary>
    public static readonly IReadOnlyList<WhitelistRule> TrustedLocal =
    [
        // Filesystem inspection
        new(@"^(ls|dir|tree|pwd|cd)\b",                   "Filesystem inspection."),
        new(@"^cat\b",                                    "Read file content."),
        new(@"^(head|tail|less|more)\b",                  "Read file content (paged)."),
        new(@"^(file|stat|du|df)\b",                      "File metadata inspection."),

        // Git inspection (read-only)
        new(@"^git\s+(status|log|diff|show|branch|remote|describe)\b", "Git read-only inspection."),
        new(@"^git\s+ls-(files|tree|remote)\b",           "Git ls-* inspection."),
        new(@"^git\s+rev-parse\b",                        "Git rev-parse inspection."),

        // dotnet inspection
        new(@"^dotnet\s+--(version|info|list-sdks|list-runtimes)\b", "dotnet version / SDK inspection."),
        new(@"^dotnet\s+sln\s+list\b",                    "dotnet solution listing."),

        // Process / environment inspection
        new(@"^(ps|tasklist|whoami|hostname|uname|date|env|printenv)\b", "Process / environment inspection."),
        new(@"^echo\b",                                   "Echo literal."),
        new(@"^which\b",                                  "Locate executable."),
        new(@"^where\b",                                  "Windows locate executable."),
    ];
}
