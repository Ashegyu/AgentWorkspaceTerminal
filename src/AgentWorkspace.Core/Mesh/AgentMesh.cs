using System;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using AgentWorkspace.Abstractions.Agents;
using AgentWorkspace.Abstractions.Mesh;
using AgentWorkspace.Abstractions.Redaction;
using AgentWorkspace.Core.Redaction;

namespace AgentWorkspace.Core.Mesh;

/// <summary>
/// Payload published on <c>agent.{parentId}.merged</c> when a child completes and its
/// result has been injected into the parent.
/// </summary>
public sealed record MergedPayload(
    AgentSessionId ChildId,
    string RedactedSummary,
    int ExitCode);

/// <summary>
/// Orchestrates a tree of agent sessions: registers root sessions, spawns children,
/// pumps events onto the <see cref="IMessageBus"/>, and auto-merges completed children
/// back into their parent.
/// </summary>
/// <remarks>
/// <para>
/// <b>Lifecycle</b>: call <see cref="RegisterRoot"/> or <see cref="SpawnAsync"/> to enter
/// sessions into the mesh.  Each session gets a background pump task that reads
/// <see cref="IAgentSession.Events"/>, publishes to the bus, and — for child sessions — calls
/// <see cref="IAgentSession.SendAsync"/> on the parent with a redacted summary when the child
/// emits <see cref="AgentDoneEvent"/>.
/// </para>
/// <para>
/// <b>Thread-safety</b>: all public methods are thread-safe.
/// </para>
/// </remarks>
public sealed class AgentMesh : IAsyncDisposable
{
    private readonly IMessageBus _bus;
    private readonly IRedactionEngine _redaction;
    private readonly SpawnPolicy _spawnPolicy;
    private readonly AgentTopologyTracker _topology;
    private readonly ConcurrentDictionary<AgentSessionId, CancellationTokenSource> _pumps = new();
    private int _disposed;

    /// <summary>The shared message bus; external components may subscribe to events here.</summary>
    public IMessageBus Bus => _bus;

    /// <param name="bus">
    ///   Shared message bus. Consumers subscribe here before calling
    ///   <see cref="RegisterRoot"/> to avoid missing early events.
    /// </param>
    /// <param name="redaction">
    ///   Engine applied to merged summaries before injecting into the parent and publishing
    ///   to the bus.  Defaults to <see cref="RegexRedactionEngine"/>.
    /// </param>
    /// <param name="spawnPolicy">
    ///   Hard pre-spawn limits.  Defaults to depth ≤ 3, parallel children ≤ 4.
    /// </param>
    public AgentMesh(
        IMessageBus bus,
        IRedactionEngine? redaction = null,
        SpawnPolicy? spawnPolicy = null)
    {
        ArgumentNullException.ThrowIfNull(bus);
        _bus = bus;
        _redaction = redaction ?? new RegexRedactionEngine();
        _spawnPolicy = spawnPolicy ?? new SpawnPolicy();
        _topology = new AgentTopologyTracker();
    }

    // ─── Registration ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Registers <paramref name="session"/> as a root agent (depth 0) and starts its event pump.
    /// </summary>
    public void RegisterRoot(AgentSessionId id, IAgentSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        _topology.RegisterRoot(id, session);
        StartPump(id, session);
    }

    // ─── Spawn ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Validates spawn policy, starts a new child session via <paramref name="adapter"/>,
    /// registers it as a child of <paramref name="parentId"/>, and starts its event pump.
    /// </summary>
    /// <returns>The new child's <see cref="AgentSessionId"/>.</returns>
    /// <exception cref="InvalidOperationException">
    ///   Thrown when <paramref name="parentId"/> is not registered.
    /// </exception>
    /// <exception cref="SpawnPolicyViolatedException">
    ///   Thrown when depth or parallel-child limits would be exceeded.
    /// </exception>
    public async ValueTask<AgentSessionId> SpawnAsync(
        AgentSessionId parentId,
        IAgentAdapter adapter,
        AgentSessionOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(adapter);
        ArgumentNullException.ThrowIfNull(options);

        var parentTopology = _topology.GetTopology(parentId)
            ?? throw new InvalidOperationException(
                $"Cannot spawn: parent session '{parentId}' is not registered in the mesh.");

        // Hard pre-check — throws SpawnPolicyViolatedException if limits exceeded.
        _spawnPolicy.Enforce(parentTopology);

        // Stamp parent id into options so transcript sinks can record lineage.
        var optionsWithParent = options with { ParentSessionId = parentId };
        var session = await adapter.StartSessionAsync(optionsWithParent, cancellationToken).ConfigureAwait(false);
        var childId = session.Id;

        _topology.RegisterChild(parentId, childId, session);

        await _bus.PublishAsync(new MeshMessage(
            Topic: $"agent.{parentId}.spawned",
            Timestamp: DateTimeOffset.UtcNow,
            Kind: "spawned",
            Payload: childId), cancellationToken).ConfigureAwait(false);

        StartPump(childId, session);
        return childId;
    }

