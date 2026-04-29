using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AgentWorkspace.Abstractions.Layout;
using AgentWorkspace.Abstractions.Templates;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace AgentWorkspace.Core.Templates;

/// <summary>
/// Serializes a <see cref="WorkspaceTemplate"/> to YAML.
/// Mirror of <see cref="YamlTemplateLoader"/> — uses the same camelCase convention and
/// omits null fields so round-tripped files stay minimal.
/// </summary>
public static class WorkspaceTemplateSerializer
{
    private static readonly ISerializer _yaml = new SerializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull)
        .Build();

    public static string Serialize(WorkspaceTemplate template)
    {
        ArgumentNullException.ThrowIfNull(template);
        return _yaml.Serialize(ToDto(template));
    }

    public static async ValueTask SaveAsync(
        WorkspaceTemplate template,
        string path,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var yaml = Serialize(template);
        await File.WriteAllTextAsync(path, yaml, ct);
    }

    // ── mapping ──────────────────────────────────────────────────────────────

    private static YamlWorkspaceOut ToDto(WorkspaceTemplate t) => new()
    {
        Name = t.Name,
        Description = t.Description,
        Panes = t.Panes.Select(ToPaneDto).ToList(),
        Layout = ToLayoutDto(t.Layout),
        Focus = t.Focus,
    };

    private static YamlPaneOut ToPaneDto(PaneTemplate p) => new()
    {
        Id = p.Id,
        Command = p.Command,
        Args = p.Args.Count > 0 ? new List<string>(p.Args) : null,
        Cwd = p.Cwd,
        Env = p.Env is { Count: > 0 }
            ? new Dictionary<string, string>(p.Env)
            : null,
    };

    private static YamlLayoutNodeOut ToLayoutDto(LayoutNodeTemplate node) => node switch
    {
        PaneRefTemplate leaf => new YamlLayoutNodeOut { Pane = leaf.Slot },
        SplitNodeTemplate split => new YamlLayoutNodeOut
        {
            Split = split.Direction switch
            {
                SplitDirection.Horizontal => "horizontal",
                SplitDirection.Vertical   => "vertical",
                _ => throw new InvalidOperationException(
                    $"Unknown split direction: {split.Direction}"),
            },
            Ratio = split.Ratio,
            A = ToLayoutDto(split.A),
            B = ToLayoutDto(split.B),
        },
        _ => throw new InvalidOperationException(
            $"Unknown layout node type: {node.GetType().Name}"),
    };

    // ── private output DTOs ──────────────────────────────────────────────────

    private sealed class YamlWorkspaceOut
    {
        public string? Name { get; set; }
        public string? Description { get; set; }
        public List<YamlPaneOut>? Panes { get; set; }
        public YamlLayoutNodeOut? Layout { get; set; }
        public string? Focus { get; set; }
    }

    private sealed class YamlPaneOut
    {
        public string? Id { get; set; }
        public string? Command { get; set; }
        public List<string>? Args { get; set; }
        public string? Cwd { get; set; }
        public Dictionary<string, string>? Env { get; set; }
    }

    private sealed class YamlLayoutNodeOut
    {
        public string? Pane { get; set; }
        public string? Split { get; set; }
        public double? Ratio { get; set; }
        public YamlLayoutNodeOut? A { get; set; }
        public YamlLayoutNodeOut? B { get; set; }
    }
}
