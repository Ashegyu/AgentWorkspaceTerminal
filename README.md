# Agent Workspace Terminal

Windows 기반 persistent terminal multiplexer + AI agent workspace runtime.

설계: [Agent_Workspace_Terminal_Design.md](Agent_Workspace_Terminal_Design.md) (원안), [DESIGN.md](DESIGN.md) (개정 설계 v1).

## Status

**MVP-3 Day 17 완료** — *실제로 다른 exe* 가 동작. WPF 클라이언트 (`AgentWorkspace.App.exe`)가 NamedPipe로 데몬 (`awtd.exe`)에 attach해 pane을 운영하고, ConPTY + SQLite 세션 스토어는 모두 데몬 측 소유. 신규 `AgentWorkspace.Client` 프로젝트가 `IControlChannel` / `IDataChannel` / `ISessionStore`를 RPC로 구현. 5개 신규 wire roundtrip 테스트로 client↔daemon end-to-end 검증.

회고: [docs/retros/mvp1-mvp2-retro.md](docs/retros/mvp1-mvp2-retro.md). 진입 결정: [DESIGN.md ADR-010](DESIGN.md#adr-010).

| 영역 | 상태 |
|---|---|
| `AgentWorkspace.Abstractions` | `IPseudoTerminal`, `BinaryLayoutManager` (Day 17 이동), `IControlChannel` / `IDataChannel`, `ISessionStore` 정의 |
| `AgentWorkspace.ConPTY` | ConPTY + Job Object + actor channel 구현 |
| `AgentWorkspace.Spike.Console` | `awt-spike` CLI 동작 (사람이 콘솔에서 실행) |
| `AgentWorkspace.Core` | SQLite 세션 스토어 (`SqliteSessionStore`, `LayoutJson`) — daemon이 인스턴스화 |
| `AgentWorkspace.Daemon` (`awtd`) | NamedPipe listener + bearer token + **PtyControlChannel** + **RpcDispatcher** + SqliteSessionStore 호스팅. Day 17 부터 *실제로 별도 exe* |
| `AgentWorkspace.Client` | **NEW (Day 17)** — `RpcProtocol` (AWT2 frame: magic+op+requestId+u32 len), `NamedPipeControlChannel`, `NamedPipeDataChannel`, `RemoteSessionStore`, `DaemonDiscovery` (token-gated, auto-spawn awtd.exe) |
| `AgentWorkspace.App.Wpf` | WPF + WebView2 + xterm.js. ConPTY/Core 직접 의존성 제거; `DaemonDiscovery.ConnectAsync` → `ClientConnection` → 채널 사용 |
| `AgentWorkspace.Tests` | **100 활성 테스트** 통과 / 2 quarantine — 위 + Daemon 5개 wire roundtrip (start/exit/subscribe/close + reattach + store) |
| Session persistence | `~/.agentworkspace/sessions.db` (SQLite WAL) — daemon이 owner, client는 RPC로만 접근 |
| Command Palette | `Ctrl+Shift+P` → 10개 명령 (Restart / Ctrl+C / Clear / Font ± / Split Right / Split Down / Close Pane / Focus Next / Focus Previous) |

### UI 프레임워크 결정 (ADR-009)

원래 계획은 WinUI 3였으나 본 환경(.NET 10.0.103)에 Windows App SDK workload 미설치로 즉시 사용 불가. DESIGN §11에 명시한 fallback대로 **WPF + `Microsoft.Web.WebView2.Wpf`**. ADR-002(단일 WebView2 + 다중 xterm.js)는 그대로 유지. MVP-2 종료 시점 재평가 결과 perf/UX 문제 없음.

### Day 17 architecture changes

```
Day 16:  AgentWorkspace.App.exe (WPF + ConPTY + SQLite 모두 in-proc)
                                          │
Day 17:  AgentWorkspace.App.exe ─NamedPipe─►  awtd.exe
            (WPF, WebView2)                     (ConPTY, SQLite, RpcDispatcher)
```

- 클라이언트는 `%LOCALAPPDATA%\AgentWorkspace\session.token`을 보고 데몬에 connect; 없으면 `awtd.exe`를 spawn 후 polling.
- 데몬은 클라이언트가 종료돼도 잔존하므로 client 재기동 시 attach가 자연스럽게 동작 (ADR-010 Day 20 시나리오의 baseline).
- Wire frame: `[magic 4B 'AWT2'] [op 1B] [requestId u32 BE] [payloadLen u32 BE] [JSON payload]`. 핸드셰이크는 op 0x01..0x03, RPC는 0x10/0x11, 푸시는 0x20/0x21. Day 18에 gRPC로 codec만 교체 예정.

## 환경

- Windows 10 1809+ (ConPTY 지원)
- .NET 10 SDK 설치 (현재 검증: 10.0.103)
- Microsoft Edge **WebView2 Runtime** (Windows 11 기본 포함)

## Build

```pwsh
dotnet build AgentWorkspaceTerminal.slnx
```

## Test

```pwsh
dotnet test src/AgentWorkspace.Tests/AgentWorkspace.Tests.csproj
```

## Run the WPF app + auto-spawn daemon

```pwsh
dotnet run --project src/AgentWorkspace.App.Wpf
```

기본 셸 검색 우선순위: `pwsh.exe` → `powershell.exe` → `cmd.exe`. App이 시작될 때 `%LOCALAPPDATA%\AgentWorkspace\session.token`을 통해 데몬에 attach; 살아 있는 데몬이 없으면 동봉된 `awtd.exe`를 spawn (ProjectReference로 App.Wpf bin에 복사됨). App을 종료해도 daemon은 남으므로 재실행 시 자동 attach.

## Run the daemon (`awtd`) directly

테스트 / 디버깅 시 데몬을 별도 콘솔에서 띄우고 싶다면:

```pwsh
dotnet run --project src/AgentWorkspace.Daemon
```

기동 시 출력:

```
[awtd] listening on \\.\pipe\agentworkspace.control.S-1-5-21-...
[awtd] session token written to C:\Users\<user>\AppData\Local\AgentWorkspace\session.token
[awtd] press Ctrl+C to stop.
```

- token 파일은 32-char base64(24 random bytes), ACL은 현재 사용자 FullControl 만 허용 (inherit 끔).
- pipe 이름은 `agentworkspace.control.{user-sid}` 로 사용자별 격리. 동시 connection 한도 4개.
- `Ctrl+C` 로 graceful shutdown — accept loop 정리, ConPTY 자식 트리 Job Object 일괄 정리, token 파일 삭제.
- 데몬은 SQLite 세션 스토어 (`~/.agentworkspace/sessions.db`)를 직접 owner. 클라이언트가 RPC (RpcMethods.Store*)로 접근.

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

CI에서 회귀를 잡는 라이트한 가드는 `src/AgentWorkspace.Tests/Perf/PerfBudgetTests.cs`가 담당합니다 (Release 빌드에서 실행).

## Run the spike

`awt-spike`는 ConPTY가 자식 셸을 정상적으로 호스팅하는지 시각적으로 확인하기 위한 도구입니다.

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
 ├─ AgentWorkspace.Abstractions/   # IPseudoTerminal, ILayoutManager, BinaryLayoutManager, IControlChannel, IDataChannel, ISessionStore
 ├─ AgentWorkspace.ConPTY/         # ConPTY + JobObject + actor 구현
 ├─ AgentWorkspace.Core/           # SqliteSessionStore + LayoutJson (daemon이 인스턴스화)
 ├─ AgentWorkspace.Spike.Console/  # awt-spike CLI
 ├─ AgentWorkspace.Daemon/         # awtd 데몬: control NamedPipe + bearer token + PtyControlChannel + RpcDispatcher
 ├─ AgentWorkspace.Client/         # NEW (Day 17): RpcProtocol, NamedPipeControlChannel, NamedPipeDataChannel, RemoteSessionStore, DaemonDiscovery
 ├─ AgentWorkspace.App.Wpf/        # WPF + WebView2 host, Workspace, PaneSession, Palette — daemon에 RPC로 attach
 ├─ AgentWorkspace.Benchmarks/     # BenchmarkDotNet harness
 └─ AgentWorkspace.Tests/          # xunit 단위 + 통합 + perf-budget + wire roundtrip 테스트
web/terminal/                       # xterm.js SPA (index.html, bridge.js)
```

## Known Issues

- `EchoHello_OutputContainsExpectedString` / `InteractiveSession_EchoesUserInputBack` 테스트는 ConPTY가 짧은 자식 출력의 cell-grid diff를 xunit testhost에서 emit하지 않는 현상으로 quarantine 상태. Day 17의 wire RPC 경로는 정상 동작 (`PaneLifecycle_StartWriteSubscribeClose_RoundTripsThroughDaemon`은 daemon이 보내는 *어떤* bytes든 클라이언트에 도달하는지를 검증). 본격적인 cell-grid 검증은 PaneOutputBroadcaster + VT decoder sink 도입 시 재시도 예정.
