namespace AgentWorkspace.Abstractions.Workflows;

/// <summary>Outcome of a single workflow execution.</summary>
public abstract record WorkflowResult;

/// <summary>The workflow completed without error.</summary>
public sealed record WorkflowSuccess(string? Summary = null) : WorkflowResult;

/// <summary>The workflow was cancelled before completion.</summary>
public sealed record WorkflowCancelled : WorkflowResult;

/// <summary>The workflow terminated with an error.</summary>
public sealed record WorkflowFailure(string Reason) : WorkflowResult;
