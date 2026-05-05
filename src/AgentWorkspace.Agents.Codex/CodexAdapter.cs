using System;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AgentWorkspace.Abstractions.Agents;

namespace AgentWorkspace.Agents.Codex;

/// <summary>
/// Spawns the OpenAI Codex CLI (<c>codex exec</c>) and wraps its stdout as
/// <see cref="AgentEvent"/> instances.
/// <para>
/// Codex CLI's structured-output formats are still evolving, so this adapter parses
/// stdout lines as plain assistant text by default. If a future Codex release stabilises
/// a stream-JSON format, replace the body of <c>CodexSession.PumpAsync</c> with a
/// JSON-line parser, mirroring <c>AgentWorkspace.Agents.Claude.Wire.StreamJsonParser</c>.
/// </para>
/// <para>
/// Requires the <c>codex</c> binary to be on PATH. If launched without auth the CLI
/// will print an error to stderr — that text is surfaced via <see cref="AgentErrorEvent"/>
/// at session end so the user can see it in the trace panel.
/// </para>
/// </summary>
public sealed class CodexAdapter : IAgentAdapter
{
    private readonly string _executable;

    /// <param name="executable">
    ///   Codex CLI executable. Defaults to <c>"codex"</c> (resolved via PATH).
    ///   Override for portable installs or testing.
    /// </param>
    public CodexAdapter(string executable = "codex")
    {
        _executable = executable;
    }

    public string Name => "Codex";

    public AgentCapabilities Capabilities { get; } = new(
        StructuredOutput:    false,   // line-as-message until JSON parser lands
        SupportsPlanProposal: false,
        SupportsCancel:      true,
        SupportsContinue:    false);

    public ValueTask<IAgentSession> StartSessionAsync(
        AgentSessionOptions options,
        CancellationToken cancellationToken = default)
    {
        var psi = new ProcessStartInfo
        {
            FileName               = _executable,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            RedirectStandardInput  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true,
            StandardOutputEncoding = Encoding.UTF8,
        };
        // `codex exec <prompt>` — non-interactive single-shot mode that prints the
        // assistant's response and exits. Equivalent to claude's `--print`.
        psi.ArgumentList.Add("exec");
        psi.ArgumentList.Add(options.Prompt);

        if (options.WorkingDirectory is not null)
            psi.WorkingDirectory = options.WorkingDirectory;

        if (options.Environment is not null)
            foreach (var (k, v) in options.Environment)
                psi.EnvironmentVariables[k] = v;

        Process process;
        try
        {
            process = Process.Start(psi)
                ?? throw new InvalidOperationException(
                    $"Failed to start '{_executable}'. Ensure Codex CLI is installed and on PATH.");
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            // File-not-found surfaces as Win32Exception 2 on Windows; rewrap with a
            // clearer message so the user sees actionable text in the status bar.
            throw new InvalidOperationException(
                $"'{_executable}' not found. Install OpenAI Codex CLI and ensure it's on PATH.", ex);
        }

        // Single-shot mode: close stdin so the CLI doesn't wait for additional input.
        try { process.StandardInput.Close(); }
        catch { /* stream may already be closed if the CLI died at startup */ }

        IAgentSession session = new CodexSession(process);
        return ValueTask.FromResult(session);
    }
}
