# Default Agent Surface Priority Plan

**작성일**: 2026-05-06
**상태**: P0 구현 완료, P1 session attach/restore, pane title 영속화, keyboard/focus/message/input guard 보강 완료
**관련 문서**: [agent-mesh-pane-as-agent.md](./agent-mesh-pane-as-agent.md), [USER_GUIDE.md](../USER_GUIDE.md)

## 결정

사용자에게는 Claude/Codex/Gemini/Ollama 같은 모델별 패널 명령을 노출하지 않는다.
앱의 기본 동작은 하나의 **기본 에이전트 패널**이며, 실제 provider는
`기본 에이전트 provider 설정...` 값으로 결정한다.

Provider registry는 내부 실행 어댑터와 capability 목록으로 유지한다. 즉 UI surface는
단일화하고, 실행 provider만 기본값으로 교체한다.

## tmux 비교에서 본 우선순위

Evidence:

- 이미 존재: pane split/focus/close, daemon 기반 세션 복원, YAML snapshot/template,
  pane 간 텍스트 전송, transcript/policy/workflow 인프라.
- tmux가 더 강한 영역: 원격 SSH 세션 운용, terminal-only 안정성, detach/attach의
  성숙도, 키보드 중심 반복 작업.
- AgentWorkspace가 더 강해질 수 있는 영역: agent 실행 표면, sub-agent 관측,
  transcript, policy, workflow, pane 간 AI handoff.

Inference:

- tmux와 정면 경쟁하는 terminal multiplexer 기능보다, AI 실행 표면의 혼란을 먼저
  줄이는 것이 현재 사용성 병목이다.
- provider별 패널 명령은 command palette를 빠르게 복잡하게 만들고, 사용자가
  "어떤 패널을 열어야 하는가"를 매번 판단하게 한다.

Uncertainty:

- Codex/Gemini/Ollama 내부 sub-agent 관측은 각 CLI가 구조화 이벤트나 transcript를
  얼마나 노출하는지에 따라 구현 가능성이 달라진다.

## Ranked Priorities

### P0 — 기본 에이전트 표면 단일화

구현 완료 범위:

- Command Palette 명령을 `에이전트 패널 열기`, `하위 에이전트 실행...`,
  `기본 에이전트 provider 설정...` 중심으로 단일화.
- Provider별 패널/하위 에이전트 명령 생성 제거.
- 앱이 시작한 sub-agent, 외부에서 관측한 Task, 카드의 패널 승격, 자식 spawn 모두
  현재 기본 provider 패널을 사용하도록 정리.
- Provider registry는 내부 adapter registry로 유지하되, user-facing title/description
  필드는 제거.
- USER_GUIDE/README/tooltip/test 기대값을 기본 패널 모델로 갱신.

### P1 — tmux급 반복 조작 보강

구현안:

- pane title chip + focused pane rename command. pane title은 세션 DB에 영속화한다.
- session list / attach target 선택 UI. 기존 세션 attach 또는 새 세션 시작을 팔레트에서
  선택한다.
- pane/window 이름 표시와 rename 명령.
- split/focus/send-to-pane 기본 키맵 정리. xterm focus 상태에서도 동작하도록 renderer
  shortcut resolver에서 host command로 전달한다.
- 현재 layout에서 focus 이동과 send-to-pane의 keyboard-only 흐름 개선.

검증:

- 기존 layout/perf benchmark 유지.
- keyboard command smoke test 추가.
- 세션 DB attach/restore 회귀 테스트 추가. 완료 커밋:
  `d78a0cd`, `2b833bf`, `d3d7ce3`, `f3bf8b5`, `6c88ec7`, `93f97ab`.
- pane title 세션 DB 영속화 추가. 완료 커밋:
  `80bb968`, `0191b3d`, `1b44269`, `85a1947`.
- keyboard-only renderer shortcut dispatcher guard 추가. 완료 커밋:
  `cfd94a8`, `44346e2`.
- renderer focusPane stale id guard 추가. 완료 커밋:
  `0a8e926`, `4f0d540`.
- pane message stale target guard 추가. 완료 커밋:
  `65e3bcc`, `9858cf5`.
- send-to-pane 선택 목록 layout order 보강. 완료 커밋:
  `17f050a`, `7e32d2d`.
- renderer input malformed payload guard 추가. 완료 커밋:
  `397d13e`, `676d591`.
- renderer resize invalid cols/rows payload guard 추가. 완료 커밋:
  `test: cover renderer resize decoding guards`, `Guard renderer resize decoding`.

### P2 — sub-agent handoff 신뢰성 강화

구현안:

- 현재 clipboard handoff는 유지하되, provider REPL 준비 신호가 확인되는 CLI부터
  prompt enqueue/inject 경로를 adapter별 capability로 추가.
- Codex/Gemini/Ollama는 구조화 관측 이벤트가 확인되는 provider부터 외부 Task
  observer를 추가한다.
- 기본 패널 계약은 유지한다. 관측 source는 카드/상태에 표시하고, 새 pane은 기본
  provider로 연다.

검증:

- provider별 "open pane + prompt handoff" 수동 매트릭스.
- auto-pane budget/release 회귀 테스트.
- transcript parent_session_id 보존 확인.

### P3 — merge/mesh 가시화

구현안:

