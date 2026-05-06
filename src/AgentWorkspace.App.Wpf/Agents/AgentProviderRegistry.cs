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

    public IEnumerable<AgentProviderDescriptor> InteractiveProviders =>
        Providers.Where(p => p.SupportsInteractivePane);

    public IEnumerable<AgentProviderDescriptor> SubAgentProviders => Providers;

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
                PanelTitle: "Claude 패널 열기",
                PanelDescription: "현재 패널을 세로로 분할하고 새 패널에서 claude REPL을 시작합니다 (PATH에 Claude Code CLI 필요)",
                PanelKeywords: "claude 패널 열기 에이전트 ai ask repl interactive assistant 클로드",
                SubAgentTitle: "Claude 하위 에이전트 실행...",
                SubAgentDescription: "AgentMesh를 통해 Claude 하위 에이전트를 스폰합니다. 결과는 에이전트 트레이스 패널에 표시됩니다.",
                SubAgentKeywords: "claude 하위 에이전트 실행 spawn subagent mesh child agent 스폰 클로드",
                StartsExternalTaskIntegration: true),

            new(
                Id: "ollama",
                DisplayName: "Ollama",
                Adapter: new OllamaAdapter(),
                InteractiveCommand: "ollama run llama3",
                PaneBadge: "ollama",
                GlobalBadge: "Ollama · llama3",
                PanelTitle: "Ollama 패널 열기",
                PanelDescription: "현재 패널을 세로로 분할하고 새 패널에서 ollama run llama3를 시작합니다 (로컬 Ollama 설치 필요)",
                PanelKeywords: "ollama 패널 열기 에이전트 로컬 ai llm llama local model repl interactive",
                SubAgentTitle: "Ollama 하위 에이전트 실행...",
                SubAgentDescription: "로컬 Ollama HTTP API로 하위 에이전트를 스폰합니다. localhost:11434와 llama3 모델이 필요합니다.",
                SubAgentKeywords: "ollama 하위 에이전트 실행 spawn subagent mesh child local llama 스폰"),

            new(
                Id: "codex",
                DisplayName: "Codex",
                Adapter: new CodexAdapter(),
                InteractiveCommand: "codex",
                PaneBadge: "codex",
                GlobalBadge: "Codex",
                PanelTitle: "Codex 패널 열기",
                PanelDescription: "현재 패널을 세로로 분할하고 새 패널에서 codex CLI를 시작합니다 (PATH에 codex 필요).",
                PanelKeywords: "codex 패널 열기 openai gpt",
                SubAgentTitle: "Codex 하위 에이전트 실행...",
                SubAgentDescription: "OpenAI Codex CLI(`codex exec`)로 하위 에이전트를 스폰합니다. PATH에 codex 필요.",
                SubAgentKeywords: "codex 하위 에이전트 실행 spawn subagent mesh child openai gpt 스폰"),

            new(
                Id: "gemini",
                DisplayName: "Gemini",
                Adapter: new GeminiAdapter(),
                InteractiveCommand: "gemini",
                PaneBadge: "gemini",
                GlobalBadge: "Gemini",
                PanelTitle: "Gemini 패널 열기",
                PanelDescription: "현재 패널을 세로로 분할하고 새 패널에서 gemini CLI를 시작합니다 (PATH에 gemini와 API key 필요).",
                PanelKeywords: "gemini 패널 열기 google 제미니",
                SubAgentTitle: "Gemini 하위 에이전트 실행...",
                SubAgentDescription: "Google Gemini CLI(`gemini -p`)로 하위 에이전트를 스폰합니다. PATH에 gemini와 GEMINI_API_KEY 필요.",
                SubAgentKeywords: "gemini 하위 에이전트 실행 spawn subagent mesh child google 제미니 스폰"),
        };

        return new AgentProviderRegistry(providers, defaultProviderId: BuiltInDefaultProviderId);
    }
}
