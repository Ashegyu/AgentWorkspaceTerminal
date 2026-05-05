using System;
using System.Threading.Tasks;
using AgentWorkspace.Abstractions.Agents;
using AgentWorkspace.Agents.Gemini;

namespace AgentWorkspace.Tests.Agents;

public sealed class GeminiAdapterTests
{
    // ── metadata ────────────────────────────────────────────────────────────────

    [Fact]
    public void Name_ReturnsGemini()
    {
        var adapter = new GeminiAdapter();
        Assert.Equal("Gemini", adapter.Name);
    }

    [Fact]
    public void Capabilities_StructuredOutput_False()
    {
        var adapter = new GeminiAdapter();
        Assert.False(adapter.Capabilities.StructuredOutput);
    }

    [Fact]
    public void Capabilities_SupportsCancel_True()
    {
        var adapter = new GeminiAdapter();
        Assert.True(adapter.Capabilities.SupportsCancel);
    }

    // ── graceful failure ────────────────────────────────────────────────────────

    [Fact]
    public async Task StartSessionAsync_NonExistentExecutable_ThrowsInvalidOperationException()
    {
        var adapter = new GeminiAdapter("definitely-not-a-real-gemini-binary-xyz");
        var options = new AgentSessionOptions(Prompt: "test");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await adapter.StartSessionAsync(options).ConfigureAwait(false));

        Assert.Contains("definitely-not-a-real-gemini-binary-xyz", ex.Message, StringComparison.Ordinal);
        Assert.Contains("PATH", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Ctor_AcceptsModelOverride()
    {
        // Just smoke-test that the model param doesn't crash construction.
        // Live verification of model arg passthrough requires a real gemini CLI.
        var adapter = new GeminiAdapter(executable: "gemini", model: "gemini-2.5-pro");
        Assert.Equal("Gemini", adapter.Name);
    }
}
