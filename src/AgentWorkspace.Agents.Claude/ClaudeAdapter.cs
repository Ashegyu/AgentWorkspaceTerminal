using System;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AgentWorkspace.Abstractions.Agents;

namespace AgentWorkspace.Agents.Claude;

/// <summary>
/// Spawns <c>claude --print &lt;prompt&gt; --output-format stream-json</c> and wraps
/// its newline-delimited JSON output as <see cref="AgentEvent"/> instances.
/// </summary>
public sealed class ClaudeAdapter : IAgentAdapter
{
    public string Name => "Claude Code";

    public AgentCapabilities Capabilities { get; } = new(
        StructuredOutput: true,
        SupportsPlanProposal: false,
        SupportsCancel: true);

    public ValueTask<IAgentSession> StartSessionAsync(
        AgentSessionOptions options,
        CancellationToken cancellationToken = default)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "claude",
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            RedirectStandardInput  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true,
            StandardOutputEncoding = Encoding.UTF8,
        };
        psi.ArgumentList.Add("--print");
        psi.ArgumentList.Add(options.Prompt);
        psi.ArgumentList.Add("--output-format");
        psi.ArgumentList.Add("stream-json");

        if (options.WorkingDirectory is not null)
            psi.WorkingDirectory = options.WorkingDirectory;

        if (options.Environment is not null)
            foreach (var (k, v) in options.Environment)
                psi.EnvironmentVariables[k] = v;

        var process = Process.Start(psi)
            ?? throw new InvalidOperationException(
                "Failed to start 'claude' process. " +
                "Ensure Claude Code CLI is installed and on PATH.");

        IAgentSession session = new ClaudeSession(process);
        return ValueTask.FromResult(session);
    }
}
