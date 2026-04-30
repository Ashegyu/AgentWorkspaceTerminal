using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace AgentWorkspace.Client.Wire;

/// <summary>
/// JSON envelope around every RPC request. <see cref="Method"/> selects the handler on the
/// daemon side; <see cref="Params"/> carries the method-specific payload (rendered as raw JSON
/// so the deserialiser can pick the right concrete type).
/// </summary>
public sealed record RpcRequest(
    [property: JsonPropertyName("method")] string Method,
    [property: JsonPropertyName("params")] System.Text.Json.JsonElement Params);

/// <summary>
/// JSON envelope around every RPC response. Exactly one of <see cref="Result"/> / <see cref="Error"/>
/// is non-null.
/// </summary>
public sealed record RpcResponse(
    [property: JsonPropertyName("result")] System.Text.Json.JsonElement? Result,
    [property: JsonPropertyName("error")] RpcError? Error);

public sealed record RpcError(
    [property: JsonPropertyName("code")] int Code,
    [property: JsonPropertyName("message")] string Message);

// -- Pane RPC params + results -------------------------------------------------------------

public sealed record StartPaneRequest(
    [property: JsonPropertyName("paneId")] string PaneId,
    [property: JsonPropertyName("command")] string Command,
    [property: JsonPropertyName("arguments")] IReadOnlyList<string> Arguments,
    [property: JsonPropertyName("workingDirectory")] string? WorkingDirectory,
    [property: JsonPropertyName("environment")] IReadOnlyDictionary<string, string>? Environment,
    [property: JsonPropertyName("cols")] short Cols,
    [property: JsonPropertyName("rows")] short Rows);

public sealed record StartPaneResult(
    [property: JsonPropertyName("state")] string State);

public sealed record WriteInputRequest(
    [property: JsonPropertyName("paneId")] string PaneId,
    [property: JsonPropertyName("bytesB64")] string BytesBase64);

public sealed record ResizePaneRequest(
    [property: JsonPropertyName("paneId")] string PaneId,
    [property: JsonPropertyName("cols")] short Cols,
    [property: JsonPropertyName("rows")] short Rows);

public sealed record SignalPaneRequest(
    [property: JsonPropertyName("paneId")] string PaneId,
    [property: JsonPropertyName("signal")] string Signal);

public sealed record ClosePaneRequest(
    [property: JsonPropertyName("paneId")] string PaneId,
    [property: JsonPropertyName("mode")] string Mode);

public sealed record ClosePaneResult(
    [property: JsonPropertyName("exitCode")] int ExitCode);

public sealed record PaneScopeRequest(
    [property: JsonPropertyName("paneId")] string PaneId);

public sealed record EmptyResult();

// -- Server pushes -------------------------------------------------------------------------

public sealed record PaneFramePushPayload(
    [property: JsonPropertyName("paneId")] string PaneId,
    [property: JsonPropertyName("sequence")] long Sequence,
    [property: JsonPropertyName("bytesB64")] string BytesBase64);

public sealed record PaneExitedPushPayload(
    [property: JsonPropertyName("paneId")] string PaneId,
    [property: JsonPropertyName("exitCode")] int ExitCode);

// -- Session-store RPC params + results ----------------------------------------------------

public sealed record CreateSessionRequest(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("workspaceRoot")] string? WorkspaceRoot);

public sealed record CreateSessionResult(
    [property: JsonPropertyName("sessionId")] string SessionId);

public sealed record SessionInfoDto(
    [property: JsonPropertyName("sessionId")] string SessionId,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("workspaceRoot")] string? WorkspaceRoot,
    [property: JsonPropertyName("createdAtUtc")] DateTimeOffset CreatedAtUtc,
    [property: JsonPropertyName("lastAttachedAtUtc")] DateTimeOffset LastAttachedAtUtc);

public sealed record ListSessionsResult(
    [property: JsonPropertyName("sessions")] IReadOnlyList<SessionInfoDto> Sessions);

public sealed record AttachSessionRequest(
    [property: JsonPropertyName("sessionId")] string SessionId);

public sealed record AttachSessionResult(
    [property: JsonPropertyName("found")] bool Found,
    [property: JsonPropertyName("info")] SessionInfoDto? Info,
    [property: JsonPropertyName("layoutJson")] string? LayoutJson,
    [property: JsonPropertyName("focusedPaneId")] string? FocusedPaneId,
    [property: JsonPropertyName("panes")] IReadOnlyList<PaneSpecDto>? Panes);

public sealed record PaneSpecDto(
    [property: JsonPropertyName("paneId")] string PaneId,
    [property: JsonPropertyName("command")] string Command,
    [property: JsonPropertyName("arguments")] IReadOnlyList<string> Arguments,
    [property: JsonPropertyName("workingDirectory")] string? WorkingDirectory,
    [property: JsonPropertyName("environment")] IReadOnlyDictionary<string, string>? Environment,
    [property: JsonPropertyName("liveState")] string? LiveState = null);

public sealed record UpsertPaneRequest(
    [property: JsonPropertyName("sessionId")] string SessionId,
    [property: JsonPropertyName("pane")] PaneSpecDto Pane);

public sealed record DeletePaneRequest(
    [property: JsonPropertyName("sessionId")] string SessionId,
    [property: JsonPropertyName("paneId")] string PaneId);

public sealed record SaveLayoutRequest(
    [property: JsonPropertyName("sessionId")] string SessionId,
    [property: JsonPropertyName("layoutJson")] string LayoutJson,
    [property: JsonPropertyName("focusedPaneId")] string FocusedPaneId);

public sealed record DeleteSessionRequest(
    [property: JsonPropertyName("sessionId")] string SessionId);

// -- Agent RPC params + results ------------------------------------------------------------

public sealed record StartAgentSessionRequest(
    [property: JsonPropertyName("paneId")] string PaneId,
    [property: JsonPropertyName("agentSessionId")] string AgentSessionId,
    [property: JsonPropertyName("prompt")] string Prompt,
    [property: JsonPropertyName("workingDirectory")] string? WorkingDirectory);

public sealed record StartAgentSessionResult(
    [property: JsonPropertyName("agentSessionId")] string AgentSessionId);

// -- Workflow RPC params + results ---------------------------------------------------------

public sealed record WorkflowStartRequest(
    [property: JsonPropertyName("workflowName")] string WorkflowName,
    [property: JsonPropertyName("argument")] string? Argument);

public sealed record WorkflowStartResult(
    [property: JsonPropertyName("executionId")] string ExecutionId);

public sealed record WorkflowCancelRequest(
    [property: JsonPropertyName("executionId")] string ExecutionId);
