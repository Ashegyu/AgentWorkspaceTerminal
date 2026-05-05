using System;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AgentWorkspace.Abstractions.Agents;

namespace AgentWorkspace.Agents.Gemini;

/// <summary>
/// Spawns Google's Gemini CLI (<c>gemini -p &lt;prompt&gt;</c>) and wraps its stdout
/// as <see cref="AgentEvent"/> instances.
/// <para>
/// Gemini CLI's stdout is plain text by default — assistant tokens stream as a flow
/// of lines rather than structured JSON. This adapter emits each non-empty line as an
/// <see cref="AgentMessageEvent"/>; if a future Gemini release exposes a stream-JSON
/// mode, replace the body of <c>GeminiSession.PumpAsync</c> with a JSON-line parser.
/// </para>
/// <para>
/// Requires the <c>gemini</c> binary on PATH and a valid API key (the CLI typically
/// reads <c>GEMINI_API_KEY</c> or <c>GOOGLE_GENERATIVE_AI_API_KEY</c> from environment).
/// </para>
/// </summary>
public sealed class GeminiAdapter : IAgentAdapter
{
    private readonly string _executable;
    private readonly string? _model;

    /// <param name="executable">
    ///   Gemini CLI executable. Defaults to <c>"gemini"</c> (resolved via PATH).
    /// </param>
    /// <param name="model">
    ///   Optional model override (e.g. <c>"gemini-2.5-pro"</c>). When null, the CLI
    ///   uses its configured default.
    /// </param>
    public GeminiAdapter(string executable = "gemini", string? model = null)
    {
        _executable = executable;
        _model      = model;
    }

    public string Name => "Gemini";

    public AgentCapabilities Capabilities { get; } = new(
        StructuredOutput:    false,   // line-as-message until structured stream lands
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
        // `gemini -p <prompt>` runs a single non-interactive prompt and prints the
        // response. AgentSessionOptions.Model wins over the constructor model param.
        psi.ArgumentList.Add("-p");
        psi.ArgumentList.Add(options.Prompt);
        var resolvedModel = options.Model ?? _model;
        if (!string.IsNullOrEmpty(resolvedModel))
        {
            psi.ArgumentList.Add("-m");
            psi.ArgumentList.Add(resolvedModel);
        }

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
                    $"Failed to start '{_executable}'. Ensure Gemini CLI is installed and on PATH.");
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new InvalidOperationException(
                $"'{_executable}' not found. Install Google Gemini CLI and ensure it's on PATH.", ex);
        }

        // Single-shot mode: close stdin so the CLI doesn't wait for additional input.
        try { process.StandardInput.Close(); }
        catch { /* may already be closed if the CLI died at startup */ }

        IAgentSession session = new GeminiSession(process);
        return ValueTask.FromResult(session);
    }
}
