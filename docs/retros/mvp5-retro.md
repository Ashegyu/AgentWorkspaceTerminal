# Retro — MVP-5 (Day 27–34)

작성일: 2026-04-30
대상: DESIGN.md §ADR-012 Day 27–34에 해당하는 작업.

## 1. 무엇을 만들었나

8일치 작업의 결과물:

| Layer | Artefact |
|---|---|
| Abstractions | `IAgentAdapter`, `IAgentSession`, `IAgentAdapter`, `AgentCapabilities`, `AgentSessionOptions`, `AgentSessionId`, `AgentEvent` 계층 (`AgentMessageEvent`, `ActionRequestEvent`, `PlanProposedEvent`, `AgentDoneEvent`, `AgentErrorEvent`, `PlannedAction`) |
| Claude Adapter | `AgentWorkspace.Agents.Claude` — `ClaudeAdapter.StartSessionAsync` (`claude --print <prompt> --output-format stream-json` 스폰), `ClaudeSession`, `StreamJsonParser` (line-delimited JSON → `AgentEvent`) |
| Bridge | `AgentPaneSession` — `PaneSession` + `IAgentSession` 합성; `PaneId` / `AgentSessionId` / `Events` 노출 |
| UI | `AgentTraceViewModel` (thread-safe `ObservableCollection<AgentEventViewModel>`, `Dispatcher` 경유), `AgentEventViewModel.From` 팩토리 (`MessageEventVm`, `ActionRequestVm`+INPC, `DoneEventVm`, `ErrorEventVm`, `UnknownEventVm`) |
| Transcript | `TranscriptSink.Open(AgentSessionId)` — `%LOCALAPPDATA%\AgentWorkspace\transcripts\{id}.jsonl` JSONL append-only |
| App Integration | `MainWindow`: `_agentAdapter`, `_agentTrace` 필드, `AskAgentAsync` (dialog → split → pane start → adapter → daemon RPC → pump), `PumpAgentEventsAsync` (await foreach + sink + error dispatch) |
| Daemon RPC | `agent.start` op 등록, `PtyControlChannel.StartAgentSessionAsync`, `ControlChannelServer` 핸들러 확장 |
| Command Palette | "Ask Agent…" entry — `AgentInputDialog` (prompt + working directory), `agent.start` RPC |
| Tests | `ClaudeAdapterTests` 15개 (`Name`, `Capabilities`, `StreamJsonParser.Parse` 전 분기) + `AgentTraceViewModelTests` 10개 (`Append`, `Clear`, `From` 전 타입, INPC) |

git diff 980200d 4972153 기준: 26 files changed, +2 341 / -47 LOC.

## 2. 잘 된 것

### 2.1 `StreamJsonParser` 분리 — 파싱 로직 독립 테스트

`ClaudeAdapter` 내부의 파싱 로직을 `StreamJsonParser`라는 `internal static` 클래스로 분리했다.
`InternalsVisibleTo("AgentWorkspace.Tests")` 한 줄 추가로 외부 프로세스 없이
모든 JSON 케이스 (text, tool_use, result success/error, 빈 배열, 알 수 없는 타입, 잘못된 JSON)를
순수 단위 테스트로 커버했다.

### 2.2 `AgentTraceViewModel` — Dispatcher 주입으로 WPF-없는 테스트

`internal AgentTraceViewModel(Dispatcher dispatcher)` 생성자를 두어 테스트에서
`Dispatcher.CurrentDispatcher`를 그대로 주입하면 `CheckAccess()` 가 항상 `true`를 반환한다.
WPF 스레드 모델을 흉내 내는 추가 인프라 없이 `Append`, `Clear`, INPC 검증이 가능했다.

### 2.3 `PumpAgentEventsAsync` — 비동기 이벤트 펌프 패턴

`await foreach`로 `IAsyncEnumerable<AgentEvent>`를 소비하고, 정상 종료·취소(`OperationCanceledException`)·오류를
각각 다른 경로로 처리하는 단일 `try/catch/finally` 블록이 명확하게 읽힌다.
`finally`에서 `sink.DisposeAsync` + `session.DisposeAsync`를 모두 처리해 누수 경로 없음.

### 2.4 `AgentPaneSession` 합성 패턴

`PaneSession`과 `IAgentSession`을 인터페이스 수준에서 합성하는 대신
단순한 `sealed class AgentPaneSession(PaneSession, IAgentSession)` 구조로 유지했다.
두 수명 주기 (`pane close` vs `agent done`)가 독립적으로 닫힐 수 있어 각 Dispose 경로가 분리됐다.

### 2.5 `TranscriptSink` — 경로 자동 생성 + 게으른 열기

`Open` 호출 시점에 `Directory.CreateDirectory`로 경로를 보장하고 첫 `AppendAsync` 전까지
파일 스트림을 열지 않아 빈 파일이 생기지 않는다.

### 2.6 Day-by-Day 계획 완주

ADR-012의 Day 27–34 계획을 순서대로 완주. 중간에 설계 변경이나 일정 슬립 없이
8일 연속 1 커밋 / 1 관심사 패턴이 유지됐다.

