using System.Collections.Generic;

namespace AgentWorkspace.Abstractions.Agents;

/// <summary>
/// Parameters for starting a new agent session.
/// <c>Continue=true</c> resumes the most recent agent session instead of starting fresh
/// (Claude CLI: <c>--continue</c>); used for follow-up turns from the trace panel.
/// <c>ParentSessionId</c> records the spawning session id so transcripts can express lineage.
/// </summary>
public sealed record AgentSessionOptions(
    string Prompt,
    string? WorkingDirectory = null,
    IReadOnlyDictionary<string, string>? Environment = null,
    bool SaveTranscript = true,
    bool Continue = false,
    string? Model = null,
    AgentSessionId? ParentSessionId = null);
