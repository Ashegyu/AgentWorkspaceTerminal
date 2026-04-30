namespace AgentWorkspace.Abstractions.Workflows;

/// <summary>
/// Sealed discriminated union — every trigger variant that WorkflowEngine understands.
/// Not an event bus: callers construct a concrete trigger and pass it directly.
/// </summary>
public abstract record WorkflowTrigger;

/// <summary>Triggered by the user directly (e.g., Command Palette).</summary>
public sealed record ManualTrigger(string WorkflowName, string? Argument = null) : WorkflowTrigger;

/// <summary>Triggered when a dotnet test run reports failures.</summary>
public sealed record TestFailedTrigger(string ProjectPath, string LogText) : WorkflowTrigger;

/// <summary>Triggered when a dotnet build reports errors.</summary>
public sealed record BuildFailedTrigger(string ProjectPath, string LogText) : WorkflowTrigger;

/// <summary>Triggered when the user detaches from a terminal session.</summary>
public sealed record SessionDetachedTrigger(string SessionId, string TranscriptPath) : WorkflowTrigger;
