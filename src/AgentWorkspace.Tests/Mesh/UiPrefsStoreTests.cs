using System.IO;
using AgentWorkspace.App.Wpf.Mesh;
using Xunit;

namespace AgentWorkspace.Tests.Mesh;

/// <summary>
/// Tests for <see cref="UiPrefsStore"/>. Each test redirects I/O to a temp
/// directory via the optional <c>path</c> override so the real
/// <c>~/.agentworkspace/ui-prefs.json</c> is never touched.
/// </summary>
public sealed class UiPrefsStoreTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _prefsFile;

    public UiPrefsStoreTests()
    {
        _tempDir  = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        _prefsFile = Path.Combine(_tempDir, "ui-prefs.json");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); }
        catch { /* best-effort */ }
    }

    // ── Tests ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void Load_WhenFileAbsent_ReturnsDefaults()
    {
        var prefs = UiPrefsStore.Load(_prefsFile);   // file does not exist yet

        Assert.Equal(SubAgentCardSortMode.NewestFirst, prefs.SubAgentCardSortMode);
        Assert.Equal(SubAgentCardFilterMode.All,       prefs.SubAgentCardFilterMode);
    }

    [Fact]
    public void SaveAndLoad_RoundTrip_PreservesValues()
    {
        UiPrefsStore.Save(SubAgentCardSortMode.StatusGrouped, SubAgentCardFilterMode.ExternalOnly, _prefsFile);
        var prefs = UiPrefsStore.Load(_prefsFile);

        Assert.Equal(SubAgentCardSortMode.StatusGrouped,  prefs.SubAgentCardSortMode);
        Assert.Equal(SubAgentCardFilterMode.ExternalOnly, prefs.SubAgentCardFilterMode);
    }

    [Fact]
    public void Load_CorruptJson_ReturnsDefaults()
    {
        File.WriteAllText(_prefsFile, "{ this is not valid JSON !!! }");

        var prefs = UiPrefsStore.Load(_prefsFile);

        Assert.Equal(SubAgentCardSortMode.NewestFirst, prefs.SubAgentCardSortMode);
        Assert.Equal(SubAgentCardFilterMode.All,       prefs.SubAgentCardFilterMode);
    }

    [Fact]
    public void Load_UnknownEnumString_FallsBackToDefault()
    {
        // A future enum value written by a newer build should not crash older builds.
        File.WriteAllText(_prefsFile, """
            {
              "subAgentCardSortMode": "SomeFutureMode",
              "subAgentCardFilterMode": "SomeFutureFilter"
            }
            """);

        var prefs = UiPrefsStore.Load(_prefsFile);

        Assert.Equal(SubAgentCardSortMode.NewestFirst, prefs.SubAgentCardSortMode);
        Assert.Equal(SubAgentCardFilterMode.All,       prefs.SubAgentCardFilterMode);
    }

    [Fact]
    public void Load_NumericEnumString_FallsBackToDefault()
    {
        // Enum.TryParse succeeds on numeric strings like "9999", producing an undefined
        // enum value. Enum.IsDefined must reject these so invalid values never reach the view.
        File.WriteAllText(_prefsFile,
            "{\"subAgentCardSortMode\":\"9999\",\"subAgentCardFilterMode\":\"9999\"}");

        var prefs = UiPrefsStore.Load(_prefsFile);

        Assert.Equal(SubAgentCardSortMode.NewestFirst, prefs.SubAgentCardSortMode);
        Assert.Equal(SubAgentCardFilterMode.All,       prefs.SubAgentCardFilterMode);
    }

    [Fact]
    public void Save_CreatesDirectoryIfAbsent()
    {
        string nestedFile = Path.Combine(_tempDir, "sub", "nested", "ui-prefs.json");

        UiPrefsStore.Save(SubAgentCardSortMode.OldestFirst, SubAgentCardFilterMode.RunningOnly, nestedFile);

        Assert.True(File.Exists(nestedFile));
        var prefs = UiPrefsStore.Load(nestedFile);
        Assert.Equal(SubAgentCardSortMode.OldestFirst,   prefs.SubAgentCardSortMode);
        Assert.Equal(SubAgentCardFilterMode.RunningOnly, prefs.SubAgentCardFilterMode);
    }
}
