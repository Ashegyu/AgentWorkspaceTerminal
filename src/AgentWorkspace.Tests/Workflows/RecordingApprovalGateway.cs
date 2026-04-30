using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
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
    private readonly Queue<bool>? _scriptedDecisions;

    public RecordingApprovalGateway(bool approve = true)
    {
        _approve = approve;
    }

    /// <summary>Returns each scripted verdict in turn; falls back to <paramref name="defaultApprove"/> after exhausting the script.</summary>
    public RecordingApprovalGateway(IEnumerable<bool> scripted, bool defaultApprove = true)
    {
        _approve = defaultApprove;
        _scriptedDecisions = new Queue<bool>(scripted);
    }

    public int CallCount { get; private set; }

    public IReadOnlyList<ApprovalRequestItem>? LastBatch { get; private set; }

    /// <summary>All batches observed in the order they were received.</summary>
    public List<IReadOnlyList<ApprovalRequestItem>> Batches { get; } = new();

    public ValueTask<ApprovalDecision> RequestApprovalAsync(
        IReadOnlyList<ApprovalRequestItem> items,
        CancellationToken cancellationToken = default)
    {
        CallCount++;
        LastBatch = items;
        Batches.Add(items);

        var approve = _scriptedDecisions is { Count: > 0 } q ? q.Dequeue() : _approve;
        var ids = items.Select(i => i.Action.ActionId).ToList();
        return ValueTask.FromResult(approve
            ? new ApprovalDecision(true, ids, [])
            : new ApprovalDecision(false, [], ids));
    }
}
