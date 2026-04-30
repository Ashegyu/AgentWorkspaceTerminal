# Performance Budget — DESIGN ADR-008

ADR-008 기준치를 어떻게 자동 측정·감시하는지 한 화면에 정리한 표.

## 측정 도구 매트릭스 (MVP-8 Day 60 이후)

| ADR-008 항목 | 자동화 | 측정 방법 |
|---|---|---|
| 키 입력 → 화면 echo p95 ≤ 50ms | △ (수동) | `awt-perfprobe echo-latency` (외부에서 수집한 ms 샘플 stdin) |
| ConPTY read → client write p95 ≤ 5ms | ✅ (수동 BDN) | `Mvp8/PtyReadWriteBench.cs` |
| 4-pane idle RSS ≤ 500MB (daemon floor) | ✅ (CI gate) | `awt-perfprobe rss --panes 4` |
| pane 1개 idle RSS 증가분 ≤ 30MB (daemon floor) | ✅ (CI gate) | `awt-perfprobe rss --panes 1` |
| 1MB burst output 표시 ≤ 250ms | ✅ (수동 BDN) | `Mvp8/BurstRenderBench.cs` |
| GC Gen2 / 분 (idle) ≤ 1 (probe-self) | ✅ (CI gate) | `awt-perfprobe gc-idle` |
| Job-Object 종료 시 좀비 자식 = 0 | ✅ (CI gate) | `awt-perfprobe zombies` + `WorkspaceLifecycleTests`, `PseudoConsoleProcessTests.Dispose_TerminatesDescendantProcessTree` |
| PolicyEngine 50-rule 평가 | ✅ (수동 BDN) | `Mvp8/PolicyEvalBench.cs` |
| Redaction 14-rule 평가 | ✅ (수동 BDN) | `Mvp8/RedactionEvalBench.cs` |

CI 게이트는 `.github/workflows/perf-gate.yml` → `scripts/Test-PerfBudget.ps1`. 매 push/PR마다 4개의 probe 메트릭을 측정한 뒤 (a) ADR-008 하드 천장과 (b) `baseline.json` × 1.5 회귀 천장 둘 다 만족해야 통과. BDN 벤치는 5분 이상 걸리므로 수동 실행만 (`scripts/...` 외부에서 `dotnet run -c Release --project src/AgentWorkspace.Benchmarks`).

## 자동 가드 (xunit Release 빌드)

`src/AgentWorkspace.Tests/Perf/PerfBudgetTests.cs`. BenchmarkDotNet의 정밀도와는 다른, **회귀 가드** 목적.

| 테스트 | 임계값 | 측정 대상 |
|---|---|---|
| `Layout_Split_AverageUnderOneMillisecond_With16PaneTree` | < 1000μs | 16-pane 트리에서 5000회 split |
| `Layout_FocusNext_AverageUnderTenMicroseconds_With64PaneTree` | < 10μs | 64-pane 트리에서 100,000회 focus 순환 |
| `Envelope_Output_64KB_AverageUnderOneMillisecond` | < 1000μs | 64KB 페이로드 1000회 base64 + JSON 인코딩 |

임계값은 BenchmarkDotNet 측정값보다 1~2 자릿수 여유를 둔 값으로, 노이즈로 인한 false-positive 없이 quadratic 회귀(μs → ms)를 잡습니다.

## 정밀 측정 (BenchmarkDotNet)

`src/AgentWorkspace.Benchmarks/`. CI에서 매 빌드마다 돌리지는 않고, perf-sensitive 변경 후 명시적으로 실행:

```pwsh
dotnet run -c Release --project src/AgentWorkspace.Benchmarks -- --filter '*'
```

벤치 클래스:

- `CommandLineBench` — `Build("cmd", 3 args)` / `BuildEnvironmentBlock(7 keys)` / `AppendArgument` 핫 루프
- `EnvelopeBench` — `Output(64B/8KB/64KB)` / `Init` / `Layout(4-pane H+V tree)`
- `LayoutBench` — `Split` / `FocusNext` / `Close` × `[1, 4, 16]` pane

각각 `MemoryDiagnoser` 활성 → allocation 표시 (Gen0/1/2, total bytes).

## Baseline 수치

본 머신: Windows 11 Pro 26200, .NET SDK 10.0.103, win-x64. BenchmarkDotNet `--job short` 1회 실행 결과를 *대략* 기록 (ShortRun은 표본 적어 noise 큼; 정밀 측정은 `--job default`).

| 벤치 | Mean (대략) | 메모 |
|---|---:|---|
| `LayoutBench.SplitOnce (1 pane)` | ~11μs | 단일-노드 트리에서 split 한 번 |
| `LayoutBench.SplitOnce (16 panes)` | single-digit μs | tree depth log₂(16)=4, 측정상 1 pane과 큰 차이 없음 |
| 그 외 항목 | 정밀 측정 필요 | `dotnet run -c Release ... -- --filter '*'` 로 수십 분 측정 |

회귀 감지의 1차 라인은 `PerfBudgetTests` (xunit, ms 단위 임계). BenchmarkDotNet은 hot path 수정 후 명시적으로 한 번씩 돌려 baseline과 비교.

## 지속 측정 항목 (수동, MVP-3 시점에 자동화 검토)

- `dotnet-counters monitor --process-id <pid>` — Gen2 / 분, allocation rate, working set
- `Get-Process AgentWorkspace.App | select WorkingSet64` — RSS 추적
- WebView2 별도 process(`msedgewebview2.exe`) — 메모리 별도 확인
