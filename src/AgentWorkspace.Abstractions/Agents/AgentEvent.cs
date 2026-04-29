using System.Collections.Generic;

namespace AgentWorkspace.Abstractions.Agents;

/// <summary>Base type for all events emitted by an agent session.</summary>
public abstract record AgentEvent;

/// <summary>A text message from the agent (role = "assistant") or user-echo (role = "user").</summary>
public sealed record AgentMessageEvent(string Role, string Text) : AgentEvent;

/// <summary>The agent proposes a multi-step plan before taking action.</summary>
public sealed record PlanProposedEvent(IReadOnlyList<PlannedAction> Actions) : AgentEvent;

/// <summary>
/// The agent requests execution of a specific action and is waiting for approval or result.
/// </summary>
public sealed record ActionRequestEvent(string ActionId, string Type, string Description) : AgentEvent;

/// <summary>The agent session has completed (successfully or via cancel).</summary>
public sealed record AgentDoneEvent(int ExitCode, string? Summary) : AgentEvent;

/// <summary>The agent process reported an error or terminated unexpectedly.</summary>
public sealed record AgentErrorEvent(string Message) : AgentEvent;

/// <summary>A single step in a proposed plan.</summary>
public sealed record PlannedAction(string Id, string Type, string Description);
