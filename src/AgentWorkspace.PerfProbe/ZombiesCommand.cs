using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AgentWorkspace.Abstractions.Ids;
using AgentWorkspace.Abstractions.Pty;
using AgentWorkspace.ConPTY;

namespace AgentWorkspace.PerfProbe;

/// <summary>
/// ADR-008 #7 — Job-Object teardown leaves zero zombie children.
/// Spawns N <see cref="PseudoConsoleProcess"/> panes, captures each child PID
/// via <see cref="PseudoConsoleProcess.ProcessId"/>, then issues
/// <see cref="KillMode.Force"/> on every pane and waits a settle window before
/// asking Windows whether each captured PID is still alive. A zombie is a
/// captured PID that resolves and reports <c>HasExited == false</c>; PIDs that
/// throw <see cref="ArgumentException"/> have already been reaped (the desired
/// state). PID-reuse races are theoretically possible inside the settle window
/// but ignored — Job-Object teardown completes well before the OS recycles a
/// PID under normal load.
/// </summary>
internal static class ZombiesCommand
{
    public static int Run(string[] args) => RunAsync(args).GetAwaiter().GetResult();

    private static async Task<int> RunAsync(string[] args)
    {
        var panes      = 4;
        var settleMs   = 500;

        for (var i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--panes" when i + 1 < args.Length:
                    if (!int.TryParse(args[++i], out panes) || panes < 1 || panes > 32)
                        return UsageError($"invalid --panes '{args[i]}' (1..32)");
                    break;
                case "--settle-ms" when i + 1 < args.Length:
                    if (!int.TryParse(args[++i], out settleMs) || settleMs < 100 || settleMs > 5000)
                        return UsageError($"invalid --settle-ms '{args[i]}' (100..5000)");
                    break;
                case "--help" or "-h":
                    PrintUsage();
                    return 0;
                default:
                    return UsageError($"unknown arg '{args[i]}'");
            }
        }

        var processes  = new List<PseudoConsoleProcess>(panes);
        var capturedPids = new List<int>(panes);

        try
        {
            for (var i = 0; i < panes; i++)
            {
                var p = new PseudoConsoleProcess(PaneId.New());
                await p.StartAsync(IdleChildOptions(), CancellationToken.None).ConfigureAwait(false);
                processes.Add(p);
                capturedPids.Add(p.ProcessId);
            }

            // Tear every pane down via Job-Object force-kill.
            foreach (var p in processes)
            {
                try { await p.KillAsync(KillMode.Force, CancellationToken.None).ConfigureAwait(false); }
                catch { /* best-effort — counted via PID probe below */ }
            }

            // Give Windows a window to fully reap the descendants.
            await Task.Delay(settleMs).ConfigureAwait(false);

            var zombies     = 0;
            var stillAlive  = new List<int>();
            foreach (var pid in capturedPids)
            {
                try
                {
                    using var probe = Process.GetProcessById(pid);
                    if (!probe.HasExited)
                    {
                        zombies++;
                        stillAlive.Add(pid);
                    }
                }
                catch (ArgumentException)
                {
                    // PID already gone — desired outcome.
                }
                catch (InvalidOperationException)
                {
                    // Already exited mid-call.
                }
            }

            var pass = zombies == 0;

            var payload = new Dictionary<string, object?>
            {
                ["metric"]            = "zombieChildren",
                ["adr008Item"]        = 7,
                ["panes"]             = panes,
                ["settleMs"]          = settleMs,
                ["capturedPidCount"]  = capturedPids.Count,
                ["zombieCount"]       = zombies,
                ["zombiePids"]        = stillAlive,
                ["threshold"]         = 0,
                ["pass"]              = pass,
            };
            Console.WriteLine(JsonSerializer.Serialize(payload));
            return pass ? 0 : 1;
        }
        finally
        {
            foreach (var p in processes)
            {
                try { await p.DisposeAsync().ConfigureAwait(false); }
                catch { /* best-effort */ }
            }
        }
    }

    /// <summary>cmd /k with no stdin piped — child sits idle waiting on input forever.</summary>
    private static PaneStartOptions IdleChildOptions() => new(
        Command:          "cmd.exe",
        Arguments:        new[] { "/k" },
        WorkingDirectory: null,
        Environment:      null,
        InitialColumns:   80,
        InitialRows:      24);

    private static int UsageError(string msg)
    {
        Console.Error.WriteLine($"zombies: {msg}");
        PrintUsage();
        return 64;
    }

    private static void PrintUsage()
    {
        Console.Error.WriteLine("""
            awt-perfprobe zombies — ADR-008 #7 zombie-child count after Job-Object close.

            Usage:
              awt-perfprobe zombies [--panes 4] [--settle-ms 500]

            Spawns N idle ConPTY child processes, captures each PID, then issues
            KillMode.Force on every pane. After --settle-ms, each captured PID is
            re-resolved via Process.GetProcessById; a PID that resolves and is
            still running counts as a zombie.

            Output (single-line JSON):
              {"metric":"zombieChildren","panes":N,"settleMs":N,
               "capturedPidCount":N,"zombieCount":N,"zombiePids":[..],
               "threshold":0,"pass":true|false}

            Exit 0 = zero zombies, 1 = at least one zombie survived.
            """);
    }
}
