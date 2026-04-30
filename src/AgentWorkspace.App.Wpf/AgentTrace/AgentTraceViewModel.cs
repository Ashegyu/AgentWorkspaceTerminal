using System.Collections.ObjectModel;
using System.Windows.Threading;
using AgentWorkspace.Abstractions.Agents;
using AgentWorkspace.Abstractions.Redaction;
using AgentWorkspace.Core.Redaction;

namespace AgentWorkspace.App.Wpf.AgentTrace;

/// <summary>
/// ViewModel for the agent event trace panel.
/// Free-form text shown in the UI is redacted via <see cref="IRedactionEngine"/> before
/// being added to the bound collection so secrets do not leak through the trace panel.
/// Thread-safe: <see cref="Append"/> can be called from any thread.
/// </summary>
public sealed class AgentTraceViewModel
{
    private readonly Dispatcher _dispatcher;
    private readonly IRedactionEngine _redaction;

    public AgentTraceViewModel() : this(Dispatcher.CurrentDispatcher, new RegexRedactionEngine()) { }

    public AgentTraceViewModel(IRedactionEngine redaction)
        : this(Dispatcher.CurrentDispatcher, redaction) { }

    internal AgentTraceViewModel(Dispatcher dispatcher, IRedactionEngine redaction)
    {
        _dispatcher = dispatcher;
        _redaction  = redaction;
    }

    public ObservableCollection<AgentEventViewModel> Events { get; } = new();

    public void Append(AgentEvent evt)
    {
        var vm = AgentEventViewModel.From(evt, _redaction);
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
