using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.Versioning;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AgentWorkspace.Abstractions.Channels;
using AgentWorkspace.Abstractions.Ids;
using AgentWorkspace.Abstractions.Pty;
using AgentWorkspace.Daemon.Channels;

namespace AgentWorkspace.Tests.Daemon.Channels;

/// <summary>
/// Day-16 control + data channel integration. Tests the in-process implementation; Day 17
/// will retarget the same scenarios at the daemon-backed pipe transport.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class PtyControlChannelTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(20);

    private static PaneStartOptions LongRunningPing() => new(
        Command: "cmd.exe",
        Arguments: new[] { "/d", "/c", "ping -n 30 127.0.0.1" },
        WorkingDirectory: null,
        Environment: null,
        InitialColumns: 80,
        InitialRows: 25);

    [SkippableFact]
    public async Task StartPaneAsync_StartsChild_AndExitsCleanly()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "ConPTY is Windows-only.");

        using var cts = new CancellationTokenSource(TestTimeout);
        await using var ch = new PtyControlChannel();

        var pane = PaneId.New();
        var state = await ch.StartPaneAsync(pane, LongRunningPing(), cts.Token);
        Assert.Equal(PaneState.Running, state);

        int pid = ch.TryGetProcessId(pane);
        Assert.True(pid > 0);

        var exit = await ch.ClosePaneAsync(pane, KillMode.Force, cts.Token);
        // Force-killed processes report a non-zero exit (typically 1) — just assert it's reapable.
        var deadline = Stopwatch.GetTimestamp() + (long)(Stopwatch.Frequency * 2);
        while (Stopwatch.GetTimestamp() < deadline && ProcessExists(pid))
        {
            await Task.Delay(50, cts.Token);
        }
        Assert.False(ProcessExists(pid), $"pid {pid} should be reaped after ClosePaneAsync (exit={exit}).");
    }

    [SkippableFact]
    public async Task StartPaneAsync_TwiceForSameId_ThrowsInvalidOperation()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "ConPTY is Windows-only.");

        using var cts = new CancellationTokenSource(TestTimeout);
        await using var ch = new PtyControlChannel();

        var pane = PaneId.New();
        await ch.StartPaneAsync(pane, LongRunningPing(), cts.Token);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await ch.StartPaneAsync(pane, LongRunningPing(), cts.Token));

        await ch.ClosePaneAsync(pane, KillMode.Force, cts.Token);
    }

    [SkippableFact]
    public async Task SubscribeAsync_DeliversBytes_FromChildProcess()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "ConPTY is Windows-only.");

        using var cts = new CancellationTokenSource(TestTimeout);
        await using var ch = new PtyControlChannel();

        var pane = PaneId.New();

        // Cmd that emits a sentinel string and exits — we just need ANY bytes to flow.
        var opts = new PaneStartOptions(
            Command: "cmd.exe",
            Arguments: new[] { "/d", "/c", "echo hello-from-pane && exit 0" },
            WorkingDirectory: null,
            Environment: null,
            InitialColumns: 80,
            InitialRows: 25);

        await ch.StartPaneAsync(pane, opts, cts.Token);

        var collected = new List<byte>();
        var subscriberDone = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var subscribeTask = Task.Run(async () =>
        {
            try
            {
                await foreach (var frame in ch.SubscribeAsync(pane, cts.Token))
                {
                    foreach (var b in frame.Bytes.Span) collected.Add(b);
                    if (collected.Count > 0)
                    {
                        // We don't need every byte — first frame tells us bytes flow.
                        subscriberDone.TrySetResult(true);
                    }
                }
            }
            catch (OperationCanceledException) { /* test wrapped cleanup */ }
        });

        await Task.WhenAny(subscriberDone.Task, Task.Delay(TestTimeout, cts.Token));
        Assert.True(subscriberDone.Task.IsCompletedSuccessfully,
            $"Expected at least one frame; collected so far: {Encoding.UTF8.GetString(collected.ToArray())}");

        await ch.ClosePaneAsync(pane, KillMode.Force, cts.Token);
        await subscribeTask;
    }

    [SkippableFact]
    public async Task PaneExited_FiresOnChildExit()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "ConPTY is Windows-only.");

        using var cts = new CancellationTokenSource(TestTimeout);
        await using var ch = new PtyControlChannel();

        var pane = PaneId.New();
        var exitTcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);

        ch.PaneExited += (_, args) =>
        {
            if (args.Pane.Equals(pane))
            {
                exitTcs.TrySetResult(args.ExitCode);
            }
        };

        var opts = new PaneStartOptions(
            Command: "cmd.exe",
            Arguments: new[] { "/d", "/c", "exit 7" },
            WorkingDirectory: null,
            Environment: null,
            InitialColumns: 80,
            InitialRows: 25);
        await ch.StartPaneAsync(pane, opts, cts.Token);

        var exitCode = await exitTcs.Task.WaitAsync(cts.Token);
        Assert.Equal(7, exitCode);

        await ch.ClosePaneAsync(pane, KillMode.Graceful, cts.Token);
    }

    [SkippableFact]
    public async Task ClosePaneAsync_ForUnknownId_ReturnsMinusOne()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "ConPTY is Windows-only.");

        await using var ch = new PtyControlChannel();
        var unknown = PaneId.New();
        var result = await ch.ClosePaneAsync(unknown, KillMode.Graceful, default);
        Assert.Equal(-1, result);
    }

    [SkippableFact]
    public async Task DisposeAsync_KillsAllPanes()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "ConPTY is Windows-only.");

        using var cts = new CancellationTokenSource(TestTimeout);
        var ch = new PtyControlChannel();

        var paneA = PaneId.New();
        var paneB = PaneId.New();
        await ch.StartPaneAsync(paneA, LongRunningPing(), cts.Token);
        await ch.StartPaneAsync(paneB, LongRunningPing(), cts.Token);

        var pidA = ch.TryGetProcessId(paneA);
        var pidB = ch.TryGetProcessId(paneB);
        Assert.True(pidA > 0 && pidB > 0);

        await ch.DisposeAsync();

        var deadline = Stopwatch.GetTimestamp() + (long)(Stopwatch.Frequency * 2);
        while (Stopwatch.GetTimestamp() < deadline &&
               (ProcessExists(pidA) || ProcessExists(pidB)))
        {
            await Task.Delay(50, cts.Token);
        }
        Assert.False(ProcessExists(pidA), $"pidA {pidA} survived DisposeAsync.");
        Assert.False(ProcessExists(pidB), $"pidB {pidB} survived DisposeAsync.");
    }

    [SkippableFact]
    public async Task UnknownPane_WriteResizeSignal_Throws()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "ConPTY is Windows-only.");

        await using var ch = new PtyControlChannel();
        var unknown = PaneId.New();

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await ch.WriteInputAsync(unknown, new byte[] { (byte)'a' }, default));
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await ch.ResizePaneAsync(unknown, 80, 24, default));
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await ch.SignalPaneAsync(unknown, PtySignal.Interrupt, default));
    }

    private static bool ProcessExists(int pid)
    {
        if (pid <= 0) return false;
        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
    }
}
