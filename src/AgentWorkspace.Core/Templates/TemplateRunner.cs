using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AgentWorkspace.Abstractions.Channels;
using AgentWorkspace.Abstractions.Ids;
using AgentWorkspace.Abstractions.Layout;
using AgentWorkspace.Abstractions.Pty;
using AgentWorkspace.Abstractions.Templates;

namespace AgentWorkspace.Core.Templates;

/// <summary>
/// Result returned by <see cref="TemplateRunner.RunAsync"/>. Bundles the initial
/// <see cref="LayoutSnapshot"/> and the slot-name → runtime <see cref="PaneId"/> map so the
/// caller can wire the snapshot into <c>BinaryLayoutManager</c> and track panes by slot name.
/// </summary>
public sealed record TemplateRunResult(
    LayoutSnapshot Layout,
    IReadOnlyDictionary<string, PaneId> SlotToPaneId);

/// <summary>
/// Translates a <see cref="WorkspaceTemplate"/> into live panes by driving
/// <see cref="IControlChannel"/>. On any start failure, already-started panes are
/// force-closed before the exception propagates.
/// </summary>
public sealed class TemplateRunner
{
    private readonly IControlChannel _control;
    private readonly short _defaultCols;
    private readonly short _defaultRows;

    public TemplateRunner(IControlChannel control, short defaultCols = 220, short defaultRows = 50)
    {
        _control = control;
        _defaultCols = defaultCols;
        _defaultRows = defaultRows;
    }

    public async ValueTask<TemplateRunResult> RunAsync(
        WorkspaceTemplate template,
        CancellationToken cancellationToken = default)
    {
        var slotMap = BuildSlotMap(template);
        await StartPanesAsync(template, slotMap, cancellationToken);
        var root = BuildNode(template.Layout, slotMap);
        var focused = ResolveFocus(template, slotMap);
        return new TemplateRunResult(new LayoutSnapshot(root, focused), slotMap);
    }

    private static Dictionary<string, PaneId> BuildSlotMap(WorkspaceTemplate template)
    {
        var map = new Dictionary<string, PaneId>(template.Panes.Count);
        foreach (var pane in template.Panes)
            map[pane.Id] = PaneId.New();
        return map;
    }

    private async Task StartPanesAsync(
        WorkspaceTemplate template,
        IReadOnlyDictionary<string, PaneId> slotMap,
        CancellationToken ct)
    {
        var started = new List<PaneId>(template.Panes.Count);
        try
        {
            foreach (var pane in template.Panes)
            {
                var opts = new PaneStartOptions(
                    pane.Command,
                    pane.Args,
                    pane.Cwd,
                    pane.Env,
                    _defaultCols,
                    _defaultRows);
                await _control.StartPaneAsync(slotMap[pane.Id], opts, ct);
                started.Add(slotMap[pane.Id]);
            }
        }
        catch
        {
            foreach (var id in started)
                try { await _control.ClosePaneAsync(id, KillMode.Force, CancellationToken.None); }
                catch { /* ignore rollback errors */ }
            throw;
        }
    }

    private static LayoutNode BuildNode(
        LayoutNodeTemplate node,
        IReadOnlyDictionary<string, PaneId> slotMap) => node switch
        {
            PaneRefTemplate leaf => new PaneNode(LayoutId.New(), slotMap[leaf.Slot]),
            SplitNodeTemplate split => new SplitNode(
                LayoutId.New(),
                split.Direction,
                split.Ratio,
                BuildNode(split.A, slotMap),
                BuildNode(split.B, slotMap)),
            _ => throw new InvalidOperationException($"Unknown layout node type: {node.GetType().Name}")
        };

    private static PaneId ResolveFocus(
        WorkspaceTemplate template,
        IReadOnlyDictionary<string, PaneId> slotMap) =>
        template.Focus is not null
            ? slotMap[template.Focus]
            : slotMap[template.Panes[0].Id];
}
