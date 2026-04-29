using System.Collections.Generic;

namespace AgentWorkspace.Abstractions.Agents;

/// <summary>Parameters for starting a new agent session.</summary>
public sealed record AgentSessionOptions(
    string Prompt,
    string? WorkingDirectory = null,
    IReadOnlyDictionary<string, string>? Environment = null,
    bool SaveTranscript = true);
