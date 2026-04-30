namespace AgentWorkspace.Abstractions.Redaction;

/// <summary>
/// Removes sensitive substrings from agent / transcript output before display or persistence.
/// Targets are listed in DESIGN.md §9.3 (env-style API tokens, SSH keys, .env content,
/// absolute user paths, etc.). Implementations must be deterministic — the same input always
/// yields the same redacted output, so transcripts diff cleanly.
/// </summary>
public interface IRedactionEngine
{
    /// <summary>Returns <paramref name="text"/> with every match replaced by a stable placeholder.</summary>
    string Redact(string text);
}
