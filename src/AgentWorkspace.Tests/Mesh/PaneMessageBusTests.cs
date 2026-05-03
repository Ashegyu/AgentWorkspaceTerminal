using System;
using System.Threading;
using System.Threading.Tasks;
using AgentWorkspace.Abstractions.Mesh;
using AgentWorkspace.Core.Mesh;
using Xunit;

namespace AgentWorkspace.Tests.Mesh;

/// <summary>
/// Verifies that the <c>pane.{paneId}.send</c> topic convention routes messages
/// to the correct per-pane handler and that cross-pane isolation is maintained.
/// </summary>
public sealed class PaneMessageBusTests
{
    // ── helper ──────────────────────────────────────────────────────────────────

    private static MeshMessage SendMessage(string paneId, string text) =>
        new(Topic: $"pane.{paneId}.send",
            Timestamp: DateTimeOffset.UtcNow,
            Kind: "send",
            Payload: text);

    // ── routing ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task PaneSend_DeliveredToCorrectPaneSubscriber()
    {
        await using var bus = new InMemoryMessageBus();
        var paneId = Guid.NewGuid().ToString();

        var received = new TaskCompletionSource<string?>();
        await using var _ = bus.Subscribe($"pane.{paneId}.", (msg, ct) =>
        {
            if (msg.Kind == "send" && msg.Payload is string text)
                received.TrySetResult(text);
            return ValueTask.CompletedTask;
        });

        await bus.PublishAsync(SendMessage(paneId, "hello pane"));

        var result = await received.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal("hello pane", result);
    }

    [Fact]
    public async Task PaneSend_DoesNotReachDifferentPane()
    {
        await using var bus = new InMemoryMessageBus();
        var paneA = Guid.NewGuid().ToString();
        var paneB = Guid.NewGuid().ToString();

        int paneACount = 0;
        int paneBCount = 0;

        var paneAReceived = new TaskCompletionSource<bool>();
        await using var subA = bus.Subscribe($"pane.{paneA}.", (msg, ct) =>
        {
            Interlocked.Increment(ref paneACount);
            paneAReceived.TrySetResult(true);
            return ValueTask.CompletedTask;
        });

        await using var subB = bus.Subscribe($"pane.{paneB}.", (msg, ct) =>
        {
            Interlocked.Increment(ref paneBCount);
            return ValueTask.CompletedTask;
        });

        // Send to pane A only.
        await bus.PublishAsync(SendMessage(paneA, "for pane A only"));

        // Wait until pane A receives, then give pane B a moment to (incorrectly) fire.
        await paneAReceived.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await Task.Delay(50); // small grace period

        Assert.Equal(1, paneACount);
        Assert.Equal(0, paneBCount);
    }

    [Fact]
    public async Task PaneSend_UnsubscribedPaneReceivesNoFurtherMessages()
    {
        await using var bus = new InMemoryMessageBus();
        var paneId = Guid.NewGuid().ToString();
        int count = 0;

        var sub = bus.Subscribe($"pane.{paneId}.", (msg, ct) =>
        {
            Interlocked.Increment(ref count);
            return ValueTask.CompletedTask;
        });

        // Publish once while subscribed.
        var firstDelivered = new TaskCompletionSource<bool>();
        await using var _ = bus.Subscribe($"pane.{paneId}.", (msg, ct) =>
        {
            firstDelivered.TrySetResult(true);
            return ValueTask.CompletedTask;
        });
        await bus.PublishAsync(SendMessage(paneId, "first"));
        await firstDelivered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await Task.Delay(50);

        int countAfterFirst = count;

        // Dispose the target subscription.
        await sub.DisposeAsync();

        // Publish again — should not reach the disposed subscription.
        await bus.PublishAsync(SendMessage(paneId, "second"));
        await Task.Delay(100);

        Assert.Equal(countAfterFirst, count);
    }

    [Fact]
    public async Task PaneSend_NonSendKindIsIgnoredByPaneHandler()
    {
        // Verify the pattern used in WirePaneSendSubscription: only Kind == "send" writes to PTY.
        // We test here that a message with a different Kind published under the pane prefix
        // does NOT have Kind == "send", allowing the handler to skip it safely.
        await using var bus = new InMemoryMessageBus();
        var paneId = Guid.NewGuid().ToString();

        string? capturedKind = null;
        var received = new TaskCompletionSource<bool>();
        await using var _ = bus.Subscribe($"pane.{paneId}.", (msg, ct) =>
        {
            capturedKind = msg.Kind;
            received.TrySetResult(true);
            return ValueTask.CompletedTask;
        });

        var otherMsg = new MeshMessage(
            Topic: $"pane.{paneId}.status",
            Timestamp: DateTimeOffset.UtcNow,
            Kind: "status",
            Payload: null);
        await bus.PublishAsync(otherMsg);

        await received.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal("status", capturedKind);
        Assert.NotEqual("send", capturedKind);
    }

    [Fact]
    public async Task MultiplePanes_EachReceiveOnlyOwnMessages()
    {
        await using var bus = new InMemoryMessageBus();
        var ids = new[] { Guid.NewGuid().ToString(), Guid.NewGuid().ToString(), Guid.NewGuid().ToString() };
        var counts = new int[3];
        var gates = new TaskCompletionSource<bool>[3];
        for (int i = 0; i < 3; i++) gates[i] = new TaskCompletionSource<bool>();

        var subs = new IAsyncDisposable[3];
        for (int i = 0; i < 3; i++)
        {
            var idx = i;
            subs[i] = bus.Subscribe($"pane.{ids[idx]}.", (msg, ct) =>
            {
                Interlocked.Increment(ref counts[idx]);
                gates[idx].TrySetResult(true);
                return ValueTask.CompletedTask;
            });
        }

        // Send one message to each pane.
        for (int i = 0; i < 3; i++)
            await bus.PublishAsync(SendMessage(ids[i], $"msg {i}"));

        // Wait for all three to fire.
        await Task.WhenAll(
            gates[0].Task.WaitAsync(TimeSpan.FromSeconds(2)),
            gates[1].Task.WaitAsync(TimeSpan.FromSeconds(2)),
            gates[2].Task.WaitAsync(TimeSpan.FromSeconds(2)));

        await Task.Delay(50);

        for (int i = 0; i < 3; i++)
        {
            await subs[i].DisposeAsync();
            Assert.Equal(1, counts[i]);
        }
    }
}