## 3. 잘 안 된 것

### 3.1 `AgentInputDialog` — 최소 기능 UI

`AgentInputDialog`는 Prompt + WorkingDirectory 두 필드만 있는 최소 WPF 다이얼로그다.
히스토리, 프리셋, 프롬프트 템플릿 등 실사용 편의 기능이 없다.
행동: MVP-6 또는 UX 이터레이션 슬롯에서 Command Palette 내 인라인 입력 방식으로 교체 검토.

### 3.2 `CancelAsync` 미구현

`IAgentSession` 인터페이스에 `CancelAsync`가 정의되어 있으나 `ClaudeSession`에서
`Process.Kill(entireProcessTree: true)`로만 구현되어 있고 SIGINT / graceful cancel 흐름이 없다.
MVP-5 완료 기준 중 "CancelAsync 호출 시 ConPTY SIGINT 전달"은 부분 충족 수준.
행동: MVP-7 Policy+Redaction 또는 별도 슬롯에서 `CancelAsync` → ConPTY SIGINT 전달 구현.

### 3.3 `PlanProposedEvent` 미방출

`StreamJsonParser`가 `PlanProposedEvent`를 파싱하는 경로가 없다.
Claude Code CLI `stream-json` 포맷에 plan 블록이 추가되면 `UnknownEventVm`으로 낙하한다.
행동: Claude Code CLI 포맷 문서가 확정되면 `plan` 타입 파서 추가.

### 3.4 `AgentTrace` UI — XAML ItemsControl 스타일 미완성

`AgentTrace.xaml`의 `ItemsControl`이 기본 DataTemplate을 사용해 ViewModel 타입 분기가
없다. 화면에서 이벤트 타입이 시각적으로 구분되지 않는다.
행동: MVP-6 전에 DataTemplate 셀렉터로 `MessageEventVm` / `ActionRequestVm` 타입별 시각화.

### 3.5 `Daemon agent.start` 핸들러 — 실제 pane I/O 연결 미구현

`StartAgentSessionAsync` RPC는 메타데이터 등록만 하고 agent 출력을 ConPTY pane으로
라우팅하지 않는다. 현재 agent 이벤트는 `_agentTrace` ViewModel에만 표시된다.
행동: MVP-6 또는 Agent I/O 슬롯에서 `IDataChannel.SendAsync` 경유 pane 표시 연결.

## 4. ADR 회고

| ADR | 결정 | 결과 (Day 34) | 유지/수정 |
|---|---|---|---|
| ADR-012 | Day 27–34 MVP-5 계획 | Day 34에 계획대로 완주 | **완료** |
| `ClaudeAdapter` line-delimited JSON | `StreamJsonParser` `internal static` + `InternalsVisibleTo` | 단위 테스트 15개 커버 | **유지** |
| `AgentTraceViewModel` `Dispatcher` 주입 | `internal` 생성자 패턴 | WPF 없이 테스트 통과 | **유지** |

## 5. MVP-5 완료 기준 자체 평가

| 기준 | 상태 |
|---|---|
| `stream-json` 출력에서 `Message`, `ActionRequest`, `Done`, `Error` 이벤트 파싱 | ✓ `StreamJsonParser` + 15개 단위 테스트 |
| `AgentTrace` UI에 이벤트 실시간 표시 (`ObservableCollection`) | ✓ `AgentTraceViewModel.Append` + WPF `ItemsControl` |
| `CancelAsync` 호출 시 세션 종료 | △ `Process.Kill` 구현 — SIGINT 미구현 |
| transcript JSONL 파일 생성 | ✓ `TranscriptSink.Open` + `AppendAsync` |
| 기존 자동 테스트 회귀 0 | ✓ 193 pass / 2 skip (quarantine) / 0 fail |

**조건부 완료**: CancelAsync graceful shutdown 제외 핵심 기능 전부 충족.

## 6. Open questions (MVP-6으로 이관)

- `IWorkflow` 인터페이스 — `WorkflowTrigger` 타입 설계 (이벤트 기반 vs. 명시적 호출?)
- `FixDotnetTestsWorkflow` — 빌드 실패 이벤트를 어디서 발행? `MSBuild` 출력 파싱? 별도 watcher?
- Approval UI — WPF modal? Command Palette 내 인라인? Daemon side-car?
- `ActionRequestEvent` approval 흐름 — per-action 또는 batch?
- Agent pane 출력 라우팅 — ConPTY pane에 `IDataChannel.SendAsync`로 직접 주입?

---

## 결론

MVP-5 Agent Pane은 Day 34에 ADR-012 계획 그대로 완료.
핵심 성과:
1. `ClaudeAdapter` (stream-json) + `AgentTrace` WPF + `TranscriptSink` JSONL 파이프라인 완성.
2. `StreamJsonParser` 분리 + `AgentTraceViewModel` Dispatcher 주입으로 WPF 없이 25개 단위 테스트.
3. 193개 자동 테스트 (회귀 0, 기존 168 → 193 +25).

**MVP-6 Workflow Engine 진입을 권고**. 자세한 진입 plan은 DESIGN.md ADR-013.
