using System;
using System.Collections.Generic;
using System.Linq;
using AgentWorkspace.Abstractions.Ids;
using AgentWorkspace.Abstractions.Mesh;

namespace AgentWorkspace.App.Wpf.PaneMessage;

internal static class PaneMessageDispatcher
{
    public static IReadOnlyList<PaneChoiceItem> BuildChoices(
        IReadOnlyList<PaneId> layoutPaneOrder,
        IReadOnlyCollection<PaneId> openPanes,
        PaneId focusedPane,
        Func<PaneId, string?> titleProvider)
    {
        var openSet = openPanes.ToHashSet();
        var choices = new List<PaneChoiceItem>();
        var index = 1;

        foreach (var pane in layoutPaneOrder)
        {
            if (!openSet.Contains(pane)) continue;
            choices.Add(new PaneChoiceItem(index, pane, pane == focusedPane, titleProvider(pane)));
            index++;
        }

        return choices;
    }

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
