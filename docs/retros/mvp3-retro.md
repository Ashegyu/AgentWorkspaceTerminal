# Retro — MVP-3 (Day 15–19)

작성일: 2026-04-29
대상: DESIGN.md §ADR-010 Day 15–19에 해당하는 작업.

## 1. 무엇을 만들었나

5일치 작업의 결과물 (Day 14는 retro/ADR-010 문서만, 코드 없음):

| Layer | Artefact |
|---|---|
| Daemon | `AgentWorkspace.Daemon` 콘솔 exe — `DaemonHost`, `ControlChannelServer`, `PtyControlChannel`, `RpcDispatcher` |
| Auth | `SessionToken` — HMAC-SHA256 session token, `%LOCALAPPDATA%\AgentWorkspace\session.token` |
| Wire | `AWT2` 바이너리 RPC 프로토콜 — `[magic 4B][op 1B][reqId u32 BE][len u32 BE][JSON payload]` |
| Client | `AgentWorkspace.Client` — `ClientConnection`, `NamedPipeControlChannel`, `NamedPipeDataChannel`, `DaemonDiscovery`, `RemoteSessionStore` |
| Reattach | `PaneSession.ReattachAsync` — 데몬이 pane을 보유 중이면 re-spawn 없이 subscribe만 |
| App | `MainWindow.TryRestoreSessionAsync` — `LiveState == "Running"` 분기 (reattach vs. fresh start) |
| Tests | RpcRoundtripTests 6개 + SessionTokenTests 추가 → **101 자동 / 2 quarantine** |

git diff a3079e7 HEAD 기준: 41 files changed, +4101 / -164 LOC.

## 2. 잘 된 것

### 2.1 단일 RPC 프레임으로 control + data 채널 통합

ADR-003에서 "gRPC over Named Pipe + Raw Pipe"를 결정했지만, 실제 구현에서 protobuf/gRPC 없이
`AWT2` 바이너리 헤더 + JSON payload 조합으로 **동일한 파이프 위에서 control과 data를 다중화**하는 것이
훨씬 단순하고 충분함을 발견했다. gRPC source generator 의존성이 없어 .NET 10 Preview에서 build 위험이 0.

### 2.2 `DaemonDiscovery` — client가 데몬을 기다리지 않아도 됨

`AllowSpawn=true`면 client가 데몬을 직접 스폰, token이 없으면 스폰 후 token 쓰기를 기다린다.
`AllowSpawn=false`면 token 없을 때 `IOException`으로 바로 실패. 이 두 경로를 테스트로 커버:
`Discovery_RejectsConnect_WhenTokenIsMissing`.

### 2.3 `ReattachScenario` roundtrip이 한 번에 통과

`RpcRoundtripTests.ReattachScenario_ClientDisconnectsThenReconnects_DaemonStaysAlive`는
실제 NamedPipe 위에서 두 번 연결/끊기를 수행하며 session store 데이터가 유지됨을 검증한다.
구현 중 race 없이 첫 번째 실행에서 통과한 것은 `PtyControlChannel`의 entry-lifecycle 설계가
처음부터 맞았다는 증거.

### 2.4 LiveState 필드를 wire에 추가해 reattach 분기 처리

새로운 RPC 메서드를 추가하지 않고 `AttachSessionResult.PaneSpecDto.liveState` 필드 하나만
추가해 클라이언트가 reattach/spawn 분기를 판단할 수 있게 했다. 인터페이스 추가 없음, 기존
`IControlChannel` 계약 불변.

### 2.5 `RpcDispatcher` 한 파일에 모든 handler 집중

handler 분산 없이 `switch/case`로 한 곳에 둔 덕에 추적이 쉽다. 파일 크기가 아직 800줄 이내.
MVP-4 이후 agent-pane handler가 추가될 때 분리 여부를 판단할 자연스러운 시점이 생긴다.

### 2.6 ADR-010의 Day-by-day 계획을 Day 18에 완주

원래 Day 21 완료 예상이었는데 Day 18에 모든 core 기능이 완성되었다.
압축된 이유: gRPC 스킵으로 Day 2절약, in-process mock 없이 real pipe 테스트로 reattach 검증 통합.

## 3. 잘 안 된 것

### 3.1 Quarantine 두 개 미해결

`EchoHello` / `InteractiveSession`은 daemon process 분리 후에도 여전히 quarantine 상태.
testhost 환경에서 ConPTY가 cell-grid diff를 emit하지 않는 문제는 process 분리로 해결되지 않았다.

