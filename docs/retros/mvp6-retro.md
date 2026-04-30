# Retro — MVP-6 (Day 35–43)

작성일: 2026-04-30
대상: DESIGN.md §ADR-013 Day 35–43에 해당하는 작업.

## 1. 무엇을 만들었나

9일치 작업의 결과물:

| Layer | Artefact |
|---|---|
| Abstractions | `WorkflowExecutionId`, `WorkflowTrigger` DU (`ManualTrigger`, `TestFailedTrigger`, `BuildFailedTrigger`, `SessionDetachedTrigger`), `WorkflowContext`, `WorkflowResult` DU (`WorkflowSuccess`, `WorkflowCancelled`, `WorkflowFailure`), `IWorkflow`, `IApprovalGateway`, `ApprovalDecision` |
| Core Workflows | `WorkflowEngine` (singleton, ConcurrentDictionary, TriggerAsync/RunAsync/Cancel/IsRunning/DisposeAsync), `ExplainBuildErrorWorkflow`, `FixDotnetTestsWorkflow`, `SummarizeSessionWorkflow` |
| UI | `ApprovalDialog.xaml` + `ApprovalDialog.xaml.cs` (WPF Window, ItemsControl, ActionViewModel), `DialogApprovalGateway` (BeginInvoke + TCS), "Summarize Session…" Command Palette entry |
| Daemon RPC | `workflow.start` / `workflow.cancel` stub handlers in `RpcDispatcher`, `WorkflowStartRequest` / `WorkflowStartResult` / `WorkflowCancelRequest` DTOs, `RpcMethods.WorkflowStart` / `WorkflowCancel` |
| App Integration | `MainWindow._workflowEngine` 필드, WorkflowEngine 생성자 등록 (3 workflows + ClaudeAdapter + DialogApprovalGateway), `SummarizeSessionAsync` (latest transcript 탐색 → ManualTrigger → RunAsync → StatusText) |
| Tests | `FakeAgentAdapter` (scripted IAsyncEnumerable, EnqueueSequence), `AutoApproveGateway` / `AutoDenyGateway`, `WorkflowEngineTests` 8개, `FixDotnetTestsWorkflowTests` 9개 |

총 자동 테스트: **211개** (Day 34 기준 193개 + 신규 18개 / 회귀 0).

## 2. 잘 된 것

### 2.1 `WorkflowTrigger` — sealed DU, 이벤트 버스 아님

`WorkflowTrigger`를 `abstract record` + 4개 `sealed record`로 설계해 이벤트 버스를 도입하지 않았다.
워크플로 매칭이 `IWorkflow.CanHandle(trigger)` 단 하나의 가상 호출로 완결되어
프로덕션 코드와 테스트 모두 `new TestFailedTrigger(...)` 한 줄로 트리거 생성이 가능하다.

### 2.2 `IApprovalGateway` — Core↔WPF 분리 seam

`IApprovalGateway`를 Abstractions에 두어 `FixDotnetTestsWorkflow`가 WPF에 의존하지 않는다.
테스트는 `AutoApproveGateway` / `AutoDenyGateway` 두 줄짜리 스텁으로 승인·거부 흐름을 모두 커버.
WPF 측은 `DialogApprovalGateway` 하나만 교체하면 된다.

### 2.3 `FakeAgentAdapter.EnqueueSequence` — 테스트 가독성

`adapter.EnqueueSequence(new AgentMessageEvent(...), new AgentDoneEvent(...))` 패턴이
테스트 의도를 한눈에 보여준다. `Queue<AgentEvent[]>` FIFO 구조로 복수 세션 시나리오도
`EnqueueSequence`를 여러 번 호출하면 자연스럽게 지원된다.

### 2.4 `WorkflowEngine` — fire-and-forget / awaitable 이중 API

`TriggerAsync(trigger)` (즉각 반환 + 백그라운드 실행) 와
`RunAsync(trigger, ct)` (await 가능 + 결과 반환) 두 진입점을 분리했다.
Command Palette에서 awaitable, 데몬 RPC 쪽에서 fire-and-forget으로 각각 쓸 수 있다.

### 2.5 Daemon RPC stub — 아키텍처 패턴 일관성

`workflow.start` / `workflow.cancel`을 MVP-5의 `agent.start`와 동일한 "stub ACK" 패턴으로
데몬에 등록했다. 실제 실행은 App.Wpf `WorkflowEngine`에서 담당하므로
UI 스레드 컨텍스트와 승인 다이얼로그가 자연스럽게 연결된다.

### 2.6 `file` 접근자 — 테스트 로컬 스텁 격리

`WorkflowEngineTests.cs`의 `NoOpWorkflow`, `FailingWorkflow`, `CancelObservingWorkflow`,
`EchoTriggerWorkflow`를 `file sealed class`로 선언해 다른 테스트로 누출되지 않는다.
C# 11 `file` 접근자가 테스트 전용 단발 스텁에 잘 맞는다는 것을 확인.

## 3. 잘 안 된 것

