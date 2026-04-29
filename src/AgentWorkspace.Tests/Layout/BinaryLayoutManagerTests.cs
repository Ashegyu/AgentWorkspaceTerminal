using System;
using System.Collections.Generic;
using System.Linq;
using AgentWorkspace.Abstractions.Ids;
using AgentWorkspace.Abstractions.Layout;
using AgentWorkspace.Core.Layout;

namespace AgentWorkspace.Tests.Layout;

/// <summary>
/// Pure-domain tests for the layout tree. No PTY, no UI — these enforce the invariants
/// described on <see cref="BinaryLayoutManager"/>.
/// </summary>
public sealed class BinaryLayoutManagerTests
{
    [Fact]
    public void Initial_HasSinglePaneAndItIsFocused()
    {
        var p0 = PaneId.New();
        var mgr = new BinaryLayoutManager(p0);

        var snap = mgr.Current;
        Assert.IsType<PaneNode>(snap.Root);
        Assert.Equal(p0, ((PaneNode)snap.Root).Pane);
        Assert.Equal(p0, snap.Focused);
        Assert.Equal(new[] { p0 }, mgr.Panes);
    }

    [Fact]
    public void Split_PutsExistingOnAAndNewOnB_AndFocusesNew()
    {
        var p0 = PaneId.New();
        var mgr = new BinaryLayoutManager(p0);

        var result = mgr.Split(p0, SplitDirection.Horizontal, 0.5);

        var split = Assert.IsType<SplitNode>(result.Snapshot.Root);
        Assert.Equal(SplitDirection.Horizontal, split.Direction);
        Assert.Equal(0.5, split.Ratio);
        Assert.Equal(p0, ((PaneNode)split.A).Pane);
        Assert.Equal(result.NewPane, ((PaneNode)split.B).Pane);
        Assert.Equal(result.NewPane, result.Snapshot.Focused);

        // Panes enumerator now reports both, A first.
        Assert.Equal(new[] { p0, result.NewPane }, mgr.Panes);
    }

    [Fact]
    public void Split_Twice_BuildsBalancedTree_AllPanesReachable()
    {
        var p0 = PaneId.New();
        var mgr = new BinaryLayoutManager(p0);

        var first = mgr.Split(p0, SplitDirection.Horizontal);
        var second = mgr.Split(p0, SplitDirection.Vertical);

        // Tree should now be: SplitH(SplitV(p0, second.NewPane), first.NewPane)
        var rootSplit = Assert.IsType<SplitNode>(second.Snapshot.Root);
        Assert.Equal(SplitDirection.Horizontal, rootSplit.Direction);
        Assert.IsType<SplitNode>(rootSplit.A);
        Assert.IsType<PaneNode>(rootSplit.B);
        Assert.Equal(first.NewPane, ((PaneNode)rootSplit.B).Pane);

        var inner = (SplitNode)rootSplit.A;
        Assert.Equal(SplitDirection.Vertical, inner.Direction);
        Assert.Equal(p0, ((PaneNode)inner.A).Pane);
        Assert.Equal(second.NewPane, ((PaneNode)inner.B).Pane);

        Assert.Equal(3, mgr.Panes.Count);
        Assert.Contains(p0, mgr.Panes);
        Assert.Contains(first.NewPane, mgr.Panes);
        Assert.Contains(second.NewPane, mgr.Panes);
    }

    [Theory]
    [InlineData(-1.0, 0.05)]
    [InlineData(0.0, 0.05)]
    [InlineData(0.04, 0.05)]
    [InlineData(0.5, 0.5)]
    [InlineData(0.96, 0.95)]
    [InlineData(2.0, 0.95)]
    public void Split_RatioIsClampedTo_5_to_95_Percent(double given, double expected)
    {
        var p0 = PaneId.New();
        var mgr = new BinaryLayoutManager(p0);

        var split = (SplitNode)mgr.Split(p0, SplitDirection.Horizontal, given).Snapshot.Root;

        Assert.Equal(expected, split.Ratio, 5);
    }

    [Fact]
    public void Close_OnlyPane_Throws()
    {
        var p0 = PaneId.New();
        var mgr = new BinaryLayoutManager(p0);

        Assert.Throws<InvalidOperationException>(() => mgr.Close(p0));
    }

