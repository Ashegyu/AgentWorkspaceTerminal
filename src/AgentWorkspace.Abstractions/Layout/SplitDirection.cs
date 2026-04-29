namespace AgentWorkspace.Abstractions.Layout;

/// <summary>
/// Orientation of a split. <see cref="Horizontal"/> means the children sit side-by-side;
/// <see cref="Vertical"/> means stacked. The terminology matches xterm-style layouts and
/// CSS flex-direction (horizontal = row, vertical = column).
/// </summary>
public enum SplitDirection
{
    Horizontal = 0,
    Vertical = 1,
}
