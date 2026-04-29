using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using AgentWorkspace.Abstractions.Ids;
using AgentWorkspace.Abstractions.Layout;
using AgentWorkspace.Abstractions.Pty;
using AgentWorkspace.App.Wpf;
using AgentWorkspace.Daemon.Channels;

namespace AgentWorkspace.Tests.App;

/// <summary>
/// End-to-end lifecycle tests covering the <see cref="Workspace"/> + <see cref="PaneSession"/>
/// + ConPTY + Job Object stack at the multi-pane level.
/// </summary>
/// <remarks>
/// These exist to lock down the §1.2 MVP-1 completion criterion "Job-Object 종료 시 좀비 자식 0개"
/// once the workspace can host multiple panes simultaneously, and to prove that
/// <see cref="Workspace.OpenSplitAsync"/> rolls the layout back if the new PTY fails to spawn.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class WorkspaceLifecycleTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(30);

    [SkippableFact]
    public async Task FourPaneWorkspace_DisposeReapsAllDescendantPings()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "ConPTY is Windows-only.");

        using var cts = new CancellationTokenSource(TestTimeout);
        var firstPane = PaneId.New();

        // Each pane runs `cmd /c ping ...` so we can verify Job Object kill sweeps both the
        // cmd parent and its ping descendant.
        var startOpts = LongRunningPing();

        await using var channel = new PtyControlChannel();
        var ws = new Workspace(
            sessionFactory: id => new PaneSession(id, NullPostToWeb, channel, channel),
            defaultOptionsFactory: () => startOpts,
            initial: firstPane);

        var firstSession = ws.Register(firstPane);
        await firstSession.StartAsync(startOpts, cts.Token);

        // Build a 4-pane tree: split three times. The result is irrelevant for this test —
        // what matters is that we end up with four live cmd processes, each parenting a ping.
        var p1 = await ws.OpenSplitAsync(firstPane, SplitDirection.Horizontal, cts.Token);
        var p2 = await ws.OpenSplitAsync(p1, SplitDirection.Vertical, cts.Token);
        var p3 = await ws.OpenSplitAsync(firstPane, SplitDirection.Vertical, cts.Token);

        Assert.Equal(4, ws.Layout.Panes.Count);

        // Snapshot the cmd pids and their ping children. The ping needs a moment to spin up.
        await Task.Delay(700, cts.Token);

        var allPids = new List<int>();
        foreach (var paneId in ws.Layout.Panes)
        {
            int cmdPid = channel.TryGetProcessId(paneId);
            allPids.Add(cmdPid);
            foreach (var childPid in SnapshotChildren(cmdPid))
            {
                allPids.Add(childPid);
            }
        }

        // We expect at minimum 4 cmd parents; one ping per parent is the typical case.
        Assert.True(allPids.Count >= 4, $"Expected at least four pids, got {allPids.Count}.");

        await ws.DisposeAsync();

        // Allow up to 2s for the kernel to settle Job Object teardown.
        var stopAt = Stopwatch.GetTimestamp() + (long)(Stopwatch.Frequency * 2);
        bool allGone = false;
        while (Stopwatch.GetTimestamp() < stopAt)
        {
            allGone = allPids.All(pid => !ProcessExists(pid));
            if (allGone) break;
            await Task.Delay(50);
        }

        Assert.True(allGone,
            $"Descendant processes still alive after Workspace dispose: [{string.Join(',', allPids.Where(ProcessExists))}]");
    }

    [SkippableFact]
    public async Task PartialClose_OnlyTargetTreeIsReaped_OthersStayAlive()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "ConPTY is Windows-only.");

        using var cts = new CancellationTokenSource(TestTimeout);
        var firstPane = PaneId.New();
        var startOpts = LongRunningPing();

        await using var channel = new PtyControlChannel();
        var ws = new Workspace(
            sessionFactory: id => new PaneSession(id, NullPostToWeb, channel, channel),
            defaultOptionsFactory: () => startOpts,
            initial: firstPane);

        var firstSession = ws.Register(firstPane);
        await firstSession.StartAsync(startOpts, cts.Token);

        var p1 = await ws.OpenSplitAsync(firstPane, SplitDirection.Horizontal, cts.Token);
        var p2 = await ws.OpenSplitAsync(p1, SplitDirection.Vertical, cts.Token);

        await Task.Delay(700, cts.Token);

        int p1Pid = channel.TryGetProcessId(p1);
        var p1Pids = new List<int> { p1Pid };
        p1Pids.AddRange(SnapshotChildren(p1Pid));

        int firstPid = channel.TryGetProcessId(firstPane);
        int p2Pid = channel.TryGetProcessId(p2);

        try
        {
            await ws.CloseAsync(p1, cts.Token);

            // Allow Job Object teardown for the closed pane.
            await Task.Delay(800, cts.Token);

            Assert.All(p1Pids, pid => Assert.False(ProcessExists(pid),
                $"pane {p1} pid {pid} still alive after CloseAsync."));

            // Sibling panes must stay alive — closing one pane must not collateral-kill others.
            Assert.True(ProcessExists(firstPid), $"firstPane pid {firstPid} died unexpectedly.");
            Assert.True(ProcessExists(p2Pid), $"p2 pid {p2Pid} died unexpectedly.");

            // Layout: started at 1 pane, two splits brought it to 3, the close drops it to 2.
            Assert.Equal(2, ws.Layout.Panes.Count);
        }
        finally
        {
            await ws.DisposeAsync();
        }
    }

    [SkippableFact]
    public async Task OpenSplitAsync_PtyStartFailure_RollsLayoutBack()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "ConPTY is Windows-only.");

        using var cts = new CancellationTokenSource(TestTimeout);
        var firstPane = PaneId.New();
        var goodOpts = LongRunningPing();

        // Default options factory always hands out a bad command line. The first pane is
        // registered+started separately with goodOpts (the factory isn't consulted there), so
        // every factory call corresponds to an OpenSplitAsync — and every OpenSplitAsync should
        // fail.
        Func<PaneStartOptions> factory = () => new PaneStartOptions(
            Command: @"C:\nonexistent\definitely-not-a-shell.exe",
            Arguments: Array.Empty<string>(),
            WorkingDirectory: null,
            Environment: null,
            InitialColumns: 80,
            InitialRows: 25);

        await using var channel = new PtyControlChannel();
        var ws = new Workspace(
            sessionFactory: id => new PaneSession(id, NullPostToWeb, channel, channel),
            defaultOptionsFactory: factory,
            initial: firstPane);

        var session = ws.Register(firstPane);
        await session.StartAsync(goodOpts, cts.Token);

        Assert.Single(ws.Layout.Panes);

        await Assert.ThrowsAnyAsync<Exception>(
            async () => await ws.OpenSplitAsync(firstPane, SplitDirection.Horizontal, cts.Token));

        // Layout must be unchanged: still a single pane, no orphaned session entry.
        Assert.Single(ws.Layout.Panes);
        Assert.Equal(firstPane, ws.Layout.Panes[0]);
        Assert.Single(ws.Sessions);
        Assert.True(ws.Sessions.ContainsKey(firstPane));

        await ws.DisposeAsync();
    }

    [SkippableFact]
    public async Task PaneSession_RestartAsync_StartsFreshChildAndKillsOld()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "ConPTY is Windows-only.");

        using var cts = new CancellationTokenSource(TestTimeout);
        var startOpts = LongRunningPing();
        await using var channel = new PtyControlChannel();
        var paneId = PaneId.New();
        var session = new PaneSession(paneId, NullPostToWeb, channel, channel);

        try
        {
            await session.StartAsync(startOpts, cts.Token);
            await Task.Delay(400, cts.Token);

            int firstPid = channel.TryGetProcessId(paneId);
            Assert.True(ProcessExists(firstPid), $"first cmd pid {firstPid} should be alive.");

            await session.RestartAsync(cts.Token);
            await Task.Delay(400, cts.Token);

            int secondPid = channel.TryGetProcessId(paneId);
            Assert.NotEqual(firstPid, secondPid);
            Assert.True(ProcessExists(secondPid), $"second cmd pid {secondPid} should be alive after restart.");

            // Original cmd (and its ping child, if any) should be gone within ~2s of the kill.
            var stopAt = Stopwatch.GetTimestamp() + (long)(Stopwatch.Frequency * 2);
            while (Stopwatch.GetTimestamp() < stopAt && ProcessExists(firstPid))
            {
                await Task.Delay(50, cts.Token);
            }
            Assert.False(ProcessExists(firstPid), $"original pid {firstPid} should have been reaped.");
        }
        finally
        {
            await session.DisposeAsync();
        }
    }

    // ----- helpers -----------------------------------------------------------------------

    private static PaneStartOptions LongRunningPing() => new(
        Command: "cmd.exe",
        Arguments: new[] { "/d", "/c", "ping -n 30 127.0.0.1" },
        WorkingDirectory: null,
        Environment: null,
        InitialColumns: 80,
        InitialRows: 25);

    private static ValueTask NullPostToWeb(string envelope) => ValueTask.CompletedTask;

    private static int[] SnapshotChildren(int parentPid)
    {
        try
        {
            var psi = new ProcessStartInfo("cmd.exe",
                $"/c wmic process where (ParentProcessId={parentPid}) get ProcessId /value")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi)!;
            string output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(2000);

            var pids = new List<int>();
            foreach (string line in output.Split('\n'))
            {
                string trimmed = line.Trim();
                if (trimmed.StartsWith("ProcessId=", StringComparison.OrdinalIgnoreCase))
                {
                    if (int.TryParse(trimmed.AsSpan("ProcessId=".Length), out int pid))
                    {
                        pids.Add(pid);
                    }
                }
            }
            return pids.ToArray();
        }
        catch
        {
            return Array.Empty<int>();
        }
    }

    private static bool ProcessExists(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
    }
}
