using System.IO;
using AgentWorkspace.Abstractions.Layout;
using AgentWorkspace.Abstractions.Templates;
using AgentWorkspace.Core.Templates;

namespace AgentWorkspace.Tests.Templates;

public sealed class YamlTemplateLoaderTests
{
    // ── inline YAML helpers ──────────────────────────────────────────────────

    private static WorkspaceTemplate Parse(string yaml) =>
        YamlTemplateLoader.ParseAndValidate(yaml);

    private static WorkspaceTemplateException ParseFails(string yaml)
    {
        var ex = Assert.Throws<WorkspaceTemplateException>(() =>
            YamlTemplateLoader.ParseAndValidate(yaml));
        return ex;
    }

    // ── happy-path: minimal single pane ──────────────────────────────────────

    [Fact]
    public void Parse_SinglePane_ReturnsTemplate()
    {
        const string yaml = """
            name: Minimal
            panes:
              - id: shell
                command: cmd
            layout:
              pane: shell
            """;

        var t = Parse(yaml);

        Assert.Equal("Minimal", t.Name);
        Assert.Single(t.Panes);
        Assert.Equal("shell", t.Panes[0].Id);
        Assert.Equal("cmd", t.Panes[0].Command);
        Assert.IsType<PaneRefTemplate>(t.Layout);
        Assert.Equal("shell", ((PaneRefTemplate)t.Layout).Slot);
        Assert.Null(t.Focus);
    }

    [Fact]
    public void Parse_WithAllOptionalPaneFields_MapsCorrectly()
    {
        const string yaml = """
            name: Full Pane
            panes:
              - id: editor
                command: nvim
                args: [foo.txt]
                cwd: C:\src
                env:
                  FOO: bar
            layout:
              pane: editor
            focus: editor
            """;

        var t = Parse(yaml);

        var pane = t.Panes[0];
        Assert.Equal("editor", pane.Id);
        Assert.Equal("nvim", pane.Command);
        Assert.Equal(["foo.txt"], pane.Args);
        Assert.Equal("C:\\src", pane.Cwd);
        Assert.NotNull(pane.Env);
        Assert.Equal("bar", pane.Env!["FOO"]);
        Assert.Equal("editor", t.Focus);
    }

    // ── happy-path: split layout ─────────────────────────────────────────────

    [Fact]
    public void Parse_HorizontalSplit_ReturnsSplitNode()
    {
        const string yaml = """
            name: Two Pane
            panes:
              - id: left
                command: cmd
              - id: right
                command: cmd
            layout:
              split: horizontal
              ratio: 0.65
              a:
                pane: left
              b:
                pane: right
            """;

        var t = Parse(yaml);

        var split = Assert.IsType<SplitNodeTemplate>(t.Layout);
        Assert.Equal(SplitDirection.Horizontal, split.Direction);
        Assert.Equal(0.65, split.Ratio, precision: 10);
        Assert.Equal("left", ((PaneRefTemplate)split.A).Slot);
        Assert.Equal("right", ((PaneRefTemplate)split.B).Slot);
    }

    [Fact]
    public void Parse_VerticalSplit_ReturnsSplitNode()
    {
        const string yaml = """
            name: Vertical
            panes:
              - id: top
                command: cmd
              - id: bottom
                command: cmd
            layout:
              split: vertical
              ratio: 0.5
              a:
                pane: top
              b:
                pane: bottom
            """;

        var t = Parse(yaml);

        var split = Assert.IsType<SplitNodeTemplate>(t.Layout);
        Assert.Equal(SplitDirection.Vertical, split.Direction);
    }

    // ── structural errors ────────────────────────────────────────────────────

    [Fact]
    public void Parse_EmptyYaml_Throws()
    {
        var ex = ParseFails("   ");
        Assert.NotNull(ex.Message);
    }

