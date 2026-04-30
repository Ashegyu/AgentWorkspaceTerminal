using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AgentWorkspace.Abstractions.Mesh;
using AgentWorkspace.Core.Mesh;

namespace AgentWorkspace.Tests.Mesh;

/// <summary>
/// Unit tests for <see cref="InMemoryMessageBus"/>: topic routing, prefix matching,
/// unsubscribe, and multi-subscriber fan-out.
/// </summary>
public sealed class MessageBusTests
{
    // ── routing ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task PublishAsync_RoutesMessageToMatchingSubscriber()
    {
        await using var bus = new InMemoryMessageBus();
        var received = new List<MeshMessage>();
        var tcs = new TaskCompletionSource();

        await using var _ = bus.Subscribe("agent.abc.", (msg, ct) =>
        {
            received.Add(msg);
            tcs.TrySetResult();
            return ValueTask.CompletedTask;
        });

        var message = new MeshMessage("agent.abc.done", DateTimeOffset.UtcNow, "done");
        await bus.PublishAsync(message);

        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Single(received);
        Assert.Equal("agent.abc.done", received[0].Topic);
    }

    [Fact]
    public async Task PublishAsync_DoesNotRouteToNonMatchingSubscriber()
    {
        await using var bus = new InMemoryMessageBus();
        var received = new List<MeshMessage>();

        await using var _ = bus.Subscribe("agent.xyz.", (msg, ct) =>
        {
            received.Add(msg);
            return ValueTask.CompletedTask;
        });

        var message = new MeshMessage("agent.abc.done", DateTimeOffset.UtcNow, "done");
        await bus.PublishAsync(message);

        // Give pump time to process if routing were wrong.
        await Task.Delay(100);

        Assert.Empty(received);
    }

    [Fact]
    public async Task PublishAsync_FansOutToMultipleMatchingSubscribers()
    {
        await using var bus = new InMemoryMessageBus();

        var tcs1 = new TaskCompletionSource();
        var tcs2 = new TaskCompletionSource();

        await using var h1 = bus.Subscribe("agent.", (msg, ct) =>
        {
            tcs1.TrySetResult();
            return ValueTask.CompletedTask;
        });

        await using var h2 = bus.Subscribe("agent.", (msg, ct) =>
        {
            tcs2.TrySetResult();
            return ValueTask.CompletedTask;
        });

        await bus.PublishAsync(new MeshMessage("agent.abc.message", DateTimeOffset.UtcNow, "message"));

        await Task.WhenAll(
            tcs1.Task.WaitAsync(TimeSpan.FromSeconds(2)),
            tcs2.Task.WaitAsync(TimeSpan.FromSeconds(2)));

        Assert.True(tcs1.Task.IsCompletedSuccessfully);
        Assert.True(tcs2.Task.IsCompletedSuccessfully);
    }

    // ── unsubscribe ───────────────────────────────────────────────────────────

    [Fact]
    public async Task DisposeSubscription_StopsDelivery()
    {
        await using var bus = new InMemoryMessageBus();
        var count = 0;

        var handle = bus.Subscribe("agent.", (msg, ct) =>
        {
            Interlocked.Increment(ref count);
            return ValueTask.CompletedTask;
        });

        // Deliver one message while subscribed.
        var tcs = new TaskCompletionSource();
        await using var _ = bus.Subscribe("agent.", (msg, ct) =>
        {
            tcs.TrySetResult();
            return ValueTask.CompletedTask;
        });

        await bus.PublishAsync(new MeshMessage("agent.abc.done", DateTimeOffset.UtcNow, "done"));
        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(2));

        // Now unsubscribe the first subscriber.
        await handle.DisposeAsync();

        var countAfterUnsub = count;

        // Publish again — should NOT reach the unsubscribed handler.
        await bus.PublishAsync(new MeshMessage("agent.abc.done", DateTimeOffset.UtcNow, "done"));
        await Task.Delay(150);

        Assert.Equal(countAfterUnsub, count);
    }

    // ── exact-prefix matching ────────────────────────────────────────────────

    [Fact]
    public async Task Subscribe_WithExactTopicPrefix_MatchesOnlyThatPrefix()
    {
        await using var bus = new InMemoryMessageBus();
        var ids = new List<string>();
        var gate = new SemaphoreSlim(0, 2);

        await using var _ = bus.Subscribe("agent.id1.", (msg, ct) =>
        {
            ids.Add(msg.Topic);
            gate.Release();
            return ValueTask.CompletedTask;
        });

        await bus.PublishAsync(new MeshMessage("agent.id1.done", DateTimeOffset.UtcNow, "done"));
        await bus.PublishAsync(new MeshMessage("agent.id2.done", DateTimeOffset.UtcNow, "done"));
        await bus.PublishAsync(new MeshMessage("agent.id1.message", DateTimeOffset.UtcNow, "message"));

        // Wait for the two matching messages.
        await gate.WaitAsync(TimeSpan.FromSeconds(2));
        await gate.WaitAsync(TimeSpan.FromSeconds(2));
        await Task.Delay(100); // ensure id2 pump silence

        Assert.Equal(2, ids.Count);
        Assert.All(ids, t => Assert.StartsWith("agent.id1.", t));
    }
}
