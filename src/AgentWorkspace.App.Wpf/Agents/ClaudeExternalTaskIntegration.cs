using System;
using System.Threading.Tasks;
using AgentWorkspace.Agents.Claude;

namespace AgentWorkspace.App.Wpf.Agents;

internal sealed record ExternalTaskStartedEvent(
    AgentProviderDescriptor Provider,
    TaskInvocation Task);

internal sealed record ExternalTaskCompletedEvent(
    AgentProviderDescriptor Provider,
    TaskResult Result);

internal sealed class ClaudeExternalTaskIntegration : IAsyncDisposable
{
    private readonly AgentProviderDescriptor _provider;
    private ClaudeTranscriptWatcher? _watcher;

    public ClaudeExternalTaskIntegration(AgentProviderDescriptor provider)
    {
        _provider = provider;
    }

    public event EventHandler<ExternalTaskStartedEvent>? TaskStarted;
    public event EventHandler<ExternalTaskCompletedEvent>? TaskCompleted;

    public bool IsStarted => _watcher is not null;

    public async Task StartAsync()
    {
        if (_watcher is not null) return;

        var watcher = new ClaudeTranscriptWatcher();
        watcher.TaskStarted   += OnTaskStarted;
        watcher.TaskCompleted += OnTaskCompleted;
        await watcher.StartAsync().ConfigureAwait(true);
        _watcher = watcher;
    }

    public async ValueTask DisposeAsync()
    {
        if (_watcher is null) return;

        _watcher.TaskStarted   -= OnTaskStarted;
        _watcher.TaskCompleted -= OnTaskCompleted;
        await _watcher.DisposeAsync().ConfigureAwait(true);
        _watcher = null;
    }

    private void OnTaskStarted(object? sender, TaskInvocation task) =>
        TaskStarted?.Invoke(this, new ExternalTaskStartedEvent(_provider, task));

    private void OnTaskCompleted(object? sender, TaskResult result) =>
        TaskCompleted?.Invoke(this, new ExternalTaskCompletedEvent(_provider, result));
}
