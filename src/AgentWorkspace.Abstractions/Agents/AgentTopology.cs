using System;
using System.Collections.Generic;

namespace AgentWorkspace.Abstractions.Agents;

/// <summary>
/// Immutable snapshot of a single agent's position in the spawn tree.
/// Produced and updated by <c>AgentTopologyTracker</c> on register/deregister operations.
/// </summary>
/// <param name="Self">This agent's session identifier.</param>
/// <param name="Parent">
///   Parent session id, or <see langword="null"/> for root agents (depth 0).
/// </param>
/// <param name="Children">
///   Currently live child sessions that were spawned by this agent and have not yet
///   completed. Updated when children are registered or deregistered.
/// </param>
/// <param name="Depth">
///   Distance from the root of the spawn tree (root = 0, direct children = 1, …).
/// </param>
/// <param name="SpawnedAt">UTC time this agent was registered in the mesh.</param>
public sealed record AgentTopology(
    AgentSessionId Self,
    AgentSessionId? Parent,
    IReadOnlySet<AgentSessionId> Children,
    int Depth,
    DateTimeOffset SpawnedAt);
