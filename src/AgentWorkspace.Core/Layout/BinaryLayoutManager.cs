using System;
using System.Collections.Generic;
using System.Threading;
using AgentWorkspace.Abstractions.Ids;
using AgentWorkspace.Abstractions.Layout;

namespace AgentWorkspace.Core.Layout;

/// <summary>
/// Thread-safe binary-tree layout manager. All mutators take a private lock and produce a fresh
/// immutable snapshot, so callers can hold onto a snapshot indefinitely without worrying about
/// it changing under them.
/// </summary>
/// <remarks>
/// <para>
/// Tree shape invariants (always true at the boundary of every public method):
/// </para>
/// <list type="bullet">
///   <item><description>Root is non-null.</description></item>
///   <item><description>Every <see cref="SplitNode"/> has two non-null children.</description></item>
///   <item><description>Each <see cref="PaneId"/> appears in exactly one <see cref="PaneNode"/> leaf.</description></item>
///   <item><description><see cref="LayoutSnapshot.Focused"/> always points at an existing leaf.</description></item>
/// </list>
/// </remarks>
public sealed class BinaryLayoutManager : ILayoutManager
{
    private const double MinRatio = 0.05;
    private const double MaxRatio = 0.95;

    private readonly Lock _gate = new();
    private LayoutSnapshot _snapshot;

    /// <summary>
    /// Creates a new manager with a single pane.
    /// </summary>
    public BinaryLayoutManager(PaneId initial)
    {
        var leaf = new PaneNode(LayoutId.New(), initial);
        _snapshot = new LayoutSnapshot(leaf, initial);
    }

    private BinaryLayoutManager(LayoutSnapshot snapshot)
    {
        _snapshot = snapshot;
    }

    /// <summary>
    /// Restores a manager from a previously persisted snapshot. Used by the session store.
    /// </summary>
    public static BinaryLayoutManager FromSnapshot(LayoutSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return new BinaryLayoutManager(snapshot);
    }

    public LayoutSnapshot Current
    {
        get { lock (_gate) return _snapshot; }
    }

    public IReadOnlyList<PaneId> Panes
    {
        get
        {
            LayoutSnapshot snap;
            lock (_gate) snap = _snapshot;
            var list = new List<PaneId>();
            CollectPanes(snap.Root, list);
            return list;
        }
    }

    public SplitResult Split(PaneId target, SplitDirection direction, double ratio = 0.5)
    {
        double clamped = ClampRatio(ratio);
        var newPane = PaneId.New();

        lock (_gate)
        {
            var newRoot = ReplacePane(_snapshot.Root, target, existingLeaf =>
                new SplitNode(
                    LayoutId.New(),
                    direction,
                    clamped,
                    existingLeaf,                              // A keeps the existing pane
                    new PaneNode(LayoutId.New(), newPane)));   // B is the freshly minted pane

            // Focus follows the user's intent: the new pane is what they wanted to create.
            _snapshot = new LayoutSnapshot(newRoot, newPane);
            return new SplitResult(_snapshot, newPane);
        }
    }

    public LayoutSnapshot Close(PaneId target)
    {
        lock (_gate)
        {
            // Closing the only pane is rejected — the workspace must always own at least one PTY.
            if (_snapshot.Root is PaneNode lone)
            {
                if (lone.Pane != target)
                {
                    throw new ArgumentException($"Pane {target} is not in the layout.", nameof(target));
                }
                throw new InvalidOperationException("Cannot close the last remaining pane.");
            }

            var (newRoot, removedFound, survivor) = RemovePane(_snapshot.Root, target);
            if (!removedFound)
            {
                throw new ArgumentException($"Pane {target} is not in the layout.", nameof(target));
            }

            // If focus was on the removed pane, jump to the surviving sibling's first leaf.
            PaneId newFocus = _snapshot.Focused == target
                ? FirstLeaf(survivor!)
                : _snapshot.Focused;

            _snapshot = new LayoutSnapshot(newRoot!, newFocus);
            return _snapshot;
        }
    }

    public LayoutSnapshot SetRatio(LayoutId splitId, double ratio)
    {
        double clamped = ClampRatio(ratio);
        lock (_gate)
        {
            var (newRoot, found) = UpdateSplit(_snapshot.Root, splitId, clamped);
            if (!found)
            {
                throw new ArgumentException($"Split {splitId} is not in the layout.", nameof(splitId));
            }
            _snapshot = new LayoutSnapshot(newRoot!, _snapshot.Focused);
            return _snapshot;
        }
    }

