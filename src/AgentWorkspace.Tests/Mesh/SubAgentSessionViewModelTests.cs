using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using AgentWorkspace.Abstractions.Agents;
using AgentWorkspace.Agents.Claude;
using AgentWorkspace.App.Wpf.Mesh;

namespace AgentWorkspace.Tests.Mesh;

/// <summary>
/// Tests for the per-card view-model that surfaces sub-agent state.
/// Covers internal vs. external mode discriminators, command activation rules, and
/// merge-summary behaviour. Uses a freshly-pumped <see cref="Dispatcher"/> so the VM's
/// dispatcher-marshalling code path runs as it would inside the WPF app.
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class SubAgentSessionViewModelTests
{
    /// <summary>
    /// Builds a VM on the calling thread's dispatcher. xUnit threads typically have a
    /// usable <see cref="Dispatcher"/>; if not, <see cref="Dispatcher.CurrentDispatcher"/>
    /// creates one.
    /// </summary>
    private static SubAgentSessionViewModel NewVm(
        bool isExternal = false,
        string? externalSubAgentType = null,
        Action<SubAgentSessionViewModel>? onSpawnChild = null)
    {
        return new SubAgentSessionViewModel(
            childId:              AgentSessionId.New(),
            originalPrompt:       "test prompt",
            adapter:              new ClaudeAdapter(),
            onFocus:              null,
            onPromoteToPane:      null,
            onSpawnChild:         onSpawnChild,
            isExternal:           isExternal,
            externalSubAgentType: externalSubAgentType);
    }

    // ── identity / metadata ──────────────────────────────────────────────────────

    [Fact]
    public void ShortId_ReturnsFirstEightChars()
    {
        var vm = NewVm();
        Assert.Equal(9, vm.ShortId.Length); // 8 chars + '…'
        Assert.EndsWith("…", vm.ShortId);
    }

    [Fact]
    public void OriginalPrompt_PreservedFromCtor()
    {
        var vm = NewVm();
        Assert.Equal("test prompt", vm.OriginalPrompt);
    }

    [Fact]
    public void AdapterName_ReflectsAdapter()
    {
        var vm = NewVm();
        Assert.Equal("Claude Code", vm.AdapterName);
    }

    // ── SourceLabel discriminator ────────────────────────────────────────────────

    [Fact]
    public void SourceLabel_Internal_UsesAdapterName()
    {
        var vm = NewVm(isExternal: false);
        Assert.Equal("Claude Code", vm.SourceLabel);
    }

    [Fact]
    public void SourceLabel_External_UsesExternalPrefix()
    {
        var vm = NewVm(isExternal: true, externalSubAgentType: "code-reviewer");
        Assert.Equal("외부 · code-reviewer", vm.SourceLabel);
    }

    [Fact]
    public void SourceLabel_External_FallsBackToQuestionMarkWhenTypeNull()
    {
        var vm = NewVm(isExternal: true, externalSubAgentType: null);
        Assert.Equal("외부 · ?", vm.SourceLabel);
    }

    // ── command activation ──────────────────────────────────────────────────────

    [Fact]
    public void SpawnChildCommand_Disabled_ForExternalCards()
    {
        var vm = NewVm(isExternal: true, externalSubAgentType: "general-purpose");
        Assert.False(vm.SpawnChildCommand.CanExecute(null));
    }

    [Fact]
    public void SpawnChildCommand_Enabled_ForInternalCards()
    {
        var vm = NewVm(isExternal: false);
        Assert.True(vm.SpawnChildCommand.CanExecute(null));
    }

    [Fact]
    public void SpawnChildCommand_Execute_InvokesCallback_ForInternalCards()
    {
        var fired = false;
        var vm = NewVm(isExternal: false, onSpawnChild: _ => fired = true);
        vm.SpawnChildCommand.Execute(null);
        Assert.True(fired);
    }

    [Fact]
    public void FocusCommand_AlwaysAvailable()
    {
        var vmInternal = NewVm(isExternal: false);
        var vmExternal = NewVm(isExternal: true);
        Assert.True(vmInternal.FocusCommand.CanExecute(null));
        Assert.True(vmExternal.FocusCommand.CanExecute(null));
    }

    [Fact]
    public void PromoteToPaneCommand_AlwaysAvailable()
    {
        // Even external cards can be promoted — clipboard copy is universal.
        var vmInternal = NewVm(isExternal: false);
        var vmExternal = NewVm(isExternal: true);
        Assert.True(vmInternal.PromoteToPaneCommand.CanExecute(null));
        Assert.True(vmExternal.PromoteToPaneCommand.CanExecute(null));
    }

    // ── status / merge summary ──────────────────────────────────────────────────

    [Fact]
    public void Status_DefaultsToRunning()
    {
        var vm = NewVm();
        Assert.Equal(SubAgentStatus.Running, vm.Status);
        Assert.True(vm.IsRunning);
        Assert.False(vm.IsCompleted);
    }

    [Fact]
    public void Status_TransitionToMerged_FlipsRunningAndCompleted()
    {
        var vm = NewVm();
        vm.Status = SubAgentStatus.Merged;
        Assert.False(vm.IsRunning);
        Assert.True(vm.IsCompleted);
    }

    [Fact]
    public void StatusLabel_Merged_IncludesExitCode()
    {
        var vm = NewVm();
        vm.ExitCode = 42;
        vm.Status   = SubAgentStatus.Merged;
        Assert.Contains("42", vm.StatusLabel);
        Assert.Contains("병합됨", vm.StatusLabel);
    }

    [Fact]
    public void MergedSummary_TruncatesPreviewToOneHundredTwenty()
    {
        var vm = NewVm();
        var longText = new string('A', 200);
        vm.MergedSummary = longText;
        Assert.Equal(121, vm.MergedSummaryPreview.Length); // 120 chars + '…'
        Assert.EndsWith("…", vm.MergedSummaryPreview);
    }

    [Fact]
    public void MergedSummary_ShortText_NotTruncated()
    {
        var vm = NewVm();
        vm.MergedSummary = "short result";
        Assert.Equal("short result", vm.MergedSummaryPreview);
    }

    [Fact]
    public void MergedSummary_CollapsesNewlinesIntoSpaces()
    {
        var vm = NewVm();
        vm.MergedSummary = "line one\nline two\r\nline three";
        Assert.DoesNotContain('\n', vm.MergedSummaryPreview);
        Assert.DoesNotContain('\r', vm.MergedSummaryPreview);
        Assert.Contains("line one", vm.MergedSummaryPreview);
        Assert.Contains("line three", vm.MergedSummaryPreview);
    }

    [Fact]
    public void HasMergedSummary_FalseWhenEmpty()
    {
        var vm = NewVm();
        Assert.False(vm.HasMergedSummary);
        vm.MergedSummary = "  ";
        Assert.False(vm.HasMergedSummary);
        vm.MergedSummary = "real summary";
        Assert.True(vm.HasMergedSummary);
    }

    // ── focus state ─────────────────────────────────────────────────────────────

    [Fact]
    public void IsFocused_DefaultsFalse()
    {
        var vm = NewVm();
        Assert.False(vm.IsFocused);
    }

    [Fact]
    public void IsFocused_FiresPropertyChanged()
    {
        var vm = NewVm();
        bool fired = false;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(vm.IsFocused)) fired = true;
        };
        vm.IsFocused = true;
        Assert.True(fired);
    }

    // ── ctor guards ─────────────────────────────────────────────────────────────

    [Fact]
    public void Ctor_NullAdapter_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new SubAgentSessionViewModel(
                AgentSessionId.New(),
                originalPrompt: "x",
                adapter:        null!));
    }

    [Fact]
    public void Ctor_NullPrompt_TreatedAsEmpty()
    {
        var vm = new SubAgentSessionViewModel(
            AgentSessionId.New(),
            originalPrompt: null!,
            adapter:        new ClaudeAdapter());
        Assert.Equal(string.Empty, vm.OriginalPrompt);
    }
}
