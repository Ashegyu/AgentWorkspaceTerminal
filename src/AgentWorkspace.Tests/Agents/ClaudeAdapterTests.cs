using AgentWorkspace.Abstractions.Agents;
using AgentWorkspace.Agents.Claude;
using AgentWorkspace.Agents.Claude.Wire;

namespace AgentWorkspace.Tests.Agents;

public sealed class ClaudeAdapterTests
{
    // ── ClaudeAdapter metadata ───────────────────────────────────────────────

    [Fact]
    public void Name_ReturnsClaude()
    {
        var adapter = new ClaudeAdapter();
        Assert.Equal("Claude Code", adapter.Name);
    }

    [Fact]
    public void Capabilities_StructuredOutput_True()
    {
        var adapter = new ClaudeAdapter();
        Assert.True(adapter.Capabilities.StructuredOutput);
    }

    [Fact]
    public void Capabilities_SupportsPlanProposal_False()
    {
        var adapter = new ClaudeAdapter();
        Assert.False(adapter.Capabilities.SupportsPlanProposal);
    }

    [Fact]
    public void Capabilities_SupportsCancel_True()
    {
        var adapter = new ClaudeAdapter();
        Assert.True(adapter.Capabilities.SupportsCancel);
    }

    // ── StreamJsonParser ─────────────────────────────────────────────────────

    [Fact]
    public void Parse_InvalidJson_ReturnsNull()
    {
        var result = StreamJsonParser.Parse("not json at all");
        Assert.Null(result);
    }

    [Fact]
    public void Parse_EmptyObject_ReturnsNull()
    {
        var result = StreamJsonParser.Parse("{}");
        Assert.Null(result);
    }

    [Fact]
    public void Parse_UnknownType_ReturnsNull()
    {
        var result = StreamJsonParser.Parse("""{"type":"system","text":"hi"}""");
        Assert.Null(result);
    }

    [Fact]
    public void Parse_AssistantTextMessage_ReturnsAgentMessageEvent()
    {
        var json = """
            {
              "type": "assistant",
              "message": {
                "content": [
                  { "type": "text", "text": "Hello, world!" }
                ]
              }
            }
            """;

        var result = StreamJsonParser.Parse(json);

        var msg = Assert.IsType<AgentMessageEvent>(result);
        Assert.Equal("assistant",    msg.Role);
        Assert.Equal("Hello, world!", msg.Text);
    }

    [Fact]
    public void Parse_AssistantMultipleTextBlocks_ConcatenatesText()
    {
        var json = """
            {
              "type": "assistant",
              "message": {
                "content": [
                  { "type": "text", "text": "Hello" },
                  { "type": "text", "text": " world" }
                ]
              }
            }
            """;

        var result = StreamJsonParser.Parse(json);

        var msg = Assert.IsType<AgentMessageEvent>(result);
        Assert.Equal("Hello world", msg.Text);
    }

    [Fact]
    public void Parse_AssistantEmptyTextBlocks_ReturnsNull()
    {
        var json = """
            {
              "type": "assistant",
              "message": {
                "content": []
              }
            }
            """;

        var result = StreamJsonParser.Parse(json);
        Assert.Null(result);
    }

    [Fact]
    public void Parse_AssistantToolUse_ReturnsActionRequestEvent()
    {
        var json = """
            {
              "type": "assistant",
              "message": {
                "content": [
                  { "type": "tool_use", "id": "tool-42", "name": "bash" }
                ]
              }
            }
            """;

        var result = StreamJsonParser.Parse(json);

        var action = Assert.IsType<ActionRequestEvent>(result);
        Assert.Equal("tool-42", action.ActionId);
        Assert.Equal("bash",    action.Type);
        Assert.Equal("bash",    action.Description);
    }

    [Fact]
    public void Parse_ResultSuccess_ReturnsAgentDoneEvent()
    {
        var json = """
            {
              "type": "result",
              "is_error": false,
              "result": "Task completed successfully."
            }
            """;

        var result = StreamJsonParser.Parse(json);

        var done = Assert.IsType<AgentDoneEvent>(result);
        Assert.Equal(0,                            done.ExitCode);
        Assert.Equal("Task completed successfully.", done.Summary);
    }

    [Fact]
    public void Parse_ResultSuccessNoSummary_ExitCodeZero()
    {
        var json = """{"type":"result","is_error":false}""";

        var result = StreamJsonParser.Parse(json);

        var done = Assert.IsType<AgentDoneEvent>(result);
        Assert.Equal(0, done.ExitCode);
        Assert.Null(done.Summary);
    }

    [Fact]
    public void Parse_ResultError_ReturnsAgentErrorEvent()
    {
        var json = """
            {
              "type": "result",
              "is_error": true,
              "error": "Rate limit exceeded"
            }
            """;

        var result = StreamJsonParser.Parse(json);

        var err = Assert.IsType<AgentErrorEvent>(result);
        Assert.Equal("Rate limit exceeded", err.Message);
    }

    [Fact]
    public void Parse_ResultErrorNoMessage_FallsBackToUnknownError()
    {
        var json = """{"type":"result","is_error":true}""";

        var result = StreamJsonParser.Parse(json);

        var err = Assert.IsType<AgentErrorEvent>(result);
        Assert.Equal("Unknown error", err.Message);
    }
}
