using System;
using AgentWorkspace.Abstractions.Ids;

namespace AgentWorkspace.App.Wpf.Sessions;

internal enum SessionSwitchAction
{
    StartNew,
    AttachExisting,
    AlreadyAttached,
    InvalidSelection,
}

internal sealed record SessionSwitchDecision(
    SessionSwitchAction Action,
    SessionId? SessionId);

internal static class SessionSwitchPlanner
{
    public static SessionSwitchDecision Decide(
        SessionChoiceItem choice,
        SessionId? currentSessionId)
    {
        ArgumentNullException.ThrowIfNull(choice);

        if (choice.CreatesNewSession)
        {
            return new SessionSwitchDecision(SessionSwitchAction.StartNew, null);
        }

        if (choice.SessionId is not { } selectedSessionId)
        {
            return new SessionSwitchDecision(SessionSwitchAction.InvalidSelection, null);
        }

        return currentSessionId == selectedSessionId
            ? new SessionSwitchDecision(SessionSwitchAction.AlreadyAttached, selectedSessionId)
            : new SessionSwitchDecision(SessionSwitchAction.AttachExisting, selectedSessionId);
    }
}
