using System;
using System.Collections.Generic;
using System.IO;
using AgentWorkspace.Abstractions.Policy;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace AgentWorkspace.Core.Policy;

/// <summary>
/// Loads <see cref="UserPolicyConfig"/> from a YAML file matching the schema:
/// <code>
/// version: 1
/// blacklist:
///   - pattern: "^rm -rf /"
///     mode: regex          # regex (default) | prefix | glob
///     risk: critical       # low | medium | high | critical
///     reason: "Recursive root delete."
/// whitelist:
///   - pattern: "git status"
///     mode: prefix
///     reason: "Read-only status check."
/// </code>
/// Validation is strict on the structural shape (unknown root keys throw) but lenient
/// on absent sections (missing blacklist/whitelist becomes empty list).
/// </summary>
public sealed class UserPolicyConfigLoader
{
    private const int SupportedVersion = 1;

    private static readonly IDeserializer _yaml = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    /// <summary>Default user policy file path: <c>%USERPROFILE%\.agentworkspace\policies.yaml</c>.</summary>
    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".agentworkspace", "policies.yaml");

    /// <summary>
    /// Loads <see cref="UserPolicyConfig"/> from <paramref name="path"/>.
    /// Returns <see cref="UserPolicyConfig.Empty"/> when the file does not exist.
    /// Throws <see cref="UserPolicyConfigException"/> on parse / validation failure
    /// (caller decides whether to surface the error to the user or proceed empty).
    /// </summary>
    public UserPolicyConfig LoadOrEmpty(string? path = null)
    {
        path ??= DefaultPath;
        if (!File.Exists(path))
        {
            return UserPolicyConfig.Empty;
        }

        string yaml;
        try
        {
            yaml = File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new UserPolicyConfigException($"Cannot read user policy file '{path}': {ex.Message}");
        }

        return ParseAndValidate(yaml, path);
    }

    /// <summary>Parses YAML directly without disk IO. Test/CLI-friendly entry point.</summary>
    public static UserPolicyConfig ParseAndValidate(string yaml, string sourceName = "<string>")
    {
        UserPolicyConfigDto dto;
        try
        {
            dto = _yaml.Deserialize<UserPolicyConfigDto>(yaml)
                ?? throw new UserPolicyConfigException($"'{sourceName}' is empty.");
        }
        catch (UserPolicyConfigException) { throw; }
        catch (Exception ex)
        {
            throw new UserPolicyConfigException($"YAML parse error in '{sourceName}': {ex.Message}");
        }

        if (dto.Version != SupportedVersion)
        {
            throw new UserPolicyConfigException(
                $"'{sourceName}': unsupported version {dto.Version}. This loader understands version {SupportedVersion}.");
        }

        var blacklist = new List<UserBlacklistRule>(dto.Blacklist?.Count ?? 0);
        if (dto.Blacklist is { Count: > 0 })
        {
            for (int i = 0; i < dto.Blacklist.Count; i++)
            {
                blacklist.Add(MapBlacklistRule(dto.Blacklist[i], sourceName, i));
            }
        }

        var whitelist = new List<UserWhitelistRule>(dto.Whitelist?.Count ?? 0);
        if (dto.Whitelist is { Count: > 0 })
        {
            for (int i = 0; i < dto.Whitelist.Count; i++)
            {
                whitelist.Add(MapWhitelistRule(dto.Whitelist[i], sourceName, i));
            }
        }

        return new UserPolicyConfig(SupportedVersion, blacklist, whitelist);
    }

    private static UserBlacklistRule MapBlacklistRule(UserRuleDto dto, string src, int idx)
    {
        if (string.IsNullOrWhiteSpace(dto.Pattern))
        {
            throw new UserPolicyConfigException($"'{src}': blacklist[{idx}].pattern is required.");
        }
        if (string.IsNullOrWhiteSpace(dto.Reason))
        {
            throw new UserPolicyConfigException($"'{src}': blacklist[{idx}].reason is required.");
        }
        return new UserBlacklistRule(
            Pattern: dto.Pattern.Trim(),
            Risk:    ParseRisk(dto.Risk, src, $"blacklist[{idx}]"),
            Reason:  dto.Reason.Trim(),
            Mode:    ParseMode(dto.Mode, src, $"blacklist[{idx}]"));
    }

    private static UserWhitelistRule MapWhitelistRule(UserRuleDto dto, string src, int idx)
    {
        if (string.IsNullOrWhiteSpace(dto.Pattern))
        {
            throw new UserPolicyConfigException($"'{src}': whitelist[{idx}].pattern is required.");
        }
        if (string.IsNullOrWhiteSpace(dto.Reason))
        {
            throw new UserPolicyConfigException($"'{src}': whitelist[{idx}].reason is required.");
        }
        return new UserWhitelistRule(
            Pattern: dto.Pattern.Trim(),
            Reason:  dto.Reason.Trim(),
            Mode:    ParseMode(dto.Mode, src, $"whitelist[{idx}]"));
    }

    private static MatchMode ParseMode(string? raw, string src, string ctx)
    {
        if (string.IsNullOrWhiteSpace(raw)) return MatchMode.Regex;
        return raw.Trim().ToLowerInvariant() switch
        {
            "regex"  => MatchMode.Regex,
            "prefix" => MatchMode.Prefix,
            "glob"   => MatchMode.Glob,
            _ => throw new UserPolicyConfigException(
                $"'{src}': {ctx}.mode '{raw}' is not one of regex|prefix|glob."),
        };
    }

    private static Risk ParseRisk(string? raw, string src, string ctx)
    {
        // Default to High when omitted — safer than Low for an unspecified user rule.
        if (string.IsNullOrWhiteSpace(raw)) return Risk.High;
        return raw.Trim().ToLowerInvariant() switch
        {
            "low"      => Risk.Low,
            "medium"   => Risk.Medium,
            "high"     => Risk.High,
            "critical" => Risk.Critical,
            _ => throw new UserPolicyConfigException(
                $"'{src}': {ctx}.risk '{raw}' is not one of low|medium|high|critical."),
        };
    }

    // ── private YAML DTOs ────────────────────────────────────────────────────

    private sealed class UserPolicyConfigDto
    {
        public int                  Version   { get; set; } = 1;
        public List<UserRuleDto>?   Blacklist { get; set; }
        public List<UserRuleDto>?   Whitelist { get; set; }
    }

    private sealed class UserRuleDto
    {
        public string? Pattern { get; set; }
        public string? Mode    { get; set; }
        public string? Risk    { get; set; }
        public string? Reason  { get; set; }
    }
}

/// <summary>Surface error type for user-policy YAML parse / validation problems.</summary>
public sealed class UserPolicyConfigException : Exception
{
    public UserPolicyConfigException(string message) : base(message) { }
}
