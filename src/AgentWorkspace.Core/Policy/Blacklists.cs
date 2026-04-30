using System.Collections.Generic;
using AgentWorkspace.Abstractions.Policy;

namespace AgentWorkspace.Core.Policy;

/// <summary>
/// Pre-canned blacklist rule sets for the built-in <see cref="PolicyLevel"/>s.
/// Patterns are sourced from DESIGN.md §9.2 and CWE-78 / OWASP guidance.
/// </summary>
public static class Blacklists
{
    /// <summary>The empty rule set — every command falls through to level-based evaluation.</summary>
    public static readonly IReadOnlyList<BlacklistRule> Empty = [];

    /// <summary>
    /// 50 hard-deny rules covering: recursive deletion of system paths, disk operations,
    /// shell eval, supply-chain pipes, destructive git, credential access, registry mods,
    /// privilege escalation, system shutdown, and accidental package publish.
    /// </summary>
    public static readonly IReadOnlyList<BlacklistRule> SafeDev =
    [
        // ── Recursive delete of system / home / root (9) ────────────────────
        new(@"\brm\s+-[\w]*[rR][\w]*\s+/(\s|$|\*)",        Risk.Critical, "Recursive delete starting at filesystem root."),
        new(@"\brm\s+-[\w]*[rR][\w]*\s+~(\s|$|/)",         Risk.Critical, "Recursive delete of home directory."),
        new(@"\brm\s+-[\w]*[rR][\w]*\s+/etc\b",            Risk.Critical, "Recursive delete of /etc system config."),
        new(@"\brm\s+-[\w]*[rR][\w]*\s+/usr\b",            Risk.Critical, "Recursive delete of /usr system tree."),
        new(@"\brm\s+-[\w]*[rR][\w]*\s+/var\b",            Risk.Critical, "Recursive delete of /var system tree."),
        new(@"\brm\s+-[\w]*[rR][\w]*\s+/bin\b",            Risk.Critical, "Recursive delete of /bin system tree."),
        new(@"\brm\s+-[\w]*[rR][\w]*\s+\$HOME\b",          Risk.Critical, "Recursive delete of $HOME."),
        new(@"\bdel\s+/[sS]\s+/[qQ]\s+[A-Za-z]:\\?\s*$",   Risk.Critical, "Windows silent recursive delete of drive root."),
        new(@"\brmdir\s+/[sS]\s+/[qQ]\s+[A-Za-z]:\\?\s*$", Risk.Critical, "Windows silent recursive rmdir of drive root."),

        // ── Disk / partition / format (5) ───────────────────────────────────
        new(@"\bformat\s+[A-Za-z]:",                       Risk.Critical, "Disk format command."),
        new(@"\bmkfs(\.[a-z0-9]+)?\b",                     Risk.Critical, "Filesystem creation (destroys partition data)."),
        new(@"\bdd\b.*\bof=/dev/[hsv]d[a-z]\d?\b",         Risk.Critical, "dd writing to raw block device."),
        new(@"\bdiskpart\b",                               Risk.Critical, "Windows diskpart utility."),
        new(@"\bwipefs\b",                                 Risk.Critical, "Filesystem signature wipe."),

        // ── Shell eval / fork bomb (5) ──────────────────────────────────────
        new(@"\bInvoke-Expression\b",                      Risk.Critical, "PowerShell Invoke-Expression — arbitrary code execution."),
        new(@"\biex\s+\(",                                 Risk.Critical, "PowerShell iex alias — arbitrary code execution."),
        new(@"(?<!\w)eval\s+[\""'\$]",                     Risk.Critical, "Shell eval — arbitrary code execution."),
        new(@"\bbash\s+-c\s+.*\$\(",                       Risk.High,     "bash -c with command substitution — injection risk."),
        new(@":\(\)\s*\{\s*:\|:&\s*\}\s*;\s*:",            Risk.Critical, "Bash fork bomb."),

        // ── Curl | sh supply chain (4) ──────────────────────────────────────
        new(@"\bcurl\b[^|;]*\|\s*(ba|z|fi)?sh\b",          Risk.Critical, "curl piped to shell — supply chain risk."),
        new(@"\bwget\b[^|;]*\|\s*(ba|z|fi)?sh\b",          Risk.Critical, "wget piped to shell — supply chain risk."),
        new(@"\biwr\b[^|;]*\|\s*iex\b",                    Risk.Critical, "PowerShell IWR | IEX — supply chain risk."),
        new(@"Invoke-WebRequest\b[^|;]*\|\s*Invoke-Expression", Risk.Critical, "PowerShell IWR | IEX — supply chain risk."),

        // ── Destructive git (6) ─────────────────────────────────────────────
        new(@"\bgit\s+push\b[^&|;]*--force(?!-with-lease)\b", Risk.High,   "git push --force overwrites remote history."),
        new(@"\bgit\s+push\b[^&|;]*\s-f(\s|$)",            Risk.High,     "git push -f overwrites remote history."),
        new(@"\bgit\s+push\b[^&|;]*--force-with-lease\b",  Risk.Medium,   "git push --force-with-lease — review impact."),
        new(@"\bgit\s+reset\s+--hard\b",                   Risk.High,     "git reset --hard discards uncommitted work."),
        new(@"\bgit\s+clean\s+-[\w]*[fF][\w]*[dD][\w]*\b", Risk.High,     "git clean -fd removes untracked files."),
        new(@"\bgit\s+filter-branch\b",                    Risk.High,     "git filter-branch rewrites repository history."),

        // ── Credential / secret access (7) ──────────────────────────────────
        new(@"(^|[\s/\\])\.env(\.\w+)?(\s|$)",             Risk.High,     "Access to .env credential file."),
        new(@"\bid_rsa\b",                                 Risk.High,     "SSH RSA private key access."),
        new(@"\bid_ed25519\b",                             Risk.High,     "SSH ed25519 private key access."),
        new(@"\.pem(\s|$|""|')",                            Risk.High,     "PEM key/certificate access."),
        new(@"~?/\.aws/credentials\b",                     Risk.High,     "AWS credentials file access."),
        new(@"~?/\.ssh/",                                  Risk.High,     "SSH config / key directory access."),
        new(@"(^|[\s/\\])\.netrc\b",                       Risk.High,     ".netrc credentials file access."),

        // ── Registry (3) ────────────────────────────────────────────────────
        new(@"\breg\s+(add|delete|import)\b",              Risk.High,     "Windows registry modification."),
        new(@"\bregedit\b",                                Risk.Critical, "Windows registry editor invocation."),
        new(@"(Set|New)-ItemProperty\b[^|;]*HKLM:",        Risk.Critical, "PowerShell registry HKLM write."),

        // ── Privilege escalation (2) ────────────────────────────────────────
        new(@"\bsudo\s+rm\s+-[\w]*[rR]",                   Risk.Critical, "sudo recursive delete."),
        new(@"\bsudo\s+su\b",                              Risk.High,     "Privilege escalation to root."),

        // ── System control (5) ──────────────────────────────────────────────
        new(@"\bshutdown\s+/[srlhSRLH]\b",                 Risk.Critical, "Windows system shutdown."),
        new(@"\bshutdown\s+-[rhRH]\b",                     Risk.Critical, "Unix system shutdown."),
        new(@"\bStop-Computer\b",                          Risk.Critical, "PowerShell Stop-Computer."),
        new(@"\bkillall\s+",                               Risk.High,     "killall — broad process termination."),
        new(@"\btaskkill\s+/[fF]\b[^|;]*\*",               Risk.High,     "Windows wildcard process kill."),

        // ── Package publish (4) ─────────────────────────────────────────────
        new(@"\bnpm\s+publish\b",                          Risk.High,     "npm publish — public package release."),
        new(@"\bcargo\s+publish\b",                        Risk.High,     "cargo publish — crates.io release."),
        new(@"\bdotnet\s+nuget\s+push\b",                  Risk.High,     "dotnet nuget push — public release."),
        new(@"\btwine\s+upload\b",                         Risk.High,     "PyPI upload — public release."),
    ];
}
