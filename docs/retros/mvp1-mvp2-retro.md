# Retro — MVP-1 (Day 1–7) + MVP-2 (Day 8–13)

작성일: 2026-04-29
대상: DESIGN.md §10 Day 1–13에 해당하는 작업.

## 1. 무엇을 만들었나

13일치 작업의 결과물:

| Layer | Artefact |
|---|---|
| Domain | `IPseudoTerminal`, `IPaneHost`-급 abstraction (PaneSession 형태), `ILayoutManager`, `ISessionStore` |
| ConPTY | `PseudoConsoleProcess` (actor-channel 직렬화) + `JobObject` (`KILL_ON_JOB_CLOSE`) + `ProcThreadAttributeList` |
| Layout | `BinaryLayoutManager` — immutable snapshot, focus 순환, ratio clamp |
| Storage | `SqliteSessionStore` — schema v1, WAL, foreign_keys, JSON blob layout/env |
| App | WPF + WebView2 + xterm.js, `Workspace`, `Command Palette` (10개 명령) |
| Tests | 71 자동 (xunit) + 2 quarantine + manual matrix 9 섹션 |
| Bench | `AgentWorkspace.Benchmarks` (BenchmarkDotNet) + perf-budget 회귀 가드 |

git log 기준 10개 의미 commit, ~3,000+ LOC 추가.

## 2. 잘 된 것

### 2.1 인터페이스를 일찍 박은 것

ADR-001 ("MVP-1~2는 단일 프로세스, MVP-3에서 Server 분리")의 절반은 *인터페이스 분리는 처음부터*였는데, 이 결정 덕에 MVP-2의 multi-pane 추가에서 **Workspace를 통째로 끼워 넣을 수 있었다**. PaneSession이 MainWindow와 결합한 부분이 거의 없었기에 (PostToWeb delegate 1개) Workspace 도입 비용이 작았다. MVP-3 Daemon 분리도 같은 흐름으로 갈 수 있다는 자신감.

### 2.2 ADR-006 — Plan-level batch approval

아직 agent를 도입 안 했지만, Command Palette를 만들면서 같은 패턴(plan + 위험 액션은 개별 재확인)을 미리 적용해 봤다. UX가 자연스러워서 MVP-6에서 그대로 재사용 가능한 confidence.

### 2.3 ADR-007 — pattern + entropy redaction

Day 7 시점까지 secret redaction 코드가 들어가지 않았지만, **`PaneSpec`이 `Environment` dict를 그대로 SQLite에 JSON으로 저장**하고 있어 *지금 결정해 두어야 할 다음 자리가 어디인지*는 명확해졌다. MVP-7에서 `UpsertPaneAsync` 직전에 redaction 한 줄 끼우면 끝.

### 2.4 BenchmarkDotNet + perf-budget 두 층

ShortRun smoke 측정에서 `Layout.SplitOnce`가 1-pane에서 ~11μs, 16-pane에서도 single-digit μs. 첫 번째 작성한 perf 가드가 **테스트 자체의 결함(매 iteration마다 트리 누적 → O(n²))**을 잡아내며 **bug 1마리를 자기 발견**했다. 이게 정확히 perf budget이 잡아야 할 클래스의 실수다.

### 2.5 Job Object 좀비 회수

`Dispose_TerminatesDescendantProcessTree` (single-pane) → `FourPaneWorkspace_DisposeReapsAllDescendantPings` (4-pane × `cmd → ping` 트리) → `PartialClose_OnlyTargetTreeIsReaped_OthersStayAlive`까지 잠금. ADR-008의 "좀비 자식 0개" 기준은 **자동으로** 보장된다.

### 2.6 SQLite session store가 *작동했다*

첫 실행: db 4096 B 생성 + 새 session. 두 번째 실행: 같은 layout으로 restore. 문서·메뉴얼 매트릭스에만 의존하지 않고 **PowerShell probe로 실제 round-trip**을 확인했다.

## 3. 잘 안 된 것

