// AgentWorkspace.PerfProbe
//
// Console probe for ADR-008 operational metrics that BenchmarkDotNet cannot measure:
//   #1 keystroke → screen echo p95   (Day 54 — needs WebView2 round-trip; one-shot manual)
//   #3 4-pane idle RSS               (Day 56 — Process.WorkingSet64 sampling)
//   #4 1-pane RSS delta              (Day 56 — A/B start vs. running)
//   #6 GC Gen2 / min idle            (Day 58 — GC.CollectionCount(2) sampling)
//   #7 Job-Object zombie children    (Day 58 — child-PID set diff after kill)
//
// Each subcommand prints results as a single JSON line on stdout so CI / scripts
// can pipe it directly into the threshold gate (Day 60).
//
// Today (Day 53) the probe is a scaffold: the dispatcher exists, the subcommands
// are stubs that print "TODO: Day NN" and exit non-zero. Daily commits replace
// stubs with real probes.

using System;
using AgentWorkspace.PerfProbe;

if (args.Length == 0)
{
    PrintUsage();
    return 64; // EX_USAGE
}

return args[0] switch
{
    "echo-latency" => EchoLatencyCommand.Run(args),
    "rss"          => RssCommand.Run(args),
    "gc-idle"      => Stub("Day 58 — GC Gen2/min during idle"),
    "zombies"      => Stub("Day 58 — Job-Object zombie child detection"),
    "--help" or "-h" => UsageOk(),
    _ => UnknownCommand(args[0]),
};

static int Stub(string label)
{
    Console.Error.WriteLine($"[stub] {label} — not implemented yet (MVP-8 scaffold).");
    return 1;
}

static int UsageOk() { PrintUsage(); return 0; }

static int UnknownCommand(string cmd)
{
    Console.Error.WriteLine($"awt-perfprobe: unknown command '{cmd}'");
    PrintUsage();
    return 64;
}

static void PrintUsage()
{
    Console.Error.WriteLine("""
        awt-perfprobe — ADR-008 operational metrics probe

        Usage:
          awt-perfprobe <command>

        Commands:
          echo-latency   Day 54 (#1) — keystroke → screen echo p95 (manual one-shot).
          rss            Day 56 (#3, #4) — idle RSS for 1-pane and 4-pane configurations.
          gc-idle        Day 58 (#6) — GC Gen2 collections per minute during idle.
          zombies        Day 58 (#7) — count zombie child processes after Job-Object close.

        Output is single-line JSON on stdout; diagnostics on stderr.
        """);
}
