using System.Collections.Generic;

namespace AgentWorkspace.Abstractions.Workflows;

/// <summary>User's batch decision on a set of proposed actions.</summary>
public sealed record ApprovalDecision(
    bool Approved,
    IReadOnlyList<string> ApprovedActionIds,
    IReadOnlyList<string> DeniedActionIds);
