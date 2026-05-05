using System.Collections.Generic;
using System.Windows;
using AgentWorkspace.Abstractions.Ids;

namespace AgentWorkspace.App.Wpf.PaneMessage;

/// <summary>
/// Represents one entry in the pane selection combo box.
/// </summary>
public sealed class PaneChoiceItem
{
    public PaneId PaneId { get; }

    /// <summary>Display label shown in the ComboBox and echoed back after send.</summary>
    public string Label { get; }

    public PaneChoiceItem(int index, PaneId paneId, bool isFocused)
    {
        PaneId = paneId;
        Label  = isFocused ? $"패널 {index}  (현재 포커스)" : $"패널 {index}";
    }
}

/// <summary>
/// Dialog that lets the user pick a target pane and type text to send to its PTY stdin.
/// </summary>
public partial class SendToPaneDialog : Window
{
    public SendToPaneDialog(IReadOnlyList<PaneChoiceItem> choices)
    {
        InitializeComponent();
        PaneCombo.ItemsSource  = choices;
        PaneCombo.SelectedIndex = 0;
    }

    // ── Result properties (read after ShowDialog() returns true) ─────────────

    /// <summary>The pane the user selected.</summary>
    public PaneId SelectedPaneId { get; private set; }

    /// <summary>The display label of the selected pane (used for status-bar echo).</summary>
    public string SelectedLabel { get; private set; } = string.Empty;

    /// <summary>The text the user entered (without any trailing newline).</summary>
    public string Text { get; private set; } = string.Empty;

    /// <summary>Whether to append <c>\n</c> before writing to the PTY.</summary>
    public bool AppendNewline { get; private set; }

    // ── Button handlers ──────────────────────────────────────────────────────

    private void OnSend(object sender, RoutedEventArgs e)
    {
        if (PaneCombo.SelectedItem is not PaneChoiceItem choice) return;

        SelectedPaneId = choice.PaneId;
        SelectedLabel  = choice.Label;
        Text           = TextInputBox.Text;
        AppendNewline  = AppendNewlineCheck.IsChecked == true;

        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
