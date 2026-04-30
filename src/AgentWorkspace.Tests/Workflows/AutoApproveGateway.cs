using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AgentWorkspace.Abstractions.Agents;
using AgentWorkspace.Abstractions.Workflows;

namespace AgentWorkspace.Tests.Workflows;

/// <summary>Test double: approves every action without showing any UI.</summary>
internal sealed class AutoApproveGateway : IApprovalGateway
{
    public static readonly AutoApproveGateway Instance = new();

    public ValueTask<ApprovalDecision> RequestApprovalAsync(
        IReadOnlyList<ActionRequestEvent> actions,
        CancellationToken cancellationToken = default)
    {
        var ids = actions.Select(a => a.ActionId).ToList();
        return ValueTask.FromResult(new ApprovalDecision(true, ids, []));
    }
}

/// <summary>Test double: denies every action.</summary>
internal sealed class AutoDenyGateway : IApprovalGateway
{
    public static readonly AutoDenyGateway Instance = new();

    public ValueTask<ApprovalDecision> RequestApprovalAsync(
        IReadOnlyList<ActionRequestEvent> actions,
        CancellationToken cancellationToken = default)
    {
        var ids = actions.Select(a => a.ActionId).ToList();
        return ValueTask.FromResult(new ApprovalDecision(false, [], ids));
    }
}
