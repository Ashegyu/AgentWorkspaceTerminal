using System.Collections.Generic;
using System.Text.RegularExpressions;
using AgentWorkspace.Abstractions.Redaction;

namespace AgentWorkspace.Core.Redaction;

/// <summary>
/// Applies an ordered list of <see cref="RedactionRule"/> substitutions to free-form text.
/// Default rule set covers the categories listed in DESIGN.md §9.3:
/// API tokens (OPENAI/ANTHROPIC/GITHUB/AWS/AZURE), SSH private keys, .env entries, absolute
/// home/user paths, Bearer tokens, JWTs, and connection strings.
/// </summary>
public sealed class RegexRedactionEngine : IRedactionEngine
{
    /// <summary>Default rule set — 14 patterns covering DESIGN.md §9.3.</summary>
    public static readonly IReadOnlyList<RedactionRule> DefaultRules =
    [
        // ── API tokens (env-style assignments) ──────────────────────────────
        new(@"OPENAI_API_KEY\s*=\s*[^\s""']+",         "OPENAI_API_KEY=[REDACTED]"),
        new(@"ANTHROPIC_API_KEY\s*=\s*[^\s""']+",      "ANTHROPIC_API_KEY=[REDACTED]"),
        new(@"GITHUB_TOKEN\s*=\s*[^\s""']+",           "GITHUB_TOKEN=[REDACTED]"),
        new(@"AZURE_[A-Z_]+\s*=\s*[^\s""']+",          "AZURE_*=[REDACTED]"),
        new(@"AWS_(SECRET_ACCESS_KEY|ACCESS_KEY_ID|SESSION_TOKEN)\s*=\s*[^\s""']+",
                                                       "AWS_*=[REDACTED]"),

        // ── Token-shaped secrets (literal patterns) ─────────────────────────
        new(@"sk-[A-Za-z0-9]{20,}",                    "[REDACTED-OPENAI-KEY]"),
        new(@"sk-ant-[A-Za-z0-9_\-]{20,}",             "[REDACTED-ANTHROPIC-KEY]"),
        new(@"gh[pousr]_[A-Za-z0-9]{30,}",             "[REDACTED-GITHUB-TOKEN]"),
        new(@"AKIA[0-9A-Z]{16}",                       "[REDACTED-AWS-AKID]"),

        // ── Authorization headers / JWTs ────────────────────────────────────
        new(@"Bearer\s+[A-Za-z0-9._\-~+/=]{16,}",      "Bearer [REDACTED]",  RegexOptions.IgnoreCase),
        new(@"\beyJ[A-Za-z0-9_\-]{8,}\.[A-Za-z0-9_\-]{8,}\.[A-Za-z0-9_\-]{8,}\b",
                                                       "[REDACTED-JWT]"),

        // ── SSH private key blocks ──────────────────────────────────────────
        new(@"-----BEGIN [A-Z ]*PRIVATE KEY-----[\s\S]*?-----END [A-Z ]*PRIVATE KEY-----",
                                                       "[REDACTED-PRIVATE-KEY]"),

        // ── Absolute home / user paths ──────────────────────────────────────
        new(@"[A-Za-z]:\\Users\\[^\\\s""']+",          @"C:\Users\[USER]"),
        new(@"/home/[^/\s""']+",                       "/home/[USER]"),
    ];

    private readonly IReadOnlyList<RedactionRule> _rules;

    public RegexRedactionEngine(IReadOnlyList<RedactionRule>? rules = null)
        => _rules = rules ?? DefaultRules;

    public string Redact(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;

        var current = text;
        foreach (var rule in _rules)
            current = rule.Apply(current);
        return current;
    }
}
