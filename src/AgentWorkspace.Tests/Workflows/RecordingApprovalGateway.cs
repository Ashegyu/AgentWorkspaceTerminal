using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AgentWorkspace.Abstractions.Agents;
using AgentWorkspace.Abstractions.Workflows;

namespace AgentWorkspace.Tests.Workflows;

/// <summary>
/// Test double that records every <see cref="RequestApprovalAsync"/> invocation and returns
/// a fixed approve/deny verdict. Useful for asserting which actions actually reach the
/// approval UI after upstream policy filtering.
/// </summary>
internal sealed class RecordingApprovalGateway : IApprovalGateway
{
    private readonly bool _approve;

    public RecordingApprovalGateway(bool approve = true) => _approve = approve;

    public int CallCount { get; private set; }

    public IReadOnlyList<ActionRequestEvent>? LastBatch { get; private set; }

    public ValueTask<ApprovalDecision> RequestApprovalAsync(
        IReadOnlyList<ActionRequestEvent> actions,
        CancellationToken cancellationToken = default)
    {
        CallCount++;
        LastBatch = actions;
        var ids = actions.Select(a => a.ActionId).ToList();
        return ValueTask.FromResult(_approve
            ? new ApprovalDecision(true, ids, [])
            : new ApprovalDecision(false, [], ids));
    }
}