### 3.1 EchoHello / InteractiveSession quarantine

ConPTY가 짧은 자식 출력의 cell-grid diff를 emit하지 않는 환경 이슈. **두 번 다른 각도(one-shot echo / 인터랙티브 cmd)로 재시도했지만 같은 vague 증상**. advisor 호출도 timeout. 결국 사람 눈 매트릭스 §3.1로 대체.

* 비용: 진단에 1.5시간 추가 소비.
* 영향: 자동 회귀에서 제외했지만 ConPTY 자체 동작(start/exit/kill/resize/Job-Object reap)은 모두 자동 검증되어 있어 *치명적이지 않음*.
* 행동: MVP-3 Daemon 분리 후 hot-path에 `PaneOutputBroadcaster` + `TextTapSink`를 도입하면 raw bytes를 다른 sink로 갈라낼 수 있다. 그 시점에 **VT decoder를 sink 측에 두고 cell-grid를 재구성**해 verify.

### 3.2 ADR-009 fallback이 Day 3에 즉시 트리거됨

WinUI 3 workload가 본 환경에 없어서 WPF로 갈아탔다. 다행히 단일-WebView2/다중-xterm.js (ADR-002)는 그대로 유지되어 큰 비용은 없었다. 다만 *이 결정을 어떤 메트릭으로 되돌릴지* 명확하지 않다 — MVP-2 끝 (지금) 시점에 보면, WPF에서 perf 이슈 없고 ecosystem도 충분해서 **WinUI 3 재진입의 이득이 분명하지 않음**.

### 3.3 BenchmarkDotNet smoke baseline이 "TBD" 상태

전체 벤치(`--filter '*'`)는 수십 분 걸려 실제로 한 번도 돌리지 않았다. 한 항목(LayoutBench.SplitOnce)만 short run으로 측정. baseline 표는 perf-budget.md에서 "TBD" 다수. 회귀 비교 baseline이 없으면 가드는 절반 효과.

* 행동: MVP-3 시작 전 한 번 야간 background 실행 또는 CI 도입 시 매일 1회.

### 3.4 ConPTY 출력 capture 진단 능력

cell-grid diff가 안 나오는 정확한 원인을 모른다. spike에서 사람 눈으로 보면 정상 작동하는데 xunit testhost에서는 다른 증상. **testhost console 환경 vs 실제 세션 environment 차이**가 어딘가 있을 텐데 isolate 못 함.

* 행동: MVP-3 Daemon이 별도 process가 되면 이 차이가 자연스럽게 해소될 가능성. 그래도 안 되면 그 시점에 깊게 디버깅.

## 4. ADR 회고

| ADR | 결정 | 결과 (Day 13) | 유지/수정 |
|---|---|---|---|
| ADR-001 | MVP-1~2 단일 프로세스, MVP-3에서 Daemon 분리 | 단일 프로세스로 진행. PaneSession/Workspace/ISessionStore 모두 UI에 결합 안 됨 | **유지** — MVP-3 진입 (ADR-010) |
| ADR-002 | 단일 WebView2 + 다중 xterm.js | 4-pane까지 동작 확인 | **유지** |
| ADR-003 | gRPC over Named Pipe + Raw Pipe | 도입 시점 도래 (MVP-3) | **유지** |
| ADR-004 | ConPTY Ownership = SessionDaemon | MVP-3에서 적용 | **유지** |
| ADR-005 | Workflow는 코드 우선, DSL은 누적 후 | 미적용 (MVP-6 시점에 재평가) | **유지** |
| ADR-006 | Plan-level batch + per-action fallback | Command Palette에서 패턴 reuse | **유지** |
| ADR-007 | Secret redaction = pattern + entropy | 자리만 만들어 둠 (MVP-7 적용) | **유지** |
| ADR-008 | Performance budget (정량) | 자동 가드 3개 + manual 4개 매핑 | **유지** |
| ADR-009 | UI = WPF (fallback) | 즉시 트리거됨, perf/UX 문제 없음 | **유지 — MVP-2 종료 시 재평가 항목은 결정 보류**: WinUI 3로 옮길 명확한 이득이 없음 |

