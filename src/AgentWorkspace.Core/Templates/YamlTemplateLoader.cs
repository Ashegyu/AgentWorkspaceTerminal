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
/// Loads a <see cref="WorkspaceTemplate"/> from a YAML file.
/// Pipeline: read → YamlDotNet deserialize → structural map → cross-ref validate.
/// </summary>
public sealed class YamlTemplateLoader : IWorkspaceTemplateLoader
{
    private static readonly IDeserializer _yaml = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    public async ValueTask<WorkspaceTemplate> LoadAsync(string path, CancellationToken ct = default)
    {
        string content;
        try
        {
            content = await File.ReadAllTextAsync(path, ct);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new WorkspaceTemplateException($"Cannot read template file '{path}': {ex.Message}");
        }

        return ParseAndValidate(content, path);
    }

    internal static WorkspaceTemplate ParseAndValidate(string yaml, string sourceName = "<string>")
    {
        YamlWorkspaceDto dto;
        try
        {
            dto = _yaml.Deserialize<YamlWorkspaceDto>(yaml)
                ?? throw new WorkspaceTemplateException($"'{sourceName}' is empty.");
        }
        catch (WorkspaceTemplateException) { throw; }
        catch (Exception ex)
        {
            throw new WorkspaceTemplateException($"YAML parse error in '{sourceName}': {ex.Message}");
        }

        var template = MapTemplate(dto, sourceName);

        var errors = WorkspaceTemplateValidator.Validate(template);
        if (errors.Count > 0)
            throw new WorkspaceTemplateException(errors);

        return template;
    }

    // ── mapping ──────────────────────────────────────────────────────────────

    private static WorkspaceTemplate MapTemplate(YamlWorkspaceDto dto, string src)
    {
        if (string.IsNullOrWhiteSpace(dto.Name))
            throw new WorkspaceTemplateException($"'{src}': 'name' is required and must not be empty.");

        if (dto.Panes is null || dto.Panes.Count == 0)
            throw new WorkspaceTemplateException($"'{src}': 'panes' must contain at least one entry.");

        if (dto.Layout is null)
            throw new WorkspaceTemplateException($"'{src}': 'layout' is required.");

        var panes = dto.Panes.Select((p, i) => MapPane(p, src, i)).ToList();
        var layout = MapLayout(dto.Layout, src);

        return new WorkspaceTemplate(dto.Name.Trim(), dto.Description, panes, layout, dto.Focus);
    }

    private static PaneTemplate MapPane(YamlPaneDto dto, string src, int index)
    {
        if (string.IsNullOrWhiteSpace(dto.Id))
            throw new WorkspaceTemplateException($"'{src}': panes[{index}].id is required.");

        if (string.IsNullOrWhiteSpace(dto.Command))
            throw new WorkspaceTemplateException($"'{src}': panes[{index}].command is required.");

        return new PaneTemplate(
            dto.Id.Trim(),
            dto.Command.Trim(),
            (IReadOnlyList<string>?)dto.Args ?? [],
            dto.Cwd,
            dto.Env is { Count: > 0 } ? dto.Env : null);
    }

    private static LayoutNodeTemplate MapLayout(YamlLayoutNodeDto dto, string src)
    {
        bool isLeaf = dto.Pane is not null;
        bool isSplit = dto.Split is not null;

        if (isLeaf && isSplit)
            throw new WorkspaceTemplateException(
                $"'{src}': a layout node cannot have both 'pane' and 'split'.");

        if (isLeaf)
            return new PaneRefTemplate(dto.Pane!.Trim());

        if (isSplit)
        {
            if (dto.Ratio is null)
                throw new WorkspaceTemplateException($"'{src}': split node requires 'ratio'.");
            if (dto.A is null)
                throw new WorkspaceTemplateException($"'{src}': split node requires 'a'.");
            if (dto.B is null)
                throw new WorkspaceTemplateException($"'{src}': split node requires 'b'.");

            if (dto.Ratio < 0.05 || dto.Ratio > 0.95)
                throw new WorkspaceTemplateException(
                    $"'{src}': split ratio {dto.Ratio:F2} is outside [0.05, 0.95].");

            var direction = dto.Split!.Trim().ToLowerInvariant() switch
            {
                "horizontal" => SplitDirection.Horizontal,
                "vertical" => SplitDirection.Vertical,
                _ => throw new WorkspaceTemplateException(
                    $"'{src}': unknown split direction '{dto.Split}'. Expected 'horizontal' or 'vertical'.")
            };

            return new SplitNodeTemplate(
                direction, dto.Ratio.Value,
                MapLayout(dto.A, src),
                MapLayout(dto.B, src));
        }

        throw new WorkspaceTemplateException(
            $"'{src}': a layout node must have either 'pane' or 'split'.");
    }

    // ── private YAML DTOs ────────────────────────────────────────────────────

    private sealed class YamlWorkspaceDto
    {
        public string? Name { get; set; }
        public string? Description { get; set; }
        public string? Version { get; set; }
        public List<YamlPaneDto>? Panes { get; set; }
        public YamlLayoutNodeDto? Layout { get; set; }
        public string? Focus { get; set; }
    }

    private sealed class YamlPaneDto
    {
        public string? Id { get; set; }
        public string? Command { get; set; }
        public List<string>? Args { get; set; }
        public string? Cwd { get; set; }
        public Dictionary<string, string>? Env { get; set; }
    }

    private sealed class YamlLayoutNodeDto
    {
        public string? Pane { get; set; }
        public string? Split { get; set; }
        public double? Ratio { get; set; }
        public YamlLayoutNodeDto? A { get; set; }
        public YamlLayoutNodeDto? B { get; set; }
    }
}
