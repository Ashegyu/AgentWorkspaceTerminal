# Agent Workspace Terminal

Windows 기반 persistent terminal multiplexer + AI agent workspace runtime.

- **사용법은 [docs/USER_GUIDE.md](docs/USER_GUIDE.md) 가 첫 정거장입니다.** 단축키, 팔레트 명령, policy yaml, perfprobe CLI 사용법까지 한 페이지에 모여 있음.
- 설계 의도: [Agent_Workspace_Terminal_Design.md](Agent_Workspace_Terminal_Design.md) (원안), [DESIGN.md](DESIGN.md) (개정 v1 + 모든 ADR).
- ADR-008 성능 budget 매트릭스: [docs/perf-budget.md](docs/perf-budget.md).
- 회고: [docs/retros/](docs/retros/) (MVP-1 ~ MVP-8).

---

## Status

**MVP-8 종료 + maintenance 슬롯 1~4 완료** (2026-04-30). MVP-9 진입 보류 (ADR-016 트리거 미충족 — 자세한 결정 근거는 [DESIGN.md ADR-016](DESIGN.md)).

| 영역 | 상태 |
|---|---|
| `AgentWorkspace.Abstractions` | `IPseudoTerminal`, `BinaryLayoutManager`, `IControlChannel`/`IDataChannel`, `ISessionStore`, `IWorkflow` + `WorkflowTrigger`, `IApprovalGateway` |
| `AgentWorkspace.ConPTY` | ConPTY + Job Object + actor channel |
| `AgentWorkspace.Core` | SQLite 세션 스토어, `PolicyEngine`, `PolicyEngineFactory` (yaml 사용자 룰 로드), `WorkflowEngine` |
| `AgentWorkspace.Daemon` (`awtd.exe`) | NamedPipe + bearer token + RpcDispatcher + SQLite owner |
| `AgentWorkspace.Client` | RpcProtocol (AWT2 frame), NamedPipeControlChannel/DataChannel, RemoteSessionStore, DaemonDiscovery |
| `AgentWorkspace.App.Wpf` | WPF + WebView2 + xterm.js. 16 팔레트 명령, AgentTrace, ApprovalDialog, EchoLatencyDump |
| `AgentWorkspace.Agents.Claude` | ClaudeAdapter — Claude Code CLI subprocess + JSONL transcript |
| `AgentWorkspace.PerfProbe` (`awt-perfprobe.exe`) | echo-latency / rss / rss-full / gc-idle / zombies |
| `AgentWorkspace.Benchmarks` | BenchmarkDotNet + MVP-8 4개 신규 벤치 |
| `AgentWorkspace.Tests` | **407 활성 테스트** 통과 / 2 quarantine |
| Session persistence | `~/.agentworkspace/sessions.db` (SQLite WAL) — daemon owns |
| User policy | `~/.agentworkspace/policies.yaml` (선택, schema v1) |
| CI perf gate | `.github/workflows/perf-gate.yml` — push/PR/dispatch 마다 4개 메트릭 회귀+천장 검사 |
| BDN nightly | `.github/workflows/bdn-nightly.yml` — 매일 05:17 UTC ShortRun |

### MVP 진행 요약

| MVP | 핵심 결과 |
|---|---|
| 1~2 | 팔레트 + 폰트/Clear/Restart/Ctrl+C, BinaryLayoutManager, 5개 split/focus 명령 |
| 3 | App ↔ daemon 분리 (NamedPipe + AWT2 frame + bearer token) |
| 4 | YAML 워크스페이스 템플릿 (Open/Save Snapshot) |
| 5 | Claude Code 에이전트 pane + AgentTrace + JSONL transcript |
| 6 | Workflow DSL 1차 (3개 hardcoded workflow + IApprovalGateway) |
| 7 | PolicyEngine (SafeDev / TrustedLocal, blacklist beats whitelist) + ApprovalDialog |
| 8 | ADR-008 7개 운영 메트릭 측정 도구 + CI gate + perf-budget 문서화 |
| Slots 1~4 | Full-stack RSS 측정, BDN nightly, Echo p95 자동 측정, yaml user policies |

---

## 환경

- Windows 10 1809+ (ConPTY 지원)
- .NET 10 SDK 설치 (검증: 10.0.103)
- Microsoft Edge **WebView2 Runtime** (Windows 11 기본 포함)
- 선택: Claude Code CLI (`claude`) — AI 에이전트 기능을 쓰려면

## Build

```pwsh
dotnet build AgentWorkspaceTerminal.slnx -c Release
```

## Test

```pwsh
dotnet test src/AgentWorkspace.Tests/AgentWorkspace.Tests.csproj
```

## Run

```pwsh
# WPF App (자동으로 데몬 spawn)
dotnet run --project src/AgentWorkspace.App.Wpf

# 데몬 단독 실행 (디버깅)
dotnet run --project src/AgentWorkspace.Daemon

# ConPTY 단독 진단 (awt-spike)
dotnet run --project src/AgentWorkspace.Spike.Console
```

