using System.Windows;

namespace AgentWorkspace.App.Wpf.Mesh;

/// <summary>
/// Minimal dark-themed prompt-input dialog. Used by the sub-agent card's
/// "🌱 자식 spawn" gesture to collect a prompt for the new grandchild.
/// </summary>
public partial class PromptInputDialog : Window
{
    public PromptInputDialog(
        string title,
        string label,
        string? hint = null,
        string? initialText = null)
    {
        InitializeComponent();
        Title             = title;
        PromptLabel.Text  = label;
        HintText.Text     = hint ?? string.Empty;
        HintText.Visibility = string.IsNullOrEmpty(hint)
            ? Visibility.Collapsed
            : Visibility.Visible;
        if (!string.IsNullOrEmpty(initialText))
        {
            PromptBox.Text = initialText;
            PromptBox.SelectAll();
        }
        Loaded += (_, _) => PromptBox.Focus();
    }

    /// <summary>The prompt the user entered (only valid after <see cref="Window.DialogResult"/> = true).</summary>
    public string Prompt { get; private set; } = string.Empty;

    private void OnConfirm(object sender, RoutedEventArgs e)
    {
        Prompt = PromptBox.Text;
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
