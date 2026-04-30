using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AgentWorkspace.Abstractions.Agents;
using AgentWorkspace.Agents.Ollama;

namespace AgentWorkspace.Tests.Agents;

public sealed class OllamaAdapterTests
{
    // ── OllamaAdapter metadata ───────────────────────────────────────────────

    [Fact]
    public void Name_ReturnsOllama()
    {
        var adapter = new OllamaAdapter();
        Assert.Equal("Ollama", adapter.Name);
    }

    [Fact]
    public void Capabilities_StructuredOutput_False()
    {
        var adapter = new OllamaAdapter();
        Assert.False(adapter.Capabilities.StructuredOutput);
    }

    [Fact]
    public void Capabilities_SupportsPlanProposal_False()
    {
        var adapter = new OllamaAdapter();
        Assert.False(adapter.Capabilities.SupportsPlanProposal);
    }

    [Fact]
    public void Capabilities_SupportsCancel_True()
    {
        var adapter = new OllamaAdapter();
        Assert.True(adapter.Capabilities.SupportsCancel);
    }

    [Fact]
    public void Capabilities_SupportsContinue_False()
    {
        var adapter = new OllamaAdapter();
        Assert.False(adapter.Capabilities.SupportsContinue);
    }

    [Fact]
    public void Capabilities_SupportsMultimodal_False()
    {
        var adapter = new OllamaAdapter();
        Assert.False(adapter.Capabilities.SupportsMultimodal);
    }

    [Fact]
    public void Capabilities_Cost_Null()
    {
        var adapter = new OllamaAdapter();
        Assert.Null(adapter.Capabilities.Cost);
    }

    [Fact]
    public async Task StartSessionAsync_ReturnsSession()
    {
        var adapter = new OllamaAdapter();
        var options = new AgentSessionOptions("hello", SaveTranscript: false);
        var session = await adapter.StartSessionAsync(options, CancellationToken.None);
        Assert.NotNull(session);
        // Dispose without consuming events — session disposes cleanly even without Ollama running.
        await session.DisposeAsync();
    }

    // ── Live integration test — skipped when Ollama is not running ───────────

    [SkippableFact]
    public async Task LiveSession_EmitsAtLeastOneAssistantMessage()
    {
        const string ollamaUrl = "http://localhost:11434";

        // Skip gracefully if Ollama is not reachable — this is a live-environment test.
        try
        {
            using var probe = new HttpClient();
            var resp = await probe.GetAsync($"{ollamaUrl}/api/tags", CancellationToken.None);
            Skip.IfNot(resp.IsSuccessStatusCode, "Ollama not reachable at localhost:11434");
        }
        catch
        {
            Skip.If(true, "Ollama not reachable at localhost:11434");
        }

        var adapter = new OllamaAdapter(ollamaUrl);
        var options = new AgentSessionOptions(
            "Reply with exactly the word: pong",
            SaveTranscript: false,
            Model: "llama3");

        await using var session = await adapter.StartSessionAsync(options, CancellationToken.None);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        bool gotAssistantMessage = false;
        bool gotDone             = false;

        await foreach (var evt in session.Events.WithCancellation(cts.Token))
        {
            switch (evt)
            {
                case AgentMessageEvent { Role: "assistant" }:
                    gotAssistantMessage = true;
                    break;
                case AgentDoneEvent:
                    gotDone = true;
                    goto done;
                case AgentErrorEvent err:
                    Assert.Fail($"Session error: {err.Message}");
                    break;
            }
        }

        done:
        Assert.True(gotAssistantMessage, "Expected at least one assistant message event.");
        Assert.True(gotDone,             "Expected AgentDoneEvent at end of stream.");
    }
}
