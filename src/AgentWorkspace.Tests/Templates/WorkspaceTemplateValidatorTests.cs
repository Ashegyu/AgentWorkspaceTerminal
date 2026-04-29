using System.Collections.Generic;
using AgentWorkspace.Abstractions.Layout;
using AgentWorkspace.Abstractions.Templates;

namespace AgentWorkspace.Tests.Templates;

public sealed class WorkspaceTemplateValidatorTests
{
    // ── helpers ─────────────────────────────────────────────────────────────

    private static PaneTemplate Pane(string id) =>
        new(id, "cmd.exe", ["/d", "/k"], null, null);

    private static WorkspaceTemplate SinglePane(string paneId, string? focus = null) =>
        new("Test", null, [Pane(paneId)], new PaneRefTemplate(paneId), focus);

    // ── valid cases ──────────────────────────────────────────────────────────

    [Fact]
    public void Validate_ReturnsNoErrors_WhenTemplateIsValid()
    {
        var errors = WorkspaceTemplateValidator.Validate(SinglePane("shell"));

        Assert.Empty(errors);
    }

    [Fact]
    public void Validate_ReturnsNoErrors_ForValidSplitWithFocus()
    {
        var template = new WorkspaceTemplate(
            "Two Pane", null,
            [Pane("editor"), Pane("shell")],
            new SplitNodeTemplate(
                SplitDirection.Horizontal, 0.6,
                new PaneRefTemplate("editor"),
                new PaneRefTemplate("shell")),
            "shell");

        Assert.Empty(WorkspaceTemplateValidator.Validate(template));
    }

    [Fact]
    public void Validate_ReturnsNoErrors_ForNestedSplit()
    {
        var template = new WorkspaceTemplate(
            "Three Pane", null,
            [Pane("a"), Pane("b"), Pane("c")],
            new SplitNodeTemplate(
                SplitDirection.Vertical, 0.65,
                new SplitNodeTemplate(
                    SplitDirection.Horizontal, 0.5,
                    new PaneRefTemplate("a"),
                    new PaneRefTemplate("b")),
                new PaneRefTemplate("c")),
            null);

        Assert.Empty(WorkspaceTemplateValidator.Validate(template));
    }

    // ── unknown layout slot ──────────────────────────────────────────────────

    [Fact]
    public void Validate_ReturnsError_WhenLayoutReferencesUnknownSlot()
    {
        var template = new WorkspaceTemplate(
            "Bad Layout", null,
            [Pane("shell")],
            new PaneRefTemplate("typo"),
            null);

        var errors = WorkspaceTemplateValidator.Validate(template);

        Assert.Single(errors);
        Assert.Contains("typo", errors[0]);
    }

    [Fact]
    public void Validate_ReturnsErrors_WhenNestedLayoutContainsUnknownSlot()
    {
        var template = new WorkspaceTemplate(
            "Bad Nested", null,
            [Pane("a")],
            new SplitNodeTemplate(
                SplitDirection.Horizontal, 0.5,
                new PaneRefTemplate("a"),
                new PaneRefTemplate("missing")),
            null);

        var errors = WorkspaceTemplateValidator.Validate(template);

        Assert.Single(errors);
        Assert.Contains("missing", errors[0]);
    }

    // ── unknown focus slot ───────────────────────────────────────────────────

    [Fact]
    public void Validate_ReturnsError_WhenFocusReferencesUnknownSlot()
    {
        var errors = WorkspaceTemplateValidator.Validate(SinglePane("shell", focus: "ghost"));

        Assert.Single(errors);
        Assert.Contains("ghost", errors[0]);
    }

    // ── duplicate pane ids ───────────────────────────────────────────────────

    [Fact]
    public void Validate_ReturnsError_WhenPaneIdsAreDuplicated()
    {
        var template = new WorkspaceTemplate(
            "Duplicates", null,
            [Pane("shell"), Pane("shell")],
            new PaneRefTemplate("shell"),
            null);

        var errors = WorkspaceTemplateValidator.Validate(template);

        Assert.Single(errors);
        Assert.Contains("shell", errors[0]);
    }

    [Fact]
    public void Validate_ReturnsMultipleErrors_WhenMultipleInvariantsAreViolated()
    {
        // duplicate id + layout unknown slot + focus unknown slot = 3 errors
        var template = new WorkspaceTemplate(
            "Many Errors", null,
            [Pane("x"), Pane("x")],
            new PaneRefTemplate("unknown"),
            "also-unknown");

        var errors = WorkspaceTemplateValidator.Validate(template);

        Assert.Equal(3, errors.Count);
    }
}