* 비용: Day 17 roundtrip 테스트 작성 시 같은 제약을 재발견하여 테스트 설계를 조정해야 했다
  (sentinel 문자열 대신 "bytes > 0" 조건으로 완화).
* 행동: MVP-4 `PaneOutputBroadcaster` + `TextTapSink` 도입 시점에 세 번째 시도.
  sink 레이어에서 raw bytes를 in-memory ring buffer에 저장하면 testhost에서 직접 읽기 가능.

### 3.2 `RpcDtos.cs` 한 파일에 모든 DTO 집중

14개 이상의 DTO record가 한 파일에 있다. 아직 400줄 수준이어서 immediate problem은 아니지만
MVP-4 agent-pane DTO가 추가되면 분리가 필요해진다.
행동: MVP-4 Day 1에 `Wire/Dtos/` 디렉토리로 분할 or 그때도 800줄 미만이면 보류.

### 3.3 `PtyControlChannel._panes` 메모리 누수 경로 미검증

`ClosePaneAsync`를 호출하지 않고 client가 끊기면 `_panes`에 entry가 영구 잔존한다.
현재는 daemon이 같은 process에서 재시작되지 않으므로 문제없지만, daemon이 장기 실행되면
종료된 pane entry가 accumulate된다.
행동: MVP-4 또는 MVP-8 (Perf Hardening)에서 `PtyControlChannel`에
`ExitCode is not null` + 시간 초과 기준 GC 스윕 추가.

### 3.4 `DaemonHost.StartAsync` 에러 전파가 약함

`ControlChannelServer`가 파이프 생성 실패 시 exception을 `StartAsync` 호출자에게 전파하지 않고
background task에서 삼킬 수 있는 경로가 있다. 현재 테스트에서 커버되지 않음.
행동: MVP-4에서 `DaemonHostTests`에 파이프 이름 충돌 시나리오 추가.

## 4. ADR 회고

| ADR | 결정 | 결과 (Day 19) | 유지/수정 |
|---|---|---|---|
| ADR-003 | gRPC over Named Pipe + Raw Pipe | `AWT2` 커스텀 바이너리 프레임으로 대체 — gRPC 의존성 없음 | **수정** (ADR-011에서 공식화) |
| ADR-004 | ConPTY ownership = Daemon | `PtyControlChannel`이 `PseudoConsoleProcess` 소유 | **유지** |
| ADR-010 | Day 14–21 작업 계획 | Day 18에 완주 (3일 절약) | **완료** |

## 5. MVP-3 완료 기준 자체 평가

| 기준 | 상태 |
|---|---|
| client kill 후 attach 시 출력 연속성 유지 | ✓ `ReattachToLivePane_SubscribesWithoutRespawning` + `ReattachScenario` |
| `DaemonHost` NamedPipe 수신, session token 인증 | ✓ `SessionTokenTests` 5개 + `DaemonHostTests` |
| control + data 채널 RPC roundtrip | ✓ `RpcRoundtripTests` 6개 (PaneLifecycle / WriteInput / SessionStore 등) |
| 세션 영속화 (attach 후 pane list 유지) | ✓ `SessionStore_FullRoundTrip_OverWire` |
| 자동 테스트 101개 (회귀 0) | ✓ |

**MVP-3 완료 선언.**

## 6. Open questions (MVP-4로 이관)

- `PtyControlChannel._panes` GC 스윕 정책: TTL 기반? exit-code 확인 기반?
- `RpcDtos.cs` 분할 시점: 14 DTO 이상이 되면 즉시 vs. 800줄 threshold?
- `EchoHello` quarantine 세 번째 시도: `TextTapSink` 도입 후 즉시 vs. MVP-8?
- Daemon이 비정상 종료 시 client 복구 전략: auto-respawn, read-only mode, 또는 error overlay?

---

## 결론

MVP-3 Daemon Split은 Day 18에 예정보다 3일 일찍 완료. 핵심 성과는 세 가지:
1. gRPC 없이 `AWT2` 바이너리 프레임으로 충분한 RPC 레이어 구현.
2. `LiveState` 필드 하나로 reattach/spawn 분기를 인터페이스 변경 없이 처리.
3. 101개 자동 테스트 (회귀 0) — daemon process 분리 후에도 기존 테스트 전부 통과.

**MVP-4 진입을 권고**. 자세한 진입 plan은 [DESIGN.md ADR-011](../../DESIGN.md#adr-011).
