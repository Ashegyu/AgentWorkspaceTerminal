using System.Text.Json;
using System.Threading.Tasks;
using AgentWorkspace.Abstractions.Agents;
using AgentWorkspace.Abstractions.Policy;
using AgentWorkspace.Abstractions.Workflows;
using AgentWorkspace.Core.Policy;
using AgentWorkspace.Core.Workflows;

namespace AgentWorkspace.Tests.Workflows;

/// <summary>
/// Verifies that <see cref="ApprovalRequestItem"/> values reaching the gateway carry the
/// upstream <see cref="PolicyDecision"/> (Risk + Reason), so the UI can render them.
/// </summary>
public sealed class ApprovalRequestItemTests
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

    [Fact]
    public async Task Queued_AskUserItem_CarriesPolicyDecision()
    {
        var gateway = new RecordingApprovalGateway();
        var adapter = new FakeAgentAdapter();
        adapter.EnqueueSequence(
            new ActionRequestEvent("a1", "Bash", "Bash",
                Input: Json("""{"command":"dotnet build"}""")),
            new AgentDoneEvent(0, null));

        await new FixDotnetTestsWorkflow().ExecuteAsync(MakeContext(adapter, gateway));

        Assert.Equal(1, gateway.CallCount);
        Assert.NotNull(gateway.LastBatch);
        var item = Assert.Single(gateway.LastBatch!);
        Assert.Equal("a1", item.Action.ActionId);
        Assert.Equal(PolicyVerdict.AskUser, item.Decision.Verdict);
        Assert.False(string.IsNullOrEmpty(item.Decision.Reason));
    }

    [Fact]
    public async Task UnknownTool_QueuedItem_CarriesSyntheticDecision()
    {
        var gateway = new RecordingApprovalGateway();
        var adapter = new FakeAgentAdapter();
        adapter.EnqueueSequence(
            new ActionRequestEvent("a1", "WeirdTool", "WeirdTool"),
            new AgentDoneEvent(0, null));

        await new FixDotnetTestsWorkflow().ExecuteAsync(MakeContext(adapter, gateway));

        Assert.Equal(1, gateway.CallCount);
        var item = Assert.Single(gateway.LastBatch!);
        Assert.Equal(PolicyVerdict.AskUser, item.Decision.Verdict);
        Assert.Contains("Unknown tool", item.Decision.Reason);
    }

    [Fact]
    public void ApprovalRequestItem_RecordEquality_ComparesNestedValues()
    {
        var act = new ActionRequestEvent("a1", "Bash", "echo");
        var dec = new PolicyDecision(PolicyVerdict.AskUser, "needs review", Risk.Medium);

        var a = new ApprovalRequestItem(act, dec);
        var b = new ApprovalRequestItem(act, dec);

        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }
}
