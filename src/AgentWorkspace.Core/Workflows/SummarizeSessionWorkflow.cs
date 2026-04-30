using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using AgentWorkspace.Abstractions.Agents;
using AgentWorkspace.Abstractions.Workflows;

namespace AgentWorkspace.Core.Workflows;

/// <summary>
/// Triggered via <see cref="ManualTrigger"/> with <c>WorkflowName = "Summarize Session"</c>
/// or by <see cref="SessionDetachedTrigger"/>. Reads the JSONL transcript and asks the agent
/// to produce a short summary, then appends the result to a <c>summaries.jsonl</c> file.
/// </summary>
public sealed class SummarizeSessionWorkflow : IWorkflow
{
    public string Name => "Summarize Session";

    public bool CanHandle(WorkflowTrigger trigger) => trigger switch
    {
        ManualTrigger m => m.WorkflowName == Name,
        SessionDetachedTrigger => true,
        _ => false,
    };

    public async ValueTask<WorkflowResult> ExecuteAsync(WorkflowContext context)
    {
        string? transcriptPath;
        string? sessionId;

        switch (context.Trigger)
        {
            case SessionDetachedTrigger s:
                transcriptPath = s.TranscriptPath;
                sessionId = s.SessionId;
                break;
            case ManualTrigger m:
                transcriptPath = m.Argument;
                sessionId = Path.GetFileNameWithoutExtension(transcriptPath ?? "");
                break;
            default:
                return new WorkflowFailure("Unexpected trigger type.");
        }

        if (string.IsNullOrWhiteSpace(transcriptPath) || !File.Exists(transcriptPath))
            return new WorkflowFailure($"Transcript not found: {transcriptPath}");

        var transcriptText = await File.ReadAllTextAsync(transcriptPath, context.CancellationToken)
            .ConfigureAwait(false);

        var prompt = BuildPrompt(transcriptText);

        await using var session = await context.AgentAdapter
            .StartSessionAsync(new AgentSessionOptions(prompt, SaveTranscript: false),
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
                case AgentDoneEvent:
                    goto done;
                case AgentErrorEvent err:
                    return new WorkflowFailure(err.Message);
            }
        }

        done:
        var summaryText = summary.ToString().Trim();
        if (summaryText.Length > 0)
            await AppendSummaryAsync(sessionId!, summaryText, context.CancellationToken)
                .ConfigureAwait(false);

        return new WorkflowSuccess(summaryText.Length > 0 ? summaryText : null);
    }

    private static string BuildPrompt(string transcriptText) =>
        $"""
         You are a session-summarizer. Below is a JSONL transcript of an agent session.
         Each line is a JSON object with a "type" field ("message", "action", "done", "error").

         Transcript:
         {transcriptText}

         Write a concise summary (3-5 sentences) of what happened in this session.
         Focus on what was accomplished and any outstanding items.
         """;

    private static async Task AppendSummaryAsync(
        string sessionId,
        string summaryText,
        System.Threading.CancellationToken ct)
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AgentWorkspace", "transcripts");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "summaries.jsonl");

        var line = JsonSerializer.Serialize(new
        {
            sessionId,
            summary = summaryText,
            ts = DateTimeOffset.UtcNow.ToString("O"),
        });

        await File.AppendAllTextAsync(path, line + Environment.NewLine, ct).ConfigureAwait(false);
    }
}
