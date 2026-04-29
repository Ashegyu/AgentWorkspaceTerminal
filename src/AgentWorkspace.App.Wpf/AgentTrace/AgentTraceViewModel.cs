using System.Collections.ObjectModel;
using System.Windows.Threading;
using AgentWorkspace.Abstractions.Agents;

namespace AgentWorkspace.App.Wpf.AgentTrace;

/// <summary>
/// ViewModel for the agent event trace panel.
/// Thread-safe: <see cref="Append"/> can be called from any thread.
/// </summary>
public sealed class AgentTraceViewModel
{
    private readonly Dispatcher _dispatcher;

    public AgentTraceViewModel() : this(Dispatcher.CurrentDispatcher) { }

    internal AgentTraceViewModel(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
    }

    public ObservableCollection<AgentEventViewModel> Events { get; } = new();

    public void Append(AgentEvent evt)
    {
        var vm = AgentEventViewModel.From(evt);
        if (_dispatcher.CheckAccess())
            Events.Add(vm);
        else
            _dispatcher.Invoke(() => Events.Add(vm));
    }

    public void Clear()
    {
        if (_dispatcher.CheckAccess())
            Events.Clear();
        else
            _dispatcher.Invoke(Events.Clear);
    }
}
