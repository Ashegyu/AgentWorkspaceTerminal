using System.Collections.Generic;
using System.Linq;
using System.Windows;
using AgentWorkspace.Abstractions.Agents;

namespace AgentWorkspace.App.Wpf.Approval;

public partial class ApprovalDialog : Window
{
    private readonly IReadOnlyList<ActionRequestEvent> _actions;

    public ApprovalDialog(IReadOnlyList<ActionRequestEvent> actions)
    {
        _actions = actions;
        InitializeComponent();
        ActionsList.ItemsSource = actions
            .Select(a => new ActionViewModel(a.Type, a.Description))
            .ToList();
    }

    public bool WasApproved { get; private set; }

    private void OnApprove(object sender, RoutedEventArgs e)
    {
        WasApproved = true;
        DialogResult = true;
    }

    private void OnDeny(object sender, RoutedEventArgs e)
    {
        WasApproved = false;
        DialogResult = false;
    }

    private sealed record ActionViewModel(string Type, string Description)
    {
        public string TypeLabel => $"[{Type}]";
    }
}