    [Fact]
    public void Close_UnknownPane_Throws()
    {
        var mgr = new BinaryLayoutManager(PaneId.New());
        Assert.Throws<ArgumentException>(() => mgr.Close(PaneId.New()));
    }

    [Fact]
    public void Close_SiblingPromotesIntoParentSlot()
    {
        var p0 = PaneId.New();
        var mgr = new BinaryLayoutManager(p0);
        var split = mgr.Split(p0, SplitDirection.Horizontal);

        // Tree: SplitH(p0, split.NewPane). Closing p0 should leave tree = PaneNode(split.NewPane).
        var snap = mgr.Close(p0);

        Assert.IsType<PaneNode>(snap.Root);
        Assert.Equal(split.NewPane, ((PaneNode)snap.Root).Pane);
        Assert.Equal(split.NewPane, snap.Focused);
    }

    [Fact]
    public void Close_FocusJumpsToFirstLeafOfSurvivor()
    {
        var p0 = PaneId.New();
        var mgr = new BinaryLayoutManager(p0);
        var s1 = mgr.Split(p0, SplitDirection.Horizontal);     // tree: H(p0, s1)
        var s2 = mgr.Split(s1.NewPane, SplitDirection.Vertical); // tree: H(p0, V(s1, s2)), focus=s2

        // Close the focused pane (s2). Sibling s1 should be promoted; focus should land on it.
        var snap = mgr.Close(s2.NewPane);

        // Tree now: H(p0, s1)
        var root = Assert.IsType<SplitNode>(snap.Root);
        Assert.Equal(p0, ((PaneNode)root.A).Pane);
        Assert.Equal(s1.NewPane, ((PaneNode)root.B).Pane);
        Assert.Equal(s1.NewPane, snap.Focused);
    }

    [Fact]
    public void Close_NestedTarget_PreservesRestOfTree()
    {
        var p0 = PaneId.New();
        var mgr = new BinaryLayoutManager(p0);
        var s1 = mgr.Split(p0, SplitDirection.Horizontal);     // H(p0, s1)
        var s2 = mgr.Split(p0, SplitDirection.Vertical);       // H(V(p0, s2), s1)

        // Close p0; V should collapse to its sibling s2.
        mgr.Close(p0);

        // Expected: H(s2, s1)
        var root = Assert.IsType<SplitNode>(mgr.Current.Root);
        Assert.Equal(SplitDirection.Horizontal, root.Direction);
        Assert.Equal(s2.NewPane, ((PaneNode)root.A).Pane);
        Assert.Equal(s1.NewPane, ((PaneNode)root.B).Pane);
        Assert.Equal(2, mgr.Panes.Count);
    }

    [Fact]
    public void SetRatio_UpdatesOnlyTheTargetSplit()
    {
        var p0 = PaneId.New();
        var mgr = new BinaryLayoutManager(p0);
        var s1 = mgr.Split(p0, SplitDirection.Horizontal, 0.4);
        var s2 = mgr.Split(s1.NewPane, SplitDirection.Vertical, 0.7);

        var rootBefore = (SplitNode)mgr.Current.Root;
        var innerBefore = (SplitNode)rootBefore.B;

        // Update inner split.
        mgr.SetRatio(innerBefore.Id, 0.3);

        var rootAfter = (SplitNode)mgr.Current.Root;
        var innerAfter = (SplitNode)rootAfter.B;
        Assert.Equal(0.4, rootAfter.Ratio, 5);   // outer untouched
        Assert.Equal(0.3, innerAfter.Ratio, 5);  // inner updated
    }

    [Fact]
    public void SetRatio_UnknownSplit_Throws()
    {
        var mgr = new BinaryLayoutManager(PaneId.New());
        Assert.Throws<ArgumentException>(() => mgr.SetRatio(LayoutId.New(), 0.5));
    }

    [Fact]
    public void Focus_SetsToTargetIfPresent()
    {
        var p0 = PaneId.New();
        var mgr = new BinaryLayoutManager(p0);
        var s1 = mgr.Split(p0, SplitDirection.Horizontal);     // focus is now s1.NewPane

        var snap = mgr.Focus(p0);
        Assert.Equal(p0, snap.Focused);
    }

    [Fact]
    public void Focus_UnknownPane_Throws()
    {
        var mgr = new BinaryLayoutManager(PaneId.New());
        Assert.Throws<ArgumentException>(() => mgr.Focus(PaneId.New()));
    }

