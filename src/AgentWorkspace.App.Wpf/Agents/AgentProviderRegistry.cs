using System;
using System.Collections.Generic;
using System.Linq;
using AgentWorkspace.Abstractions.Agents;
using AgentWorkspace.Agents.Claude;
using AgentWorkspace.Agents.Codex;
using AgentWorkspace.Agents.Gemini;
using AgentWorkspace.Agents.Ollama;

namespace AgentWorkspace.App.Wpf.Agents;

internal sealed class AgentProviderRegistry
{
    internal const string BuiltInDefaultProviderId = "claude";

    private readonly Dictionary<string, AgentProviderDescriptor> _byId;
    private readonly Dictionary<string, AgentProviderDescriptor> _byAdapterName;

    private AgentProviderRegistry(
        IReadOnlyList<AgentProviderDescriptor> providers,
        string defaultProviderId)
    {
        Providers = providers;
        _byId = providers.ToDictionary(p => p.Id, StringComparer.OrdinalIgnoreCase);
        _byAdapterName = providers
            .GroupBy(p => p.Adapter.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        DefaultProvider = GetRequired(defaultProviderId);
    }

    public IReadOnlyList<AgentProviderDescriptor> Providers { get; }

    public AgentProviderDescriptor DefaultProvider { get; }

    public AgentProviderDescriptor GetRequired(string id) =>
        _byId.TryGetValue(id, out var provider)
            ? provider
            : throw new InvalidOperationException($"Agent provider '{id}' is not registered.");

    public AgentProviderDescriptor ResolveOrDefault(string? id) =>
        !string.IsNullOrWhiteSpace(id) && _byId.TryGetValue(id.Trim(), out var provider)
            ? provider
            : DefaultProvider;

    public bool TryGet(string id, out AgentProviderDescriptor provider) =>
        _byId.TryGetValue(id, out provider!);

    public AgentProviderDescriptor? FindByAdapter(IAgentAdapter adapter) =>
        _byAdapterName.TryGetValue(adapter.Name, out var provider) ? provider : null;

    public static AgentProviderRegistry CreateDefault()
    {
        var providers = new AgentProviderDescriptor[]
        {
            new(
                Id: "claude",
                DisplayName: "Claude Code",
                Adapter: new ClaudeAdapter(),
                InteractiveCommand: "claude",
                PaneBadge: "claude",
                GlobalBadge: "Claude Code",
                StartsExternalTaskIntegration: true),

            new(
                Id: "ollama",
                DisplayName: "Ollama",
                Adapter: new OllamaAdapter(),
                InteractiveCommand: "ollama run llama3",
                PaneBadge: "ollama",
                GlobalBadge: "Ollama · llama3"),

            new(
                Id: "codex",
                DisplayName: "Codex",
                Adapter: new CodexAdapter(),
                InteractiveCommand: "codex",
                PaneBadge: "codex",
                GlobalBadge: "Codex"),

            new(
                Id: "gemini",
                DisplayName: "Gemini",
                Adapter: new GeminiAdapter(),
                InteractiveCommand: "gemini",
                PaneBadge: "gemini",
                GlobalBadge: "Gemini"),
        };

        return new AgentProviderRegistry(providers, defaultProviderId: BuiltInDefaultProviderId);
    }
}
