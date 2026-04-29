using System;
using System.Globalization;

namespace AgentWorkspace.Abstractions.Agents;

public readonly record struct AgentSessionId(Guid Value)
{
    public static AgentSessionId New() => new(Guid.NewGuid());

    public static AgentSessionId Parse(string s) => new(Guid.Parse(s, CultureInfo.InvariantCulture));

    public override string ToString() => Value.ToString("N", CultureInfo.InvariantCulture);
}
