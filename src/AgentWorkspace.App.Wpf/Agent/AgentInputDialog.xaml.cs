using System.Windows;

namespace AgentWorkspace.App.Wpf.Agent;

public partial class AgentInputDialog : Window
{
    public AgentInputDialog()
    {
        InitializeComponent();
        Loaded += (_, _) => PromptBox.Focus();
    }

    public string Prompt => PromptBox.Text.Trim();

    public string? WorkingDirectory =>
        string.IsNullOrWhiteSpace(WorkDirBox.Text) ? null : WorkDirBox.Text.Trim();

    private void OnAsk(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(PromptBox.Text)) { PromptBox.Focus(); return; }
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
