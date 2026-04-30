using System.Text.Json;
using AgentWorkspace.Abstractions.Agents;
using AgentWorkspace.Core.Redaction;
using AgentWorkspace.Core.Transcripts;

namespace AgentWorkspace.Tests.Transcripts;

/// <summary>
/// Verifies that <see cref="TranscriptSink"/> redacts free-form text fields before
/// they hit the JSONL stream. Targets the internal <c>Serialize</c> entry point so the
/// tests don't touch the real <c>%LOCALAPPDATA%</c> path.
/// </summary>
public sealed class TranscriptSinkRedactionTests
{
    private static readonly RegexRedactionEngine Redaction = new();

    private static JsonElement Js(string raw) => JsonDocument.Parse(raw).RootElement.Clone();

    [Fact]
    public void MessageEvent_TextRedacted()
    {
        var line = TranscriptSink.Serialize(
            new AgentMessageEvent("assistant", "OPENAI_API_KEY=sk-foobar123 used"),
            Redaction);

        var doc = JsonDocument.Parse(line);
        var text = doc.RootElement.GetProperty("text").GetString();
        Assert.NotNull(text);
        Assert.Contains("OPENAI_API_KEY=[REDACTED]", text!);
        Assert.DoesNotContain("sk-foobar123", text!);
    }

    [Fact]
    public void ActionRequestEvent_DescriptionRedacted()
    {
        var line = TranscriptSink.Serialize(
            new ActionRequestEvent("a1", "bash", @"run on /home/alice/x"),
            Redaction);

        var doc = JsonDocument.Parse(line);
        var desc = doc.RootElement.GetProperty("description").GetString();
        Assert.NotNull(desc);
        Assert.Contains("/home/[USER]", desc!);
        Assert.DoesNotContain("alice", desc!);
    }

    [Fact]
    public void DoneEvent_SummaryRedacted()
    {
        var line = TranscriptSink.Serialize(
            new AgentDoneEvent(0, "GITHUB_TOKEN=ghp_abcdefghij ok"),
            Redaction);

        var doc = JsonDocument.Parse(line);
        var summary = doc.RootElement.GetProperty("summary").GetString();
        Assert.NotNull(summary);
        Assert.Contains("GITHUB_TOKEN=[REDACTED]", summary!);
    }

    [Fact]
    public void DoneEvent_NullSummary_StaysNull()
    {
        var line = TranscriptSink.Serialize(new AgentDoneEvent(0, null), Redaction);
        var doc = JsonDocument.Parse(line);
        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("summary").ValueKind);
    }

    [Fact]
    public void ErrorEvent_MessageRedacted()
    {
        var line = TranscriptSink.Serialize(
            new AgentErrorEvent(@"crash at C:\Users\jgkim\bin\app.exe"),
            Redaction);

        var doc = JsonDocument.Parse(line);
        var msg = doc.RootElement.GetProperty("message").GetString();
        Assert.NotNull(msg);
        Assert.Contains(@"C:\Users\[USER]", msg!);
        Assert.DoesNotContain("jgkim", msg!);
    }

    [Fact]
    public void StructuralFields_NotRedacted()
    {
        // ActionId / Type are not free-form text and must pass through unchanged.
        var line = TranscriptSink.Serialize(
            new ActionRequestEvent("id-with-AKIAIOSFODNN7EXAMPLE", "Bash", "ls"),
            Redaction);

        var doc = JsonDocument.Parse(line);
        Assert.Equal("id-with-AKIAIOSFODNN7EXAMPLE", doc.RootElement.GetProperty("id").GetString());
        Assert.Equal("Bash",                          doc.RootElement.GetProperty("actionType").GetString());
    }
}
