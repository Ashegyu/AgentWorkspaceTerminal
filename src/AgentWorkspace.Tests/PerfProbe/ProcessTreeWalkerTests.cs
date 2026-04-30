using System.Diagnostics;
using System.Linq;
using AgentWorkspace.PerfProbe;

namespace AgentWorkspace.Tests.PerfProbe;

/// <summary>
/// Maintenance slot — <see cref="ProcessTreeWalker"/> sanity tests against real
/// Win32 process state. Doesn't try to exercise the WebView2 case (the test
/// host has no WPF process); instead verifies the walker correctly anchors on
/// any live PID and produces a non-empty self-rooted tree.
/// </summary>
public sealed class ProcessTreeWalkerTests
{
    [Fact]
    public void Walk_OnSelf_ReturnsAtLeastSelfNode()
    {
        var selfPid = Process.GetCurrentProcess().Id;
        var nodes   = ProcessTreeWalker.Walk(selfPid);

        Assert.NotEmpty(nodes);
        Assert.Contains(nodes, n => n.Pid == selfPid);
        var selfNode = nodes.First(n => n.Pid == selfPid);
        Assert.True(selfNode.WorkingSetBytes > 0, "Self WorkingSet64 must be positive.");
        Assert.False(string.IsNullOrEmpty(selfNode.Name), "Self process name must be non-empty.");
    }

    [Fact]
    public void Walk_NonExistentPid_ReturnsEmpty()
    {
        // PID 0 is the System Idle Process pseudo-PID — Process.GetProcessById(0) throws.
        // Walking from there yields whatever toolhelp lists with parent=0 (kernel-rooted),
        // but our TryReadProcess silently filters dead/inaccessible nodes, so the result
        // for a clearly-impossible PID is empty.
        const int impossiblePid = 0x7FFFFFFF;
        var nodes = ProcessTreeWalker.Walk(impossiblePid);

        Assert.Empty(nodes);
    }

    [Fact]
    public void Walk_OnSelf_ParentPidPointsAtRealAncestor()
    {
        var selfPid = Process.GetCurrentProcess().Id;
        var nodes   = ProcessTreeWalker.Walk(selfPid);
        var self    = nodes.First(n => n.Pid == selfPid);

        // Self ParentPid must be > 0 (Windows reserves 0 / 4 for system pseudo-procs).
        Assert.True(self.ParentPid > 0, $"ParentPid for self must be positive, got {self.ParentPid}.");
    }
}
