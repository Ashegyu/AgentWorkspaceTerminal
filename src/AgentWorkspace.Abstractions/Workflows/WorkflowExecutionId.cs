using System;
using System.Globalization;

namespace AgentWorkspace.Abstractions.Workflows;

public readonly record struct WorkflowExecutionId(Guid Value)
{
    public static WorkflowExecutionId New() => new(Guid.NewGuid());

    public static WorkflowExecutionId Parse(string s) => new(Guid.Parse(s, CultureInfo.InvariantCulture));

    public override string ToString() => Value.ToString("N", CultureInfo.InvariantCulture);
}