App 실행 직후의 첫 단계는 [docs/USER_GUIDE.md §2 기본 조작](docs/USER_GUIDE.md#2-기본-조작) 참조.

## Run benchmarks

```pwsh
# 모든 벤치 (수십 분 — Release 필수)
dotnet run -c Release --project src/AgentWorkspace.Benchmarks -- --filter '*'

# 특정 부분만
dotnet run -c Release --project src/AgentWorkspace.Benchmarks -- --filter '*Mvp8*'

# 빠른 smoke
dotnet run -c Release --project src/AgentWorkspace.Benchmarks -- --job short --filter '*'
```

## Run perf probe (수동)

```pwsh
$probe = "src/AgentWorkspace.PerfProbe/bin/Release/net10.0-windows/awt-perfprobe.exe"
& $probe rss --panes 4
& $probe gc-idle --duration-sec 60
& $probe zombies --panes 4 --settle-ms 500

# 전체 스택 (App.Wpf 가 떠 있어야 함)
$pid = (Get-Process AgentWorkspace.App.Wpf).Id
& $probe rss-full --pid $pid --warmup-sec 3 --sample-sec 5
```

자세한 스위치/출력 포맷: [docs/USER_GUIDE.md §9 PerfProbe CLI](docs/USER_GUIDE.md#9-성능-측정-도구-perfprobe-cli).

---

## Architecture (요약)

```
AgentWorkspace.App.exe (WPF + WebView2 + xterm.js)
        │
        │  NamedPipe (AWT2 frame: magic+op+requestId+u32 len)
        │  bearer token: %LOCALAPPDATA%\AgentWorkspace\session.token
        │
        ▼
awtd.exe  ── RpcDispatcher
        ├─ ConPTY × N  (각 pane 의 자식 셸 + Job Object)
        ├─ SqliteSessionStore (~/.agentworkspace/sessions.db, WAL)
        ├─ PolicyEngine  (built-ins + ~/.agentworkspace/policies.yaml)
        └─ WorkflowEngine  (3 hardcoded workflows + ApprovalGateway)
```

App 종료 ≠ daemon 종료. 다음 App 실행 시 자동으로 동일 daemon 에 attach.

Wire frame: `[magic 4B 'AWT2'] [op 1B] [requestId u32 BE] [payloadLen u32 BE] [JSON payload]`. 핸드셰이크 op `0x01..0x03`, RPC `0x10/0x11`, 푸시 `0x20/0x21`.

---

## Project Layout

```
src/
 ├─ AgentWorkspace.Abstractions/   # interfaces + DTO records
 ├─ AgentWorkspace.ConPTY/         # ConPTY + JobObject + actor
 ├─ AgentWorkspace.Core/           # SQLite store, PolicyEngine, WorkflowEngine
 ├─ AgentWorkspace.Spike.Console/  # awt-spike CLI (ConPTY 진단)
 ├─ AgentWorkspace.Daemon/         # awtd.exe — NamedPipe + Rpc + SQLite owner
 ├─ AgentWorkspace.Client/         # RpcProtocol, NamedPipe channels, RemoteSessionStore
 ├─ AgentWorkspace.App.Wpf/        # WPF + WebView2 + xterm.js host
 ├─ AgentWorkspace.Agents.Claude/  # ClaudeAdapter (Claude Code CLI bridge)
 ├─ AgentWorkspace.PerfProbe/      # awt-perfprobe.exe (ADR-008 metrics)
 ├─ AgentWorkspace.Benchmarks/     # BenchmarkDotNet harness
 └─ AgentWorkspace.Tests/          # xunit (407 active)
web/terminal/                       # xterm.js SPA (index.html, bridge.js)
schemas/                            # workspace YAML schema + 예시
scripts/                            # Test-PerfBudget.ps1, Test-BdnBaseline.ps1
.github/workflows/                  # perf-gate.yml, bdn-nightly.yml
docs/                               # USER_GUIDE, perf-budget, manual-test-matrix, retros, policies.example.yaml
```

---

## Known Issues

- `EchoHello_OutputContainsExpectedString` / `InteractiveSession_EchoesUserInputBack` — quarantine 상태. ConPTY 가 짧은 자식 출력의 cell-grid diff 를 xunit testhost 에서 emit 하지 않는 현상. wire RPC 경로 자체는 정상 (`PaneLifecycle_StartWriteSubscribeClose_RoundTripsThroughDaemon` 가 검증). PaneOutputBroadcaster + VT decoder sink 도입 시 재시도 예정.
- Echo latency 측정에서 키 autorepeat 가 p95 를 부풀림 — `web/terminal/bridge.js` 주석 참조. 깨끗한 측정은 ≥ 50ms 간격 입력.
- `bdn-nightly.yml` / `perf-gate.yml` 첫 실행이 `setup-dotnet@v4 + dotnet-version: '10.0.x'` 에 걸릴 가능성 있음 (.NET 10 GA 미등록 시기). 첫 실패 시 동일 패치로 두 yaml 동시 수정.
