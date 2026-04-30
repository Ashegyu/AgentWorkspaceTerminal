# MVP-8 Performance Hardening — Mvp8/

Per-ADR-015 perf harness for the seven ADR-008 budgets.
This folder collects only the new MVP-8 measurement code; the existing
`CommandLineBench.cs` / `EnvelopeBench.cs` / `LayoutBench.cs` benches under
the parent folder predate MVP-8 and stay where they are.

## ADR-008 → measurement map

| # | Budget | Target (p95) | Where it lives | Day |
|---|---|---:|---|---|
| 1 | Keystroke → screen echo | ≤ 50 ms | `awt-perfprobe echo-latency` (manual one-shot) | 54 |
| 2 | ConPTY read → client write | ≤ 5 ms | `Mvp8/PtyReadWriteBench.cs` (BDN) | 55 |
| 3 | 4-pane idle RSS | ≤ 500 MB | `awt-perfprobe rss` | 56 |
| 4 | 1-pane RSS delta | ≤ 30 MB | `awt-perfprobe rss` | 56 |
| 5 | 1 MB burst render | ≤ 250 ms | `Mvp8/BurstRenderBench.cs` (BDN) | 57 |
| 6 | GC Gen2 / min idle | ≤ 1 | `awt-perfprobe gc-idle` | 58 |
| 7 | Job-Object zombies | 0 | `awt-perfprobe zombies` | 58 |

Day 59 adds `Mvp8/PolicyBench.cs` and `Mvp8/RedactionBench.cs` (BDN) for
hot-path budget verification of the 50-rule blacklist + 14-rule redaction
engine, then folds optimisation into the same or following day.

## Running benches

```bash
# All MVP-8 benches:
dotnet run -c Release --project src/AgentWorkspace.Benchmarks -- --filter '*Mvp8*'

# Single bench:
dotnet run -c Release --project src/AgentWorkspace.Benchmarks -- --filter '*PtyReadWriteBench*'
```

`BenchmarkDotNet.Artifacts/` is `.gitignore`d. The committed numbers live
in `baseline.json` next to this README.

## Running the probe

```bash
dotnet run -c Release --project src/AgentWorkspace.PerfProbe -- rss
dotnet run -c Release --project src/AgentWorkspace.PerfProbe -- gc-idle
```

Output is single-line JSON on stdout for CI pipe-through.

## CI gate (Day 60)

The CI threshold check reads `baseline.json` and compares the latest run
against `1.5×` the saved values (advisor's "baseline-relative" pattern,
not absolute thresholds — BDN p95 varies 10–30 % run-to-run).
The baseline is updated only by an explicit `--update-baseline` run.
