using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Threading;
using AgentWorkspace.Abstractions.Agents;
using AgentWorkspace.Agents.Claude;
using AgentWorkspace.App.Wpf.Mesh;

namespace AgentWorkspace.Tests.Mesh;

/// <summary>
/// Behavioural tests for <see cref="SubAgentCardView"/>. The view wraps an
/// <c>ICollectionView</c> over a live <c>ObservableCollection</c>, so each test
/// constructs the underlying observable, mutates it, and asserts on the filtered/
/// sorted view enumeration.
/// </summary>
public sealed class SubAgentCardViewTests
{
    private static readonly IAgentAdapter _adapter = new ClaudeAdapter();

    private static SubAgentSessionViewModel NewVm(
        bool isExternal = false,
        SubAgentStatus status = SubAgentStatus.Running)
    {
        var vm = new SubAgentSessionViewModel(
            childId:               AgentSessionId.New(),
            originalPrompt:        "test",
            adapter:               _adapter,
            dispatcher:            Dispatcher.CurrentDispatcher,
            isExternal:            isExternal,
            externalSubAgentType:  isExternal ? "general-purpose" : null);
        if (status != SubAgentStatus.Running) vm.Status = status;
        return vm;
    }

    private static List<SubAgentSessionViewModel> Snapshot(SubAgentCardView view)
        => view.View.Cast<SubAgentSessionViewModel>().ToList();

    // ── sort ─────────────────────────────────────────────────────────────────

    [Fact]
    public void NewestFirst_OrdersByStartedAtDescending()
    {
        var src = new ObservableCollection<SubAgentSessionViewModel>();
        var older = NewVm(); System.Threading.Thread.Sleep(5);
        var newer = NewVm();
        src.Add(older);
        src.Add(newer);

        var view = new SubAgentCardView(src) { SortMode = SubAgentCardSortMode.NewestFirst };
        var ordered = Snapshot(view);
        Assert.Equal(newer, ordered[0]);
        Assert.Equal(older, ordered[1]);
    }

    [Fact]
    public void OldestFirst_OrdersByStartedAtAscending()
    {
        var src = new ObservableCollection<SubAgentSessionViewModel>();
        var first  = NewVm(); System.Threading.Thread.Sleep(5);
        var second = NewVm();
        src.Add(second);
        src.Add(first);

        var view = new SubAgentCardView(src) { SortMode = SubAgentCardSortMode.OldestFirst };
        var ordered = Snapshot(view);
        Assert.Equal(first,  ordered[0]);
        Assert.Equal(second, ordered[1]);
    }

    [Fact]
    public void StatusGrouped_PutsRunningFirstThenMergedThenError()
    {
        var src = new ObservableCollection<SubAgentSessionViewModel>();
        var error   = NewVm(status: SubAgentStatus.Error);
        var merged  = NewVm(status: SubAgentStatus.Merged);
        var running = NewVm(status: SubAgentStatus.Running);
        src.Add(error);
        src.Add(merged);
        src.Add(running);

        var view = new SubAgentCardView(src) { SortMode = SubAgentCardSortMode.StatusGrouped };
        var ordered = Snapshot(view);
        Assert.Equal(SubAgentStatus.Running, ordered[0].Status);
        Assert.Equal(SubAgentStatus.Merged,  ordered[1].Status);
        Assert.Equal(SubAgentStatus.Error,   ordered[2].Status);
    }

    [Fact]
    public void SortMode_SettingSameValue_DoesNotRefreshGratuitously()
    {
        // Defensive: the property setter short-circuits when value is unchanged.
        // We assert by ensuring the view enumerates without throwing after a no-op.
        var src = new ObservableCollection<SubAgentSessionViewModel> { NewVm() };
        var view = new SubAgentCardView(src);
        var before = view.SortMode;
        view.SortMode = before; // should be a no-op
        Assert.Equal(before, view.SortMode);
        Assert.Single(Snapshot(view));
    }

    // ── filter ────────────────────────────────────────────────────────────────

    [Fact]
    public void All_ShowsBothInternalAndExternal()
    {
        var src = new ObservableCollection<SubAgentSessionViewModel>
        {
            NewVm(isExternal: false),
            NewVm(isExternal: true),
        };
        var view = new SubAgentCardView(src) { FilterMode = SubAgentCardFilterMode.All };
        Assert.Equal(2, Snapshot(view).Count);
    }

    [Fact]
    public void RunningOnly_HidesMergedAndError()
    {
        var src = new ObservableCollection<SubAgentSessionViewModel>
        {
            NewVm(status: SubAgentStatus.Running),
            NewVm(status: SubAgentStatus.Merged),
            NewVm(status: SubAgentStatus.Error),
        };
        var view = new SubAgentCardView(src) { FilterMode = SubAgentCardFilterMode.RunningOnly };
        var visible = Snapshot(view);
        Assert.Single(visible);
        Assert.Equal(SubAgentStatus.Running, visible[0].Status);
    }

    [Fact]
    public void ExternalOnly_HidesInternal()
    {
        var src = new ObservableCollection<SubAgentSessionViewModel>
        {
            NewVm(isExternal: false),
            NewVm(isExternal: true),
            NewVm(isExternal: true),
        };
        var view = new SubAgentCardView(src) { FilterMode = SubAgentCardFilterMode.ExternalOnly };
        var visible = Snapshot(view);
        Assert.Equal(2, visible.Count);
        Assert.All(visible, vm => Assert.True(vm.IsExternal));
    }

    [Fact]
    public void InternalOnly_HidesExternal()
    {
        var src = new ObservableCollection<SubAgentSessionViewModel>
        {
            NewVm(isExternal: false),
            NewVm(isExternal: true),
        };
        var view = new SubAgentCardView(src) { FilterMode = SubAgentCardFilterMode.InternalOnly };
        var visible = Snapshot(view);
        Assert.Single(visible);
        Assert.False(visible[0].IsExternal);
    }

    // ── live updates ──────────────────────────────────────────────────────────

    [Fact]
    public void AddingNewItem_AppearsInViewLive_UnderCurrentFilter()
    {
        var src = new ObservableCollection<SubAgentSessionViewModel>();
        var view = new SubAgentCardView(src) { FilterMode = SubAgentCardFilterMode.ExternalOnly };

        // Adding an internal VM should not appear under ExternalOnly.
        src.Add(NewVm(isExternal: false));
        Assert.Empty(Snapshot(view));

        // Adding an external VM should appear immediately.
        src.Add(NewVm(isExternal: true));
        Assert.Single(Snapshot(view));
    }

    [Fact]
    public void NullSource_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new SubAgentCardView(null!));
    }
}
