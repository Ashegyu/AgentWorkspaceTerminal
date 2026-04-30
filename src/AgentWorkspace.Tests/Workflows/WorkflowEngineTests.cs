using System.Threading;
using System.Threading.Tasks;
using AgentWorkspace.Abstractions.Workflows;
using AgentWorkspace.Core.Workflows;

namespace AgentWorkspace.Tests.Workflows;

public sealed class WorkflowEngineTests
{
    // ── helpers ───────────────────────────────────────────────────────────────

    private static WorkflowEngine BuildEngine(params IWorkflow[] workflows)
        => new(workflows, new FakeAgentAdapter(), AutoApproveGateway.Instance);

    // ── RunAsync ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task RunAsync_NoMatchingWorkflow_ReturnsNull()
    {
        await using var engine = BuildEngine();
        var result = await engine.RunAsync(new ManualTrigger("anything"));
        Assert.Null(result);
    }

    [Fact]
    public async Task RunAsync_MatchingWorkflow_ReturnsWorkflowSuccess()
    {
        await using var engine = BuildEngine(new NoOpWorkflow());
        var result = await engine.RunAsync(new ManualTrigger("no-op"));
        Assert.IsType<WorkflowSuccess>(result);
    }

    [Fact]
    public async Task RunAsync_WorkflowReturnsFailure_ExposedToCallers()
    {
        await using var engine = BuildEngine(new FailingWorkflow());
        var result = await engine.RunAsync(new ManualTrigger("fail"));
        var failure = Assert.IsType<WorkflowFailure>(result);
        Assert.Equal("deliberate failure", failure.Reason);
    }

    [Fact]
    public async Task RunAsync_PreCancelledToken_WorkflowSeesCancellation_ReturnsWorkflowCancelled()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await using var engine = BuildEngine(new CancelObservingWorkflow());
        var result = await engine.RunAsync(new ManualTrigger("cancel-check"), cts.Token);
        Assert.IsType<WorkflowCancelled>(result);
    }

    [Fact]
    public async Task RunAsync_ManualTrigger_WithArgument_WorkflowReceivesTrigger()
    {
        await using var engine = BuildEngine(new EchoTriggerWorkflow());
        var result = await engine.RunAsync(new ManualTrigger("echo", "hello"));
        var success = Assert.IsType<WorkflowSuccess>(result);
        Assert.Equal("hello", success.Summary);
    }

    // ── TriggerAsync ──────────────────────────────────────────────────────────

    [Fact]
    public async Task TriggerAsync_NoMatchingWorkflow_ReturnsNull()
    {
        await using var engine = BuildEngine();
        var id = engine.TriggerAsync(new ManualTrigger("anything"));
        Assert.Null(id);
    }

    [Fact]
    public async Task TriggerAsync_MatchingWorkflow_ReturnsNonNullExecutionId()
    {
        await using var engine = BuildEngine(new NoOpWorkflow());
        var id = engine.TriggerAsync(new ManualTrigger("no-op"));
        Assert.NotNull(id);
    }

    [Fact]
    public async Task TriggerAsync_TwoMatching_ReturnsDifferentIds()
    {
        await using var engine = BuildEngine(new NoOpWorkflow());
        var id1 = engine.TriggerAsync(new ManualTrigger("no-op"));
        var id2 = engine.TriggerAsync(new ManualTrigger("no-op"));
        Assert.NotNull(id1);
        Assert.NotNull(id2);
        Assert.NotEqual(id1, id2);
    }

    // ── DisposeAsync ──────────────────────────────────────────────────────────

    [Fact]
    public async Task DisposeAsync_CalledTwice_DoesNotThrow()
    {
        var engine = BuildEngine();
        await engine.DisposeAsync();
        await engine.DisposeAsync();
    }
}

// ── local workflow stubs ──────────────────────────────────────────────────────

file sealed class NoOpWorkflow : IWorkflow
{
    public string Name => "no-op";
    public bool CanHandle(WorkflowTrigger t) => t is ManualTrigger { WorkflowName: "no-op" };
    public ValueTask<WorkflowResult> ExecuteAsync(WorkflowContext ctx)
        => ValueTask.FromResult<WorkflowResult>(new WorkflowSuccess());
}

file sealed class FailingWorkflow : IWorkflow
{
    public string Name => "fail";
    public bool CanHandle(WorkflowTrigger t) => t is ManualTrigger { WorkflowName: "fail" };
    public ValueTask<WorkflowResult> ExecuteAsync(WorkflowContext ctx)
        => ValueTask.FromResult<WorkflowResult>(new WorkflowFailure("deliberate failure"));
}

file sealed class CancelObservingWorkflow : IWorkflow
{
    public string Name => "cancel-check";
    public bool CanHandle(WorkflowTrigger t) => t is ManualTrigger { WorkflowName: "cancel-check" };
    public ValueTask<WorkflowResult> ExecuteAsync(WorkflowContext ctx)
    {
        ctx.CancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<WorkflowResult>(new WorkflowSuccess());
    }
}

file sealed class EchoTriggerWorkflow : IWorkflow
{
    public string Name => "echo";
    public bool CanHandle(WorkflowTrigger t) => t is ManualTrigger { WorkflowName: "echo" };
    public ValueTask<WorkflowResult> ExecuteAsync(WorkflowContext ctx)
    {
        var arg = ctx.Trigger is ManualTrigger mt ? mt.Argument : null;
        return ValueTask.FromResult<WorkflowResult>(new WorkflowSuccess(arg));
    }
}
