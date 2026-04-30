using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AgentWorkspace.Abstractions.Agents;

namespace AgentWorkspace.Agents.Ollama;

/// <summary>
/// Connects to a locally-running Ollama instance via its HTTP API
/// (<c>POST http://localhost:11434/api/chat</c>) and exposes it as an
/// <see cref="IAgentAdapter"/>.
/// A single <see cref="HttpClient"/> is shared across all sessions spawned by this
/// adapter — HttpClient is designed to be long-lived and reused.
/// </summary>
public sealed class OllamaAdapter : IAgentAdapter
{
    // One HttpClient per adapter lifetime; do NOT create per-session.
    private static readonly HttpClient SharedClient = new();

    private readonly string _baseUrl;

    /// <param name="baseUrl">Ollama base URL, e.g. <c>http://localhost:11434</c>.</param>
    public OllamaAdapter(string baseUrl = "http://localhost:11434")
    {
        _baseUrl = baseUrl.TrimEnd('/');
    }

    public string Name => "Ollama";

    public AgentCapabilities Capabilities { get; } = new(
        StructuredOutput: false,
        SupportsPlanProposal: false,
        SupportsCancel: true,
        SupportsContinue: false,
        SupportsMultimodal: false);

    public ValueTask<IAgentSession> StartSessionAsync(
        AgentSessionOptions options,
        CancellationToken cancellationToken = default)
    {
        var model = options.Model ?? "llama3";
        IAgentSession session = new OllamaSession(SharedClient, _baseUrl, model, options.Prompt);
        return ValueTask.FromResult(session);
    }
}
