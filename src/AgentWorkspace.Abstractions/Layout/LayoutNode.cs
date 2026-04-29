using AgentWorkspace.Abstractions.Ids;

namespace AgentWorkspace.Abstractions.Layout;

/// <summary>
/// Immutable node in a binary layout tree. Either a <see cref="PaneNode"/> leaf carrying a
/// <see cref="PaneId"/>, or a <see cref="SplitNode"/> internal node with two children.
/// </summary>
public abstract record LayoutNode(LayoutId Id);

/// <summary>
/// Leaf carrying a single pane.
/// </summary>
public sealed record PaneNode(LayoutId Id, PaneId Pane) : LayoutNode(Id);

/// <summary>
/// Internal node with two children. <paramref name="Ratio"/> is the share allocated to
/// <paramref name="A"/>; the remaining <c>1 - Ratio</c> goes to <paramref name="B"/>. The value
/// is clamped to <c>[0.05, 0.95]</c> to keep both children visible.
/// </summary>
public sealed record SplitNode(
    LayoutId Id,
    SplitDirection Direction,
    double Ratio,
    LayoutNode A,
    LayoutNode B) : LayoutNode(Id);
