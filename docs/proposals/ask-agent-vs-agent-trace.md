# Proposal: "에이전트에게 질문" → 패널 기반 interactive 모드, AgentTrace는 워크플로우 전용

**작성일**: 2026-04-30
**상태**: 제안 (구현 보류, 트리거 대기)
**관련 ADR**: ADR-012 (MVP-5 Agent Pane), ADR-013 (MVP-6 Workflow Engine v1)

## 배경

MVP-5에서 `에이전트에게 질문…` palette 명령을 통해 ad-hoc 채팅을, MVP-6에서 `WorkflowEngine`을 통해 자동화 워크플로(`SummarizeSession`, `FixDotnetTests`, `ExplainBuildError`)를 도입했다. 두 경로 모두 동일한 `IAgentAdapter` (현재 `ClaudeAdapter`) + `AgentEvent` 스트림을 통과한다.

`af3e44e` (2026-04-30) 이후 우측 `AgentTrace` 패널이 `에이전트에게 질문…` 흐름에 마운트되어, 사용자는 한 줄짜리 follow-up TextBox로 멀티턴 대화를 할 수 있게 됐다. 다만 이 흐름은 **`claude --print --output-format stream-json --verbose`**를 매 턴 spawn하는 단발성 RPC 모델이라 다음 한계가 있다:

1. 매 턴마다 프로세스 시작 → 응답 latency 증가 (cold start ~1–2s)
2. `claude` CLI의 interactive 기능 (`/init`, `/clear`, `/resume`, file picker, slash commands) 사용 불가
3. AgentInputDialog → TextBox로 분리된 두 입력 surface가 일관성 없음

## 사용자 관찰

> "에이전트 트레이스는 왜있는거야 ? 그냥 패널을 열어서 CLI 호출해서 사용하면 되는거 아니야?"

타당한 지적. 사용자가 직접 채팅하려는 ad-hoc 시나리오라면 **새 pane에서 `claude` interactive 실행**이 더 자연스럽고 강력하다 — 이미 ConPTY + xterm.js 인프라가 있으므로 추가 코드 거의 없음.

## AgentTrace의 진짜 가치

AgentTrace 인프라가 살아남아야 하는 이유는 **사용자 직접 채팅이 아닌 시스템 트리거 워크플로**:

| 사용처 | 구조화 이벤트 필요성 |
|---|---|
| `SummarizeSessionWorkflow` | `IAgentAdapter.StartSessionAsync(prompt, ...)` → `AgentDoneEvent.Summary` 추출 → 다음 step에 전달 |
| `FixDotnetTestsWorkflow` | `tool_use` 이벤트를 `IApprovalGateway`로 라우팅 → 사용자 승인 받기 |
| `ExplainBuildErrorWorkflow` | 빌드 에러 텍스트를 prompt로 자동 생성, 결과를 status bar에 dispatch |
| 미래: 정책 게이팅 | `PolicyEngine.Evaluate(ProposedAction)` → 위험한 tool_use 차단 (interactive raw 출력으론 불가) |
| 미래: Redaction | `RegexRedactionEngine`이 화면 표시 전 비밀키 마스킹 (raw 터미널 출력은 사후 redact 불가) |
| 미래: Agent → Agent handoff | TranscriptSink JSONL이 다른 워크플로의 입력이 됨 |

이 다섯 케이스는 모두 **사용자가 직접 입력하지 않는** 흐름이고, 각자 `AgentEvent` 스트림에 의존한다. UI는 진단/시각화 surface일 뿐 데이터 흐름의 본질이 아니다.

## 제안

`에이전트에게 질문…` palette 명령의 시맨틱을 두 개로 쪼갠다:

### 1. `에이전트에게 질문…` → **claude pane 열기** (새 동작)

- 현재 focus된 pane을 vertical split해서 새 pane에 `claude` 실행 (인자 없이, interactive 모드)
- prompt를 입력받지 않거나, 받더라도 `claude --resume <id>`나 stdin pipe 정도로만 활용
- AgentTrace 패널은 펼치지 않음
- 사용자는 그 pane에서 자연스럽게 멀티턴 채팅, slash commands, file picker 등 CLI full UX 사용

장점:
- AgentInputDialog 제거 가능
- follow-up TextBox 제거 가능 (CLI 자체가 입력 surface)
- claude CLI 신기능 (예: 새 slash commands)이 자동 반영됨

