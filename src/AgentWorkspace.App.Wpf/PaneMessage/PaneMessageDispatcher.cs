using System;
using System.Collections.Generic;
using AgentWorkspace.Abstractions.Ids;
using AgentWorkspace.Abstractions.Mesh;

namespace AgentWorkspace.App.Wpf.PaneMessage;

internal static class PaneMessageDispatcher
{
    public static bool TryCreateSendMessage(
        IReadOnlyCollection<PaneId> openPanes,
        PaneId targetPane,
        string? text,
        out MeshMessage? message)
    {
        message = null;
        if (string.IsNullOrEmpty(text)) return false;
        if (!openPanes.Contains(targetPane)) return false;

        message = new MeshMessage(
            Topic: $"pane.{targetPane}.send",
            Timestamp: DateTimeOffset.UtcNow,
            Kind: "send",
            Payload: text);
        return true;
    }
}
