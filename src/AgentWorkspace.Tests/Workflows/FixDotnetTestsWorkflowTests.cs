using System.Threading;
using System.Threading.Tasks;
using AgentWorkspace.Abstractions.Agents;
using AgentWorkspace.Abstractions.Policy;
using AgentWorkspace.Abstractions.Workflows;
using AgentWorkspace.Core.Policy;
using AgentWorkspace.Core.Workflows;

namespace AgentWorkspace.Tests.Workflows;

public sealed class FixDotnetTestsWorkflowTests
{
    private const string FakeProject = @"C:\fake\project";
    private const string FakeLog     = "1 test failed: NullReferenceException";

    private static WorkflowContext MakeContext(
        FakeAgentAdapter adapter,
        IApprovalGateway gateway,
        IPolicyEngine? policy = null,
        CancellationToken ct = default)
    {
        var trigger = new TestFailedTrigger(FakeProject, FakeLog);
        return new WorkflowContext(
            WorkflowExecutionId.New(),
            trigger,
            adapter,
            gateway,
            policy ?? PassThroughPolicyEngine.Instance,
            new PolicyContext(WorkspaceRoot: FakeProject, Level: PolicyLevel.SafeDev, AgentName: adapter.Name),
            ct);
    }

    // ── assistant text only ───────────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_AssistantTextThenDone_ReturnsSuccessWithText()
    {
        var adapter = new FakeAgentAdapter();
        adapter.EnqueueSequence(
            new AgentMessageEvent("assistant", "Found the bug. Fixing NullRef."),
            new AgentDoneEvent(0, null));

        var result = await new FixDotnetTestsWorkflow()
            .ExecuteAsync(MakeContext(adapter, AutoApproveGateway.Instance));

        var success = Assert.IsType<WorkflowSuccess>(result);
        Assert.NotNull(success.Summary);
        Assert.Contains("Found the bug", success.Summary);
    }

    [Fact]
    public async Task ExecuteAsync_DoneWithSummary_NoText_UsesDoneSummary()
    {
        var adapter = new FakeAgentAdapter();
        adapter.EnqueueSequence(new AgentDoneEvent(0, "All tests fixed."));

        var result = await new FixDotnetTestsWorkflow()
            .ExecuteAsync(MakeContext(adapter, AutoApproveGateway.Instance));

        var success = Assert.IsType<WorkflowSuccess>(result);
        Assert.Equal("All tests fixed.", success.Summary);
    }

    // ── action requests — approved ────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_ActionsWithDone_AutoApproved_ReturnsSuccess()
    {
        var adapter = new FakeAgentAdapter();
        adapter.EnqueueSequence(
            new ActionRequestEvent("a1", "bash",       "dotnet test"),
            new ActionRequestEvent("a2", "write_file", "fix Foo.cs"),
            new AgentDoneEvent(0, null));

        var result = await new FixDotnetTestsWorkflow()
            .ExecuteAsync(MakeContext(adapter, AutoApproveGateway.Instance));

        Assert.IsType<WorkflowSuccess>(result);
    }

    [Fact]
    public async Task ExecuteAsync_ActionsWithDone_AutoDenied_ReturnsCancelled()
    {
        var adapter = new FakeAgentAdapter();
        adapter.EnqueueSequence(
            new ActionRequestEvent("a1", "bash", "rm -rf /"),
            new AgentDoneEvent(0, null));

        var result = await new FixDotnetTestsWorkflow()
            .ExecuteAsync(MakeContext(adapter, AutoDenyGateway.Instance));

        Assert.IsType<WorkflowCancelled>(result);
    }

    // ── stream ends without AgentDoneEvent ────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_StreamEnds_NoPendingActions_ReturnsSuccess()
    {
        var adapter = new FakeAgentAdapter();
        adapter.EnqueueSequence(
            new AgentMessageEvent("assistant", "Nothing to do."));
        // no AgentDoneEvent — stream simply ends

        var result = await new FixDotnetTestsWorkflow()
            .ExecuteAsync(MakeContext(adapter, AutoApproveGateway.Instance));

        Assert.IsType<WorkflowSuccess>(result);
    }

    [Fact]
    public async Task ExecuteAsync_StreamEnds_PendingActions_AutoApproved_ReturnsSuccess()
    {
        var adapter = new FakeAgentAdapter();
        adapter.EnqueueSequence(
            new ActionRequestEvent("a1", "bash", "dotnet restore"));
        // no AgentDoneEvent

        var result = await new FixDotnetTestsWorkflow()
            .ExecuteAsync(MakeContext(adapter, AutoApproveGateway.Instance));

        Assert.IsType<WorkflowSuccess>(result);
    }

    [Fact]
    public async Task ExecuteAsync_StreamEnds_PendingActions_AutoDenied_ReturnsCancelled()
    {
        var adapter = new FakeAgentAdapter();
        adapter.EnqueueSequence(
            new ActionRequestEvent("a1", "bash", "dangerous"));
        // no AgentDoneEvent

        var result = await new FixDotnetTestsWorkflow()
            .ExecuteAsync(MakeContext(adapter, AutoDenyGateway.Instance));

        Assert.IsType<WorkflowCancelled>(result);
    }

    // ── agent error ───────────────────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_AgentError_ReturnsWorkflowFailure()
    {
        var adapter = new FakeAgentAdapter();
        adapter.EnqueueSequence(new AgentErrorEvent("Rate limit exceeded"));

        var result = await new FixDotnetTestsWorkflow()
            .ExecuteAsync(MakeContext(adapter, AutoApproveGateway.Instance));

        var failure = Assert.IsType<WorkflowFailure>(result);
        Assert.Equal("Rate limit exceeded", failure.Reason);
    }

    // ── cancellation ─────────────────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_PreCancelledToken_ThrowsOrReturnsCancelled()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var adapter = new FakeAgentAdapter();
        // Enqueue a long sequence; cancellation should cut it short.
        adapter.EnqueueSequence(
            new AgentMessageEvent("assistant", "thinking..."),
            new AgentDoneEvent(0, null));

        // StartSessionAsync itself does not throw — the cancellation surfaces
        // when iterating Events, so we wrap in try/catch for either path.
        try
        {
            var result = await new FixDotnetTestsWorkflow()
                .ExecuteAsync(MakeContext(adapter, AutoApproveGateway.Instance, ct: cts.Token));

            // Acceptable: workflow detected cancellation and returned WorkflowCancelled.
            Assert.IsType<WorkflowCancelled>(result);
        }
        catch (OperationCanceledException)
        {
            // Also acceptable: OperationCanceledException propagated before catch.
        }
    }
}
