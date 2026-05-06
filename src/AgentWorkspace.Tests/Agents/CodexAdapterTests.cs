using System;
using System.Threading.Tasks;
using AgentWorkspace.Abstractions.Agents;
using AgentWorkspace.Agents.Codex;

namespace AgentWorkspace.Tests.Agents;

public sealed class CodexAdapterTests
{
    private static async Task<IReadOnlyList<AgentEvent>> DrainEventsAsync(IAgentSession session)
    {
        var events = new List<AgentEvent>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await foreach (var evt in session.Events.WithCancellation(cts.Token))
        {
            events.Add(evt);
            if (evt is AgentDoneEvent or AgentErrorEvent) break;
        }
        return events;
    }

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

    [Fact]
    public async Task StartSessionAsync_WindowsCmdShim_ExecutesThroughCommandShell()
    {
        if (!OperatingSystem.IsWindows()) return;

        var tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tempDir);
        try
        {
            var shim = Path.Combine(tempDir, "codex.cmd");
            File.WriteAllText(shim, """
                @echo off
                echo codex-shim:%1:%2
                exit /b 0
                """);

            var adapter = new CodexAdapter(shim);
            await using var session = await adapter.StartSessionAsync(
                new AgentSessionOptions(Prompt: "hello"));

            var events = await DrainEventsAsync(session);

            Assert.Contains(
                events.OfType<AgentMessageEvent>(),
                evt => evt.Text == "codex-shim:exec:hello");
            Assert.Contains(events.OfType<AgentDoneEvent>(), evt => evt.ExitCode == 0);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); }
            catch { /* best-effort */ }
        }
    }
}
