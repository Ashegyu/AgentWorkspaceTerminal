using System;

namespace AgentWorkspace.Core.Mesh;

/// <summary>Identifies which hard limit was exceeded when spawning a sub-agent.</summary>
public enum SpawnViolationKind
{
    /// <summary>The requested spawn would exceed the maximum allowed tree depth.</summary>
    MaxDepth,

    /// <summary>The parent already has the maximum number of live parallel children.</summary>
    MaxParallelChildren,
}

/// <summary>
/// Thrown by <see cref="SpawnPolicy.Enforce"/> when a hard spawn limit is exceeded.
/// This is a pre-check that fires before any adapter call is made, so no session is
/// created and no resource is consumed.
/// </summary>
public sealed class SpawnPolicyViolatedException : Exception
{
    /// <summary>Which limit was exceeded.</summary>
    public SpawnViolationKind Kind { get; }

    /// <summary>The limit value that was exceeded.</summary>
    public int Limit { get; }

    /// <summary>The actual value that caused the violation.</summary>
    public int Actual { get; }

    public SpawnPolicyViolatedException(SpawnViolationKind kind, int limit, int actual, string message)
        : base(message)
    {
        Kind = kind;
        Limit = limit;
        Actual = actual;
    }
}
