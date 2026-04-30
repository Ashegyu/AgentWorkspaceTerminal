using System;
using System.Collections.Generic;
using System.Text.Json;

namespace AgentWorkspace.Abstractions.Policy;

/// <summary>
/// Structured representation of an action an agent wants to take.
/// Discriminated union — concrete subclasses describe each action category the policy
/// engine knows about. Mirrors DESIGN.md §3.6.
/// </summary>
public abstract record ProposedAction;

/// <summary>Run an external process / shell command.</summary>
public sealed record ExecuteCommand(
    string Cmd,
    IReadOnlyList<string> Args,
    string? Cwd = null,
    IReadOnlyDictionary<string, string>? Env = null) : ProposedAction
{
    /// <summary>Convenience: full command line as a single string ("cmd arg1 arg2").</summary>
    public string CommandLine => Args is { Count: > 0 } ? $"{Cmd} {string.Join(' ', Args)}" : Cmd;
}

/// <summary>Write or overwrite a file.</summary>
public sealed record WriteFile(
    string Path,
    long ContentLength,
    FileWriteMode Mode = FileWriteMode.Create) : ProposedAction;

/// <summary>Delete a file or directory tree.</summary>
public sealed record DeletePath(
    string Path,
    bool Recursive) : ProposedAction;

/// <summary>Outbound network call.</summary>
public sealed record NetworkCall(
    Uri Url,
    string Method) : ProposedAction;

/// <summary>Invoke a Model Context Protocol tool. Reserved for future MCP integration.</summary>
public sealed record InvokeMcpTool(
    string ServerId,
    string ToolName,
    JsonElement Args) : ProposedAction;

/// <summary>How a <see cref="WriteFile"/> action treats existing content.</summary>
public enum FileWriteMode
{
    Create,
    Overwrite,
    Append,
}
