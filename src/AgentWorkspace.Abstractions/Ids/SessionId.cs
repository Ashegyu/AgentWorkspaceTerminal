using System;
using System.Globalization;

namespace AgentWorkspace.Abstractions.Ids;

/// <summary>
/// Strongly-typed identifier for a stored session.
/// </summary>
public readonly record struct SessionId(Guid Value)
{
    public static SessionId New() => new(Guid.NewGuid());

    public static SessionId Parse(string s) => new(Guid.Parse(s, CultureInfo.InvariantCulture));

    public override string ToString() => Value.ToString("N", CultureInfo.InvariantCulture);
}
