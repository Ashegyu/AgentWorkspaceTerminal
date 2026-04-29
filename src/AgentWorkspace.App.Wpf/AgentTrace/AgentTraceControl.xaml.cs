using System.Collections.Specialized;
using System.Windows.Controls;

namespace AgentWorkspace.App.Wpf.AgentTrace;

public partial class AgentTraceControl : UserControl
{
    public AgentTraceControl()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => ResubscribeCollection();
    }

    private INotifyCollectionChanged? _watched;

    private void ResubscribeCollection()
    {
        if (_watched is not null)
            _watched.CollectionChanged -= OnEventsChanged;

        _watched = (DataContext as AgentTraceViewModel)?.Events;

        if (_watched is not null)
            _watched.CollectionChanged += OnEventsChanged;
    }

    private void OnEventsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Add)
            Scroll.ScrollToBottom();
    }
}
