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
| 8 | PolicyEngine 50-rule eval | (no hard ADR ceiling) | `Mvp8/PolicyEvalBench.cs` (BDN) | 59 |
| 9 | Redaction 14-rule eval | (no hard ADR ceiling) | `Mvp8/RedactionEvalBench.cs` (BDN) | 59 |

Items #8/#9 are hot-path measurements without numeric ADR-008 budgets. Day 59
captured the 50-rule miss / 14-rule miss medians as the regression baseline;
both are well under the soft 100 µs / 50 µs informal targets, so no
optimisation work shipped on Day 59.

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

# echo-latency reads pre-collected ms samples on stdin (or --input file).
# Real round-trip data must come from a future xterm.js → host bridge;
# until then, paste collected values manually:
printf "12.4\n11.0\n13.3\n" | awt-perfprobe echo-latency --threshold-ms 50
```

Output is single-line JSON on stdout for CI pipe-through. Exit code is 0 on
pass, 1 on threshold violation, 64 on usage error, 65 when no samples found.

## CI gate (Day 60)

The CI threshold check reads `baseline.json` and compares the latest run
against `1.5×` the saved values (advisor's "baseline-relative" pattern,
not absolute thresholds — BDN p95 varies 10–30 % run-to-run).
The baseline is updated only by an explicit `--update-baseline` run.
