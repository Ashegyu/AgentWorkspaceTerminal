using System;
using System.Windows.Input;

namespace AgentWorkspace.App.Wpf.Mesh;

/// <summary>
/// Minimal <see cref="ICommand"/> implementation for binding view-model actions
/// to XAML <c>Button.Command</c> / <c>MenuItem.Command</c> without pulling in
/// MVVM Light or CommunityToolkit.Mvvm.
/// </summary>
/// <remarks>
/// <para>
/// Designed for the sub-agent card buttons (focus, promote-to-pane, spawn-child).
/// The action delegates are short and synchronous; long-running work (e.g. opening
/// a new provider pane) is dispatched on the UI thread via fire-and-forget tasks
/// inside the action body, which is acceptable for these one-shot user gestures.
/// </para>
/// <para>
/// Thread-safety: <see cref="RaiseCanExecuteChanged"/> may be called from any
/// thread; WPF marshals <see cref="CanExecuteChanged"/> handlers via the
/// <c>CommandManager</c> internally.
/// </para>
/// </remarks>
public sealed class RelayCommand : ICommand
{
    private readonly Action _execute;
    private readonly Func<bool>? _canExecute;

    public RelayCommand(Action execute, Func<bool>? canExecute = null)
    {
        _execute    = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged
    {
        add    => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    public bool CanExecute(object? parameter) => _canExecute?.Invoke() ?? true;

    public void Execute(object? parameter) => _execute();

    /// <summary>Forces WPF to re-query <see cref="CanExecute"/> on bound controls.</summary>
    public void RaiseCanExecuteChanged() => CommandManager.InvalidateRequerySuggested();
}
