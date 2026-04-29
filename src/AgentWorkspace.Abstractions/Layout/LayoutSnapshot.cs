using AgentWorkspace.Abstractions.Ids;

namespace AgentWorkspace.Abstractions.Layout;

/// <summary>
/// Immutable view of the layout tree plus the currently focused pane.
/// </summary>
/// <param name="Root">Top-level node of the tree (always non-null while the workspace lives).</param>
/// <param name="Focused">PaneId that has keyboard focus. Always points at a leaf in the tree.</param>
public sealed record LayoutSnapshot(LayoutNode Root, PaneId Focused);
