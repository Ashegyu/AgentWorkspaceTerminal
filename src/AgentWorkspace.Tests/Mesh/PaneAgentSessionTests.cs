using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows.Threading;
using AgentWorkspace.Abstractions.Agents;
using AgentWorkspace.Abstractions.Redaction;
using AgentWorkspace.App.Wpf.AgentTrace;
using AgentWorkspace.App.Wpf.Mesh;

namespace AgentWorkspace.Tests.Mesh;

/// <summary>
/// Unit tests for <see cref="PaneAgentSession"/>: null-guard, SendAsync no-op,
/// CancelAsync sentinel injection, and DisposeAsync quiet completion.
/// </summary>
public sealed class PaneAgentSessionTests
{
    // ── test helpers ──────────────────────────────────────────────────────────

    /// <summary>
    /// Passthrough redaction engine — never redacts anything.
    /// Avoids pulling in the real regex engine to keep these tests lightweight.
    /// </summary>
    private sealed class NullRedactionEngine : IRedactionEngine
    {
        public string Redact(string text) => text;
    }

    /// <summary>
    /// Builds a <see cref="AgentTraceViewModel"/> bound to the current thread's
    /// dispatcher.  Because <see cref="PaneAgentSession.SendAsync"/> is a no-op
    /// the dispatcher is never actually invoked in these tests.
    /// </summary>
    private static AgentTraceViewModel CreateTrace() =>
        new(Dispatcher.CurrentDispatcher, new NullRedactionEngine());

    // ── constructor guard ─────────────────────────────────────────────────────

    [Fact]
    public void Constructor_NullTrace_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => new PaneAgentSession(null!));
    }

    // ── SendAsync — intentional no-op ─────────────────────────────────────────

    [Fact]
    public async Task SendAsync_IsNoOp_DoesNotAppendToTrace()
    {
        // Arrange
        var trace = CreateTrace();
        await using var session = new PaneAgentSession(trace);

        // Act
        await session.SendAsync(new AgentMessage("hello from child"));

        // Assert — the trace Events collection must stay empty
        Assert.Empty(trace.Events);
    }

    [Fact]
    public async Task SendAsync_CalledRepeatedly_TraceRemainsEmpty()
    {
        // Arrange
        var trace = CreateTrace();
        await using var session = new PaneAgentSession(trace);

        // Act — multiple calls must all be no-ops
        for (var i = 0; i < 5; i++)
            await session.SendAsync(new AgentMessage($"message {i}"));

        // Assert
        Assert.Empty(trace.Events);
    }

    // ── CancelAsync — sentinel + channel completion ───────────────────────────

    [Fact]
    public async Task CancelAsync_WritesDoneEventThenCompletesChannel()
    {
        // Arrange
        var trace = CreateTrace();
        await using var session = new PaneAgentSession(trace);

        // Act — cancel before any reader is attached
        await session.CancelAsync();

        // Read everything the channel now holds
        var events = new List<AgentEvent>();
        await foreach (var evt in session.Events)
            events.Add(evt);

        // Assert — exactly one AgentDoneEvent with exit-code 0
        var done = Assert.Single(events);
        var doneEvent = Assert.IsType<AgentDoneEvent>(done);
        Assert.Equal(0, doneEvent.ExitCode);
    }

    [Fact]
    public async Task CancelAsync_CalledTwice_ChannelCompletesCleanly()
    {
        // Arrange
        var trace = CreateTrace();
        await using var session = new PaneAgentSession(trace);

        // Act — second TryWrite/TryComplete on a completed channel must not throw
        await session.CancelAsync();
        await session.CancelAsync();

        var events = new List<AgentEvent>();
        await foreach (var evt in session.Events)
            events.Add(evt);

        // Assert — still exactly one sentinel from the first call
        Assert.Single(events);
    }

    // ── DisposeAsync — quiet channel completion (no sentinel) ─────────────────

    [Fact]
    public async Task DisposeAsync_CompletesChannelWithoutWritingDoneEvent()
    {
        // Arrange
        var trace = CreateTrace();
        var session = new PaneAgentSession(trace);

        // Act — dispose directly (not via CancelAsync)
        await session.DisposeAsync();

        // Enumerate; should end immediately with nothing yielded
        var events = new List<AgentEvent>();
        await foreach (var evt in session.Events)
            events.Add(evt);

        // Assert — channel completed silently; no DoneEvent written
        Assert.Empty(events);
    }

    // ── Events async enumerable ends after CancelAsync ────────────────────────

    [Fact]
    public async Task Events_EnumerationEnds_AfterCancelAsync()
    {
        // Arrange
        var trace = CreateTrace();
        var session = new PaneAgentSession(trace);

        // Start enumerating in the background, then cancel.
        var collectedEvents = new List<AgentEvent>();
        var enumTask = Task.Run(async () =>
        {
            await foreach (var evt in session.Events)
                collectedEvents.Add(evt);
        });

        // Brief yield to let the consumer block on the empty channel.
        await Task.Delay(50);

        // Act
        await session.CancelAsync();
        await enumTask.WaitAsync(TimeSpan.FromSeconds(5));

        // Assert — enumeration ended and exactly one DoneEvent was delivered
        await session.DisposeAsync();
        var done = Assert.Single(collectedEvents);
        Assert.IsType<AgentDoneEvent>(done);
    }
}
