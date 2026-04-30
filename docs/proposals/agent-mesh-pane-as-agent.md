# Proposal: Agent Mesh — Pane-as-Agent + Sub-agent Spawn/Merge

**작성일**: 2026-04-30
**상태**: 제품 비전 캡처 (구현 단계 미정)
**선행/관계**: 이 문서가 [`ask-agent-vs-agent-trace.md`](./ask-agent-vs-agent-trace.md)의 상위 컨텍스트.
       MVP-1∼8 + ADR-012(Agent Pane), ADR-013(Workflow Engine v1)이 부분 인프라.

## 제품 비전 (사용자 진술 인용)

> "이 동작을 위해서 현 프로그램을 만들고 있는거야. 다중 cmd, powershell 때문이 아니라 여러 패널 간의 통신을 원할하게 하기 위해서. 그리고 서브에이전트를 생성시 새로운 패널을 열고 패널에서 작업 후 main 패널로 합쳐지는 거야."

**즉**: Agent Workspace의 차별점은 멀티 셸 워크스페이스가 아니라 **multi-agent mesh + pane-merge orchestration**이다. 패널은 셸이 아니라 **agent의 작업 surface**.

## 핵심 모델

### 1. Pane-as-Agent

- 하나의 pane = 하나의 agent 세션 (또는 셸 — agent 모드는 opt-in)
- Pane의 입출력은 두 채널:
  - **Terminal stream** (raw text): agent가 도구를 실행해 사용자에게 보여주는 결과 + 사용자 자유 입력
  - **Structured event channel** (AgentEvent): assistant message, tool_use, done 등 호스트가 인식 가능한 이벤트
- 기존 ConPTY 인프라는 terminal stream을 운반하고, structured 채널은 별도 IPC (현재의 `IAgentSession.Events`와 유사)

### 2. Sub-agent Spawn

흐름:
1. Main pane의 Agent M이 작업 T 처리 중 sub-agent가 필요하다고 판단
2. M이 `spawn_subagent(prompt, scope)` 형태의 tool_use 이벤트 발행
3. 호스트 (Daemon + WPF)가:
   - PolicyEngine으로 spawn 승인 (depth limit, 동시 sub-agent 개수, scope 위험도)
   - 새 pane open (split direction은 정책 또는 사용자 prefs)
   - 새 IAgentSession 시작, prompt = T
   - 부모-자식 관계를 topology에 등록
4. Sub-agent S는 자기 pane에서 자율 작업
5. S의 events는 자기 pane의 terminal/구조화 채널로 흐름 + transcript JSONL에 기록

### 3. Merge (sub-agent 결과를 main으로 합치기)

S가 `done` 이벤트 발행 시:
1. 호스트가 S의 final 결과(assistant message + 구조화 payload)를 추출
2. M의 세션에 결과를 stdin/메시지로 inject (`claude --resume <M-id>` + 결과 텍스트)
3. M은 결과를 받아 다음 step 진행
4. S의 pane은 두 동작 중 하나:
   - **Auto-close** (default): pane 닫히고 layout이 재배치
   - **Persist for inspection**: pane은 남되 "merged" 상태 표시 (회색 chrome, 입력 비활성)
5. Transcript에서 부모-자식 chain은 보존 (`parent_session_id` 필드)

## 호스트 측 통신 primitives

### MessageBus (Daemon-hosted)

Daemon이 in-memory pub/sub 운영:

```
agent.<sessionId>.message      // assistant/user 메시지
agent.<sessionId>.tool_use     // tool 호출
agent.<sessionId>.done         // 세션 종료
agent.<sessionId>.spawned      // 자식 spawn 알림
agent.<parent>.merged          // 자식 결과 merge 시
```

- 각 session의 PumpAsync가 Bus에 publish
- 다른 session/UI/워크플로가 subscribe
- 정책: cross-tenant subscribe 거부 (필요시)

### Spawn API (RPC)

```csharp
// AgentWorkspace.Daemon RPC
SpawnSubagentRequest { ParentSessionId, Prompt, Scope, SplitDirection }
SpawnSubagentResponse { ChildSessionId, ChildPaneId }

MergeSubagentRequest { ChildSessionId, AcceptResult }
MergeSubagentResponse { Summary, ParentResumed }
```

### Topology

각 agent session에 부모 포인터:

```csharp
public sealed record AgentTopology(
    AgentSessionId Self,
    AgentSessionId? Parent,
    IReadOnlySet<AgentSessionId> Children,
    int Depth,
    DateTime SpawnedAt);
```

Daemon이 in-memory tree 관리, 영속화는 transcript에 reference로만 (트리 자체는 transient).

## 기존 코드와의 정합성

| 기존 자산 | 메쉬 모델에서 역할 |
|---|---|
| `IAgentAdapter` / `ClaudeAdapter` | sub-agent도 동일 adapter로 spawn — 모델에 무관 |
| `WorkflowEngine` | 시스템이 트리거하는 자동 워크플로 (사용자 채팅 아님). sub-agent를 워크플로 step으로 활용 가능 |
| `IApprovalGateway` | spawn 자체가 tool_use이므로 자연스럽게 게이팅. depth/parallel limits도 여기서 enforce |
| `PolicyEngine` | spawn scope (어떤 tools/dirs를 자식이 쓸 수 있는지) 결정. 자식은 부모 정책 inherit + narrow |
| `RegexRedactionEngine` | merge 시 child→parent 메시지 inject 전 redact. 재귀적 secret leak 방지 |
| `TranscriptSink` | parent_session_id 필드 추가 → tree로 재구성 가능 |
| `AgentTrace` 패널 | sub-agent의 진행 상황을 부모 pane에서 미니 view로 보거나, dedicated mesh visualizer로 진화 |
| `ConPTY` + xterm.js | terminal stream 운반자 그대로 |
| `Workspace.OpenSplitAsync` | sub-agent 패널 spawn에 재사용 — 다만 default shell 대신 agent session으로 시작 |

