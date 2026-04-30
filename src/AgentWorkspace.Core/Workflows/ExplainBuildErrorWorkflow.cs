using System;
using System.Text;
using System.Threading.Tasks;
using AgentWorkspace.Abstractions.Agents;
using AgentWorkspace.Abstractions.Workflows;

namespace AgentWorkspace.Core.Workflows;

/// <summary>
/// Triggered by <see cref="BuildFailedTrigger"/>. Sends the build log to the agent and
/// streams back an explanation. No code changes are executed; this is read-only analysis.
/// </summary>
public sealed class ExplainBuildErrorWorkflow : IWorkflow
{
    public string Name => "Explain Build Error";

    public bool CanHandle(WorkflowTrigger trigger) => trigger is BuildFailedTrigger;

    public async ValueTask<WorkflowResult> ExecuteAsync(WorkflowContext context)
    {
        if (context.Trigger is not BuildFailedTrigger t)
            return new WorkflowFailure("Unexpected trigger type.");

        var prompt = BuildPrompt(t);

        await using var session = await context.AgentAdapter
            .StartSessionAsync(new AgentSessionOptions(prompt, WorkingDirectory: t.ProjectPath),
                context.CancellationToken)
            .ConfigureAwait(false);

        var summary = new StringBuilder();
        await foreach (var evt in session.Events.WithCancellation(context.CancellationToken))
        {
            switch (evt)
            {
                case AgentMessageEvent { Role: "assistant" } msg:
                    summary.Append(msg.Text);
                    break;
                case AgentDoneEvent done:
                    return new WorkflowSuccess(summary.Length > 0 ? summary.ToString() : done.Summary);
                case AgentErrorEvent err:
                    return new WorkflowFailure(err.Message);
            }
        }

        return new WorkflowSuccess(summary.Length > 0 ? summary.ToString() : null);
    }

    private static string BuildPrompt(BuildFailedTrigger t) =>
        $"""
         You are a build-error explainer. The following dotnet build failed.
         Project: {t.ProjectPath}

         Build output:
         {t.LogText}

         Summarise the root causes in plain language (no code fixes needed — explanation only).
         """;
}