- 완료된 sub-agent 결과를 부모 세션에 명시적으로 merge/inject하는 명령.
- active/merged/error 상태를 graph 또는 compact timeline으로 표시.
- 정책: depth/parallel/budget/redaction 규칙을 UI에 노출.

검증:

- merge event contract test.
- redaction display/clipboard 경계 테스트.
- 장시간 session에서 subscription 누수 확인.

## P0 Verification

현재 P0 변경 후 확인한 항목:

- `dotnet build AgentWorkspaceTerminal.slnx -c Release`
- `dotnet test src\AgentWorkspace.Tests\AgentWorkspace.Tests.csproj -c Release --no-build --filter "FullyQualifiedName~AgentProviderRegistryTests|FullyQualifiedName~UiPrefsStoreTests|FullyQualifiedName~ExternalTaskCoordinatorTests"`
- `dotnet test src\AgentWorkspace.Tests\AgentWorkspace.Tests.csproj -c Release --no-build`

## P1 Verification

session attach/restore 회귀 테스트 보강 후 확인한 항목:

- `dotnet test src\AgentWorkspace.Tests\AgentWorkspace.Tests.csproj -c Release --no-restore /p:UseSharedCompilation=false /nr:false --filter "FullyQualifiedName~SessionRestorePlanTests"`
- `dotnet test src\AgentWorkspace.Tests\AgentWorkspace.Tests.csproj -c Release --no-build /p:UseSharedCompilation=false /nr:false --filter "FullyQualifiedName~SessionChoiceItemTests|FullyQualifiedName~SessionRestorePlanTests|FullyQualifiedName~SessionSwitchPlannerTests|FullyQualifiedName~SqliteSessionStoreTests|FullyQualifiedName~WorkspaceSnapshotTests|FullyQualifiedName~ExternalTaskCoordinatorTests|FullyQualifiedName~SubAgentSessionViewModelTests"`
- `dotnet test src\AgentWorkspace.Tests\AgentWorkspace.Tests.csproj -c Release --no-restore /p:UseSharedCompilation=false /nr:false --filter "FullyQualifiedName~RendererShortcutDispatcherTests|FullyQualifiedName~RendererShortcutCommandTests"`
- `dotnet test src\AgentWorkspace.Tests\AgentWorkspace.Tests.csproj -c Release --no-restore /p:UseSharedCompilation=false /nr:false /m:1 --filter "FullyQualifiedName~WorkspaceFocusGuardTests|FullyQualifiedName~RendererShortcutDispatcherTests|FullyQualifiedName~WorkspaceSnapshotTests"`
- `dotnet test src\AgentWorkspace.Tests\AgentWorkspace.Tests.csproj -c Release --no-restore /p:UseSharedCompilation=false /nr:false /m:1 --filter "FullyQualifiedName~PaneMessageDispatcherTests|FullyQualifiedName~WorkspaceFocusGuardTests|FullyQualifiedName~RendererShortcutDispatcherTests"`
- `dotnet test src\AgentWorkspace.Tests\AgentWorkspace.Tests.csproj -c Release --no-restore /p:UseSharedCompilation=false /nr:false /m:1 --filter "FullyQualifiedName~RendererInputDecoderTests|FullyQualifiedName~PaneMessageDispatcherTests|FullyQualifiedName~WorkspaceFocusGuardTests|FullyQualifiedName~RendererShortcutDispatcherTests"`
- `dotnet test src\AgentWorkspace.Tests\AgentWorkspace.Tests.csproj -c Release --no-restore /p:UseSharedCompilation=false /nr:false /m:1 --filter "FullyQualifiedName~RendererResizeDecoderTests"`
- `node --test web\terminal\shortcuts.test.cjs web\terminal\bridge-shortcuts.test.cjs`
- `dotnet build AgentWorkspaceTerminal.slnx -c Release --no-restore /p:UseSharedCompilation=false /nr:false`

고정한 계약:

- 기존 세션 선택은 선택한 session id로 attach한다.
- "새 세션 시작" 선택은 fresh session 경로로 분기한다.
- 현재 세션 선택은 재attach하지 않는다.
- 세션 전환은 stale sub-agent/external task/auto-pane 상태를 비운다.
- restore는 저장된 layout focus를 보존하고 restored pane에 기본 title을 다시 발행한다.
- restore는 `LiveState == "Running"` pane만 reattach하고 나머지는 start한다.
- pane title은 `PaneSpec.Title`로 저장되고 restore 시 기본 title보다 우선한다.
- rename/set title 경로는 title-only store update를 사용해 command/env pane spec을 보존한다.
- renderer shortcut은 workspace/open pane 준비 상태를 먼저 판정한 뒤 split/focus/send action으로 dispatch한다.
- renderer focusPane 메시지는 target pane이 workspace session map에도 있을 때만 layout focus를 변경한다.
- paneMessage/send-to-pane publish는 target pane이 현재 open pane set에 있을 때만 mesh send message를 만든다.
- send-to-pane 선택 목록은 dictionary key 순서가 아니라 layout pane order를 따른다.
- renderer input base64 디코딩은 null/empty/malformed payload를 예외 없이 no-op 처리한다.
- renderer resize cols/rows 디코딩은 누락/비정수/0 이하 값을 예외 없이 no-op 처리한다.