    public LayoutSnapshot Focus(PaneId target)
    {
        lock (_gate)
        {
            if (!Contains(_snapshot.Root, target))
            {
                throw new ArgumentException($"Pane {target} is not in the layout.", nameof(target));
            }
            if (_snapshot.Focused == target) return _snapshot;
            _snapshot = _snapshot with { Focused = target };
            return _snapshot;
        }
    }

    public LayoutSnapshot FocusNext() => CycleFocus(+1);
    public LayoutSnapshot FocusPrevious() => CycleFocus(-1);

    private LayoutSnapshot CycleFocus(int delta)
    {
        lock (_gate)
        {
            var panes = new List<PaneId>();
            CollectPanes(_snapshot.Root, panes);
            int idx = panes.IndexOf(_snapshot.Focused);
            if (idx < 0)
            {
                // Snapshot invariant should have prevented this; recover by focusing the first pane.
                _snapshot = _snapshot with { Focused = panes[0] };
                return _snapshot;
            }
            int next = ((idx + delta) % panes.Count + panes.Count) % panes.Count;
            _snapshot = _snapshot with { Focused = panes[next] };
            return _snapshot;
        }
    }

    // ---- Tree helpers (pure, immutable) -----------------------------------------------------

    private static double ClampRatio(double value) => Math.Max(MinRatio, Math.Min(MaxRatio, value));

    private static void CollectPanes(LayoutNode node, List<PaneId> into)
    {
        switch (node)
        {
            case PaneNode p:
                into.Add(p.Pane);
                break;
            case SplitNode s:
                CollectPanes(s.A, into);
                CollectPanes(s.B, into);
                break;
        }
    }

    private static bool Contains(LayoutNode node, PaneId target) => node switch
    {
        PaneNode p => p.Pane == target,
        SplitNode s => Contains(s.A, target) || Contains(s.B, target),
        _ => false,
    };

    private static PaneId FirstLeaf(LayoutNode node) => node switch
    {
        PaneNode p => p.Pane,
        SplitNode s => FirstLeaf(s.A),
        _ => throw new InvalidOperationException("Unrecognised layout node type."),
    };

    private static LayoutNode ReplacePane(LayoutNode root, PaneId target, Func<PaneNode, LayoutNode> replacement)
    {
        switch (root)
        {
            case PaneNode p when p.Pane == target:
                return replacement(p);
            case PaneNode:
                return root;
            case SplitNode s:
                var newA = ReplacePane(s.A, target, replacement);
                var newB = newA == s.A ? ReplacePane(s.B, target, replacement) : s.B;
                if (newA == s.A && newB == s.B) return s;
                return s with { A = newA, B = newB };
            default:
                throw new InvalidOperationException("Unrecognised layout node type.");
        }
    }

    private static (LayoutNode? NewNode, bool Removed, LayoutNode? Survivor) RemovePane(LayoutNode root, PaneId target)
    {
        switch (root)
        {
            case PaneNode p when p.Pane == target:
                // Removed but cannot be replaced by anything at this level. Caller decides.
                return (null, true, null);
            case PaneNode:
                return (root, false, null);
            case SplitNode s:
                if (s.A is PaneNode pa && pa.Pane == target)
                {
                    // Sibling B promotes into the parent's slot.
                    return (s.B, true, s.B);
                }
                if (s.B is PaneNode pb && pb.Pane == target)
                {
                    return (s.A, true, s.A);
                }
                var (newA, removedInA, survA) = RemovePane(s.A, target);
                if (removedInA)
                {
                    return (s with { A = newA! }, true, survA);
                }
                var (newB, removedInB, survB) = RemovePane(s.B, target);
                if (removedInB)
                {
                    return (s with { B = newB! }, true, survB);
                }
                return (s, false, null);
            default:
                throw new InvalidOperationException("Unrecognised layout node type.");
        }
    }

    private static (LayoutNode? NewNode, bool Found) UpdateSplit(LayoutNode root, LayoutId splitId, double ratio)
    {
        switch (root)
        {
            case PaneNode:
                return (root, false);
            case SplitNode s when s.Id == splitId:
                return (s with { Ratio = ratio }, true);
            case SplitNode s:
                var (newA, foundA) = UpdateSplit(s.A, splitId, ratio);
                if (foundA) return (s with { A = newA! }, true);
                var (newB, foundB) = UpdateSplit(s.B, splitId, ratio);
                if (foundB) return (s with { B = newB! }, true);
                return (s, false);
            default:
                throw new InvalidOperationException("Unrecognised layout node type.");
        }
    }
}
