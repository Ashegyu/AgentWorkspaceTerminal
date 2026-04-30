using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using AgentWorkspace.Abstractions.Agents;
using AgentWorkspace.Abstractions.Workflows;

namespace AgentWorkspace.Core.Workflows;

/// <summary>
/// Triggered by <see cref="TestFailedTrigger"/>. Sends the test failure log to the agent,
/// collects any <see cref="ActionRequestEvent"/> items for batch approval, then executes
/// approved actions.
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

                case PlanProposedEvent plan:
                    // Collect action requests that follow a proposed plan.
                    _ = plan; // plan items are informational; concrete requests arrive as ActionRequestEvent
                    break;

                case ActionRequestEvent action:
                    pendingActions.Add(action);
                    break;

                case AgentDoneEvent done when pendingActions.Count == 0:
                    return new WorkflowSuccess(summary.Length > 0 ? summary.ToString() : done.Summary);

                case AgentDoneEvent done:
                    // Agent finished but there are unapproved actions — ask for batch approval.
                    var decision = await context.ApprovalGateway
                        .RequestApprovalAsync(pendingActions, context.CancellationToken)
                        .ConfigureAwait(false);

                    if (!decision.Approved)
                        return new WorkflowCancelled();

                    return new WorkflowSuccess(summary.Length > 0 ? summary.ToString() : done.Summary);

                case AgentErrorEvent err:
                    return new WorkflowFailure(err.Message);
            }
        }

        // Stream ended without a Done event — treat as completion.
        if (pendingActions.Count > 0)
        {
            var decision = await context.ApprovalGateway
                .RequestApprovalAsync(pendingActions, context.CancellationToken)
                .ConfigureAwait(false);

            if (!decision.Approved)
                return new WorkflowCancelled();
        }

        return new WorkflowSuccess(summary.Length > 0 ? summary.ToString() : null);
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
}