    [Fact]
    public void Parse_MissingName_Throws()
    {
        const string yaml = """
            panes:
              - id: shell
                command: cmd
            layout:
              pane: shell
            """;

        var ex = ParseFails(yaml);
        Assert.Contains("name", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_EmptyPanes_Throws()
    {
        const string yaml = """
            name: Bad
            panes: []
            layout:
              pane: shell
            """;

        var ex = ParseFails(yaml);
        Assert.Contains("panes", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_MissingLayout_Throws()
    {
        const string yaml = """
            name: Bad
            panes:
              - id: shell
                command: cmd
            """;

        var ex = ParseFails(yaml);
        Assert.Contains("layout", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_MissingPaneId_Throws()
    {
        const string yaml = """
            name: Bad
            panes:
              - command: cmd
            layout:
              pane: shell
            """;

        var ex = ParseFails(yaml);
        Assert.Contains("id", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_MissingPaneCommand_Throws()
    {
        const string yaml = """
            name: Bad
            panes:
              - id: shell
            layout:
              pane: shell
            """;

        var ex = ParseFails(yaml);
        Assert.Contains("command", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_LayoutNodeBothPaneAndSplit_Throws()
    {
        const string yaml = """
            name: Bad
            panes:
              - id: shell
                command: cmd
            layout:
              pane: shell
              split: horizontal
              ratio: 0.5
              a:
                pane: shell
              b:
                pane: shell
            """;

        var ex = ParseFails(yaml);
        Assert.NotNull(ex);
    }

    [Fact]
    public void Parse_LayoutNodeNeitherPaneNorSplit_Throws()
    {
        const string yaml = """
            name: Bad
            panes:
              - id: shell
                command: cmd
            layout:
              ratio: 0.5
            """;

        var ex = ParseFails(yaml);
        Assert.NotNull(ex);
    }

    [Fact]
    public void Parse_SplitMissingRatio_Throws()
    {
        const string yaml = """
            name: Bad
            panes:
              - id: a
                command: cmd
              - id: b
                command: cmd
            layout:
              split: horizontal
              a:
                pane: a
              b:
                pane: b
            """;

        var ex = ParseFails(yaml);
        Assert.Contains("ratio", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_SplitRatioTooLow_Throws()
    {
        const string yaml = """
            name: Bad
            panes:
              - id: a
                command: cmd
              - id: b
                command: cmd
            layout:
              split: horizontal
              ratio: 0.01
              a:
                pane: a
              b:
                pane: b
            """;

        var ex = ParseFails(yaml);
        Assert.Contains("0.01", ex.Message);
    }

    [Fact]
    public void Parse_SplitRatioTooHigh_Throws()
    {
        const string yaml = """
            name: Bad
            panes:
              - id: a
                command: cmd
              - id: b
                command: cmd
            layout:
              split: horizontal
              ratio: 0.99
              a:
                pane: a
              b:
                pane: b
            """;

        var ex = ParseFails(yaml);
        Assert.Contains("0.99", ex.Message);
    }

    [Fact]
    public void Parse_UnknownSplitDirection_Throws()
    {
        const string yaml = """
            name: Bad
            panes:
              - id: a
                command: cmd
              - id: b
                command: cmd
            layout:
              split: diagonal
              ratio: 0.5
              a:
                pane: a
              b:
                pane: b
            """;

        var ex = ParseFails(yaml);
        Assert.Contains("diagonal", ex.Message);
    }

    // ── cross-ref errors (validator) ─────────────────────────────────────────

    [Fact]
    public void Parse_LayoutReferencesUnknownPane_Throws()
    {
        const string yaml = """
            name: Bad
            panes:
              - id: shell
                command: cmd
            layout:
              pane: typo
            """;

        var ex = ParseFails(yaml);
        Assert.Contains("typo", ex.Message);
    }

    [Fact]
    public void Parse_FocusReferencesUnknownPane_Throws()
    {
        const string yaml = """
            name: Bad
            panes:
              - id: shell
                command: cmd
            layout:
              pane: shell
            focus: ghost
            """;

        var ex = ParseFails(yaml);
        Assert.Contains("ghost", ex.Message);
    }

    // ── example files ────────────────────────────────────────────────────────

    [Fact]
    public async Task LoadAsync_BasicYaml_ReturnsValidTemplate()
    {
        var path = Path.Combine("TestData", "basic.yaml");
        Skip.If(!File.Exists(path), "TestData/basic.yaml not found — check CopyToOutputDirectory");

        var loader = new YamlTemplateLoader();
        var t = await loader.LoadAsync(path);

        Assert.Equal("Basic Dev", t.Name);
        Assert.Equal(2, t.Panes.Count);
        Assert.IsType<SplitNodeTemplate>(t.Layout);
        Assert.Equal("editor", t.Focus);
    }

    [Fact]
    public async Task LoadAsync_ThreePaneYaml_ReturnsValidTemplate()
    {
        var path = Path.Combine("TestData", "three-pane.yaml");
        Skip.If(!File.Exists(path), "TestData/three-pane.yaml not found — check CopyToOutputDirectory");

        var loader = new YamlTemplateLoader();
        var t = await loader.LoadAsync(path);

        Assert.Equal("Three-Pane Agent", t.Name);
        Assert.Equal(3, t.Panes.Count);
        var outer = Assert.IsType<SplitNodeTemplate>(t.Layout);
        Assert.Equal(SplitDirection.Vertical, outer.Direction);
        Assert.Equal(0.65, outer.Ratio, precision: 10);
        Assert.IsType<SplitNodeTemplate>(outer.A);
        Assert.Equal("editor", t.Focus);
    }

    [Fact]
    public async Task LoadAsync_NonExistentFile_ThrowsWorkspaceTemplateException()
    {
        var loader = new YamlTemplateLoader();
        await Assert.ThrowsAsync<WorkspaceTemplateException>(
            () => loader.LoadAsync("no-such-file.yaml").AsTask());
    }
}
