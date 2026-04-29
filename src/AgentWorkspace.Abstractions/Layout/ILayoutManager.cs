using System.Collections.Generic;
using AgentWorkspace.Abstractions.Ids;

namespace AgentWorkspace.Abstractions.Layout;

/// <summary>
/// Result of <see cref="ILayoutManager.Split"/>. The new pane id is what the host passes to
/// the session manager to spawn a new <c>PseudoConsoleProcess</c>.
/// </summary>
public sealed record SplitResult(LayoutSnapshot Snapshot, PaneId NewPane);

/// <summary>
/// Manages a binary layout tree. Implementations are required to be thread-safe; mutators
/// return a fresh <see cref="LayoutSnapshot"/> rather than exposing the live tree.
/// </summary>
public interface ILayoutManager
{
    /// <summary>Current snapshot.</summary>
    LayoutSnapshot Current { get; }

    /// <summary>
    /// Replaces <paramref name="target"/>'s pane node with a split, putting <paramref name="target"/>
    /// on the A side and a freshly minted <see cref="PaneId"/> on the B side.
    /// </summary>
    /// <param name="target">Pane to split.</param>
    /// <param name="direction">Horizontal places the new pane to the right; vertical places it below.</param>
    /// <param name="ratio">Share for the existing pane. Clamped to [0.05, 0.95].</param>
    /// <exception cref="System.ArgumentException">Thrown if <paramref name="target"/> is not in the tree.</exception>
    SplitResult Split(PaneId target, SplitDirection direction, double ratio = 0.5);

    /// <summary>
    /// Removes <paramref name="target"/>. The sibling is promoted into the parent's slot. Closing
    /// the only remaining pane is rejected.
    /// </summary>
    /// <exception cref="System.InvalidOperationException">Thrown if attempting to close the last pane.</exception>
    /// <exception cref="System.ArgumentException">Thrown if <paramref name="target"/> is not in the tree.</exception>
    LayoutSnapshot Close(PaneId target);

    /// <summary>
    /// Updates the ratio on a split node, clamped to [0.05, 0.95].
    /// </summary>
    LayoutSnapshot SetRatio(LayoutId splitId, double ratio);

    /// <summary>
    /// Sets the focused pane to <paramref name="target"/>.
    /// </summary>
    LayoutSnapshot Focus(PaneId target);

    /// <summary>
    /// Cycles focus to the next pane in left-to-right depth-first order. Wraps around at the end.
    /// </summary>
    LayoutSnapshot FocusNext();

    /// <summary>
    /// Cycles focus to the previous pane. Wraps around at the start.
    /// </summary>
    LayoutSnapshot FocusPrevious();

    /// <summary>
    /// Enumerates all panes in the tree in left-to-right order. Useful for tests and for hosts
    /// that need to drive each pane (e.g. resize broadcast).
    /// </summary>
    IReadOnlyList<PaneId> Panes { get; }
}
