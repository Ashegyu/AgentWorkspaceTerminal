using System;
using System.Threading;
using System.Threading.Tasks;

namespace AgentWorkspace.Abstractions.Mesh;

/// <summary>
/// A single message published on the agent mesh bus.
/// </summary>
/// <param name="Topic">
///   Full topic string, e.g. <c>agent.{sessionId}.done</c>.
///   Prefix-matched by subscribers: a subscriber with prefix <c>agent.{id}.</c> receives
///   all events for that agent.
/// </param>
/// <param name="Timestamp">UTC time the message was published.</param>
/// <param name="Kind">Short event kind label that mirrors the topic suffix (message, tool_use, done, spawned, merged, error).</param>
/// <param name="Payload">Typed event payload; cast to the concrete type at the subscriber side.</param>
public sealed record MeshMessage(
    string Topic,
    DateTimeOffset Timestamp,
    string Kind,
    object? Payload = null);

/// <summary>
/// In-process publish/subscribe bus that routes <see cref="MeshMessage"/> instances to
/// subscribers by topic prefix.
/// <para>
/// Used by <c>AgentMesh</c> to propagate agent lifecycle events (message, tool_use, done,
/// spawned, merged) without coupling agents or UI components to one another.
/// </para>
/// <para>Standard topic format: <c>agent.{sessionId}.{kind}</c></para>
/// </summary>
public interface IMessageBus
{
    /// <summary>Publishes <paramref name="message"/> to all matching subscribers.</summary>
    ValueTask PublishAsync(MeshMessage message, CancellationToken cancellationToken = default);

    /// <summary>
    /// Subscribes to messages whose <see cref="MeshMessage.Topic"/> starts with
    /// <paramref name="topicPrefix"/>. The handler is invoked sequentially for each
    /// matching message. Dispose the returned handle to unsubscribe.
    /// </summary>
    IAsyncDisposable Subscribe(
        string topicPrefix,
        Func<MeshMessage, CancellationToken, ValueTask> handler);
}
