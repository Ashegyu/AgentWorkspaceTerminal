using System.Collections.Generic;

namespace AgentWorkspace.Abstractions.Pty;

/// <summary>
/// Inputs required to launch a child process under a pseudo-console.
/// Immutable; constructed by the caller, consumed by <c>IPseudoTerminal.StartAsync</c>.
/// </summary>
/// <param name="Command">Executable name or full path. Resolved via PATH if not absolute.</param>
/// <param name="Arguments">Argument list. Each element is quoted independently when serialized.</param>
/// <param name="WorkingDirectory">Initial working directory. Null means inherit caller's CWD.</param>
/// <param name="Environment">Environment variables. Null means inherit caller's environment as-is.</param>
/// <param name="InitialColumns">Initial pseudo-console width in cells. Must be 1..32767.</param>
/// <param name="InitialRows">Initial pseudo-console height in cells. Must be 1..32767.</param>
public sealed record PaneStartOptions(
    string Command,
    IReadOnlyList<string> Arguments,
    string? WorkingDirectory,
    IReadOnlyDictionary<string, string>? Environment,
    short InitialColumns,
    short InitialRows);
