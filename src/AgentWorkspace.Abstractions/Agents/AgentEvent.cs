using System.Collections.Generic;
using System.Text.Json;

namespace AgentWorkspace.Abstractions.Agents;

/// <summary>Base type for all events emitted by an agent session.</summary>
public abstract record AgentEvent;

/// <summary>A text message from the agent (role = "assistant") or user-echo (role = "user").</summary>
public sealed record AgentMessageEvent(string Role, string Text) : AgentEvent;

/// <summary>The agent proposes a multi-step plan before taking action.</summary>
public sealed record PlanProposedEvent(IReadOnlyList<PlannedAction> Actions) : AgentEvent;

/// <summary>
/// The agent requests execution of a specific action and is waiting for approval or result.
/// <paramref name="Input"/> carries the raw structured payload from the agent's output (e.g.
/// stream-json `tool_use.input`) so downstream policy / approval components can inspect the
/// real arguments. May be null when the source format only provides a name.
/// </summary>
public sealed record ActionRequestEvent(
    string ActionId,
    string Type,
    string Description,
    JsonElement? Input = null) : AgentEvent;

/// <summary>The agent session has completed (successfully or via cancel).</summary>
public sealed record AgentDoneEvent(int ExitCode, string? Summary) : AgentEvent;

/// <summary>The agent process reported an error or terminated unexpectedly.</summary>
public sealed record AgentErrorEvent(string Message) : AgentEvent;

/// <summary>A single step in a proposed plan.</summary>
public sealed record PlannedAction(string Id, string Type, string Description);