### 3.1 `FixDotnetTestsWorkflow` — 실제 `TestFailedTrigger` 발행 경로 없음

`TestFailedTrigger`를 발행하는 실제 코드가 없다. 빌드 출력을 감시하는 MSBuild logger나
터미널 출력 파서가 없어 트리거는 테스트에서만 수동으로 생성한다.
행동: MVP-7 또는 별도 슬롯에서 `dotnet test` 출력 파서 → `TestFailedTrigger` 자동 발행.

### 3.2 `ApprovalDialog` — "Approve All" / "Deny All" 만 있음

개별 액션 선택 승인(체크박스 per-action)이 없다.
모든 액션을 하나로 묶어서 승인/거부만 가능하다.
행동: UX 이터레이션에서 per-action 체크박스 + partial approval 구현 검토.

### 3.3 `SummarizeSessionWorkflow` — 파일 I/O 테스트 미작성

`SummarizeSessionWorkflow`는 실제 파일 시스템에 접근하는 코드가 있음에도
단위 테스트가 없다. `IAgentAdapter`를 통한 end-to-end 흐름을 격리하려면
파일 추상화 레이어가 필요하다.
행동: MVP-7 또는 별도 슬롯에서 `IFileSystem` 추상화 + `SummarizeSessionWorkflowTests` 추가.

### 3.4 `DialogApprovalGateway` — UI 스레드 전제가 테스트에서 검증 불가

`Application.Current.Dispatcher.BeginInvoke` 를 직접 호출하므로
xUnit에서 `DialogApprovalGateway`를 직접 테스트할 수 없다.
`IDispatcher` 추상화를 두었다면 테스트 가능한 구조가 됐을 것이다.
행동: 향후 리팩터링 슬롯에서 `IDispatcher` 주입 패턴 도입 검토.

### 3.5 `WorkflowEngine.DisposeAsync` — 실행 완료 대기 없음

`DisposeAsync`가 `cts.Cancel()` 후 100ms `Task.Delay`로만 대기한다.
느린 워크플로가 있으면 정리가 완전하지 않을 수 있다.
행동: MVP-8 이전에 `Task[]` 추적 + `Task.WhenAll` 패턴으로 교체.

## 4. ADR 회고

| ADR | 결정 | 결과 (Day 43) | 유지/수정 |
|---|---|---|---|
| ADR-013 | Day 35–43 MVP-6 계획 | Day 43에 계획대로 완주 | **완료** |
| `WorkflowTrigger` DU (이벤트 버스 아님) | `abstract record` + `sealed record` 4개 | `CanHandle` 1줄 매칭, 테스트 자연스러움 | **유지** |
| `IApprovalGateway` Abstractions 배치 | Core↔WPF seam, 테스트 스텁 교체 | 18개 신규 테스트 WPF 없이 통과 | **유지** |
| Daemon RPC stub ("stub ACK" 패턴) | 실행은 App.Wpf, 데몬은 ACK만 | agent.start 패턴과 일관성 유지 | **유지** |

## 5. MVP-6 완료 기준 자체 평가

| 기준 | 상태 |
|---|---|
| `FixDotnetTestsWorkflow` FakeAdapter end-to-end | ✓ 9개 단위 테스트 (승인/거부/오류/취소/스트림조기종료) |
| `ApprovalUI` batch approve/deny | ✓ `ApprovalDialog` WPF + `DialogApprovalGateway` |
| `SummarizeSessionWorkflow` transcript → summary JSONL | ✓ ManualTrigger 경유 Command Palette 연결 |
| 기존 자동 테스트 193개 회귀 0 | ✓ 211 pass / 2 skip (quarantine) / 0 fail |

**완료**: 4개 기준 전부 충족.

## 6. Open questions (MVP-7로 이관)

- `TestFailedTrigger` 실제 발행 — `dotnet test` 출력 파서? MSBuild binary logger?
- `BuildFailedTrigger` 실제 발행 — `dotnet build` stderr 파서?
- Policy engine 위치 — `IWorkflow.CanHandle` 전 차단? `IApprovalGateway` 후 차단? 별도 레이어?
- Redaction engine — `AgentMessageEvent.Text` 내 경로·토큰 마스킹 → `IRedactionEngine`?
- `IDispatcher` 추상화 — `DialogApprovalGateway` 테스트 가능성을 위해 MVP-7 포함 여부?
- `WorkflowEngine.DisposeAsync` — `Task.WhenAll` 교체 우선순위?

---

## 결론

MVP-6 Workflow Engine v1은 Day 43에 ADR-013 계획 그대로 완료.
핵심 성과:
1. `IWorkflow` + `WorkflowTrigger` DU + `IApprovalGateway` seam — Core가 WPF에 의존하지 않는 설계 확립.
2. `FakeAgentAdapter` + `AutoApproveGateway`/`AutoDenyGateway` — 외부 프로세스 없이 18개 워크플로 테스트.
3. 211개 자동 테스트 (회귀 0, 193 → 211 +18).

**MVP-7 Policy + Redaction 진입을 권고**. 자세한 진입 plan은 DESIGN.md ADR-014.
