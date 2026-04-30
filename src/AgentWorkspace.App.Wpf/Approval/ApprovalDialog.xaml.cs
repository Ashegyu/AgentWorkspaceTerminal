using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using AgentWorkspace.Abstractions.Policy;
using AgentWorkspace.Abstractions.Workflows;

namespace AgentWorkspace.App.Wpf.Approval;

public partial class ApprovalDialog : Window
{
    private readonly IReadOnlyList<ApprovalRequestItem> _items;

    public ApprovalDialog(IReadOnlyList<ApprovalRequestItem> items)
    {
        _items = items;
        InitializeComponent();
        ActionsList.ItemsSource = items.Select(ActionRowVm.From).ToList();
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

    /// <summary>Per-row view model surfaced into the ItemsControl template.</summary>
    internal sealed record ActionRowVm(
        string TypeLabel,
        string Description,
        string RiskLabel,
        Brush  RiskBrush,
        string Reason,
        bool   ShowReason)
    {
        public static ActionRowVm From(ApprovalRequestItem item)
        {
            var risk    = item.Decision.Risk;
            var reason  = item.Decision.Reason ?? string.Empty;
            return new ActionRowVm(
                TypeLabel:   $"[{item.Action.Type}]",
                Description: item.Action.Description,
                RiskLabel:   risk.ToString().ToUpperInvariant(),
                RiskBrush:   BrushFor(risk),
                Reason:      reason,
                ShowReason:  reason.Length > 0);
        }

        private static Brush BrushFor(Risk r) => r switch
        {
            Risk.Low      => new SolidColorBrush(Color.FromRgb(0x9E, 0x9E, 0x9E)), // gray
            Risk.Medium   => new SolidColorBrush(Color.FromRgb(0x4C, 0xAF, 0x50)), // green
            Risk.High     => new SolidColorBrush(Color.FromRgb(0xFB, 0x8C, 0x00)), // orange
            Risk.Critical => new SolidColorBrush(Color.FromRgb(0xD3, 0x2F, 0x2F)), // red
            _             => new SolidColorBrush(Color.FromRgb(0x9E, 0x9E, 0x9E)),
        };
    }
}
