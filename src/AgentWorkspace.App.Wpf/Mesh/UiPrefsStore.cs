using System;
using System.IO;
using System.Text.Json;
using AgentWorkspace.App.Wpf.Agents;

namespace AgentWorkspace.App.Wpf.Mesh;

/// <summary>
/// Persists and restores sort/filter UI preferences to/from
/// <c>~/.agentworkspace/ui-prefs.json</c>. All I/O is best-effort: any read or
/// write failure silently falls back to defaults so a corrupted or missing file
/// never blocks application startup.
/// </summary>
public static class UiPrefsStore
{
    public static readonly string DefaultPrefsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".agentworkspace",
        "ui-prefs.json");

    private static readonly JsonSerializerOptions s_writeOptions =
        new() { WriteIndented = true };

    /// <summary>Loaded preference snapshot. Defaults apply when the file is absent or corrupt.</summary>
    public sealed record UiPrefs(
        SubAgentCardSortMode   SubAgentCardSortMode   = SubAgentCardSortMode.NewestFirst,
        SubAgentCardFilterMode SubAgentCardFilterMode = SubAgentCardFilterMode.All,
        string DefaultAgentProviderId = AgentProviderRegistry.BuiltInDefaultProviderId);

    /// <summary>
    /// Reads the prefs file and returns parsed values, or defaults on any failure.
    /// </summary>
    /// <param name="path">Override path for testing. Uses <see cref="DefaultPrefsPath"/> when null.</param>
    public static UiPrefs Load(string? path = null)
    {
        string filePath = path ?? DefaultPrefsPath;
        try
        {
            if (!File.Exists(filePath)) return new UiPrefs();

            string json = File.ReadAllText(filePath);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var sort = SubAgentCardSortMode.NewestFirst;
            if (root.TryGetProperty("subAgentCardSortMode", out var sortProp) &&
                sortProp.GetString() is string sortStr &&
                Enum.TryParse(sortStr, out SubAgentCardSortMode parsedSort) &&
                Enum.IsDefined(parsedSort))
            {
                sort = parsedSort;
            }

            var filter = SubAgentCardFilterMode.All;
            if (root.TryGetProperty("subAgentCardFilterMode", out var filterProp) &&
                filterProp.GetString() is string filterStr &&
                Enum.TryParse(filterStr, out SubAgentCardFilterMode parsedFilter) &&
                Enum.IsDefined(parsedFilter))
            {
                filter = parsedFilter;
            }

            var defaultProviderId = AgentProviderRegistry.BuiltInDefaultProviderId;
            if (root.TryGetProperty("defaultAgentProviderId", out var providerProp) &&
                providerProp.GetString() is string providerStr &&
                !string.IsNullOrWhiteSpace(providerStr))
            {
                defaultProviderId = providerStr.Trim();
            }

            return new UiPrefs(sort, filter, defaultProviderId);
        }
        catch
        {
            return new UiPrefs();
        }
    }

    /// <summary>
    /// Writes current sort/filter state to the prefs file. Silently swallows I/O errors.
    /// </summary>
    /// <param name="sort">Sort mode to persist.</param>
    /// <param name="filter">Filter mode to persist.</param>
    /// <param name="path">Override path for testing. Uses <see cref="DefaultPrefsPath"/> when null.</param>
    public static void Save(SubAgentCardSortMode sort, SubAgentCardFilterMode filter, string? path = null) =>
        Save(sort, filter, defaultAgentProviderId: AgentProviderRegistry.BuiltInDefaultProviderId, path);

    /// <summary>
    /// Writes current sort/filter/default-provider state to the prefs file. Silently swallows I/O errors.
    /// </summary>
    /// <param name="sort">Sort mode to persist.</param>
    /// <param name="filter">Filter mode to persist.</param>
    /// <param name="defaultAgentProviderId">Provider id used by workflows and default agent actions.</param>
    /// <param name="path">Override path for testing. Uses <see cref="DefaultPrefsPath"/> when null.</param>
    public static void Save(
        SubAgentCardSortMode sort,
        SubAgentCardFilterMode filter,
        string defaultAgentProviderId,
        string? path = null)
    {
        string filePath = path ?? DefaultPrefsPath;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);

            var obj = new
            {
                subAgentCardSortMode = sort.ToString(),
                subAgentCardFilterMode = filter.ToString(),
                defaultAgentProviderId = string.IsNullOrWhiteSpace(defaultAgentProviderId)
                    ? AgentProviderRegistry.BuiltInDefaultProviderId
                    : defaultAgentProviderId.Trim(),
            };
            string json = JsonSerializer.Serialize(obj, s_writeOptions);
            File.WriteAllText(filePath, json);
        }
        catch
        {
            // best-effort
        }
    }
}
