using AgentWorkspace.Abstractions.Agents;

namespace AgentWorkspace.App.Wpf.Agents;

internal sealed record AgentProviderDescriptor(
    string Id,
    string DisplayName,
    IAgentAdapter Adapter,
    string? InteractiveCommand,
    string PaneBadge,
    string GlobalBadge,
    bool StartsExternalTaskIntegration = false)
{
    public bool SupportsInteractivePane =>
        !string.IsNullOrWhiteSpace(InteractiveCommand);
}
