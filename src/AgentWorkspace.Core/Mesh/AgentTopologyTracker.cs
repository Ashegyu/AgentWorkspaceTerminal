using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using AgentWorkspace.Abstractions.Agents;

namespace AgentWorkspace.Core.Mesh;

/// <summary>
/// Thread-safe, in-memory registry of the agent spawn tree.
/// <para>
/// Maintains one <see cref="AgentTopology"/> snapshot per registered session and keeps
/// parent/child cross-references consistent when agents are registered or deregistered.
/// </para>
/// <para>
/// <see cref="GetTopology"/> and <see cref="GetSession"/> may be called from any thread at
/// any time without locking.  Mutations (<see cref="RegisterRoot"/>,
/// <see cref="RegisterChild"/>, <see cref="Deregister"/>) are protected by a write lock so
/// that the parent's <c>Children</c> set is updated atomically together with the child entry.
/// </para>
/// </summary>
internal sealed class AgentTopologyTracker
{
    private sealed record Entry(AgentTopology Topology, IAgentSession Session);

    private readonly ConcurrentDictionary<AgentSessionId, Entry> _entries = new();
    private readonly object _writeLock = new();

    /// <summary>
    /// Registers <paramref name="session"/> as a root agent (depth 0, no parent).
    /// Overwrites any existing entry for the same id.
    /// </summary>
    public void RegisterRoot(AgentSessionId id, IAgentSession session)
    {
        ArgumentNullException.ThrowIfNull(session);

        var topology = new AgentTopology(
            Self: id,
            Parent: null,
            Children: new HashSet<AgentSessionId>(),
            Depth: 0,
            SpawnedAt: DateTimeOffset.UtcNow);

        _entries[id] = new Entry(topology, session);
    }

    /// <summary>
    /// Registers <paramref name="session"/> as a child of <paramref name="parentId"/>.
    /// Also updates the parent entry to include <paramref name="childId"/> in its
    /// <see cref="AgentTopology.Children"/> set.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    ///   Thrown when <paramref name="parentId"/> is not registered.
    /// </exception>
    public void RegisterChild(AgentSessionId parentId, AgentSessionId childId, IAgentSession session)
    {
        ArgumentNullException.ThrowIfNull(session);

        lock (_writeLock)
        {
            if (!_entries.TryGetValue(parentId, out var parentEntry))
                throw new InvalidOperationException(
                    $"Cannot register child: parent session '{parentId}' is not registered in the mesh.");

            // Build new parent Children set.
            var newParentChildren = new HashSet<AgentSessionId>(parentEntry.Topology.Children) { childId };
            var newParentTopology = parentEntry.Topology with { Children = newParentChildren };
            _entries[parentId] = parentEntry with { Topology = newParentTopology };

            // Register child.
            var childTopology = new AgentTopology(
                Self: childId,
                Parent: parentId,
                Children: new HashSet<AgentSessionId>(),
                Depth: newParentTopology.Depth + 1,
                SpawnedAt: DateTimeOffset.UtcNow);
            _entries[childId] = new Entry(childTopology, session);
        }
    }

    /// <summary>
    /// Returns the topology snapshot for <paramref name="id"/>, or <see langword="null"/>
    /// if the session is not registered.
    /// </summary>
    public AgentTopology? GetTopology(AgentSessionId id)
        => _entries.TryGetValue(id, out var entry) ? entry.Topology : null;

    /// <summary>
    /// Returns the live <see cref="IAgentSession"/> for <paramref name="id"/>, or
    /// <see langword="null"/> if the session is not registered.
    /// </summary>
    public IAgentSession? GetSession(AgentSessionId id)
        => _entries.TryGetValue(id, out var entry) ? entry.Session : null;

    /// <summary>
    /// Removes <paramref name="id"/> from the registry and removes it from its parent's
    /// <see cref="AgentTopology.Children"/> set.  No-op if <paramref name="id"/> is not
    /// registered.
    /// </summary>
    public void Deregister(AgentSessionId id)
    {
        lock (_writeLock)
        {
            if (!_entries.TryRemove(id, out var entry))
                return;

            // Remove from parent's Children set.
            if (entry.Topology.Parent is { } parentId
                && _entries.TryGetValue(parentId, out var parentEntry))
            {
                var newChildren = new HashSet<AgentSessionId>(parentEntry.Topology.Children);
                newChildren.Remove(id);
                var newParentTopology = parentEntry.Topology with { Children = newChildren };
                _entries[parentId] = parentEntry with { Topology = newParentTopology };
            }
        }
    }
}
