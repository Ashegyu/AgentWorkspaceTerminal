using System;
using System.Threading.Tasks;
using AgentWorkspace.Abstractions.Agents;
using AgentWorkspace.Agents.Codex;

namespace AgentWorkspace.Tests.Agents;

public sealed class CodexAdapterTests
{
    // ── metadata ────────────────────────────────────────────────────────────────

    [Fact]
    public void Name_ReturnsCodex()
    {
        var adapter = new CodexAdapter();
        Assert.Equal("Codex", adapter.Name);
    }

    [Fact]
    public void Capabilities_StructuredOutput_False()
    {
        var adapter = new CodexAdapter();
        // Until Codex stabilises a stream-JSON format, line-as-message means no structured output.
        Assert.False(adapter.Capabilities.StructuredOutput);
    }

    [Fact]
    public void Capabilities_SupportsCancel_True()
    {
        var adapter = new CodexAdapter();
        Assert.True(adapter.Capabilities.SupportsCancel);
    }

    [Fact]
    public void Capabilities_SupportsContinue_False()
    {
        var adapter = new CodexAdapter();
        // codex `exec` is single-shot, no follow-up turns supported.
        Assert.False(adapter.Capabilities.SupportsContinue);
    }

    // ── graceful failure ────────────────────────────────────────────────────────

    [Fact]
    public async Task StartSessionAsync_NonExistentExecutable_ThrowsInvalidOperationException()
    {
        // Pass an executable name that definitely does not resolve via PATH.
        var adapter = new CodexAdapter("definitely-not-a-real-codex-binary-xyz");
        var options = new AgentSessionOptions(Prompt: "test");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await adapter.StartSessionAsync(options).ConfigureAwait(false));

        // Message must mention the executable name and "PATH" so the user knows what's wrong.
        Assert.Contains("definitely-not-a-real-codex-binary-xyz", ex.Message, StringComparison.Ordinal);
        Assert.Contains("PATH", ex.Message, StringComparison.OrdinalIgnoreCase);
    }
}
