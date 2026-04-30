using System.Text.Json;
using System.Threading.Tasks;
using AgentWorkspace.Abstractions.Agents;
using AgentWorkspace.Abstractions.Policy;
using AgentWorkspace.Abstractions.Workflows;
using AgentWorkspace.Core.Policy;
using AgentWorkspace.Core.Workflows;

namespace AgentWorkspace.Tests.Workflows;

/// <summary>
/// Polish 3 — verifies <see cref="FixDotnetTestsWorkflow"/> respects
/// <see cref="PolicyDecision.RequireIndividualApproval"/>: Critical-risk items
/// are confirmed one at a time and a single deny short-circuits the rest.
/// </summary>
public sealed class FixDotnetTestsWorkflowIndividualApprovalTests
{
    private const string Project = @"C:\fake\project";
    private const string Log     = "1 test failed.";

    private static JsonElement Json(string raw) => JsonDocument.Parse(raw).RootElement.Clone();

    private static WorkflowContext MakeContext(
        FakeAgentAdapter adapter,
        IApprovalGateway gateway,
        PolicyLevel level = PolicyLevel.SafeDev) =>
        new(WorkflowExecutionId.New(),
            new TestFailedTrigger(Project, Log),
            adapter,
            gateway,
            new PolicyEngine(),
            new PolicyContext(WorkspaceRoot: Project, Level: level, AgentName: adapter.Name),
            default);

    /// <summary>Write outside workspace under SafeDev → AskUser + RequireIndividualApproval=true.</summary>
    private static ActionRequestEvent IndividualWriteAction(string id) =>
        new(id, "Write", "Write",
            Input: Json("""{"file_path":"C:\\other\\important.txt","content":"hi"}"""));

    /// <summary>Write inside workspace under SafeDev → AskUser + RequireIndividualApproval=false.</summary>
    private static ActionRequestEvent BatchWriteAction(string id) =>
        new(id, "Write", "Write",
            Input: Json("""{"file_path":"C:\\fake\\project\\src\\foo.cs","content":"x"}"""));

    [Fact]
    public async Task Individual_Approved_Then_Batch_Approved_TwoCalls()
    {
        var gateway = new RecordingApprovalGateway();
        var adapter = new FakeAgentAdapter();
        adapter.EnqueueSequence(
            IndividualWriteAction("ind1"),
            BatchWriteAction("batch1"),
            new AgentDoneEvent(0, null));

        var result = await new FixDotnetTestsWorkflow().ExecuteAsync(
            MakeContext(adapter, gateway));

        Assert.IsType<WorkflowSuccess>(result);
        Assert.Equal(2, gateway.CallCount);
        Assert.Single(gateway.Batches[0]);                          // individual went alone
        Assert.Equal("ind1",   gateway.Batches[0][0].Action.ActionId);
        Assert.True(gateway.Batches[0][0].Decision.RequireIndividualApproval);
        Assert.Single(gateway.Batches[1]);                          // batch carries the rest
        Assert.Equal("batch1", gateway.Batches[1][0].Action.ActionId);
        Assert.False(gateway.Batches[1][0].Decision.RequireIndividualApproval);
    }

    [Fact]
    public async Task Individual_Denied_ShortCircuits_BatchNeverCalled()
    {
        // Script: first individual denied; batch should never be reached.
        var gateway = new RecordingApprovalGateway(scripted: [false]);
        var adapter = new FakeAgentAdapter();
        adapter.EnqueueSequence(
            IndividualWriteAction("ind1"),
            BatchWriteAction("batch1"),
            new AgentDoneEvent(0, null));

        var result = await new FixDotnetTestsWorkflow().ExecuteAsync(
            MakeContext(adapter, gateway));

        Assert.IsType<WorkflowCancelled>(result);
        Assert.Equal(1, gateway.CallCount);                         // batch never reached
        Assert.Single(gateway.Batches[0]);
    }

    [Fact]
    public async Task Multiple_Individuals_AllApproved_EachOwnCall_PlusBatch()
    {
        var gateway = new RecordingApprovalGateway();
        var adapter = new FakeAgentAdapter();
        adapter.EnqueueSequence(
            IndividualWriteAction("ind1"),
            IndividualWriteAction("ind2"),
            BatchWriteAction("batch1"),
            BatchWriteAction("batch2"),
            new AgentDoneEvent(0, null));

        var result = await new FixDotnetTestsWorkflow().ExecuteAsync(
            MakeContext(adapter, gateway));

        Assert.IsType<WorkflowSuccess>(result);
        Assert.Equal(3, gateway.CallCount);                         // 2 individuals + 1 batch
        Assert.Single(gateway.Batches[0]);                          // ind1 alone
        Assert.Single(gateway.Batches[1]);                          // ind2 alone
        Assert.Equal(2, gateway.Batches[2].Count);                  // batch1 + batch2 together
    }

    [Fact]
    public async Task Multiple_Individuals_SecondDenied_StopsImmediately()
    {
        // First individual approved, second denied; batch must not be reached.
        var gateway = new RecordingApprovalGateway(scripted: [true, false]);
        var adapter = new FakeAgentAdapter();
        adapter.EnqueueSequence(
            IndividualWriteAction("ind1"),
            IndividualWriteAction("ind2"),
            BatchWriteAction("batch1"),
            new AgentDoneEvent(0, null));

        var result = await new FixDotnetTestsWorkflow().ExecuteAsync(
            MakeContext(adapter, gateway));

        Assert.IsType<WorkflowCancelled>(result);
        Assert.Equal(2, gateway.CallCount);
    }

    [Fact]
    public async Task OnlyIndividuals_NoBatchCall()
    {
        var gateway = new RecordingApprovalGateway();
        var adapter = new FakeAgentAdapter();
        adapter.EnqueueSequence(
            IndividualWriteAction("ind1"),
            new AgentDoneEvent(0, null));

        var result = await new FixDotnetTestsWorkflow().ExecuteAsync(
            MakeContext(adapter, gateway));

        Assert.IsType<WorkflowSuccess>(result);
        Assert.Equal(1, gateway.CallCount);
        Assert.Single(gateway.Batches[0]);
    }

    [Fact]
    public async Task OnlyBatchItems_SingleCall()
    {
        var gateway = new RecordingApprovalGateway();
        var adapter = new FakeAgentAdapter();
        adapter.EnqueueSequence(
            BatchWriteAction("a"),
            BatchWriteAction("b"),
            new AgentDoneEvent(0, null));

        var result = await new FixDotnetTestsWorkflow().ExecuteAsync(
            MakeContext(adapter, gateway));

        Assert.IsType<WorkflowSuccess>(result);
        Assert.Equal(1, gateway.CallCount);
        Assert.Equal(2, gateway.Batches[0].Count);
    }

    [Fact]
    public async Task BatchDenied_AfterIndividualApproved_ReturnsCancelled()
    {
        // Individual approved (true), batch denied (false).
        var gateway = new RecordingApprovalGateway(scripted: [true, false]);
        var adapter = new FakeAgentAdapter();
        adapter.EnqueueSequence(
            IndividualWriteAction("ind1"),
            BatchWriteAction("b1"),
            new AgentDoneEvent(0, null));

        var result = await new FixDotnetTestsWorkflow().ExecuteAsync(
            MakeContext(adapter, gateway));

        Assert.IsType<WorkflowCancelled>(result);
        Assert.Equal(2, gateway.CallCount);
    }
}
