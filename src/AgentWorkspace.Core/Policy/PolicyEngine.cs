using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using AgentWorkspace.Abstractions.Policy;

namespace AgentWorkspace.Core.Policy;

/// <summary>
/// Built-in <see cref="IPolicyEngine"/>. Combines a hard-deny blacklist (regex matched against
/// <see cref="ExecuteCommand.CommandLine"/>) with a level-based fallback policy that mirrors
/// the permission profiles from DESIGN.md §9.1.
/// </summary>
public sealed class PolicyEngine : IPolicyEngine
{
    private readonly IReadOnlyList<BlacklistRule> _blacklist;

    /// <param name="blacklist">
    /// Optional hard-deny rule set evaluated against <see cref="ExecuteCommand"/>.
    /// Defaults to <see cref="Blacklists.SafeDev"/>.
    /// </param>
    public PolicyEngine(IReadOnlyList<BlacklistRule>? blacklist = null)
    {
        _blacklist = blacklist ?? Blacklists.SafeDev;
    }

    public ValueTask<PolicyDecision> EvaluateAsync(
        ProposedAction action,
        PolicyContext context,
        CancellationToken cancellationToken = default)
    {
        var decision = action switch
        {
            ExecuteCommand cmd => EvaluateExecute(cmd, context),
            WriteFile      wf  => EvaluateWriteFile(wf, context),
            DeletePath     del => EvaluateDelete(del, context),
            NetworkCall    net => EvaluateNetwork(net, context),
            InvokeMcpTool  tool => EvaluateMcp(tool, context),
            _ => new PolicyDecision(
                PolicyVerdict.Deny,
                $"Unknown action type: {action.GetType().Name}",
                Risk.High),
        };
        return ValueTask.FromResult(decision);
    }

    // ── ExecuteCommand ───────────────────────────────────────────────────────

    private PolicyDecision EvaluateExecute(ExecuteCommand cmd, PolicyContext ctx)
    {
        // Blacklist applies regardless of level — even TrustedLocal blocks `rm -rf /`.
        var line = cmd.CommandLine;
        foreach (var rule in _blacklist)
        {
            if (rule.IsMatch(line))
            {
                return new PolicyDecision(
                    PolicyVerdict.Deny,
                    rule.Reason,
                    rule.Risk,
                    RequireIndividualApproval: rule.Risk == Risk.Critical);
            }
        }

        return ctx.Level switch
        {
            PolicyLevel.ReadOnly => new PolicyDecision(
                PolicyVerdict.Deny,
                "ReadOnly profile forbids command execution.",
                Risk.Medium),
            PolicyLevel.SafeDev => new PolicyDecision(
                PolicyVerdict.AskUser,
                "SafeDev profile asks before executing commands.",
                Risk.Medium),
            PolicyLevel.TrustedLocal => new PolicyDecision(
                PolicyVerdict.AskUser,
                "TrustedLocal profile still asks before executing commands.",
                Risk.Low),
            _ => UnknownLevel(ctx),
        };
    }

    // ── WriteFile ────────────────────────────────────────────────────────────

    private static PolicyDecision EvaluateWriteFile(WriteFile wf, PolicyContext ctx)
    {
        var outsideWorkspace = IsOutsideWorkspace(wf.Path, ctx.WorkspaceRoot);

        return ctx.Level switch
        {
            PolicyLevel.ReadOnly => new PolicyDecision(
                PolicyVerdict.Deny,
                "ReadOnly profile forbids writing files.",
                Risk.High),
            PolicyLevel.SafeDev when outsideWorkspace => new PolicyDecision(
                PolicyVerdict.AskUser,
                "Write outside workspace requires confirmation.",
                Risk.High,
                RequireIndividualApproval: true),
            PolicyLevel.SafeDev => new PolicyDecision(
                PolicyVerdict.AskUser,
                "SafeDev profile asks before writing files.",
                Risk.Medium),
            PolicyLevel.TrustedLocal when outsideWorkspace => new PolicyDecision(
                PolicyVerdict.AskUser,
                "Write outside workspace still requires confirmation under TrustedLocal.",
                Risk.High,
                RequireIndividualApproval: true),
            PolicyLevel.TrustedLocal => new PolicyDecision(
                PolicyVerdict.Allow,
                "TrustedLocal profile allows writes inside workspace.",
                Risk.Low),
            _ => UnknownLevel(ctx),
        };
    }

