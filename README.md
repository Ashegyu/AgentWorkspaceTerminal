# Agent Workspace Terminal

Windows 기반 persistent terminal multiplexer + AI agent workspace runtime.

설계: [Agent_Workspace_Terminal_Design.md](Agent_Workspace_Terminal_Design.md) (원안), [DESIGN.md](DESIGN.md) (개정 설계 v1).

## Status

**MVP-2 Day 13 완료** — SQLite session store + layout 자동 저장/복구. 다음은 Day 14 (회고 + MVP-3 진입 결정).

| 영역 | 상태 |
|---|---|
| `AgentWorkspace.Abstractions` | `IPseudoTerminal`, `PaneId`, `PtyChunk`, `PaneStartOptions` 정의 완료 |
| `AgentWorkspace.ConPTY` | ConPTY + Job Object + actor channel 구현 완료 |
| `AgentWorkspace.Spike.Console` | `awt-spike` CLI 동작 (사람이 콘솔에서 실행) |
| `AgentWorkspace.App.Wpf` | WPF host + WebView2 + xterm.js bridge — 8초 startup 검증 통과 |
| `web/terminal/` | xterm.js SPA, virtual-host 매핑으로 로드 |
| `AgentWorkspace.Core` | `BinaryLayoutManager` — immutable binary split tree, focus cycling, ratio clamping |
| `AgentWorkspace.App.Wpf.Workspace` | 다중 `PaneSession` 컨테이너, layout 변경과 PTY lifecycle 동기화 |
| `AgentWorkspace.Tests` | **71 활성 테스트** 통과 / 2 quarantine — 위 + SqliteSessionStore 9개 |
| `AgentWorkspace.Benchmarks` | BenchmarkDotNet harness — `CommandLine.Build`, `Envelope.Output (64B/8KB/64KB)`, `BinaryLayoutManager.{Split,FocusNext,Close}` |
| Session persistence | `~/.agentworkspace/sessions.db` (SQLite WAL) — 앱 재시작 시 자동 복구 |
| Command Palette | `Ctrl+Shift+P` → 10개 명령 (Restart / Ctrl+C / Clear / Font ± / **Split Right** / **Split Down** / **Close Pane** / **Focus Next** / **Focus Previous**) |

### UI 프레임워크 결정 (ADR-009)

원래 계획은 WinUI 3였으나 본 환경(.NET 10.0.103)에 Windows App SDK workload 미설치로 즉시 사용 불가. DESIGN §11에 명시한 fallback대로 **WPF + `Microsoft.Web.WebView2.Wpf`** 로 진행. ADR-002(단일 WebView2 + 다중 xterm.js)는 그대로 유지. MVP-2 종료 시점에 WinUI 3 환경이 갖춰졌는지 재평가.

## 환경

- Windows 10 1809+ (ConPTY 지원)
- .NET 10 SDK 설치 (현재 검증: 10.0.103)

## Build

```pwsh
dotnet build AgentWorkspaceTerminal.slnx
```

## Test

```pwsh
dotnet test src/AgentWorkspace.Tests/AgentWorkspace.Tests.csproj
```

## Run the WPF app (single pane terminal)

```pwsh
dotnet run --project src/AgentWorkspace.App.Wpf
```

기본 셸 검색 우선순위: `pwsh.exe` → `powershell.exe` → `cmd.exe`. 창을 닫으면 ConPTY/자식 프로세스 트리가 Job Object 정리로 함께 종료됩니다.

요구사항: Microsoft Edge **WebView2 Runtime** (Windows 11 기본 포함).

## Run benchmarks

BenchmarkDotNet은 항상 Release 빌드에서 실행해야 정확한 수치가 나옵니다.

```pwsh
# 모든 벤치마크 (수십 분)
dotnet run -c Release --project src/AgentWorkspace.Benchmarks -- --filter '*'

# 특정 부분만
dotnet run -c Release --project src/AgentWorkspace.Benchmarks -- --filter '*Layout*'
dotnet run -c Release --project src/AgentWorkspace.Benchmarks -- --filter '*Envelope*'

# 빠른 smoke run (정확도 ↓, 시간 ↓)
dotnet run -c Release --project src/AgentWorkspace.Benchmarks -- --job short --filter '*'
```

CI에서 회귀를 잡는 라이트한 가드는 `src/AgentWorkspace.Tests/Perf/PerfBudgetTests.cs`가 담당합니다 (Release 빌드에서 실행). BenchmarkDotNet의 분 단위 측정 대신 ms 임계값을 사용해 false-positive를 줄였습니다.

## Run the spike

`awt-spike`는 ConPTY가 자식 셸을 정상적으로 호스팅하는지 시각적으로 확인하기 위한 도구입니다. 인자 없이 실행하면 `pwsh.exe`(없으면 `powershell.exe` → `cmd.exe`)를 띄웁니다.

```pwsh
# 기본: 시스템 셸을 ConPTY 안에서 실행
dotnet run --project src/AgentWorkspace.Spike.Console

# 임의 명령
dotnet run --project src/AgentWorkspace.Spike.Console -- cmd /K dir
```

조작:

- `Ctrl+C` — 자식 프로세스에 SIGINT 전달
- `Ctrl+]` — spike 자체를 종료

## Project Layout

```
src/
 ├─ AgentWorkspace.Abstractions/   # IPseudoTerminal, ILayoutManager, LayoutNode 등
 ├─ AgentWorkspace.Core/           # BinaryLayoutManager 등 도메인 구현
 ├─ AgentWorkspace.ConPTY/         # ConPTY + JobObject + actor 구현
 ├─ AgentWorkspace.Spike.Console/  # awt-spike CLI
 ├─ AgentWorkspace.App.Wpf/        # WPF + WebView2 host, Workspace, PaneSession, Palette
 ├─ AgentWorkspace.Benchmarks/     # BenchmarkDotNet harness
 └─ AgentWorkspace.Tests/          # xunit 단위 + 통합 + perf-budget 테스트
web/terminal/                       # xterm.js SPA (index.html, bridge.js)
```

## Known Issues

- `EchoHello_OutputContainsExpectedString` 테스트는 Windows 11 Pro 26200에서 ConPTY가 짧은 자식 출력의 cell-grid diff를 emit하지 않는 현상으로 quarantine 상태. ConPTY 자체 동작(start/exit/kill/resize/Job-Object 자식 정리)은 모두 통과.
- 추후 `PaneOutputBroadcaster` + sink 도입 시 재조사 예정 (DESIGN §4 Hot Path).
