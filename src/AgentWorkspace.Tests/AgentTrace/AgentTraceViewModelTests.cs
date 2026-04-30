using System.Windows.Threading;
using AgentWorkspace.Abstractions.Agents;
using AgentWorkspace.App.Wpf.AgentTrace;
using AgentWorkspace.Core.Redaction;

namespace AgentWorkspace.Tests.AgentTrace;

public sealed class AgentTraceViewModelTests
{
    private static readonly RegexRedactionEngine Redaction = new();

    private static AgentTraceViewModel CreateVm() =>
        new AgentTraceViewModel(Dispatcher.CurrentDispatcher, Redaction);

    // ── AgentEventViewModel.From ─────────────────────────────────────────────

    [Fact]
    public void From_MessageEvent_ReturnsMessageEventVm()
    {
        var evt = new AgentMessageEvent("assistant", "hello");
        var vm = AgentEventViewModel.From(evt, Redaction);

        var msg = Assert.IsType<MessageEventVm>(vm);
        Assert.Equal("assistant", msg.Role);
        Assert.Equal("hello", msg.Text);
    }

    [Fact]
    public void From_ActionRequestEvent_ReturnsActionRequestVm()
    {
        var evt = new ActionRequestEvent("id-1", "bash", "run shell");
        var vm = AgentEventViewModel.From(evt, Redaction);

        var action = Assert.IsType<ActionRequestVm>(vm);
        Assert.Equal("id-1",      action.ActionId);
        Assert.Equal("bash",      action.ActionType);
        Assert.Equal("run shell", action.Description);
        Assert.False(action.IsExpanded);
    }

    [Fact]
    public void From_DoneEvent_ReturnsDoneEventVm()
    {
        var evt = new AgentDoneEvent(0, "all done");
        var vm = AgentEventViewModel.From(evt, Redaction);

        var done = Assert.IsType<DoneEventVm>(vm);
        Assert.Equal(0,        done.ExitCode);
        Assert.Equal("all done", done.Summary);
    }

    [Fact]
    public void From_ErrorEvent_ReturnsErrorEventVm()
    {
        var evt = new AgentErrorEvent("oops");
        var vm = AgentEventViewModel.From(evt, Redaction);

        var err = Assert.IsType<ErrorEventVm>(vm);
        Assert.Equal("oops", err.Message);
    }

    [Fact]
    public void From_UnknownEvent_ReturnsUnknownEventVm()
    {
        var vm = AgentEventViewModel.From(new PlanProposedEvent([]), Redaction);
        Assert.IsType<UnknownEventVm>(vm);
    }

    // ── Redaction wire-up (Polish 1) ─────────────────────────────────────────

    [Fact]
    public void From_MessageEvent_RedactsTextField()
    {
        var evt = new AgentMessageEvent("assistant", "OPENAI_API_KEY=sk-foobar123 in env");
        var vm  = AgentEventViewModel.From(evt, Redaction);

        var msg = Assert.IsType<MessageEventVm>(vm);
        Assert.Contains("OPENAI_API_KEY=[REDACTED]", msg.Text);
        Assert.DoesNotContain("sk-foobar123", msg.Text);
    }

    [Fact]
    public void From_ActionRequestEvent_RedactsDescription()
    {
        var evt = new ActionRequestEvent("id-1", "bash", @"run on C:\Users\jgkim\proj");
        var vm  = AgentEventViewModel.From(evt, Redaction);

        var action = Assert.IsType<ActionRequestVm>(vm);
        Assert.Contains(@"C:\Users\[USER]", action.Description);
        Assert.DoesNotContain("jgkim", action.Description);
    }

    [Fact]
    public void From_DoneEvent_RedactsSummary()
    {
        var vm = AgentEventViewModel.From(new AgentDoneEvent(0, "GITHUB_TOKEN=ghp_abcdefghij used"), Redaction);
        var done = Assert.IsType<DoneEventVm>(vm);
        Assert.NotNull(done.Summary);
        Assert.Contains("GITHUB_TOKEN=[REDACTED]", done.Summary!);
    }

    [Fact]
    public void From_DoneEvent_NullSummary_StaysNull()
    {
        var vm = AgentEventViewModel.From(new AgentDoneEvent(0, null), Redaction);
        var done = Assert.IsType<DoneEventVm>(vm);
        Assert.Null(done.Summary);
    }

    [Fact]
    public void From_ErrorEvent_RedactsMessage()
    {
        var vm = AgentEventViewModel.From(new AgentErrorEvent(@"crash at C:\Users\jgkim\bin"), Redaction);
        var err = Assert.IsType<ErrorEventVm>(vm);
        Assert.DoesNotContain("jgkim", err.Message);
    }

    // ── AgentTraceViewModel ──────────────────────────────────────────────────

    [Fact]
    public void Append_AddsViewModelToEvents()
    {
        var trace = CreateVm();
        trace.Append(new AgentMessageEvent("assistant", "hi"));

        Assert.Single(trace.Events);
        var vm = Assert.IsType<MessageEventVm>(trace.Events[0]);
        Assert.Equal("hi", vm.Text);
    }

    [Fact]
    public void Append_MultipleEvents_PreservesOrder()
    {
        var trace = CreateVm();
        trace.Append(new AgentMessageEvent("assistant", "first"));
        trace.Append(new AgentMessageEvent("assistant", "second"));
        trace.Append(new AgentDoneEvent(0, null));

        Assert.Equal(3, trace.Events.Count);
        Assert.IsType<MessageEventVm>(trace.Events[0]);
        Assert.IsType<MessageEventVm>(trace.Events[1]);
        Assert.IsType<DoneEventVm>(trace.Events[2]);
    }

    [Fact]
    public void Clear_RemovesAllEvents()
    {
        var trace = CreateVm();
        trace.Append(new AgentMessageEvent("assistant", "hi"));
        trace.Append(new AgentErrorEvent("boom"));

        trace.Clear();

        Assert.Empty(trace.Events);
    }

    [Fact]
    public void Clear_OnEmptyCollection_DoesNotThrow()
    {
        var trace = CreateVm();
        trace.Clear();
        Assert.Empty(trace.Events);
    }

    [Fact]
    public void ActionRequestVm_IsExpanded_RaisesPropertyChanged()
    {
        var vm = new ActionRequestVm("id", "type", "desc");
        string? changedProp = null;
        vm.PropertyChanged += (_, e) => changedProp = e.PropertyName;

        vm.IsExpanded = true;

        Assert.Equal(nameof(ActionRequestVm.IsExpanded), changedProp);
        Assert.True(vm.IsExpanded);
    }
}
