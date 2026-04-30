using System.Collections.Generic;
using System.Text;
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

        var pendingActions = new List<ActionRequestEvent>();
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
                            // Allowed by policy — execute silently. No pendingActions entry.
                            break;
                        case PolicyRouting.Queue:
                            pendingActions.Add(action);
                            break;
                    }
                    break;
                }

                case AgentDoneEvent done when pendingActions.Count == 0:
                    return new WorkflowSuccess(summary.Length > 0 ? summary.ToString() : done.Summary);

                case AgentDoneEvent done:
                    var decision = await context.ApprovalGateway
                        .RequestApprovalAsync(pendingActions, context.CancellationToken)
                        .ConfigureAwait(false);

                    if (!decision.Approved) return new WorkflowCancelled();
                    return new WorkflowSuccess(summary.Length > 0 ? summary.ToString() : done.Summary);

                case AgentErrorEvent err:
                    return new WorkflowFailure(err.Message);
            }
        }

        if (pendingActions.Count > 0)
        {
            var decision = await context.ApprovalGateway
                .RequestApprovalAsync(pendingActions, context.CancellationToken)
                .ConfigureAwait(false);

            if (!decision.Approved) return new WorkflowCancelled();
        }

        return new WorkflowSuccess(summary.Length > 0 ? summary.ToString() : null);
    }

    private static async ValueTask<PolicyRouting> EvaluateAsync(
        ActionRequestEvent action,
        WorkflowContext context)
    {
        var proposed = ActionRequestPolicyMapper.ToProposedAction(action);
        if (proposed is null)
            return PolicyRouting.Queue.Instance; // Unknown tool — default to user confirmation.

        var decision = await context.PolicyEngine
            .EvaluateAsync(proposed, context.PolicyContext, context.CancellationToken)
            .ConfigureAwait(false);

        return decision.Verdict switch
        {
            PolicyVerdict.Deny    => new PolicyRouting.Denied($"Policy denied action '{action.Type}': {decision.Reason}"),
            PolicyVerdict.Allow   => PolicyRouting.AutoApprove.Instance,
            PolicyVerdict.AskUser => PolicyRouting.Queue.Instance,
            _                     => PolicyRouting.Queue.Instance,
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

        public sealed record Queue : PolicyRouting
        {
            public static readonly Queue Instance = new();
        }
    }
}
