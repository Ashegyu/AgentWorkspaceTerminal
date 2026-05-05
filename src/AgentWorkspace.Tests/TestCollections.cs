using Xunit;

namespace AgentWorkspace.Tests;

/// <summary>
/// xUnit collection that serialises test classes which contend for OS-level resources
/// (ConPTY pseudo-console handles, named pipes, daemon RPC sockets). Running these
/// in parallel under the default xUnit collection-per-class behaviour exhausts the
/// system's pseudo-console pool / pipe-server slots on busy CI machines and causes
/// 50-second timeouts in otherwise-passing tests.
/// <para>
/// Membership is opt-in via <c>[Collection(OsResourcesCollection.Name)]</c> on the test
/// class. Tests outside this collection still run in parallel as before.
/// </para>
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class OsResourcesCollection
{
    public const string Name = "OsResources";
}

/// <summary>
/// xUnit collection that serialises in-memory bus / mesh test classes. These don't
/// consume OS handles but are sensitive to dispatcher contention under heavy parallel
/// load (TaskCompletionSource awaits with bounded timeouts). Kept separate from
/// <see cref="OsResourcesCollection"/> so the two groups can run in parallel with each
/// other while each group is internally serial.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class InMemoryBusCollection
{
    public const string Name = "InMemoryBus";
}