## ⚠ 즉시 결정해야 할 항목

이 vision을 받아들이면 [`ask-agent-vs-agent-trace.md`](./ask-agent-vs-agent-trace.md)의 입장이 바뀐다:

- 그 doc은 "ad-hoc 채팅은 pane으로, 워크플로는 trace로" 분리 제안
- 메쉬 vision에선 모든 agent 활동이 pane에 호스트되며 trace는 mesh visualizer로 흡수 → trace의 dedicated input 자체가 사라짐
- → 이 메쉬 vision이 채택되면 `ask-agent-vs-agent-trace.md`의 결론은 *유지*되지만 (pane으로 옮기는 방향 자체는 옳음) 그 다음 단계로 sub-agent + merge 인프라가 들어옴

## 단계적 구현 (P0 → P3)

### P0 (선행조건, 이미 부분적으로 보유)
- ✅ ConPTY pane lifecycle (`PaneSession`, `Workspace`)
- ✅ Structured agent events (`IAgentSession.Events`)
- ✅ TranscriptSink JSONL
- ✅ PolicyEngine + ApprovalGateway

### P1 — Pane이 agent를 host (단일 agent, sub-agent 미지원)
- `Workspace.OpenAgentPaneAsync(prompt, options)`: split + agent session 시작 + structured events를 그 pane에 binding
- pane의 "agent 모드" 토글 (chrome에 ⚙ 표시)
- AgentTrace 패널 → mesh visualizer로 진화하거나 deprecate
- [`ask-agent-vs-agent-trace.md`](./ask-agent-vs-agent-trace.md)의 P1 step

**진입 트리거**: 사용자가 ad-hoc agent를 자주 쓰기 시작하면. (현재는 워크플로 위주)

### P2 — Sub-agent spawn + auto-merge
- `SpawnSubagentRequest` RPC 추가
- Adapter에 spawn hook (`ClaudeAdapter`는 `Task` tool을 spawn으로 매핑하거나, custom event recognise)
- Topology tracker (Daemon에 in-memory tree)
- Merge protocol: child done → parent stdin/resume + redact
- Pane의 "child of X" 표시
- ApprovalGateway에 spawn rule (depth ≤ 3, parallel ≤ 4 등)

**진입 트리거**: P1 안정화 + claude CLI가 task spawning을 stable API로 제공할 때.

### P3 — Mesh visualizer + free-form pub/sub
- Daemon MessageBus
- Mesh view (graph of active agents)
- Cross-agent broadcast (drep / consensus 같은 패턴)
- Persistent agent sessions (앱 재시작 후 mesh 복원)

**진입 트리거**: P2 사용 패턴 누적 후, 4개 이상 agent 동시 운용 시나리오 명확해질 때.

## 알려진 리스크 / 오픈 질문

1. **자원 폭발**: 한 사용자 prompt가 transitive하게 N개 sub-agent 생성 → 비용 + 시스템 부하. depth/parallel limits + 비용 visualization 필수.
2. **Merge 의미**: assistant 텍스트 단순 inject은 단순 케이스. 복잡한 구조화 결과(JSON, 파일 변경 list 등)를 부모가 활용하려면 typed result protocol 필요.
3. **Failure 전파**: 자식이 error로 끝나면 부모는 어떻게 알아야 하나? "merged with error" 이벤트 필요.
4. **Cancel 시맨틱**: 부모 cancel → 모든 transitive 자식 cancel? 자식만 cancel 하고 부모는 진행?
5. **UI 혼잡**: 4 panes 중 2개가 sub-agent로 spawn되면 layout이 자동 재배치 → 사용자 mental model 깨질 위험. animation + history undo 필요.
6. **Multi-tenant**: 만약 다른 사용자/세션이 mesh에 합류 가능해지면 (협업) 정책 + 인증 layer 필요. 단기 out-of-scope.
7. **Claude CLI 의존성**: claude의 task spawning 메커니즘이 변하면 어댑터 측 매핑 수정 필요. adapter abstraction을 단단히 유지.

## 비-결정 (intentionally deferred)

- Mesh visualizer의 구체 UI (graph? tree? tab?) — P3 진입 시 사용자 피드백으로 결정.
- 다른 agent provider 추가 (Anthropic API 직접, OpenAI, local LLM) — `IAgentAdapter`가 잘 추상화돼 있어 P1 이후 어느 시점이든 가능.
- DSL: agents/tasks를 yaml로 선언하는 방식 — Workflow DSL과 함께 (ADR-016에서 보류된 항목).

## 진입 트리거 (재확인)

P1은 "사용자가 ad-hoc agent를 자주 쓰기 시작" 신호로 시작.
P2는 P1 + claude task spawning stable API.
P3는 P2 사용 패턴 누적.

당장 무엇도 강제하지 않음 — 이 doc는 vision lock-in 용도.

## 참고 (현 코드 anchor)

- `src/AgentWorkspace.Abstractions/Agents/IAgentAdapter.cs` — adapter 인터페이스
- `src/AgentWorkspace.App.Wpf/Workspace.cs:OpenSplitAsync` — pane spawn 진입점
- `src/AgentWorkspace.Core/Workflows/WorkflowEngine.cs` — 시스템 트리거 자동화
- `src/AgentWorkspace.Abstractions/Workflows/IApprovalGateway.cs` — spawn 게이팅 자리
- `src/AgentWorkspace.Core/Transcripts/TranscriptSink.cs` — parent_session_id 필드 추가 자리
- `src/AgentWorkspace.Daemon/` — MessageBus가 들어갈 자리
