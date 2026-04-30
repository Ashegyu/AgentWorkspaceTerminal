using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AgentWorkspace.Abstractions.Agents;
using AgentWorkspace.Abstractions.Policy;
using AgentWorkspace.Abstractions.Workflows;
using AgentWorkspace.Core.Policy;

namespace AgentWorkspace.Core.Workflows;

/// <summary>
/// Triggered by <see cref="TestFailedTrigger"/>. Sends the test failure log to the agent,
/// translates each <see cref="ActionRequestEvent"/> into a <see cref="ProposedAction"/>,
/// runs it through <see cref="IPolicyEngine"/>, and routes by verdict:
/// Deny → immediate <see cref="WorkflowFailure"/>; Allow → silently auto-approved;
/// AskUser (or unknown tool) → batched into <see cref="IApprovalGateway"/>.
///
/// Items whose <see cref="PolicyDecision.RequireIndividualApproval"/> is set (Critical-risk)
/// are pulled out of the batch and confirmed one at a time first; a single deny on any of
/// them short-circuits the rest of the plan as <see cref="WorkflowCancelled"/>.
/// </summary>
public sealed class FixDotnetTestsWorkflow : IWorkflow
{
    public string Name => "Fix Dotnet Tests";

    public bool CanHandle(WorkflowTrigger trigger) => trigger is TestFailedTrigger;

    public async ValueTask<WorkflowResult> ExecuteAsync(WorkflowContext context)
    {
        if (context.Trigger is not TestFailedTrigger t)
            return new WorkflowFailure("Unexpected trigger type.");

        var prompt = BuildPrompt(t);

        await using var session = await context.AgentAdapter
            .StartSessionAsync(new AgentSessionOptions(prompt, WorkingDirectory: t.ProjectPath),
                context.CancellationToken)
            .ConfigureAwait(false);

        var pending = new List<ApprovalRequestItem>();
        var summary = new StringBuilder();

        await foreach (var evt in session.Events.WithCancellation(context.CancellationToken))
        {
            switch (evt)
            {
                case AgentMessageEvent { Role: "assistant" } msg:
                    summary.Append(msg.Text);
                    break;

                case PlanProposedEvent:
                    // Plan items are informational; concrete requests arrive as ActionRequestEvent.
                    break;

                case ActionRequestEvent action:
                {
                    var routing = await EvaluateAsync(action, context).ConfigureAwait(false);
                    switch (routing)
                    {
                        case PolicyRouting.Denied denied:
                            return new WorkflowFailure(denied.Reason);
                        case PolicyRouting.AutoApprove:
                            // Allowed by policy — execute silently. No pending entry.
                            break;
                        case PolicyRouting.Queue queue:
                            pending.Add(new ApprovalRequestItem(action, queue.Decision));
                            break;
                    }
                    break;
                }

                case AgentDoneEvent done when pending.Count == 0:
                    return new WorkflowSuccess(summary.Length > 0 ? summary.ToString() : done.Summary);

                case AgentDoneEvent done:
                {
                    var cancelled = await ConfirmAsync(pending, context).ConfigureAwait(false);
                    if (cancelled is not null) return cancelled;
                    return new WorkflowSuccess(summary.Length > 0 ? summary.ToString() : done.Summary);
                }

                case AgentErrorEvent err:
                    return new WorkflowFailure(err.Message);
            }
        }

        if (pending.Count > 0)
        {
            var cancelled = await ConfirmAsync(pending, context).ConfigureAwait(false);
            if (cancelled is not null) return cancelled;
        }

        return new WorkflowSuccess(summary.Length > 0 ? summary.ToString() : null);
    }

    /// <summary>
    /// Drains <paramref name="pending"/> through the approval gateway:
    /// items requiring individual approval are confirmed one at a time first; on any
    /// individual deny the workflow short-circuits to <see cref="WorkflowCancelled"/>.
    /// Returns non-null only when the user cancels.
    /// </summary>
    private static async ValueTask<WorkflowResult?> ConfirmAsync(
        List<ApprovalRequestItem> pending,
        WorkflowContext context)
    {
        var individual = pending.Where(p => p.Decision.RequireIndividualApproval).ToList();
        var batch      = pending.Where(p => !p.Decision.RequireIndividualApproval).ToList();

        foreach (var item in individual)
        {
            var single   = new[] { item };
            var decision = await context.ApprovalGateway
                .RequestApprovalAsync(single, context.CancellationToken)
                .ConfigureAwait(false);

            if (!decision.Approved) return new WorkflowCancelled();
        }

        if (batch.Count == 0) return null;

        var batchDecision = await context.ApprovalGateway
            .RequestApprovalAsync(batch, context.CancellationToken)
            .ConfigureAwait(false);

        return batchDecision.Approved ? null : new WorkflowCancelled();
    }

    private static async ValueTask<PolicyRouting> EvaluateAsync(
        ActionRequestEvent action,
        WorkflowContext context)
    {
        var proposed = ActionRequestPolicyMapper.ToProposedAction(action);
        if (proposed is null)
        {
            // Unknown tool — default to user confirmation with synthetic decision.
            var unknown = new PolicyDecision(
                PolicyVerdict.AskUser,
                $"Unknown tool '{action.Type}' — defaulting to user approval.",
                Risk.Medium);
            return new PolicyRouting.Queue(unknown);
        }

        var decision = await context.PolicyEngine
            .EvaluateAsync(proposed, context.PolicyContext, context.CancellationToken)
            .ConfigureAwait(false);

        return decision.Verdict switch
        {
            PolicyVerdict.Deny    => new PolicyRouting.Denied($"Policy denied action '{action.Type}': {decision.Reason}"),
            PolicyVerdict.Allow   => PolicyRouting.AutoApprove.Instance,
            PolicyVerdict.AskUser => new PolicyRouting.Queue(decision),
            _                     => new PolicyRouting.Queue(decision),
        };
    }

    private static string BuildPrompt(TestFailedTrigger t) =>
        $"""
         You are a test-fix assistant. The following dotnet test run failed.
         Project: {t.ProjectPath}

         Test output:
         {t.LogText}

         Analyse the failures and propose a minimal fix plan.
         List each required code change as an action request before making any edits.
         """;

    /// <summary>3-state routing result for one ActionRequestEvent post-policy evaluation.</summary>
    private abstract record PolicyRouting
    {
        public sealed record Denied(string Reason) : PolicyRouting;

        public sealed record AutoApprove : PolicyRouting
        {
            public static readonly AutoApprove Instance = new();
        }

        /// <summary>The action needs explicit user confirmation; carries the upstream decision.</summary>
        public sealed record Queue(PolicyDecision Decision) : PolicyRouting;
    }
}
