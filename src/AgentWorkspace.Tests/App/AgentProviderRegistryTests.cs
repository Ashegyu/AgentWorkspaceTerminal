using AgentWorkspace.App.Wpf.Agents;

namespace AgentWorkspace.Tests.App;

public sealed class AgentProviderRegistryTests
{
    [Fact]
    public void CreateDefault_RegistersKnownProviders()
    {
        var registry = AgentProviderRegistry.CreateDefault();

        Assert.Equal(
            new[] { "claude", "ollama", "codex", "gemini" },
            registry.Providers.Select(p => p.Id).ToArray());
    }

    [Fact]
    public void DefaultProvider_IsClaudeButResolvedThroughRegistry()
    {
        var registry = AgentProviderRegistry.CreateDefault();

        Assert.Equal(AgentProviderRegistry.BuiltInDefaultProviderId, registry.DefaultProvider.Id);
        Assert.Equal("claude", registry.DefaultProvider.Id);
        Assert.Equal("Claude Code", registry.DefaultProvider.DisplayName);
        Assert.True(registry.DefaultProvider.StartsExternalTaskIntegration);
    }

    [Fact]
    public void ResolveOrDefault_ReturnsRegisteredProviderOrBuiltInDefault()
    {
        var registry = AgentProviderRegistry.CreateDefault();

        Assert.Equal("codex", registry.ResolveOrDefault(" codex ").Id);
        Assert.Equal(AgentProviderRegistry.BuiltInDefaultProviderId, registry.ResolveOrDefault(null).Id);
        Assert.Equal(AgentProviderRegistry.BuiltInDefaultProviderId, registry.ResolveOrDefault("").Id);
        Assert.Equal(AgentProviderRegistry.BuiltInDefaultProviderId, registry.ResolveOrDefault("missing").Id);
    }

    [Fact]
    public void InteractiveProviders_OnlyExposeRegisteredInteractiveCommands()
    {
        var registry = AgentProviderRegistry.CreateDefault();

        Assert.Equal(
            new[] { "claude", "ollama", "codex", "gemini" },
            registry.InteractiveProviders.Select(p => p.Id).ToArray());
        Assert.All(registry.InteractiveProviders, p => Assert.True(p.SupportsInteractivePane));
    }

    [Fact]
    public void SubAgentProviders_ExposeEveryAdapterProvider()
    {
        var registry = AgentProviderRegistry.CreateDefault();

        Assert.Equal(
            new[] { "claude", "ollama", "codex", "gemini" },
            registry.SubAgentProviders.Select(p => p.Id).ToArray());
    }

    [Fact]
    public void FindByAdapter_ResolvesProviderFromAdapterName()
    {
        var registry = AgentProviderRegistry.CreateDefault();

        var codex = registry.GetRequired("codex");
        var resolved = registry.FindByAdapter(codex.Adapter);

        Assert.Same(codex, resolved);
    }
}