    [Fact]
    public void FocusNext_CyclesLeftToRight_AndWraps()
    {
        var p0 = PaneId.New();
        var mgr = new BinaryLayoutManager(p0);
        var s1 = mgr.Split(p0, SplitDirection.Horizontal);  // H(p0, s1), focus=s1
        var s2 = mgr.Split(p0, SplitDirection.Vertical);    // H(V(p0, s2), s1), focus=s2

        // pane order DFS-LR: [p0, s2, s1]
        Assert.Equal(s2.NewPane, mgr.Current.Focused);

        Assert.Equal(s1.NewPane, mgr.FocusNext().Focused);
        Assert.Equal(p0,         mgr.FocusNext().Focused);   // wraps to first
        Assert.Equal(s2.NewPane, mgr.FocusNext().Focused);
    }

    [Fact]
    public void FocusPrevious_CyclesBackwards_AndWraps()
    {
        var p0 = PaneId.New();
        var mgr = new BinaryLayoutManager(p0);
        var s1 = mgr.Split(p0, SplitDirection.Horizontal);
        var s2 = mgr.Split(p0, SplitDirection.Vertical);
        // order: [p0, s2, s1], focus=s2

        Assert.Equal(p0,         mgr.FocusPrevious().Focused);
        Assert.Equal(s1.NewPane, mgr.FocusPrevious().Focused); // wraps to last
        Assert.Equal(s2.NewPane, mgr.FocusPrevious().Focused);
    }

    [Fact]
    public void Snapshot_IsImmutable_AcrossMutations()
    {
        var p0 = PaneId.New();
        var mgr = new BinaryLayoutManager(p0);
        var before = mgr.Current;

        mgr.Split(p0, SplitDirection.Horizontal);

        // The snapshot the caller already held must not have changed.
        Assert.IsType<PaneNode>(before.Root);
        Assert.Equal(p0, ((PaneNode)before.Root).Pane);
        Assert.Equal(p0, before.Focused);
    }

    [Fact]
    public void Random_SequenceOfOperations_KeepsInvariants()
    {
        // Property-style smoke test: 200 random ops never break invariants.
        var rng = new Random(unchecked((int)0xBA5EBA11));
        var p0 = PaneId.New();
        var mgr = new BinaryLayoutManager(p0);

        for (int i = 0; i < 200; i++)
        {
            var panes = mgr.Panes;
            int op = rng.Next(0, panes.Count > 1 ? 4 : 3); // forbid Close when only one pane
            switch (op)
            {
                case 0: // split a random pane
                    var target = panes[rng.Next(panes.Count)];
                    var dir = rng.Next(2) == 0 ? SplitDirection.Horizontal : SplitDirection.Vertical;
                    mgr.Split(target, dir, rng.NextDouble());
                    break;
                case 1: // focus a random pane
                    mgr.Focus(panes[rng.Next(panes.Count)]);
                    break;
                case 2: // cycle focus
                    if (rng.Next(2) == 0) mgr.FocusNext(); else mgr.FocusPrevious();
                    break;
                case 3: // close a random pane (never the only one — guarded above)
                    mgr.Close(panes[rng.Next(panes.Count)]);
                    break;
            }

            AssertInvariants(mgr);
        }
    }

    private static void AssertInvariants(ILayoutManager mgr)
    {
        var snap = mgr.Current;
        Assert.NotNull(snap.Root);
        var seen = new HashSet<PaneId>();
        WalkAndCheck(snap.Root, seen);
        Assert.Contains(snap.Focused, seen);
        Assert.Equal(seen.Count, mgr.Panes.Count);
        // Panes enumerator order is deterministic and matches DFS-LR.
        Assert.Equal(seen.OrderBy(p => p.Value).ToArray(), mgr.Panes.OrderBy(p => p.Value).ToArray());
    }

    private static void WalkAndCheck(LayoutNode n, HashSet<PaneId> seen)
    {
        switch (n)
        {
            case PaneNode p:
                Assert.True(seen.Add(p.Pane), $"Duplicate pane {p.Pane}");
                break;
            case SplitNode s:
                Assert.NotNull(s.A);
                Assert.NotNull(s.B);
                Assert.InRange(s.Ratio, 0.05, 0.95);
                WalkAndCheck(s.A, seen);
                WalkAndCheck(s.B, seen);
                break;
            default:
                Assert.Fail("Unrecognised layout node type.");
                break;
        }
    }
}