    // ─── Pump ──────────────────────────────────────────────────────────────────────

    private void StartPump(AgentSessionId id, IAgentSession session)
    {
        var cts = new CancellationTokenSource();
        _pumps[id] = cts;
        _ = Task.Run(() => PumpAsync(id, session, cts.Token), cts.Token);
    }

    private async Task PumpAsync(AgentSessionId id, IAgentSession session, CancellationToken ct)
    {
        try
        {
            await foreach (var evt in session.Events.WithCancellation(ct).ConfigureAwait(false))
            {
                var (suffix, kind) = MapEvent(evt);

                await _bus.PublishAsync(new MeshMessage(
                    Topic: $"agent.{id}.{suffix}",
                    Timestamp: DateTimeOffset.UtcNow,
                    Kind: kind,
                    Payload: evt), ct).ConfigureAwait(false);

                if (evt is AgentDoneEvent done)
                {
                    await HandleChildDoneAsync(id, done, ct).ConfigureAwait(false);
                    break; // enumerator is exhausted after done
                }
            }
        }
        catch (OperationCanceledException) { /* mesh is shutting down */ }
        catch (Exception ex)
        {
            // Publish a synthetic error event so subscribers can react.
            try
            {
                await _bus.PublishAsync(new MeshMessage(
                    Topic: $"agent.{id}.error",
                    Timestamp: DateTimeOffset.UtcNow,
                    Kind: "error",
                    Payload: new AgentErrorEvent($"Pump fault: {ex.Message}")),
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch { /* best effort */ }
        }
        finally
        {
            _pumps.TryRemove(id, out _);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static (string suffix, string kind) MapEvent(AgentEvent evt) => evt switch
    {
        AgentMessageEvent  => ("message",  "message"),
        ActionRequestEvent => ("tool_use", "tool_use"),
        AgentDoneEvent     => ("done",     "done"),
        AgentErrorEvent    => ("error",    "error"),
        _                  => ("event",    "event"),
    };

    // ─── Auto-merge ────────────────────────────────────────────────────────────────

    private async Task HandleChildDoneAsync(AgentSessionId childId, AgentDoneEvent done, CancellationToken ct)
    {
        var topology = _topology.GetTopology(childId);
        if (topology?.Parent is not { } parentId)
        {
            // Root completed — no merge needed.
            _topology.Deregister(childId);
            return;
        }

        var parent = _topology.GetSession(parentId);
        if (parent is null)
        {
            _topology.Deregister(childId);
            return;
        }

        // Redact before injecting so neither the parent session nor the bus sees raw secrets.
        var rawSummary = done.Summary ?? $"[Subagent {childId} completed with exit code {done.ExitCode}.]";
        var redacted   = _redaction.Redact(rawSummary);

        try
        {
            await parent.SendAsync(new AgentMessage(redacted), ct).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Parent may have already completed; do not propagate.
        }

        await _bus.PublishAsync(new MeshMessage(
            Topic: $"agent.{parentId}.merged",
            Timestamp: DateTimeOffset.UtcNow,
            Kind: "merged",
            Payload: new MergedPayload(childId, redacted, done.ExitCode)), ct).ConfigureAwait(false);

        _topology.Deregister(childId);
    }

    // ─── Disposal ─────────────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        foreach (var (_, cts) in _pumps)
        {
            try { await cts.CancelAsync().ConfigureAwait(false); }
            catch { /* best effort */ }
        }

        // Give pumps a moment to observe cancellation.
        await Task.Delay(50).ConfigureAwait(false);

        foreach (var (_, cts) in _pumps)
        {
            try { cts.Dispose(); }
            catch { /* best effort */ }
        }
        _pumps.Clear();
    }
}
