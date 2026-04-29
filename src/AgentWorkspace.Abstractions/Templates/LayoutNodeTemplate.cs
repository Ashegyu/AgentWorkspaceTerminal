using AgentWorkspace.Abstractions.Layout;

namespace AgentWorkspace.Abstractions.Templates;

/// <summary>
/// Node in a template layout tree. Either a <see cref="PaneRefTemplate"/> leaf or a
/// <see cref="SplitNodeTemplate"/> internal node.
/// </summary>
public abstract record LayoutNodeTemplate;

/// <summary>
/// Leaf node — holds the symbolic slot name (the <c>id</c> field of a
/// <see cref="PaneTemplate"/>, NOT a runtime <c>PaneId</c>).
/// </summary>
public sealed record PaneRefTemplate(string Slot) : LayoutNodeTemplate;

/// <summary>
/// Internal node — divides space between <see cref="A"/> and <see cref="B"/>.
/// <see cref="Ratio"/> is the fraction allocated to <see cref="A"/>; clamped to [0.05, 0.95].
/// </summary>
public sealed record SplitNodeTemplate(
    SplitDirection Direction,
    double Ratio,
    LayoutNodeTemplate A,
    LayoutNodeTemplate B) : LayoutNodeTemplate;