    // ── DeletePath ───────────────────────────────────────────────────────────

    private static PolicyDecision EvaluateDelete(DeletePath del, PolicyContext ctx)
    {
        // Recursive deletes are always Critical — even TrustedLocal asks individually.
        if (del.Recursive)
        {
            return ctx.Level switch
            {
                PolicyLevel.ReadOnly => new PolicyDecision(
                    PolicyVerdict.Deny,
                    "ReadOnly profile forbids deletion.",
                    Risk.Critical,
                    RequireIndividualApproval: true),
                _ => new PolicyDecision(
                    PolicyVerdict.AskUser,
                    "Recursive delete requires individual confirmation.",
                    Risk.Critical,
                    RequireIndividualApproval: true),
            };
        }

        var outsideWorkspace = IsOutsideWorkspace(del.Path, ctx.WorkspaceRoot);

        return ctx.Level switch
        {
            PolicyLevel.ReadOnly => new PolicyDecision(
                PolicyVerdict.Deny,
                "ReadOnly profile forbids deletion.",
                Risk.High),
            PolicyLevel.SafeDev when outsideWorkspace => new PolicyDecision(
                PolicyVerdict.AskUser,
                "Delete outside workspace requires confirmation.",
                Risk.High,
                RequireIndividualApproval: true),
            PolicyLevel.SafeDev => new PolicyDecision(
                PolicyVerdict.AskUser,
                "SafeDev profile asks before deleting files.",
                Risk.Medium),
            PolicyLevel.TrustedLocal when outsideWorkspace => new PolicyDecision(
                PolicyVerdict.AskUser,
                "Delete outside workspace still requires confirmation.",
                Risk.High,
                RequireIndividualApproval: true),
            PolicyLevel.TrustedLocal => new PolicyDecision(
                PolicyVerdict.AskUser,
                "TrustedLocal profile asks before deleting files.",
                Risk.Low),
            _ => UnknownLevel(ctx),
        };
    }

    // ── NetworkCall ──────────────────────────────────────────────────────────

    private static PolicyDecision EvaluateNetwork(NetworkCall net, PolicyContext ctx) =>
        ctx.Level switch
        {
            PolicyLevel.ReadOnly => new PolicyDecision(
                PolicyVerdict.Deny,
                "ReadOnly profile forbids network access.",
                Risk.High),
            PolicyLevel.SafeDev => new PolicyDecision(
                PolicyVerdict.AskUser,
                $"SafeDev profile asks before network call to {net.Url.Host}.",
                Risk.High),
            PolicyLevel.TrustedLocal => new PolicyDecision(
                PolicyVerdict.Allow,
                "TrustedLocal profile allows network calls.",
                Risk.Low),
            _ => UnknownLevel(ctx),
        };

    // ── InvokeMcpTool ────────────────────────────────────────────────────────

    private static PolicyDecision EvaluateMcp(InvokeMcpTool tool, PolicyContext ctx) =>
        ctx.Level switch
        {
            PolicyLevel.ReadOnly => new PolicyDecision(
                PolicyVerdict.Deny,
                "ReadOnly profile forbids MCP tool invocation.",
                Risk.Medium),
            _ => new PolicyDecision(
                PolicyVerdict.AskUser,
                $"MCP tool '{tool.ToolName}' on '{tool.ServerId}' requires confirmation.",
                Risk.Medium),
        };

    // ── helpers ──────────────────────────────────────────────────────────────

    private static PolicyDecision UnknownLevel(PolicyContext ctx) =>
        new(PolicyVerdict.Deny, $"Unknown policy level: {ctx.Level}", Risk.High);

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="path"/> falls outside
    /// <paramref name="workspaceRoot"/>. A null/empty workspace root is treated as "no constraint",
    /// so paths are considered inside.
    /// </summary>
    private static bool IsOutsideWorkspace(string path, string? workspaceRoot)
    {
        if (string.IsNullOrEmpty(workspaceRoot)) return false;

        try
        {
            var fullPath = Path.GetFullPath(path);
            var fullRoot = Path.GetFullPath(workspaceRoot);
            return !fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException)
        {
            // Malformed path — treat as outside to err on the safe side.
            return true;
        }
    }
}
