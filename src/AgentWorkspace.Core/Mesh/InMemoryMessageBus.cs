using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using AgentWorkspace.Abstractions.Mesh;

namespace AgentWorkspace.Core.Mesh;

/// <summary>
/// Channel-per-subscriber, prefix-matched in-process message bus.
/// <para>
/// Each <see cref="Subscribe"/> call allocates a bounded <see cref="Channel{T}"/> and starts
/// a background pump that drains the channel into the caller's handler.  Concurrency is per-
/// subscriber: handlers for the same subscription are invoked sequentially; handlers across
/// different subscriptions execute concurrently (each has its own pump task).
/// </para>
/// <para>Thread-safe: <see cref="PublishAsync"/> and <see cref="Subscribe"/> may be called
/// from any thread.</para>
/// </summary>
public sealed class InMemoryMessageBus : IMessageBus, IAsyncDisposable
{
    private const int ChannelCapacity = 1024;

    private readonly object _lock = new();
    private readonly List<Subscription> _subscribers = new();
    private bool _disposed;

    /// <inheritdoc/>
    public ValueTask PublishAsync(MeshMessage message, CancellationToken cancellationToken = default)
    {
        List<Subscription> snapshot;
        lock (_lock)
        {
            if (_subscribers.Count == 0) return ValueTask.CompletedTask;
            snapshot = new List<Subscription>(_subscribers);
        }

        foreach (var sub in snapshot)
        {
            if (message.Topic.StartsWith(sub.TopicPrefix, StringComparison.Ordinal))
            {
                // TryWrite: if the channel is full or completed, silently drop.
                sub.Channel.Writer.TryWrite(message);
            }
        }

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc/>
    public IAsyncDisposable Subscribe(
        string topicPrefix,
        Func<MeshMessage, CancellationToken, ValueTask> handler)
    {
        ArgumentNullException.ThrowIfNull(topicPrefix);
        ArgumentNullException.ThrowIfNull(handler);

        var sub = new Subscription(topicPrefix, handler);

        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _subscribers.Add(sub);
        }

        sub.Start();
        return new SubscriptionHandle(this, sub);
    }

    private void Unsubscribe(Subscription sub)
    {
        lock (_lock)
        {
            _subscribers.Remove(sub);
        }
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        List<Subscription> all;
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            all = new List<Subscription>(_subscribers);
            _subscribers.Clear();
        }

        foreach (var sub in all)
        {
            await sub.StopAsync().ConfigureAwait(false);
        }
    }

    // ─── Inner types ──────────────────────────────────────────────────────────────

    internal sealed class Subscription
    {
        private readonly Func<MeshMessage, CancellationToken, ValueTask> _handler;
        private readonly CancellationTokenSource _cts = new();
        private Task? _pump;

        public string TopicPrefix { get; }
        public Channel<MeshMessage> Channel { get; } =
            System.Threading.Channels.Channel.CreateBounded<MeshMessage>(
                new BoundedChannelOptions(ChannelCapacity)
                {
                    SingleReader = true,
                    FullMode = BoundedChannelFullMode.DropOldest,
                });

        public Subscription(string topicPrefix, Func<MeshMessage, CancellationToken, ValueTask> handler)
        {
            TopicPrefix = topicPrefix;
            _handler = handler;
        }

        public void Start()
        {
            _pump = Task.Run(() => PumpAsync(_cts.Token));
        }

        private async Task PumpAsync(CancellationToken ct)
        {
            try
            {
                await foreach (var msg in Channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
                {
                    await _handler(msg, ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { /* stopping */ }
            catch (Exception) { /* handler errors must not crash the pump */ }
        }

        public async ValueTask StopAsync()
        {
            Channel.Writer.TryComplete();

            try { await _cts.CancelAsync().ConfigureAwait(false); }
            catch (ObjectDisposedException) { /* already disposed */ }

            if (_pump is not null)
            {
                try { await _pump.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
                catch { /* best effort */ }
            }

            _cts.Dispose();
        }
    }

    private sealed class SubscriptionHandle : IAsyncDisposable
    {
        private readonly InMemoryMessageBus _bus;
        private readonly Subscription _sub;
        private int _disposed;

        public SubscriptionHandle(InMemoryMessageBus bus, Subscription sub)
        {
            _bus = bus;
            _sub = sub;
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _bus.Unsubscribe(_sub);
            await _sub.StopAsync().ConfigureAwait(false);
        }
    }
}
