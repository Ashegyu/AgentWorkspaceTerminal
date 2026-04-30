using AgentWorkspace.Abstractions.Agents;

namespace AgentWorkspace.Core.Mesh;

/// <summary>
/// Enforces hard pre-spawn limits before any <see cref="IAgentAdapter"/> call is made.
/// Violations throw <see cref="SpawnPolicyViolatedException"/> immediately; they are NOT
/// routed through <c>IApprovalGateway</c> (which is reserved for user-visible decisions).
/// </summary>
public sealed class SpawnPolicy
{
    /// <summary>Maximum allowed depth in the spawn tree (root = 0). Default: 3.</summary>
    public int MaxDepth { get; }

    /// <summary>Maximum number of live parallel children per parent. Default: 4.</summary>
    public int MaxParallelChildren { get; }

    public SpawnPolicy(int maxDepth = 3, int maxParallelChildren = 4)
    {
        MaxDepth = maxDepth;
        MaxParallelChildren = maxParallelChildren;
    }

    /// <summary>
    /// Checks whether a new child can be spawned under <paramref name="parentTopology"/>.
    /// </summary>
    /// <param name="parentTopology">Topology snapshot of the prospective parent.</param>
    /// <exception cref="SpawnPolicyViolatedException">
    ///   Thrown when the spawn would exceed <see cref="MaxDepth"/> or
    ///   <see cref="MaxParallelChildren"/>.
    /// </exception>
    public void Enforce(AgentTopology parentTopology)
    {
        var childDepth = parentTopology.Depth + 1;
        if (childDepth > MaxDepth)
        {
            throw new SpawnPolicyViolatedException(
                SpawnViolationKind.MaxDepth,
                limit: MaxDepth,
                actual: childDepth,
                message: $"Cannot spawn at depth {childDepth}: maximum allowed depth is {MaxDepth}.");
        }

        var currentChildren = parentTopology.Children.Count;
        if (currentChildren >= MaxParallelChildren)
        {
            throw new SpawnPolicyViolatedException(
                SpawnViolationKind.MaxParallelChildren,
                limit: MaxParallelChildren,
                actual: currentChildren,
                message: $"Cannot spawn: parent '{parentTopology.Self}' already has "
                         + $"{currentChildren} live children (limit: {MaxParallelChildren}).");
        }
    }
}