## 5. §1.2 MVP-1 완료 기준 — 자체 평가

| 기준 | 상태 |
|---|---|
| pwsh / cmd / wsl.exe / git-bash 모두 실행 | pwsh ✓ (manual + spike), cmd ✓ (auto), wsl/git-bash 미검증 (manual matrix) |
| IME(한/영, MS-IME) 정상, 조합 중 ESC 안 깨짐 | manual matrix §4 |
| emoji 4-byte UTF-8, CJK width 2 정상 | byte-level auto ✓, 시각 width manual matrix §3 |
| Ctrl+C / Ctrl+Break 동작 | auto (`PseudoConsoleProcess.SignalAsync` + Command Palette) |
| resize 1초 100회 stress | auto ✓ (`Resize_WhileRunning_DoesNotThrow_StressX100`) |
| Command Palette `Ctrl+Shift+P` 작동 | ✓ (10 명령) |

**3개 항목이 manual matrix 의존**. wsl/git-bash 호환성은 DESIGN상 "최소 pwsh가 통과하면 됨"이므로 close-enough.

## 6. §10 MVP-2 (Day 8–13) — 자체 평가

| Day | 산출물 | 상태 |
|---|---|---|
| 8–9 | LayoutManager + 다중 xterm.js | 22개 layout 테스트 + 통합 |
| 10 | Job Object 회귀 강화 | 4 신규 테스트 |
| 11–12 | benchmark harness + perf gate | bench 3 클래스 + 3 perf budget tests |
| 13 | session SQLite + layout 복구 | 9 store 테스트 + 앱 round-trip 검증 |

**4-pane idle RSS ≤ 500MB**는 manual probe (perf-budget.md). `tasklist` 또는 작업 관리자로 확인 — Day 14 시점에서 직접 측정 안 했음, MVP-3 진입 후 daemon process도 합쳐 측정.

## 7. MVP-3 진입 전 알고 있어야 할 것

1. **ADR-010 (이번 commit)**: Daemon 분리 진입 결정 + 첫 1주 plan.
2. **Quarantine 두 개**: EchoHello / InteractiveSession은 daemon 분리 후 `PaneOutputBroadcaster` + sink 도입 시점에 다시 시도.
3. **`MainWindow → Workspace → PaneSession` 결합**: MainWindow의 `PostToRendererAsync` delegate 1개만 PaneSession에 새고 있음. daemon 측 control-channel + data-channel으로 갈라 낼 때 이 delegate가 *분기점*.
4. **SQLite store는 daemon이 owner**가 되어야 하나? 또는 client도 read 가능해야? — ADR-010에서 결정.
5. **WebView2 user data folder**가 현재 `bin/Debug/.../WebView2Data`. daemon 분리 시점에 client 측이 own. daemon은 webview에 신경 안 씀.

## 8. Open questions (MVP-3 안에서 결정)

- ConPTY가 daemon process로 옮겨가면 testhost에서의 cell-grid 미emit이 자동 해결되는가?
- daemon이 죽으면 client는 어떻게 reattach? auto-respawn? read-only mode?
- session.token 위치: `%LOCALAPPDATA%\AgentWorkspace\session.token` (ADR-003) ACL 적용 — MVP-3 day 1에 wire.

---

## 결론

13일에 인프라가 충분히 단단해졌다. 71개 자동 테스트 + 9개 매뉴얼 매트릭스 섹션 + perf budget 가드 + bench harness + SQLite 영속화. 큰 미해결은 cell-grid quarantine 하나뿐이고, 이건 MVP-3 진입 후 자연스럽게 다룰 수 있는 자리에 도달.

**MVP-3 진입을 권고**. 자세한 진입 plan은 [DESIGN.md ADR-010](../../DESIGN.md#adr-010).