### 2. AgentTrace 패널 → **워크플로우 전용**

- `세션 요약…` 같은 워크플로 명령에서만 자동으로 펼쳐짐
- 사용자 입력 surface 없음 (input box 제거)
- 진단 + 정책 게이팅 + redaction의 **시각화 창**으로 역할 한정
- "✕" 닫기 + cleanup 로직은 그대로

장점:
- 두 흐름의 책임 명확 (사용자 채팅 vs 시스템 자동화)
- AgentTrace는 deterministic data sink로 단순화
- WorkflowEngine 진화 (예: ApprovalGateway UI, 정책 거부 시각화) 시 AgentTrace가 자연스러운 자리

## 구현 스케치 (참고용)

### MainWindow.xaml.cs

```csharp
// 새 동작: split + claude 실행
private async ValueTask AskAgentAsync(CancellationToken ct)
{
    if (_workspace is null) return;

    PaneId newPane;
    var focused = _workspace.Layout.Current.Focused;
    newPane = await _workspace.OpenSplitAsync(focused, SplitDirection.Vertical, ct);

    // 새 pane이 default shell로 시작된 직후, claude 명령을 input으로 전송
    var session = _workspace.Sessions[newPane];
    await PostToRendererAsync(Envelope.OpenPane(newPane));
    await PostToRendererAsync(Envelope.Layout(_workspace.Layout.Current));

    // shell이 ready된 뒤 "claude\r"을 stdin으로 보내서 자동 진입
    await Task.Delay(200, ct); // shell prompt 출력 대기
    await session.WriteInputAsync(System.Text.Encoding.UTF8.GetBytes("claude\r"), ct);
}
```

### MainWindow.xaml

- `<Border DockPanel.Dock="Bottom"><TextBox x:Name="FollowupBox" .../></Border>` 제거
- `at:AgentTraceControl`은 그대로 유지 (워크플로용)

### 제거 가능 코드

- `AgentPaneSession.cs` (현재도 사실 미사용)
- `AgentInputDialog.xaml/.xaml.cs` (사용자 입력은 CLI에서 받음)
- `RunAgentTurnAsync`, `OnFollowupKeyDown`, `_agentSink` follow-up 관리 로직
- ClaudeAdapter의 `Continue` 옵션 (interactive CLI는 자체 multi-turn)

### 유지 코드

- `IAgentAdapter` + `ClaudeAdapter` (워크플로용)
- `AgentTraceViewModel`, `AgentTraceControl` (워크플로 시각화)
- `TranscriptSink` (워크플로 JSONL 영속화)
- `WorkflowEngine` 전체

## 트레이드오프 / 리스크

- **claude CLI 의존성 증가**: interactive 모드가 작동하지 않는 환경(예: Windows ConPTY edge case)에서 ad-hoc 채팅 자체가 불가. 현재 `--print` 모드는 더 견고.
- **세션 식별자 부재**: interactive `claude` 세션은 사용자가 `/resume` 등으로 관리. AgentTrace의 명시적 transcript처럼 외부에서 추적하기 어려움.
- **redaction 미적용**: pane 출력은 raw 터미널 stream — 비밀키가 화면에 노출될 수 있음. 다만 사용자가 직접 명령한 채팅이므로 책임 소재는 명확.

## 진행 트리거

다음 중 하나가 발생하면 이 제안 진행:
1. 사용자가 follow-up TextBox UX에 추가 불편(예: file picker, slash command 모방)을 신호하는 경우
2. claude CLI에 새 interactive 기능이 추가되어 `--print` 모드로는 못 쓸 때
3. AgentTrace의 input surface가 워크플로 진화(예: ApprovalGateway UI)와 충돌할 때

위 트리거 없으면 현 상태 유지.

## 참고

- 현 구현: `MainWindow.xaml.cs:AskAgentAsync` + `MainWindow.xaml` `<Popup>` 아래 `<at:AgentTraceControl>` 마운트
- 워크플로 진입점: `MainWindow.xaml.cs:SummarizeSessionAsync` (정상 케이스 — AgentTrace 패널과 무관하게 status bar로만 결과 보고)
- 커밋 히스토리: `af3e44e` (현재 흐름 마무리 커밋), `9d54f09`–`b1ccecf` (선행 startup 픽스 체인)
