using System;
using System.Globalization;

namespace AgentWorkspace.Abstractions.Ids;

/// <summary>
/// Identifies a node (split or pane container) inside a layout tree.
/// Distinct from <see cref="PaneId"/> — a <see cref="PaneId"/> is the *PTY* identity, while a
/// <see cref="LayoutId"/> is the *position* inside the tree. They happen to coincide for leaf
/// pane nodes by convention but conceptually they are independent.
/// </summary>
public readonly record struct LayoutId(Guid Value)
{
    public static LayoutId New() => new(Guid.NewGuid());

    public static LayoutId Parse(string s) => new(Guid.Parse(s, CultureInfo.InvariantCulture));

    public override string ToString() => Value.ToString("N", CultureInfo.InvariantCulture);
}
