using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using AgentWorkspace.Abstractions.Layout;
using AgentWorkspace.Abstractions.Templates;
using AgentWorkspace.Core.Templates;

namespace AgentWorkspace.Tests.Templates;

public sealed class WorkspaceTemplateSerializerTests
{
    // ── test templates ───────────────────────────────────────────────────────

    private static WorkspaceTemplate SinglePane(string name = "Test") =>
        new(
            Name: name,
            Description: null,
            Panes: [new PaneTemplate("shell", "pwsh", [], null, null)],
            Layout: new PaneRefTemplate("shell"),
            Focus: null);

    private static WorkspaceTemplate TwoPane() =>
        new(
            Name: "Two",
            Description: "test desc",
            Panes: [
                new PaneTemplate("left", "cmd", [], "C:\\", null),
                new PaneTemplate("right", "pwsh", ["-NoExit", "-Command", "cls"], null,
                    new Dictionary<string, string> { ["FOO"] = "bar" })
            ],
            Layout: new SplitNodeTemplate(
                SplitDirection.Horizontal, 0.6,
                new PaneRefTemplate("left"),
                new PaneRefTemplate("right")),
            Focus: "right");

    // ── serialize field presence ─────────────────────────────────────────────

    [Fact]
    public void Serialize_SinglePane_ContainsNameAndCommand()
    {
        var yaml = WorkspaceTemplateSerializer.Serialize(SinglePane("MyWs"));

        Assert.Contains("name: MyWs", yaml);
        Assert.Contains("command: pwsh", yaml);
    }

    [Fact]
    public void Serialize_NullDescription_OmittedFromOutput()
    {
        var yaml = WorkspaceTemplateSerializer.Serialize(SinglePane());

        Assert.DoesNotContain("description:", yaml);
    }

    [Fact]
    public void Serialize_NullCwdAndEnv_OmittedFromOutput()
    {
        var yaml = WorkspaceTemplateSerializer.Serialize(SinglePane());

        Assert.DoesNotContain("cwd:", yaml);
        Assert.DoesNotContain("env:", yaml);
    }

    [Fact]
    public void Serialize_TwoPane_ContainsSplitDirectionAndRatio()
    {
        var yaml = WorkspaceTemplateSerializer.Serialize(TwoPane());

        Assert.Contains("split: horizontal", yaml);
        Assert.Contains("ratio: 0.6", yaml);
    }

    [Fact]
    public void Serialize_TwoPane_ContainsFocusSlot()
    {
        var yaml = WorkspaceTemplateSerializer.Serialize(TwoPane());

        Assert.Contains("focus: right", yaml);
    }

    [Fact]
    public void Serialize_PaneWithArgs_ArgsPresent()
    {
        var yaml = WorkspaceTemplateSerializer.Serialize(TwoPane());

        Assert.Contains("- -NoExit", yaml);
    }

    [Fact]
    public void Serialize_PaneWithEnv_EnvPresent()
    {
        var yaml = WorkspaceTemplateSerializer.Serialize(TwoPane());

        Assert.Contains("FOO: bar", yaml);
    }

    [Fact]
    public void Serialize_EmptyArgs_ArgsOmittedFromOutput()
    {
        var yaml = WorkspaceTemplateSerializer.Serialize(SinglePane());

        Assert.DoesNotContain("args:", yaml);
    }

    // ── round-trip ───────────────────────────────────────────────────────────

    [Fact]
    public void Serialize_SinglePane_RoundTrips()
    {
        var original = SinglePane("RT");
        var yaml = WorkspaceTemplateSerializer.Serialize(original);

        var restored = YamlTemplateLoader.ParseAndValidate(yaml, "<test>");

        Assert.Equal("RT", restored.Name);
        Assert.Single(restored.Panes);
        Assert.Equal("shell", restored.Panes[0].Id);
        Assert.Equal("pwsh", restored.Panes[0].Command);
        var leaf = Assert.IsType<PaneRefTemplate>(restored.Layout);
        Assert.Equal("shell", leaf.Slot);
    }

    [Fact]
    public void Serialize_TwoPane_RoundTrips()
    {
        var original = TwoPane();
        var yaml = WorkspaceTemplateSerializer.Serialize(original);

        var restored = YamlTemplateLoader.ParseAndValidate(yaml, "<test>");

        Assert.Equal("Two", restored.Name);
        Assert.Equal(2, restored.Panes.Count);
        var split = Assert.IsType<SplitNodeTemplate>(restored.Layout);
        Assert.Equal(SplitDirection.Horizontal, split.Direction);
        Assert.Equal(0.6, split.Ratio, precision: 10);
        Assert.Equal("right", restored.Focus);
    }

    [Fact]
    public void Serialize_TwoPane_PaneCommandsRoundTrip()
    {
        var original = TwoPane();
        var yaml = WorkspaceTemplateSerializer.Serialize(original);

        var restored = YamlTemplateLoader.ParseAndValidate(yaml, "<test>");

        Assert.Equal("cmd", restored.Panes[0].Command);
        Assert.Equal("pwsh", restored.Panes[1].Command);
    }

    [Fact]
    public void Serialize_PaneWithEnv_EnvRoundTrips()
    {
        var original = TwoPane();
        var yaml = WorkspaceTemplateSerializer.Serialize(original);

        var restored = YamlTemplateLoader.ParseAndValidate(yaml, "<test>");

        Assert.NotNull(restored.Panes[1].Env);
        Assert.Equal("bar", restored.Panes[1].Env!["FOO"]);
    }

    // ── SaveAsync ────────────────────────────────────────────────────────────

    [Fact]
    public async Task SaveAsync_WritesFileLoadableByYamlTemplateLoader()
    {
        var template = SinglePane("SaveTest");
        var path = Path.GetTempFileName();
        try
        {
            await WorkspaceTemplateSerializer.SaveAsync(template, path);

            var loader = new YamlTemplateLoader();
            var loaded = await loader.LoadAsync(path);

            Assert.Equal("SaveTest", loaded.Name);
            Assert.Equal("shell", loaded.Panes[0].Id);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
