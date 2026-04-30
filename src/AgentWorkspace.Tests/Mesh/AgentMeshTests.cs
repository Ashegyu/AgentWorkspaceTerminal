using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using AgentWorkspace.Abstractions.Agents;
using AgentWorkspace.Abstractions.Mesh;
using AgentWorkspace.Core.Mesh;

namespace AgentWorkspace.Tests.Mesh;

/// <summary>
/// Unit tests for <see cref="AgentMesh"/>: spawn-policy enforcement, unregistered-parent
/// guard, spawned-event publication, and auto-merge (summary injection + merged-event).
/// </summary>
public sealed class AgentMeshTests
{
    // ── test helpers ──────────────────────────────────────────────────────────

    private static AgentSessionOptions DefaultOptions => new("test");

    /// <summary>
    /// Session whose event stream ends immediately (no AgentDoneEvent).
    /// Keeps the topology entry alive across SpawnAsync calls.
    /// </summary>
    private sealed class EmptySession : IAgentSession
    {
        public AgentSessionId Id { get; } = AgentSessionId.New();

        public IAsyncEnumerable<AgentEvent> Events => EmptyStream();

        private static async IAsyncEnumerable<AgentEvent> EmptyStream(
            [EnumeratorCancellation] CancellationToken _ = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public ValueTask SendAsync(AgentMessage msg, CancellationToken ct) =>
            ValueTask.CompletedTask;

        public ValueTask CancelAsync(CancellationToken ct) =>
            ValueTask.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>Session that emits a scripted sequence of events then ends.</summary>
    private sealed class ScriptedSession : IAgentSession
    {
        private readonly AgentEvent[] _events;

        public ScriptedSession(params AgentEvent[] events) => _events = events;

        public AgentSessionId Id { get; } = AgentSessionId.New();

        public IAsyncEnumerable<AgentEvent> Events => StreamAsync();

        private async IAsyncEnumerable<AgentEvent> StreamAsync(
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            foreach (var evt in _events)
            {
                ct.ThrowIfCancellationRequested();
                await Task.Yield();
                yield return evt;
            }
        }

        public ValueTask SendAsync(AgentMessage msg, CancellationToken ct) =>
            ValueTask.CompletedTask;

        public ValueTask CancelAsync(CancellationToken ct) =>
            ValueTask.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>
    /// Parent session whose stream never ends (stays alive so the pump never exits).
    /// Records every SendAsync call for assertion.
    /// </summary>
    private sealed class TrackingSession : IAgentSession
    {
        public AgentSessionId Id { get; } = AgentSessionId.New();

        public List<string> ReceivedTexts { get; } = new();

        public IAsyncEnumerable<AgentEvent> Events => NeverEndingStream();

        private static async IAsyncEnumerable<AgentEvent> NeverEndingStream(
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
            yield break;
        }

        public ValueTask SendAsync(AgentMessage msg, CancellationToken ct)
        {
            ReceivedTexts.Add(msg.Text);
            return ValueTask.CompletedTask;
        }

        public ValueTask CancelAsync(CancellationToken ct) =>
            ValueTask.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>Parent session whose SendAsync always throws to test fault tolerance.</summary>
    private sealed class ThrowingSession : IAgentSession
    {
        public AgentSessionId Id { get; } = AgentSessionId.New();

        public IAsyncEnumerable<AgentEvent> Events => NeverEndingStream();

        private static async IAsyncEnumerable<AgentEvent> NeverEndingStream(
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
            yield break;
        }

        public ValueTask SendAsync(AgentMessage msg, CancellationToken ct) =>
            throw new InvalidOperationException("Simulated: parent already gone.");

        public ValueTask CancelAsync(CancellationToken ct) =>
            ValueTask.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>Adapter that always returns the same pre-constructed session.</summary>
    private sealed class FixedAdapter : IAgentAdapter
    {
        private readonly IAgentSession _session;

        public FixedAdapter(IAgentSession session) => _session = session;

        public string Name => "Fixed";

        public AgentCapabilities Capabilities => new(
            StructuredOutput: false,
            SupportsPlanProposal: false,
            SupportsCancel: false);

        public ValueTask<IAgentSession> StartSessionAsync(
            AgentSessionOptions options,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(_session);
    }

    // ── spawn policy — depth limit ────────────────────────────────────────────

    [Fact]
    public async Task SpawnAsync_ExceedingMaxDepth_ThrowsWithDepthKind()
    {
        await using var bus  = new InMemoryMessageBus();
        await using var mesh = new AgentMesh(bus, spawnPolicy: new SpawnPolicy(maxDepth: 1));

        var rootId = AgentSessionId.New();
        mesh.RegisterRoot(rootId, new EmptySession());

        // Depth 1 — allowed.
        var child = await mesh.SpawnAsync(rootId, new FixedAdapter(new EmptySession()), DefaultOptions);

        // Depth 2 — exceeds maxDepth 1.
        var ex = await Assert.ThrowsAsync<SpawnPolicyViolatedException>(
            () => mesh.SpawnAsync(child, new FixedAdapter(new EmptySession()), DefaultOptions).AsTask());

        Assert.Equal(SpawnViolationKind.MaxDepth, ex.Kind);
        Assert.Equal(1, ex.Limit);
        Assert.Equal(2, ex.Actual);
    }

    [Fact]
    public async Task SpawnAsync_AtDefaultMaxDepth_AllowsDepth3ButBlocksDepth4()
    {
        await using var bus  = new InMemoryMessageBus();
        await using var mesh = new AgentMesh(bus); // default: maxDepth = 3

        var rootId = AgentSessionId.New();
        mesh.RegisterRoot(rootId, new EmptySession());
        var opts = DefaultOptions;

        var d1 = await mesh.SpawnAsync(rootId, new FixedAdapter(new EmptySession()), opts);
        var d2 = await mesh.SpawnAsync(d1,     new FixedAdapter(new EmptySession()), opts);
        var d3 = await mesh.SpawnAsync(d2,     new FixedAdapter(new EmptySession()), opts);

        var ex = await Assert.ThrowsAsync<SpawnPolicyViolatedException>(
            () => mesh.SpawnAsync(d3, new FixedAdapter(new EmptySession()), opts).AsTask());

        Assert.Equal(SpawnViolationKind.MaxDepth, ex.Kind);
        Assert.Equal(3, ex.Limit);
        Assert.Equal(4, ex.Actual);
    }

    // ── spawn policy — parallel children limit ────────────────────────────────

    [Fact]
    public async Task SpawnAsync_ExceedingParallelChildrenLimit_ThrowsWithParallelKind()
    {
        await using var bus  = new InMemoryMessageBus();
        await using var mesh = new AgentMesh(bus, spawnPolicy: new SpawnPolicy(maxParallelChildren: 2));

        var rootId = AgentSessionId.New();
        mesh.RegisterRoot(rootId, new EmptySession());
        var opts = DefaultOptions;

        await mesh.SpawnAsync(rootId, new FixedAdapter(new EmptySession()), opts);
        await mesh.SpawnAsync(rootId, new FixedAdapter(new EmptySession()), opts);

        var ex = await Assert.ThrowsAsync<SpawnPolicyViolatedException>(
            () => mesh.SpawnAsync(rootId, new FixedAdapter(new EmptySession()), opts).AsTask());

        Assert.Equal(SpawnViolationKind.MaxParallelChildren, ex.Kind);
        Assert.Equal(2, ex.Limit);
    }

    [Fact]
    public async Task SpawnAsync_AtDefaultParallelLimit_AllowsFourChildrenBlocksFifth()
    {
        await using var bus  = new InMemoryMessageBus();
        await using var mesh = new AgentMesh(bus); // default: maxParallelChildren = 4

        var rootId = AgentSessionId.New();
        mesh.RegisterRoot(rootId, new EmptySession());
        var opts = DefaultOptions;

        for (var i = 0; i < 4; i++)
            await mesh.SpawnAsync(rootId, new FixedAdapter(new EmptySession()), opts);

        var ex = await Assert.ThrowsAsync<SpawnPolicyViolatedException>(
            () => mesh.SpawnAsync(rootId, new FixedAdapter(new EmptySession()), opts).AsTask());

        Assert.Equal(SpawnViolationKind.MaxParallelChildren, ex.Kind);
        Assert.Equal(4, ex.Limit);
    }

    // ── unregistered parent guard ─────────────────────────────────────────────

    [Fact]
    public async Task SpawnAsync_UnregisteredParent_ThrowsInvalidOperationException()
    {
        await using var bus  = new InMemoryMessageBus();
        await using var mesh = new AgentMesh(bus);
        var unknownId = AgentSessionId.New();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => mesh.SpawnAsync(unknownId,
                new FixedAdapter(new EmptySession()), DefaultOptions).AsTask());
    }

    // ── spawned event ─────────────────────────────────────────────────────────

    [Fact]
    public async Task SpawnAsync_PublishesSpawnedEventOnBus()
    {
        await using var bus  = new InMemoryMessageBus();
        await using var mesh = new AgentMesh(bus);

        var rootId = AgentSessionId.New();
        mesh.RegisterRoot(rootId, new EmptySession());

        var tcs = new TaskCompletionSource<MeshMessage>();
        await using var _ = bus.Subscribe($"agent.{rootId}.spawned", (msg, ct) =>
        {
            tcs.TrySetResult(msg);
            return ValueTask.CompletedTask;
        });

        await mesh.SpawnAsync(rootId, new FixedAdapter(new EmptySession()), DefaultOptions);

        var spawned = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal("spawned", spawned.Kind);
    }

    // ── auto-merge — summary injection ────────────────────────────────────────

    [Fact]
    public async Task ChildDoneEvent_InjectsSummaryIntoParentSession()
    {
        await using var bus  = new InMemoryMessageBus();
        await using var mesh = new AgentMesh(bus);

        var parentSession = new TrackingSession();
        mesh.RegisterRoot(parentSession.Id, parentSession);

        const string summary = "Child task completed successfully.";
        var childSession = new ScriptedSession(
            new AgentDoneEvent(ExitCode: 0, Summary: summary));

        // Use the merged event as the synchronisation point.
        var mergedTcs = new TaskCompletionSource<MeshMessage>();
        await using var _ = bus.Subscribe($"agent.{parentSession.Id}.merged", (msg, ct) =>
        {
            mergedTcs.TrySetResult(msg);
            return ValueTask.CompletedTask;
        });

        await mesh.SpawnAsync(parentSession.Id, new FixedAdapter(childSession), DefaultOptions);

        await mergedTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Parent should have received the (redacted) summary.
        Assert.Single(parentSession.ReceivedTexts);
        Assert.Contains("Child task completed", parentSession.ReceivedTexts[0]);
    }

    [Fact]
    public async Task ChildDoneEvent_PublishesMergedEventWithCorrectPayload()
    {
        await using var bus  = new InMemoryMessageBus();
        await using var mesh = new AgentMesh(bus);

        var parentSession = new TrackingSession();
        mesh.RegisterRoot(parentSession.Id, parentSession);

        const string summary = "Analysis complete.";
        var childSession = new ScriptedSession(
            new AgentDoneEvent(ExitCode: 0, Summary: summary));

        var mergedTcs = new TaskCompletionSource<MeshMessage>();
        await using var _ = bus.Subscribe($"agent.{parentSession.Id}.merged", (msg, ct) =>
        {
            mergedTcs.TrySetResult(msg);
            return ValueTask.CompletedTask;
        });

        await mesh.SpawnAsync(parentSession.Id, new FixedAdapter(childSession), DefaultOptions);

        var mergedMsg = await mergedTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("merged", mergedMsg.Kind);
        var payload = Assert.IsType<MergedPayload>(mergedMsg.Payload);
        Assert.Equal(0, payload.ExitCode);
        Assert.NotEmpty(payload.RedactedSummary);
    }

    [Fact]
    public async Task ChildDoneEvent_NullSummary_InjectsDefaultFallbackMessage()
    {
        await using var bus  = new InMemoryMessageBus();
        await using var mesh = new AgentMesh(bus);

        var parentSession = new TrackingSession();
        mesh.RegisterRoot(parentSession.Id, parentSession);

        var childSession = new ScriptedSession(
            new AgentDoneEvent(ExitCode: 0, Summary: null)); // null summary

        var mergedTcs = new TaskCompletionSource();
        await using var _ = bus.Subscribe($"agent.{parentSession.Id}.merged", (msg, ct) =>
        {
            mergedTcs.TrySetResult();
            return ValueTask.CompletedTask;
        });

        await mesh.SpawnAsync(parentSession.Id, new FixedAdapter(childSession), DefaultOptions);

        await mergedTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // A fallback message must still have been sent.
        Assert.Single(parentSession.ReceivedTexts);
        Assert.NotEmpty(parentSession.ReceivedTexts[0]);
    }

    // ── fault tolerance — parent already gone ────────────────────────────────

    [Fact]
    public async Task ChildDoneEvent_ParentSendAsyncThrows_StillPublishesMergedEvent()
    {
        await using var bus  = new InMemoryMessageBus();
        await using var mesh = new AgentMesh(bus);

        var throwingParent = new ThrowingSession();
        mesh.RegisterRoot(throwingParent.Id, throwingParent);

        var childSession = new ScriptedSession(
            new AgentDoneEvent(ExitCode: 1, Summary: "Done."));

        var mergedTcs = new TaskCompletionSource<MeshMessage>();
        await using var _ = bus.Subscribe($"agent.{throwingParent.Id}.merged", (msg, ct) =>
        {
            mergedTcs.TrySetResult(msg);
            return ValueTask.CompletedTask;
        });

        await mesh.SpawnAsync(throwingParent.Id, new FixedAdapter(childSession), DefaultOptions);

        // Should not time out even though SendAsync threw.
        var mergedMsg = await mergedTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("merged", mergedMsg.Kind);
    }
}
