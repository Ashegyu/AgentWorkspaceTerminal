using System.Collections.Generic;
using System.Windows;
using System.Windows.Input;

namespace AgentWorkspace.App.Wpf.Sessions;

/// <summary>
/// Dark-themed picker for switching to a stored workspace session or starting a new one.
/// </summary>
public partial class SessionPickerDialog : Window
{
    public SessionPickerDialog(IReadOnlyList<SessionChoiceItem> choices)
    {
        InitializeComponent();
        SessionList.ItemsSource = choices;

        var selectedIndex = 0;
        for (var i = 0; i < choices.Count; i++)
        {
            if (!choices[i].IsCurrent) continue;
            selectedIndex = i;
            break;
        }
        SessionList.SelectedIndex = selectedIndex;

        Loaded += (_, _) => SessionList.Focus();
    }

    public SessionChoiceItem? SelectedChoice { get; private set; }

    private void OnAttach(object sender, RoutedEventArgs e)
    {
        if (SessionList.SelectedItem is not SessionChoiceItem choice) return;

        SelectedChoice = choice;
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            DialogResult = false;
            e.Handled = true;
            return;
        }

        base.OnKeyDown(e);
    }
}
