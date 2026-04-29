# Agent Workspace Terminal

Windows 기반 persistent terminal multiplexer + AI agent workspace runtime.

설계: [Agent_Workspace_Terminal_Design.md](Agent_Workspace_Terminal_Design.md) (원안), [DESIGN.md](DESIGN.md) (개정 설계 v1).

## Status

**MVP-1 Day 1–2 완료** — ConPTY 단일 spike. 다음은 Day 3–4 (WinUI 3 + WebView2 + xterm.js).

| 영역 | 상태 |
|---|---|
| `AgentWorkspace.Abstractions` | `IPseudoTerminal`, `PaneId`, `PtyChunk`, `PaneStartOptions` 정의 완료 |
| `AgentWorkspace.ConPTY` | ConPTY + Job Object + actor channel 구현 완료 |
| `AgentWorkspace.Spike.Console` | `awt-spike` CLI 동작 (사람이 콘솔에서 실행) |
| `AgentWorkspace.Tests` | 13 활성 테스트 통과 / 1 quarantine |

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
 ├─ AgentWorkspace.Abstractions/   # IPseudoTerminal 등 공용 contract
 ├─ AgentWorkspace.ConPTY/         # ConPTY + JobObject + actor 구현
 ├─ AgentWorkspace.Spike.Console/  # awt-spike CLI
 └─ AgentWorkspace.Tests/          # xunit 통합 테스트
```

## Known Issues

- `EchoHello_OutputContainsExpectedString` 테스트는 Windows 11 Pro 26200에서 ConPTY가 짧은 자식 출력의 cell-grid diff를 emit하지 않는 현상으로 quarantine 상태. ConPTY 자체 동작(start/exit/kill/resize/Job-Object 자식 정리)은 모두 통과.
- 추후 `PaneOutputBroadcaster` + sink 도입 시 재조사 예정 (DESIGN §4 Hot Path).
