using System;
using System.Globalization;

namespace AgentWorkspace.Abstractions.Ids;

/// <summary>
/// Strongly-typed identifier for a pane (terminal instance).
/// Backed by Guid; equality and hashing inherited via record struct.
/// </summary>
public readonly record struct PaneId(Guid Value)
{
    public static PaneId New() => new(Guid.NewGuid());

    public static PaneId Parse(string s) => new(Guid.Parse(s, CultureInfo.InvariantCulture));

    public override string ToString() => Value.ToString("N", CultureInfo.InvariantCulture);
}
