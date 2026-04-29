using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace AgentWorkspace.App.Wpf.CommandPalette;

/// <summary>
/// Modal-ish overlay providing a quick searchable command list.
/// </summary>
/// <remarks>
/// MVP-1 ships five hard-coded commands. Filtering is a simple case-insensitive substring match
/// against <see cref="CommandEntry.Search"/>. We avoid a fuzzy-match library for now: the list
/// is short, and ranking quirks tend to surprise more than they help at this size.
/// </remarks>
public partial class CommandPalette : UserControl
{
    private IReadOnlyList<CommandEntry> _all = Array.Empty<CommandEntry>();

    /// <summary>
    /// Raised when the palette is dismissed (ESC, backdrop click, or after a command runs).
    /// The host should restore terminal focus.
    /// </summary>
    public event EventHandler? Dismissed;

    public CommandPalette()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Replaces the available commands. Call once at startup; calling again re-renders.
    /// </summary>
    public void SetCommands(IReadOnlyList<CommandEntry> commands)
    {
        _all = commands ?? Array.Empty<CommandEntry>();
        Refilter();
    }

    public void Show()
    {
        QueryBox.Text = string.Empty;
        Refilter();
        Visibility = Visibility.Visible;
        // Defer focus until layout has happened — TextBox.Focus on a just-shown control is
        // unreliable without a dispatcher hop.
        Dispatcher.BeginInvoke(() =>
        {
            QueryBox.Focus();
            Keyboard.Focus(QueryBox);
            QueryBox.SelectAll();
        }, System.Windows.Threading.DispatcherPriority.Input);
    }

    public void Hide()
    {
        if (Visibility != Visibility.Visible) return;
        Visibility = Visibility.Collapsed;
        Dismissed?.Invoke(this, EventArgs.Empty);
    }

    public bool IsOpen => Visibility == Visibility.Visible;

    private void Refilter()
    {
        string q = QueryBox.Text?.Trim().ToLowerInvariant() ?? string.Empty;
        var filtered = new List<CommandEntry>(_all.Count);
        foreach (var c in _all)
        {
            if (q.Length == 0 || c.Search.Contains(q, StringComparison.Ordinal))
            {
                filtered.Add(c);
            }
        }
        Results.ItemsSource = filtered;
        if (filtered.Count > 0)
        {
            Results.SelectedIndex = 0;
        }
    }

    private void OnQueryChanged(object sender, TextChangedEventArgs e) => Refilter();

    private void OnQueryPreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                e.Handled = true;
                Hide();
                break;

            case Key.Down:
                e.Handled = true;
                MoveSelection(+1);
                break;

            case Key.Up:
                e.Handled = true;
                MoveSelection(-1);
                break;

            case Key.Enter:
                e.Handled = true;
                _ = InvokeSelectedAsync();
                break;
        }
    }

    private void MoveSelection(int delta)
    {
        if (Results.Items.Count == 0) return;
        int idx = Results.SelectedIndex + delta;
        if (idx < 0) idx = 0;
        if (idx >= Results.Items.Count) idx = Results.Items.Count - 1;
        Results.SelectedIndex = idx;
        Results.ScrollIntoView(Results.SelectedItem);
    }

    private void OnResultsDoubleClick(object sender, MouseButtonEventArgs e)
    {
        _ = InvokeSelectedAsync();
    }

    private void OnBackdropClick(object sender, MouseButtonEventArgs e)
    {
        // Ignore clicks that hit the inner card; only direct hits on the backdrop dismiss.
        if (ReferenceEquals(e.OriginalSource, sender))
        {
            Hide();
        }
    }

    private async Task InvokeSelectedAsync()
    {
        if (Results.SelectedItem is not CommandEntry entry) return;
        Hide();
        try
        {
            await entry.Invoke(CancellationToken.None);
        }
        catch (Exception ex)
        {
            // Log only; the palette is gone and we don't want to surface arbitrary exceptions
            // back through the modal.
            System.Diagnostics.Debug.WriteLine($"Command '{entry.Title}' failed: {ex}");
        }
    }
}
