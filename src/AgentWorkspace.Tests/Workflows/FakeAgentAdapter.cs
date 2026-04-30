using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using AgentWorkspace.Abstractions.Agents;

namespace AgentWorkspace.Tests.Workflows;

/// <summary>
/// Scripted IAgentAdapter. Each call to StartSessionAsync dequeues one event sequence
/// from the pre-registered list (FIFO). Throws when the list is exhausted.
/// </summary>
internal sealed class FakeAgentAdapter : IAgentAdapter
{
    private readonly Queue<AgentEvent[]> _sequences = new();

    public string Name => "Fake";

    public AgentCapabilities Capabilities => new(
        StructuredOutput: true,
        SupportsPlanProposal: true,
        SupportsCancel: true);

    /// <summary>Registers the event sequence returned by the next StartSessionAsync call.</summary>
    public void EnqueueSequence(params AgentEvent[] events)
        => _sequences.Enqueue(events);

    public ValueTask<IAgentSession> StartSessionAsync(
        AgentSessionOptions options,
        CancellationToken cancellationToken = default)
    {
        if (!_sequences.TryDequeue(out var events))
            throw new InvalidOperationException(
                "FakeAgentAdapter: no more event sequences registered.");

        IAgentSession session = new FakeAgentSession(events);
        return ValueTask.FromResult(session);
    }

    // ── inner session ─────────────────────────────────────────────────────────

    private sealed class FakeAgentSession : IAgentSession
    {
        private readonly AgentEvent[] _events;

        public FakeAgentSession(AgentEvent[] events)
        {
            _events = events;
            Id = AgentSessionId.New();
        }

        public AgentSessionId Id { get; }

        public IAsyncEnumerable<AgentEvent> Events => StreamAsync();

        private async IAsyncEnumerable<AgentEvent> StreamAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            foreach (var evt in _events)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Yield();
                yield return evt;
            }
        }

        public ValueTask SendAsync(AgentMessage msg, CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;

        public ValueTask CancelAsync(CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
