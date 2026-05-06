using System;
using AgentWorkspace.Abstractions.Ids;
using AgentWorkspace.Abstractions.Sessions;

namespace AgentWorkspace.App.Wpf.Sessions;

/// <summary>
/// User-facing row in the session attach picker.
/// </summary>
public sealed class SessionChoiceItem
{
    private SessionChoiceItem(
        SessionId? sessionId,
        bool createsNewSession,
        bool isCurrent,
        string label,
        string description)
    {
        SessionId = sessionId;
        CreatesNewSession = createsNewSession;
        IsCurrent = isCurrent;
        Label = label;
        Description = description;
    }

    public SessionId? SessionId { get; }

    public bool CreatesNewSession { get; }

    public bool IsCurrent { get; }

    public string Label { get; }

    public string Description { get; }

    public static SessionChoiceItem FromSession(SessionInfo info, bool isCurrent)
    {
        ArgumentNullException.ThrowIfNull(info);

        var shortId = info.Id.ToString()[..6];
        var current = isCurrent ? " · 현재" : string.Empty;
        var root = string.IsNullOrWhiteSpace(info.WorkspaceRoot)
            ? "workspace root 없음"
            : info.WorkspaceRoot;
        var lastAttached = info.LastAttachedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");

        return new SessionChoiceItem(
            info.Id,
            createsNewSession: false,
            isCurrent,
            label: $"{info.Name} · {shortId}{current}",
            description: $"{root} · last attach {lastAttached}");
    }

    public static SessionChoiceItem NewSession() =>
        new(
            sessionId: null,
            createsNewSession: true,
            isCurrent: false,
            label: "새 세션 시작",
            description: "현재 기본 셸로 새 workspace session을 만듭니다.");
}
