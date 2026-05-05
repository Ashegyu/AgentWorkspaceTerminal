using System;
using AgentWorkspace.Abstractions.Redaction;
using AgentWorkspace.Agents.Claude;
using AgentWorkspace.App.Wpf.Mesh;
using AgentWorkspace.Core.Redaction;

namespace AgentWorkspace.Tests.Mesh;

/// <summary>
/// Behavioural tests for <see cref="ExternalTaskDisplayFormatter"/>. Verify that
/// every external-Task display surface (start message, result output) goes through
/// redaction so a secret embedded in the user's prompt or the sub-agent's response
/// never surfaces verbatim on screen.
/// </summary>
public sealed class ExternalTaskDisplayFormatterTests
{
    private readonly ExternalTaskDisplayFormatter _formatter = new(new RegexRedactionEngine());

    private static TaskInvocation Inv(string prompt) =>
        new("toolu_01ABC", "general-purpose", prompt, DateTimeOffset.UtcNow);

    private static TaskResult Res(string output, bool isError = false) =>
        new("toolu_01ABC", output, isError, DateTimeOffset.UtcNow);

    // ── start-message redaction ──────────────────────────────────────────────

    [Fact]
    public void FormatStartMessage_OpenAiKey_RedactedFromPrompt()
    {
        var msg = _formatter.FormatStartMessage(Inv("please deploy with OPENAI_API_KEY=sk-foobar123 and verify"));
        Assert.Contains("OPENAI_API_KEY=[REDACTED]", msg);
        Assert.DoesNotContain("sk-foobar123", msg);
    }

    [Fact]
    public void FormatStartMessage_AnthropicKey_RedactedFromPrompt()
    {
        var msg = _formatter.FormatStartMessage(Inv("token=sk-ant-abcdefghij1234567890"));
        Assert.Contains("[REDACTED-ANTHROPIC-KEY]", msg);
        Assert.DoesNotContain("sk-ant-abc", msg);
    }

    [Fact]
    public void FormatStartMessage_GitHubToken_RedactedFromPrompt()
    {
        var msg = _formatter.FormatStartMessage(Inv("auth with ghp_abcdefghijklmnopqrstuvwxyz1234"));
        Assert.Contains("[REDACTED-GITHUB-TOKEN]", msg);
        Assert.DoesNotContain("ghp_abc", msg);
    }

    [Fact]
    public void FormatStartMessage_PreservesSubAgentTypeAndStructure()
    {
        // Header label should still surface so users can tell sub-agent kinds apart;
        // prompt body sits on the second line per the format contract.
        var msg = _formatter.FormatStartMessage(Inv("safe content"));
        Assert.StartsWith("🔗 외부 Task 시작: general-purpose\n", msg);
        Assert.EndsWith("safe content", msg);
    }

    [Fact]
    public void FormatStartMessage_EmptyPrompt_DoesNotThrow()
    {
        var msg = _formatter.FormatStartMessage(Inv(""));
        Assert.Equal("🔗 외부 Task 시작: general-purpose\n", msg);
    }

    [Fact]
    public void FormatStartMessage_NullTask_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => _formatter.FormatStartMessage(null!));
    }

    // ── result-output redaction ──────────────────────────────────────────────

    [Fact]
    public void FormatResultOutput_BearerToken_Redacted()
    {
        var output = _formatter.FormatResultOutput(Res(
            "Curl request done: Authorization: Bearer eyJhbcdefghi.payload.signature1234"));
        Assert.Contains("Bearer [REDACTED]", output);
        Assert.DoesNotContain("eyJhbcdefghi", output);
    }

    [Fact]
    public void FormatResultOutput_HomePath_Redacted()
    {
        var output = _formatter.FormatResultOutput(Res("found at C:\\Users\\alice\\secrets.json"));
        Assert.Contains(@"C:\Users\[USER]", output);
        Assert.DoesNotContain("alice", output);
    }

    [Fact]
    public void FormatResultOutput_PrivateKey_Redacted()
    {
        var output = _formatter.FormatResultOutput(Res(
            "key file contents:\n-----BEGIN RSA PRIVATE KEY-----\nMIIEpAIBAAKCAQEA...\n-----END RSA PRIVATE KEY-----"));
        Assert.Contains("[REDACTED-PRIVATE-KEY]", output);
        Assert.DoesNotContain("MIIEpAIBAAKCAQEA", output);
    }

    [Fact]
    public void FormatResultOutput_EmptyOutput_ReturnsEmpty()
    {
        Assert.Equal("", _formatter.FormatResultOutput(Res("")));
    }

    [Fact]
    public void FormatResultOutput_NoSecrets_PassesThrough()
    {
        var output = _formatter.FormatResultOutput(Res("Found 3 files: main.go, utils.go, README.md"));
        Assert.Equal("Found 3 files: main.go, utils.go, README.md", output);
    }

    [Fact]
    public void FormatResultOutput_NullResult_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => _formatter.FormatResultOutput(null!));
    }

    // ── ctor guard ───────────────────────────────────────────────────────────

    [Fact]
    public void Ctor_NullEngine_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new ExternalTaskDisplayFormatter(null!));
    }
}
