using System.Text.Json;
using System.Threading.Tasks;
using AgentWorkspace.Abstractions.Agents;
using AgentWorkspace.Abstractions.Policy;
using AgentWorkspace.Abstractions.Workflows;
using AgentWorkspace.Core.Policy;
using AgentWorkspace.Core.Workflows;

namespace AgentWorkspace.Tests.Workflows;

/// <summary>
/// Integration tests covering Day 50: FixDotnetTestsWorkflow + PolicyEngine routing.
/// Verifies that ActionRequestEvents are translated and routed by verdict.
/// </summary>
public sealed class FixDotnetTestsWorkflowPolicyTests
{
    private const string Project = @"C:\fake\project";
    private const string Log     = "1 test failed.";

    private static JsonElement Json(string raw) => JsonDocument.Parse(raw).RootElement.Clone();

    private static WorkflowContext MakeContext(
        FakeAgentAdapter adapter,
        IApprovalGateway gateway,
        IPolicyEngine policy,
        PolicyLevel level = PolicyLevel.SafeDev) =>
        new(WorkflowExecutionId.New(),
            new TestFailedTrigger(Project, Log),
            adapter,
            gateway,
            policy,
            new PolicyContext(WorkspaceRoot: Project, Level: level, AgentName: adapter.Name),
            default);

    // ── Deny path: blacklist match → WorkflowFailure ──────────────────────────

    [Fact]
    public async Task Action_HittingBlacklist_ReturnsWorkflowFailure()
    {
        var adapter = new FakeAgentAdapter();
        adapter.EnqueueSequence(
            new ActionRequestEvent("a1", "Bash", "Bash",
                Input: Json("""{"command":"rm -rf /"}""")),
            new AgentDoneEvent(0, null));

        var result = await new FixDotnetTestsWorkflow().ExecuteAsync(
            MakeContext(adapter, AutoApproveGateway.Instance, new PolicyEngine()));

        var failure = Assert.IsType<WorkflowFailure>(result);
        Assert.Contains("Policy denied", failure.Reason);
    }

    // ── Allow path: TrustedLocal whitelist → silent auto-approve ──────────────

    [Fact]
    public async Task Action_WhitelistedUnderTrustedLocal_AutoApproved_NoGatewayCall()
    {
        var gateway = new RecordingApprovalGateway();
        var adapter = new FakeAgentAdapter();
        adapter.EnqueueSequence(
            new ActionRequestEvent("a1", "Bash", "Bash",
                Input: Json("""{"command":"git status"}""")),
            new AgentDoneEvent(0, "ok"));

        var result = await new FixDotnetTestsWorkflow().ExecuteAsync(
            MakeContext(adapter, gateway, new PolicyEngine(), PolicyLevel.TrustedLocal));

        Assert.IsType<WorkflowSuccess>(result);
        Assert.Equal(0, gateway.CallCount); // policy auto-approved → gateway never asked
    }

    // ── AskUser path: SafeDev → gateway is consulted ──────────────────────────

    [Fact]
    public async Task Action_SafeDev_ExecCommand_FlowsToApprovalGateway()
    {
        var gateway = new RecordingApprovalGateway(approve: true);
        var adapter = new FakeAgentAdapter();
        adapter.EnqueueSequence(
            new ActionRequestEvent("a1", "Bash", "Bash",
                Input: Json("""{"command":"dotnet build"}""")),
            new AgentDoneEvent(0, null));

        var result = await new FixDotnetTestsWorkflow().ExecuteAsync(
            MakeContext(adapter, gateway, new PolicyEngine(), PolicyLevel.SafeDev));

        Assert.IsType<WorkflowSuccess>(result);
        Assert.Equal(1, gateway.CallCount);
        Assert.Single(gateway.LastBatch!);
    }

    // ── Unknown tool → AskUser path ───────────────────────────────────────────

    [Fact]
    public async Task Action_UnknownToolType_GoesToApprovalGateway()
    {
        var gateway = new RecordingApprovalGateway(approve: true);
        var adapter = new FakeAgentAdapter();
        adapter.EnqueueSequence(
            new ActionRequestEvent("a1", "WeirdTool", "WeirdTool",
                Input: Json("""{"foo":"bar"}""")),
            new AgentDoneEvent(0, null));

        var result = await new FixDotnetTestsWorkflow().ExecuteAsync(
            MakeContext(adapter, gateway, new PolicyEngine()));

        Assert.IsType<WorkflowSuccess>(result);
        Assert.Equal(1, gateway.CallCount);
    }

    // ── Mixed: Allow + AskUser → only AskUser hits the gateway ────────────────

    [Fact]
    public async Task Mixed_AllowAndAskUser_OnlyAskUserBatched()
    {
        var gateway = new RecordingApprovalGateway(approve: true);
        var adapter = new FakeAgentAdapter();
        adapter.EnqueueSequence(
            // Whitelisted under TrustedLocal → Allow
            new ActionRequestEvent("a1", "Bash", "Bash",
                Input: Json("""{"command":"ls"}""")),
            // Network call under TrustedLocal → Allow
            new ActionRequestEvent("a2", "WebFetch", "WebFetch",
                Input: Json("""{"url":"https://example.com/x"}""")),
            // Mcp under TrustedLocal → AskUser
            new ActionRequestEvent("a3", "WeirdMcp", "WeirdMcp"),
            new AgentDoneEvent(0, null));

        var result = await new FixDotnetTestsWorkflow().ExecuteAsync(
            MakeContext(adapter, gateway, new PolicyEngine(), PolicyLevel.TrustedLocal));

        Assert.IsType<WorkflowSuccess>(result);
        Assert.Equal(1, gateway.CallCount);
        Assert.Single(gateway.LastBatch!); // only the unknown-tool action queued
    }
}
