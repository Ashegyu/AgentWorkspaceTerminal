using System;
using AgentWorkspace.Abstractions.Redaction;
using AgentWorkspace.Agents.Claude;

namespace AgentWorkspace.App.Wpf.Mesh;

/// <summary>
/// Builds the redacted display strings shown in external Task cards.
/// <para>
/// External cards source their content from the user's interactive Claude CLI via
/// <see cref="ClaudeTranscriptWatcher"/>. The raw <see cref="TaskInvocation.Prompt"/>
/// and <see cref="TaskResult.Output"/> may include secrets (API keys, tokens, paths,
/// JWTs) inlined by the user's prompt or the sub-agent's response. This formatter is
/// the single point where those raw strings are scrubbed before reaching any display
/// surface — card body, merged summary, status bar.
/// </para>
/// <para>
/// Clipboard handoff (auto-pane prompt re-input) deliberately bypasses this formatter:
/// redacted prompts aren't actionable when re-played into a new Claude pane, and the
/// user is the original author of that text.
/// </para>
/// </summary>
public sealed class ExternalTaskDisplayFormatter
{
    private readonly IRedactionEngine _redaction;

    public ExternalTaskDisplayFormatter(IRedactionEngine redaction)
    {
        _redaction = redaction ?? throw new ArgumentNullException(nameof(redaction));
    }

    /// <summary>
    /// Builds the "in-progress" message shown in the card body when an external Task
    /// is first observed. Includes the sub-agent type label and the redacted prompt.
    /// </summary>
    public string FormatStartMessage(TaskInvocation task)
    {
        ArgumentNullException.ThrowIfNull(task);
        var displayPrompt = _redaction.Redact(task.Prompt ?? string.Empty);
        return $"🔗 외부 Task 시작: {task.SubAgentType}\n{displayPrompt}";
    }

    /// <summary>
    /// Returns the redacted version of the Task's tool_result text, used for both the
    /// final <c>Trace.Append</c> message and the collapsed-card MergedSummary.
    /// </summary>
    public string FormatResultOutput(TaskResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return _redaction.Redact(result.Output ?? string.Empty);
    }
}
