using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using AgentWorkspace.Abstractions.Agents;
using AgentWorkspace.Core.Mesh;

namespace AgentWorkspace.Tests.Mesh;

/// <summary>
/// Unit tests for <see cref="AgentTopologyTracker"/>: root registration, child registration,
/// deregistration, and null-return behaviour for unknown ids.
/// </summary>
public sealed class AgentTopologyTrackerTests
{
    // ── helpers ───────────────────────────────────────────────────────────────

    private static IAgentSession StubSession() => new NoOpSession();

    private sealed class NoOpSession : IAgentSession
    {
        public AgentSessionId Id { get; } = AgentSessionId.New();

        public IAsyncEnumerable<AgentEvent> Events => EmptyStream();

        private static async IAsyncEnumerable<AgentEvent> EmptyStream(
            [EnumeratorCancellation] CancellationToken _ = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public ValueTask SendAsync(AgentMessage msg, CancellationToken ct) =>
            ValueTask.CompletedTask;

        public ValueTask CancelAsync(CancellationToken ct) =>
            ValueTask.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    // ── RegisterRoot ──────────────────────────────────────────────────────────

    [Fact]
    public void RegisterRoot_CreatesDepthZeroTopologyWithNoParent()
    {
        var tracker = new AgentTopologyTracker();
        var id      = AgentSessionId.New();

        tracker.RegisterRoot(id, StubSession());

        var topo = tracker.GetTopology(id);

        Assert.NotNull(topo);
        Assert.Equal(id, topo.Self);
        Assert.Null(topo.Parent);
        Assert.Equal(0, topo.Depth);
        Assert.Empty(topo.Children);
    }

    [Fact]
    public void RegisterRoot_StoresSessionReturnableViaGetSession()
    {
        var tracker = new AgentTopologyTracker();
        var id      = AgentSessionId.New();
        var session = StubSession();

        tracker.RegisterRoot(id, session);

        Assert.Same(session, tracker.GetSession(id));
    }

    // ── RegisterChild ─────────────────────────────────────────────────────────

    [Fact]
    public void RegisterChild_ChildHasParentDepthPlusOne()
    {
        var tracker  = new AgentTopologyTracker();
        var parentId = AgentSessionId.New();
        var childId  = AgentSessionId.New();

        tracker.RegisterRoot(parentId, StubSession());
        tracker.RegisterChild(parentId, childId, StubSession());

        var childTopo = tracker.GetTopology(childId);

        Assert.NotNull(childTopo);
        Assert.Equal(childId, childTopo.Self);
        Assert.Equal(parentId, childTopo.Parent);
        Assert.Equal(1, childTopo.Depth);
        Assert.Empty(childTopo.Children);
    }

    [Fact]
    public void RegisterChild_ParentChildrenSetContainsChild()
    {
        var tracker  = new AgentTopologyTracker();
        var parentId = AgentSessionId.New();
        var childId  = AgentSessionId.New();

        tracker.RegisterRoot(parentId, StubSession());
        tracker.RegisterChild(parentId, childId, StubSession());

        var parentTopo = tracker.GetTopology(parentId);

        Assert.NotNull(parentTopo);
        Assert.Contains(childId, parentTopo.Children);
    }

    [Fact]
    public void RegisterChild_MultipleChildren_AllPresentInParentChildrenSet()
    {
        var tracker  = new AgentTopologyTracker();
        var parentId = AgentSessionId.New();
        var child1   = AgentSessionId.New();
        var child2   = AgentSessionId.New();

        tracker.RegisterRoot(parentId, StubSession());
        tracker.RegisterChild(parentId, child1, StubSession());
        tracker.RegisterChild(parentId, child2, StubSession());

        var parentTopo = tracker.GetTopology(parentId);

        Assert.NotNull(parentTopo);
        Assert.Equal(2, parentTopo.Children.Count);
        Assert.Contains(child1, parentTopo.Children);
        Assert.Contains(child2, parentTopo.Children);
    }

    [Fact]
    public void RegisterChild_GrandchildHasDepthTwo()
    {
        var tracker      = new AgentTopologyTracker();
        var rootId       = AgentSessionId.New();
        var childId      = AgentSessionId.New();
        var grandChildId = AgentSessionId.New();

        tracker.RegisterRoot(rootId, StubSession());
        tracker.RegisterChild(rootId, childId, StubSession());
        tracker.RegisterChild(childId, grandChildId, StubSession());

        var grandChildTopo = tracker.GetTopology(grandChildId);

        Assert.NotNull(grandChildTopo);
        Assert.Equal(2, grandChildTopo.Depth);
        Assert.Equal(childId, grandChildTopo.Parent);
    }

    [Fact]
    public void RegisterChild_UnknownParent_ThrowsInvalidOperationException()
    {
        var tracker  = new AgentTopologyTracker();
        var parentId = AgentSessionId.New();
        var childId  = AgentSessionId.New();

        Assert.Throws<InvalidOperationException>(
            () => tracker.RegisterChild(parentId, childId, StubSession()));
    }

    // ── Deregister ────────────────────────────────────────────────────────────

    [Fact]
    public void Deregister_RemovesChildFromRegistry()
    {
        var tracker  = new AgentTopologyTracker();
        var parentId = AgentSessionId.New();
        var childId  = AgentSessionId.New();

        tracker.RegisterRoot(parentId, StubSession());
        tracker.RegisterChild(parentId, childId, StubSession());
        tracker.Deregister(childId);

        Assert.Null(tracker.GetTopology(childId));
        Assert.Null(tracker.GetSession(childId));
    }

    [Fact]
    public void Deregister_RemovesChildFromParentChildrenSet()
    {
        var tracker  = new AgentTopologyTracker();
        var parentId = AgentSessionId.New();
        var childId  = AgentSessionId.New();

        tracker.RegisterRoot(parentId, StubSession());
        tracker.RegisterChild(parentId, childId, StubSession());
        tracker.Deregister(childId);

        var parentTopo = tracker.GetTopology(parentId);

        Assert.NotNull(parentTopo);
        Assert.DoesNotContain(childId, parentTopo.Children);
    }

    [Fact]
    public void Deregister_Root_RemovesRootFromRegistry()
    {
        var tracker = new AgentTopologyTracker();
        var rootId  = AgentSessionId.New();

        tracker.RegisterRoot(rootId, StubSession());
        tracker.Deregister(rootId);

        Assert.Null(tracker.GetTopology(rootId));
    }

    [Fact]
    public void Deregister_UnknownId_IsNoOp()
    {
        var tracker = new AgentTopologyTracker();
        var unknown = AgentSessionId.New();

        // Should not throw.
        tracker.Deregister(unknown);
    }

    // ── Null returns for unknown ids ──────────────────────────────────────────

    [Fact]
    public void GetTopology_UnknownId_ReturnsNull()
    {
        var tracker = new AgentTopologyTracker();
        Assert.Null(tracker.GetTopology(AgentSessionId.New()));
    }

    [Fact]
    public void GetSession_UnknownId_ReturnsNull()
    {
        var tracker = new AgentTopologyTracker();
        Assert.Null(tracker.GetSession(AgentSessionId.New()));
    }
}
