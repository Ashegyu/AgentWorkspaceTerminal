using System;
using System.Collections;
using System.ComponentModel;
using System.Windows.Data;

namespace AgentWorkspace.App.Wpf.Mesh;

/// <summary>Sort direction options for the sub-agent card list.</summary>
public enum SubAgentCardSortMode
{
    NewestFirst,
    OldestFirst,
    StatusGrouped,    // Running → Merged → Error
    AdapterGrouped,   // by AdapterName ascending
}

/// <summary>Visibility filter options for the sub-agent card list.</summary>
public enum SubAgentCardFilterMode
{
    All,
    RunningOnly,
    ExternalOnly,
    InternalOnly,
}

/// <summary>
/// Wraps an <see cref="ICollectionView"/> over the live <c>_subAgentSessions</c> collection,
/// and provides imperative methods to switch sort + filter without forcing every consumer
/// to know about CollectionViewSource internals.
/// <para>
/// Owns NO data — the underlying collection stays the source of truth. Mutations to that
/// collection (Add/Remove on the ObservableCollection) auto-propagate through the view,
/// so the card list re-sorts and re-filters live as new external Tasks arrive.
/// </para>
/// <para>
/// <b>Thread affinity</b>: <see cref="SortMode"/> / <see cref="FilterMode"/> setters call
/// <see cref="ICollectionView.Refresh"/> synchronously, which WPF requires to run on the
/// UI thread. Callers must invoke these from the dispatcher.
/// </para>
/// <para>
/// <b>Shared default view</b>: this class uses
/// <see cref="CollectionViewSource.GetDefaultView(object)"/>, which returns the SAME
/// <see cref="ICollectionView"/> instance for a given source collection. Other code that
/// binds to the same source observable will see this view's filter/sort state. Today
/// the source is private to <c>MainWindow</c> so the coupling is unobservable; future
/// callers binding the same collection to a second control should use a fresh
/// <c>new CollectionViewSource { Source = source }.View</c> instead.
/// </para>
/// </summary>
public sealed class SubAgentCardView
{
    private readonly ICollectionView _view;
    private SubAgentCardSortMode    _sort   = SubAgentCardSortMode.NewestFirst;
    private SubAgentCardFilterMode  _filter = SubAgentCardFilterMode.All;
    /// <summary>True while the constructor is running — suppresses the per-mutator
    /// <c>Refresh()</c> so we only invalidate the view ONCE at the end of init.</summary>
    private bool _initializing = true;

    /// <param name="source">The live <c>ObservableCollection&lt;SubAgentSessionViewModel&gt;</c>.</param>
    public SubAgentCardView(IEnumerable source)
    {
        ArgumentNullException.ThrowIfNull(source);
        _view = CollectionViewSource.GetDefaultView(source);
        ApplySort();
        ApplyFilter();
        _initializing = false;
        _view.Refresh(); // single coalesced refresh after both sort + filter are configured
    }

    /// <summary>The live view bound to <c>ItemsControl.ItemsSource</c>.</summary>
    public ICollectionView View => _view;

    public SubAgentCardSortMode SortMode
    {
        get => _sort;
        set
        {
            if (_sort == value) return;
            _sort = value;
            ApplySort();
        }
    }

    public SubAgentCardFilterMode FilterMode
    {
        get => _filter;
        set
        {
            if (_filter == value) return;
            _filter = value;
            ApplyFilter();
        }
    }

    private void ApplySort()
    {
        _view.SortDescriptions.Clear();
        switch (_sort)
        {
            case SubAgentCardSortMode.NewestFirst:
                _view.SortDescriptions.Add(new SortDescription(
                    nameof(SubAgentSessionViewModel.StartedAt), ListSortDirection.Descending));
                break;
            case SubAgentCardSortMode.OldestFirst:
                _view.SortDescriptions.Add(new SortDescription(
                    nameof(SubAgentSessionViewModel.StartedAt), ListSortDirection.Ascending));
                break;
            case SubAgentCardSortMode.StatusGrouped:
                // SubAgentStatus enum values are Running=0, Merged=1, Error=2 — ascending
                // gives Running first, which is what the user wants to see at the top.
                _view.SortDescriptions.Add(new SortDescription(
                    nameof(SubAgentSessionViewModel.Status), ListSortDirection.Ascending));
                _view.SortDescriptions.Add(new SortDescription(
                    nameof(SubAgentSessionViewModel.StartedAt), ListSortDirection.Descending));
                break;
            case SubAgentCardSortMode.AdapterGrouped:
                _view.SortDescriptions.Add(new SortDescription(
                    nameof(SubAgentSessionViewModel.AdapterName), ListSortDirection.Ascending));
                _view.SortDescriptions.Add(new SortDescription(
                    nameof(SubAgentSessionViewModel.StartedAt), ListSortDirection.Descending));
                break;
        }
        if (!_initializing) _view.Refresh();
    }

    private void ApplyFilter()
    {
        _view.Filter = _filter switch
        {
            SubAgentCardFilterMode.All          => null,
            SubAgentCardFilterMode.RunningOnly  => o => o is SubAgentSessionViewModel vm && vm.Status == SubAgentStatus.Running,
            SubAgentCardFilterMode.ExternalOnly => o => o is SubAgentSessionViewModel vm && vm.IsExternal,
            SubAgentCardFilterMode.InternalOnly => o => o is SubAgentSessionViewModel vm && !vm.IsExternal,
            _                                   => null,
        };
        if (!_initializing) _view.Refresh();
    }
}
