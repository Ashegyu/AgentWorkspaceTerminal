using System.ComponentModel;
using System.Runtime.CompilerServices;
using AgentWorkspace.Abstractions.Agents;

namespace AgentWorkspace.App.Wpf.AgentTrace;

/// <summary>Base ViewModel for a single <see cref="AgentEvent"/> entry.</summary>
public abstract class AgentEventViewModel
{
    public static AgentEventViewModel From(AgentEvent evt) => evt switch
    {
        AgentMessageEvent  m => new MessageEventVm(m.Role, m.Text),
        ActionRequestEvent a => new ActionRequestVm(a.ActionId, a.Type, a.Description),
        AgentDoneEvent     d => new DoneEventVm(d.ExitCode, d.Summary),
        AgentErrorEvent    e => new ErrorEventVm(e.Message),
        _                    => new UnknownEventVm(),
    };
}

public sealed class MessageEventVm : AgentEventViewModel
{
    public string Role { get; }
    public string Text { get; }
    public MessageEventVm(string role, string text) { Role = role; Text = text; }
}

public sealed class ActionRequestVm : AgentEventViewModel, INotifyPropertyChanged
{
    private bool _expanded;

    public string ActionId    { get; }
    public string ActionType  { get; }
    public string Description { get; }

    public bool IsExpanded
    {
        get => _expanded;
        set { _expanded = value; OnPropertyChanged(); }
    }

    public ActionRequestVm(string actionId, string actionType, string description)
    {
        ActionId    = actionId;
        ActionType  = actionType;
        Description = description;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class DoneEventVm : AgentEventViewModel
{
    public int     ExitCode { get; }
    public string? Summary  { get; }
    public DoneEventVm(int exitCode, string? summary) { ExitCode = exitCode; Summary = summary; }
}

public sealed class ErrorEventVm : AgentEventViewModel
{
    public string Message { get; }
    public ErrorEventVm(string message) { Message = message; }
}

public sealed class UnknownEventVm : AgentEventViewModel { }
