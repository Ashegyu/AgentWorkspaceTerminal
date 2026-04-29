using System.Collections.Generic;
using System.Linq;

namespace AgentWorkspace.Abstractions.Templates;

/// <summary>
/// Cross-ref validation for a parsed <see cref="WorkspaceTemplate"/>.
/// Structural / schema validation is the loader's responsibility;
/// this class checks semantic invariants that JSON Schema cannot express.
/// </summary>
public static class WorkspaceTemplateValidator
{
    /// <summary>
    /// Returns a list of human-readable error messages. Empty list means the template is valid.
    /// </summary>
    public static IReadOnlyList<string> Validate(WorkspaceTemplate template)
    {
        var errors = new List<string>();
        var knownIds = new HashSet<string>();

        foreach (var pane in template.Panes)
        {
            if (!knownIds.Add(pane.Id))
                errors.Add($"Duplicate pane id '{pane.Id}'.");
        }

        var layoutSlots = CollectSlots(template.Layout);
        foreach (var slot in layoutSlots)
        {
            if (!knownIds.Contains(slot))
                errors.Add($"Layout references unknown pane id '{slot}'.");
        }

        if (template.Focus is not null && !knownIds.Contains(template.Focus))
            errors.Add($"Focus references unknown pane id '{template.Focus}'.");

        return errors;
    }

    private static IEnumerable<string> CollectSlots(LayoutNodeTemplate node) =>
        node switch
        {
            PaneRefTemplate leaf => [leaf.Slot],
            SplitNodeTemplate split => CollectSlots(split.A).Concat(CollectSlots(split.B)),
            _ => []
        };
}
