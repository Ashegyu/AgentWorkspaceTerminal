using System.Windows.Media;
using AgentWorkspace.Abstractions.Agents;
using AgentWorkspace.Abstractions.Policy;
using AgentWorkspace.Abstractions.Workflows;
using AgentWorkspace.App.Wpf.Approval;

namespace AgentWorkspace.Tests.App;

/// <summary>
/// Validates the per-row view-model rendered inside <see cref="ApprovalDialog"/>:
/// Risk badge label, badge brush colour, and Reason visibility.
/// </summary>
public sealed class ApprovalDialogRowVmTests
{
    private static ApprovalDialog.ActionRowVm Row(Risk r, string reason = "needs review")
    {
        var item = new ApprovalRequestItem(
            new ActionRequestEvent("a1", "Bash", "echo hi"),
            new PolicyDecision(PolicyVerdict.AskUser, reason, r));
        return ApprovalDialog.ActionRowVm.From(item);
    }

    [Fact]
    public void TypeLabel_BracketsType()
        => Assert.Equal("[Bash]", Row(Risk.Low).TypeLabel);

    [Fact]
    public void Description_CarriedThrough()
        => Assert.Equal("echo hi", Row(Risk.Low).Description);

    [Fact]
    public void RiskLabel_IsUppercase()
    {
        Assert.Equal("LOW",      Row(Risk.Low).RiskLabel);
        Assert.Equal("MEDIUM",   Row(Risk.Medium).RiskLabel);
        Assert.Equal("HIGH",     Row(Risk.High).RiskLabel);
        Assert.Equal("CRITICAL", Row(Risk.Critical).RiskLabel);
    }

    [Fact]
    public void RiskBrush_LowIsGray()
    {
        var brush = Assert.IsType<SolidColorBrush>(Row(Risk.Low).RiskBrush);
        Assert.Equal(Color.FromRgb(0x9E, 0x9E, 0x9E), brush.Color);
    }

    [Fact]
    public void RiskBrush_HighIsOrange()
    {
        var brush = Assert.IsType<SolidColorBrush>(Row(Risk.High).RiskBrush);
        Assert.Equal(Color.FromRgb(0xFB, 0x8C, 0x00), brush.Color);
    }

    [Fact]
    public void RiskBrush_CriticalIsRed()
    {
        var brush = Assert.IsType<SolidColorBrush>(Row(Risk.Critical).RiskBrush);
        Assert.Equal(Color.FromRgb(0xD3, 0x2F, 0x2F), brush.Color);
    }

    [Fact]
    public void ShowReason_TrueWhenReasonNonEmpty()
        => Assert.True(Row(Risk.Low, "blocked by blacklist").ShowReason);

    [Fact]
    public void ShowReason_FalseWhenReasonEmpty()
        => Assert.False(Row(Risk.Low, "").ShowReason);

    [Fact]
    public void Reason_PassedThrough()
        => Assert.Equal("needs explicit review", Row(Risk.High, "needs explicit review").Reason);
}
