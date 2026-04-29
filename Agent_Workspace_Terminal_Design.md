---
title: "Agent Workspace Terminal 설계 문서"
subtitle: "C# / Windows 기반 터미널 멀티플렉서 + AI Agent 작업공간 런타임"
author: "ChatGPT"
date: "2026-04-29"
lang: ko-KR
---

# 문서 개요

이 문서는 **Windows에서 tmux처럼 세션을 유지하는 터미널**을 출발점으로 하되, 단순한 터미널 멀티플렉서가 아니라 **Agent Workspace Terminal**이라는 개발 작업공간 런타임으로 확장하는 설계안이다.

목표는 다음과 같다.

- 여러 터미널 pane을 하나의 workspace로 관리한다.
- UI를 닫아도 세션과 프로세스가 유지된다.
- AI Agent CLI/API/MCP를 작업공간에 연결한다.
- build/test/log/git 상태를 이벤트로 수집한다.
- agent가 제안한 명령과 파일 변경은 policy와 approval을 거쳐 실행한다.
- C#/.NET 기준으로 고성능, 저할당, 유지보수 가능한 구조를 설계한다.

## 핵심 한 줄 정의

> Agent Workspace Terminal은 **Persistent Terminal Multiplexer + Agent Orchestration Runtime + Developer Workflow Dashboard**이다.

## 설계 기준

| 항목 | 기준 |
|---|---|
| 주 언어 | C# |
| 주 플랫폼 | Windows |
| 런타임 | .NET 10 LTS 기준 |
| 터미널 호스팅 | Windows ConPTY / Pseudoconsole |
| 초기 UI | WinUI 3 + WebView2 + xterm.js |
| 장기 UI 대안 | Avalonia, Native renderer |
| IPC | gRPC over Named Pipe + Raw Named Pipe stream |
| 저장소 | SQLite + JSON/YAML workspace template |
| 기본 보안 | ask-before-write, ask-before-execute |

# 1. 제품 경계 정의

## 1.1 해결하려는 문제

일반적인 개발 작업은 다음처럼 분산된다.

```text
터미널 1: dotnet watch
터미널 2: dotnet test
터미널 3: git status / git diff
터미널 4: Claude Code / Codex / Gemini CLI
터미널 5: logs
브라우저: 이슈 / 문서 / API 문서
IDE: 코드 수정
```

이 구조의 문제는 **프로세스 상태, 로그, agent 대화, build/test 결과, git 상태가 서로 분리되어 있다**는 것이다. Agent Workspace Terminal은 이것들을 하나의 개발 작업공간으로 묶는다.

```text
Workspace: MyApp
 ├─ Pane: Shell
 ├─ Pane: dotnet watch
 ├─ Pane: Tests
 ├─ Pane: Git Diff
 ├─ Pane: Agent CLI
 ├─ Panel: Agent Trace
 ├─ Panel: Build/Test Events
 └─ Panel: Workflow Timeline
```

## 1.2 포함 범위

| 분류 | 기능 |
|---|---|
| Terminal | pane split, window/tab, scrollback, resize |
| Session | detach/attach, layout 저장, 프로세스 유지 |
| Workspace | 프로젝트별 작업공간, 명령 템플릿, cwd/env 관리 |
| Agent | CLI agent 실행, API agent 연결, MCP adapter |
| Workflow | build/test 실패 감지, agent 분석 요청, 승인 후 명령 실행 |
| Observability | event log, command history, agent trace, workflow timeline |
| Safety | 명령 승인, 위험 명령 차단, secret redaction |

## 1.3 초기 제외 범위

| 제외 기능 | 제외 이유 |
|---|---|
| 자체 VT renderer | 구현 범위가 크고 초반 검증 속도를 떨어뜨림 |
| 완전한 tmux 호환성 | 제품 목표가 tmux clone이 아님 |
| 원격 멀티유저 협업 | 인증/권한/동기화 복잡도 증가 |
| IDE 수준 코드 편집기 | 터미널/워크플로우/agent runtime에 집중 |
| 기본 완전 자동 코드 수정 | 보안 리스크가 큼 |

## 1.4 트레이드오프 점검

| 선택 | 장점 | 단점 | 결정 |
|---|---|---|---|
| tmux clone | 요구사항이 명확함 | 차별성이 낮음 | 제외 |
| Agent Workspace | 차별성이 큼 | 설계 복잡도 증가 | 채택 |
| 전체 기능을 한 번에 구현 | 완성형에 가까움 | 실패 확률 높음 | 제외 |
| 단계별 MVP | 검증이 빠름 | 초반 기능 제한 | 채택 |

# 2. 최상위 아키텍처

핵심 원칙은 다음이다.

> UI는 화면일 뿐이고, 세션과 프로세스는 Server가 소유한다.

따라서 구조는 반드시 **Client / Server 분리형**으로 설계한다.

![Agent Workspace Terminal 최상위 아키텍처](assets/architecture.png)

## 2.1 프로세스 구성

```text
AgentWorkspace.Server.exe
AgentWorkspace.Client.exe
AgentWorkspace.Tray.exe      선택
AgentWorkspace.Cli.exe       선택
```

### Server 책임

- Workspace 생성/로드/저장
- Session/Window/Pane 생명주기 관리
- ConPTY process 소유
- Agent runtime 소유
- Workflow engine 실행
- Policy/approval 상태 관리
- Event log와 transcript 저장

### Client 책임

- pane layout 표시
- terminal renderer 표시
- 사용자 입력 전달
- command palette
- agent trace panel
- approval dialog
- workflow timeline 표시

### CLI 책임

자동화와 빠른 조작을 제공한다.

```powershell
awt attach dev
awt new-pane "dotnet watch"
awt run-workflow fix-tests
awt snapshot save before-refactor
```

## 2.2 Control Plane / Data Plane 분리

| Plane | 역할 | 추천 구현 |
|---|---|---|
| Control Plane | workspace, session, pane, workflow, approval 제어 | gRPC over Named Pipe |
| Data Plane | terminal input/output raw bytes, resize event | Raw Named Pipe / stream |

터미널 output은 매우 뜨거운 hot path다. 모든 terminal byte를 protobuf/gRPC 메시지로 감싸면 복사와 allocation이 늘 수 있다. 따라서 제어 명령은 gRPC, 터미널 바이트 스트림은 raw stream으로 분리한다.

## 2.3 핵심 컴포넌트

| 컴포넌트 | 책임 |
|---|---|
| SessionManager | session 생성, attach/detach, session 상태 복구 |
| WorkspaceManager | workspace root, template, env, startup profile 관리 |
| LayoutManager | window/pane split tree 관리 |
| PaneManager | pane별 ConPTY/process 관리 |
| AgentRuntime | CLI/API/MCP agent adapter 관리 |
| WorkflowEngine | event-driven workflow 실행 |
| EventBus | build/test/log/agent/workflow 이벤트 fan-out |
| PolicyEngine | 명령 실행/파일쓰기/network 권한 판단 |
| Storage | SQLite, transcript, snapshot, config 저장 |

## 2.4 트레이드오프 점검

| 항목 | 선택 A | 선택 B | 판단 |
|---|---|---|---|
| 프로세스 구조 | 단일 GUI 앱 | Client/Server 분리 | Client/Server 채택 |
| IPC | TCP localhost | Named Pipe | Named Pipe 채택 |
| Terminal stream | gRPC stream | Raw stream | Raw stream 채택 |
| 세션 소유권 | Client | Server | Server 소유 |

# 3. 터미널 엔진 설계

Windows에서 네이티브 터미널을 호스팅하려면 **ConPTY / Pseudoconsole** 기반으로 간다. Server는 ConPTY를 소유하고, Client는 terminal renderer를 가진다.

![터미널 입출력 흐름](assets/terminal_flow.png)

## 3.1 ConPTY Host 흐름

```text
1. input/output pipe 생성
2. CreatePseudoConsole 호출
3. STARTUPINFOEX 구성
4. PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE 연결
5. CreateProcessW로 child process 실행
6. output pipe를 비동기로 읽음
7. output bytes를 client data stream으로 전송
8. 사용자 입력 bytes를 input pipe로 write
9. resize 시 ResizePseudoConsole 호출
10. 종료 시 handle/process/session 정리
```

## 3.2 핵심 인터페이스 초안

```csharp
public interface IPseudoTerminal : IAsyncDisposable
{
    PaneId PaneId { get; }

    ValueTask StartAsync(
        PaneStartOptions options,
        CancellationToken cancellationToken);

    ValueTask WriteInputAsync(
        ReadOnlyMemory<byte> input,
        CancellationToken cancellationToken);

    ValueTask ResizeAsync(
        int columns,
        int rows,
        CancellationToken cancellationToken);

    IAsyncEnumerable<ReadOnlyMemory<byte>> ReadOutputAsync(
        CancellationToken cancellationToken);

    ValueTask KillAsync(
        KillMode mode,
        CancellationToken cancellationToken);
}
```

## 3.3 출력 처리 구조

```text
ConPTY Output Pipe
    ↓
PipeReader
    ↓
ArrayPool<byte>
    ↓
Bounded Channel
    ↓
Pane Output Broadcaster
    ├─ Client Data Stream
    └─ Text/Event Analyzer
```

## 3.4 Low allocation 설계 포인트

피해야 하는 방식:

```csharp
var text = Encoding.UTF8.GetString(buffer);
textBox.Text += text;
```

이 방식은 다음 문제가 있다.

- 출력마다 string allocation 발생
- 긴 출력에서 Large Object Heap 위험
- UI 전체 갱신으로 프리즈 가능
- UTF-8 partial sequence, 한글 조합, emoji 처리가 깨질 수 있음
- scrollback이 커질수록 GC pressure 증가

권장 방식:

```text
ReadOnlyMemory<byte>
ReadOnlySequence<byte>
ArrayPool<byte>
System.IO.Pipelines
Bounded Channel
Frame Coalescing
Ring Buffer Scrollback
```

## 3.5 Renderer 선택

### MVP renderer

```text
WebView2 + xterm.js
```

장점:

- VT sequence 처리 부담이 작다.
- IME, CJK, emoji, mouse event, scrollback 지원을 빨리 확보할 수 있다.
- 초기 MVP 검증 속도가 빠르다.

단점:

- WebView2 프로세스/메모리 비용이 있다.
- native renderer만큼 세밀한 제어는 어렵다.

### 장기 renderer

```text
DirectWrite / Win2D / Skia 기반 native renderer
```

장점:

- 완전한 렌더링 제어 가능
- GPU batching 최적화 가능
- WebView2 dependency 제거 가능

단점:

- VT parser, glyph shaping, IME, CJK width, selection, mouse reporting 등을 직접 구현해야 한다.

## 3.6 트레이드오프 점검

| 선택 | 장점 | 단점 | 결정 |
|---|---|---|---|
| xterm.js | 빠르고 검증됨 | WebView2 비용 | MVP 채택 |
| 자체 renderer | 최고 성능과 제어권 | 구현 난이도 매우 높음 | 장기 과제 |
| 서버에서 전체 VT parsing | 서버가 화면 상태 이해 가능 | hot path 부담 | 제외 |
| 서버에서 TextTap만 분석 | 가볍고 이벤트 추출 가능 | 화면 상태는 Client 의존 | 채택 |

# 4. 세션 / 워크스페이스 모델

## 4.1 도메인 모델

```text
Workspace
 ├─ WorkspaceId
 ├─ Name
 ├─ RootPath
 ├─ Environment
 ├─ StartupProfile[]
 └─ Session[]

Session
 ├─ SessionId
 ├─ Name
 ├─ Windows[]
 ├─ Agents[]
 ├─ Workflows[]
 ├─ CreatedAt
 └─ LastAttachedAt

Window
 ├─ WindowId
 ├─ Name
 └─ LayoutTree

Pane
 ├─ PaneId
 ├─ Kind: Terminal / Agent / Log / Git / Monitor
 ├─ Command
 ├─ Cwd
 ├─ Env
 ├─ ProcessState
 ├─ Size
 └─ ScrollbackRef
```

## 4.2 Layout Tree

pane split은 binary tree로 관리한다.

```json
{
  "type": "split",
  "direction": "horizontal",
  "ratio": 0.62,
  "left": {
    "type": "pane",
    "paneId": "shell"
  },
  "right": {
    "type": "split",
    "direction": "vertical",
    "ratio": 0.5,
    "top": {
      "type": "pane",
      "paneId": "tests"
    },
    "bottom": {
      "type": "pane",
      "paneId": "agent"
    }
  }
}
```

## 4.3 Workspace Template 예시

```yaml
name: dotnet-agent-workspace
root: D:\Projects\MyApp

env:
  DOTNET_CLI_TELEMETRY_OPTOUT: "1"

panes:
  - id: shell
    title: Shell
    command: pwsh
    cwd: ${workspace.root}

  - id: watch
    title: dotnet watch
    command: dotnet watch run
    cwd: ${workspace.root}

  - id: tests
    title: Tests
    command: dotnet test --logger "console;verbosity=normal"
    cwd: ${workspace.root}

  - id: agent
    title: Agent
    command: claude
    cwd: ${workspace.root}

agents:
  - id: coder
    type: cli
    pane: agent
    role: coding-assistant
    permissions: ask-before-write

workflows:
  - id: fix-tests
    trigger: test_failed
    steps:
      - collect_recent_test_output
      - ask_agent_for_plan
      - require_user_approval
      - execute_approved_commands
      - rerun_tests
```

## 4.4 저장 대상

| 대상 | 저장 여부 | 설명 |
|---|---:|---|
| workspace root | 저장 | 프로젝트 재진입 |
| pane layout | 저장 | UI 복구 |
| command/cwd/env | 저장 | 재시작 복구 |
| process PID | 임시 | server가 살아 있을 때만 유효 |
| scrollback | 제한 저장 | 용량 관리 필요 |
| transcript | 선택 저장 | agent 분석용 |
| workflow history | 저장 | 디버깅/회고용 |
| approval history | 저장 | 감사 및 사용자 편의 |

## 4.5 트레이드오프 점검

| 항목 | 저장 | 비저장 | 결정 |
|---|---|---|---|
| Layout | 복구 가능 | 단순 | 저장 |
| Scrollback 전체 | 분석 좋음 | 용량 큼 | 제한 저장 |
| Raw terminal bytes | 완전 재현 | 저장량 큼 | 최근 N MB만 |
| Text transcript | 검색 쉬움 | VT 정보 손실 | 저장 |
| 프로세스 상태 | detach 가능 | 복잡 | Server live 상태에서 유지 |

# 5. Agent Runtime 설계

## 5.1 Agent Adapter 계층

초기부터 여러 agent를 모두 직접 구현하려 하지 않는다. 공통 adapter 계층을 만들고, 우선 CLI agent를 pane에서 실행하는 방식을 채택한다.

```text
IAgentAdapter
 ├─ CliAgentAdapter
 ├─ ApiAgentAdapter
 ├─ McpAgentAdapter
 └─ LocalRuleAgentAdapter
```

## 5.2 공통 인터페이스 초안

```csharp
public interface IAgentAdapter
{
    AgentId Id { get; }
    AgentCapabilities Capabilities { get; }

    ValueTask<AgentResponse> SendAsync(
        AgentRequest request,
        AgentContext context,
        CancellationToken cancellationToken);

    IAsyncEnumerable<AgentEvent> ObserveAsync(
        CancellationToken cancellationToken);
}
```

## 5.3 CLI Agent Adapter

예상 대상:

```text
claude
codex
gemini
aider
custom-agent.exe
```

CLI agent는 ConPTY pane 안에서 실행한다. Workflow Engine은 해당 pane에 요청을 보내거나 transcript/event를 관찰한다.

```text
Workflow Engine
    ↓
CliAgentAdapter
    ↓
Agent Pane
    ↓
ConPTY
    ↓
Agent CLI
```

### 장점

- 기존 CLI agent를 그대로 활용할 수 있다.
- 사용자도 실제 agent의 동작을 terminal에서 확인할 수 있다.
- API adapter 없이도 빠르게 MVP가 가능하다.

### 단점

- CLI 출력 파싱이 어렵다.
- agent별 출력 형식이 다르다.
- 완전 구조화된 응답을 보장하기 어렵다.

따라서 adapter는 다음처럼 분리한다.

```text
ClaudeAdapter
CodexAdapter
GeminiAdapter
GenericCliAdapter
```

## 5.4 MCP Adapter

MCP는 tool/server 연결을 표준화하는 계층으로 사용한다. 단, MCP 서버는 외부 실행 파일일 수 있으므로 기본적으로 신뢰하지 않는다.

```text
AgentWorkspace.Server
 └─ MCP Client
     ├─ Filesystem MCP Server
     ├─ Git MCP Server
     ├─ Database MCP Server
     └─ Custom Tool Server
```

MCP tool 호출도 PolicyEngine을 통과해야 한다.

## 5.5 Agent Context Packet

agent에게 화면 전체를 그대로 던지지 않는다. 구조화된 context packet을 생성한다.

```csharp
public sealed record AgentContext(
    WorkspaceInfo Workspace,
    GitSnapshot Git,
    IReadOnlyList<BuildEvent> RecentBuildEvents,
    IReadOnlyList<TestFailure> RecentTestFailures,
    IReadOnlyList<PaneTranscript> RelevantTranscripts,
    PermissionProfile Permission);
```

## 5.6 트레이드오프 점검

| 선택 | 장점 | 단점 | 결정 |
|---|---|---|---|
| CLI agent 우선 | 기존 도구 활용 | 출력 파싱 어려움 | MVP 채택 |
| API agent 우선 | 구조화 쉬움 | 비용/벤더 종속 | 2차 |
| MCP 우선 | 표준화 | 보안/권한 복잡 | 제한적 채택 |
| 완전 자동 실행 | 생산성 높음 | 위험 | 기본 비활성 |

# 6. Workflow Engine 설계

Workflow Engine은 AI에게 모든 권한을 넘기는 장치가 아니다. 목적은 **이벤트 감지 -> 분석 요청 -> 승인 가능한 계획 생성 -> 정책 통과 후 실행 -> 검증**이다.

![테스트 실패 자동 분석 워크플로우](assets/fix_tests_workflow.png){height=5.8in}

## 6.1 기본 워크플로우 단계

```text
이벤트 감지
    ↓
상황 수집
    ↓
agent에게 분석/계획 요청
    ↓
사용자 승인
    ↓
승인된 명령 실행
    ↓
결과 검증
    ↓
상태 업데이트
```

## 6.2 Workflow DSL 초안

```yaml
id: fix-dotnet-tests
trigger:
  type: event
  name: test_failed

policy:
  commandExecution: ask
  fileWrite: ask
  network: deny-by-default

steps:
  - type: collect
    source: pane
    pane: tests
    lastLines: 300

  - type: collect
    source: git
    includeDiff: true

  - type: agent.plan
    agent: coder
    prompt: |
      Analyze the test failure and propose minimal changes.

  - type: approval
    require: true

  - type: command
    run: dotnet test
    cwd: ${workspace.root}
```

## 6.3 Event Bus 모델

```csharp
public abstract record WorkspaceEvent(
    WorkspaceId WorkspaceId,
    DateTimeOffset Timestamp);

public sealed record PaneOutputEvent(
    PaneId PaneId,
    ReadOnlyMemory<byte> Data) : WorkspaceEvent;

public sealed record TestFailedEvent(
    string Project,
    IReadOnlyList<TestFailure> Failures) : WorkspaceEvent;

public sealed record AgentPlanCreatedEvent(
    AgentId AgentId,
    string Summary,
    IReadOnlyList<PlannedAction> Actions) : WorkspaceEvent;
```

## 6.4 대표 workflow 후보

| Workflow | Trigger | 결과 |
|---|---|---|
| fix-dotnet-tests | test_failed | agent 분석 후 승인 기반 수정/재실행 |
| explain-build-error | build_failed | 최근 오류 로그 요약 |
| review-git-diff | git_diff_changed | 변경사항 리뷰 |
| generate-commit-message | git staged 변경 | commit message 초안 생성 |
| monitor-dev-server | process output pattern | 서버 상태/포트 표시 |
| summarize-session | session detach | 작업 요약 저장 |

## 6.5 트레이드오프 점검

| 항목 | 선택 A | 선택 B | 결정 |
|---|---|---|---|
| 코드 기반 workflow | 타입 안정성 | 수정 불편 | 내부 구현 |
| YAML workflow | 사용자가 수정 쉬움 | 검증 필요 | 외부 설정 |
| 완전 자동 실행 | 빠름 | 위험 | 승인 기반 |
| event-driven | 확장성 좋음 | 디버깅 필요 | 채택 |

# 7. UI / UX 설계

## 7.1 기본 화면 구조

```text
┌────────────────────────────────────────────────────────────┐
│ Top Bar: Workspace / Branch / Agent Status / Command Palette│
├───────────────────────────────────────────────┬────────────┤
│                                               │ Agent Trace│
│              Terminal Pane Grid              │            │
│                                               │ Plan       │
│  ┌───────────────┬─────────────────────────┐  │ Actions    │
│  │ Shell         │ dotnet watch             │  │ Approvals  │
│  ├───────────────┼─────────────────────────┤  │            │
│  │ Tests         │ Agent CLI                │  │            │
│  └───────────────┴─────────────────────────┘  │            │
├───────────────────────────────────────────────┴────────────┤
│ Bottom Event Timeline: build failed / tests passed / git... │
└────────────────────────────────────────────────────────────┘
```

## 7.2 핵심 UX 요소

### Command Palette

```text
Ctrl+Shift+P
> New Pane
> Run Workflow: Fix Tests
> Attach Session
> Save Snapshot
> Ask Agent About Current Pane
```

### Pane Role Badge

```text
[Shell]
[Build]
[Tests]
[Agent]
[Logs]
[Git]
```

### Agent Trace Panel

```text
Agent: coder
Status: waiting approval

Plan:
1. Read failing test output
2. Inspect UserServiceTests
3. Modify validation logic
4. Run dotnet test

Requested Actions:
[Approve] dotnet test
[Approve] edit src/UserService.cs
[Reject]
```

### Workspace Snapshot

```text
Snapshot: before-refactor-2026-04-29
- branch: feature/auth-refactor
- panes: 4
- running commands: 3
- git diff: included
- transcripts: last 5000 lines
```

## 7.3 UI Framework 결정

| 선택 | 장점 | 단점 | 결정 |
|---|---|---|---|
| WinUI 3 | Windows native 품질, Fluent UI | Windows 전용 | 1차 채택 |
| Avalonia | 크로스플랫폼 확장성 | Windows native 감성 약함 | 2차 후보 |
| WebView2 terminal | 빠른 구현 | 메모리/프로세스 비용 | MVP 채택 |
| Native terminal renderer | 성능/제어 최고 | 구현 난이도 큼 | 장기 과제 |

# 8. 성능 / GC 설계

## 8.1 Hot Path

다음 경로는 allocation을 극도로 조심해야 한다.

```text
1. Terminal output bytes
2. User input bytes
3. Event stream fan-out
4. Scrollback append
5. Client broadcast
```

## 8.2 금지 패턴

```text
- terminal output마다 string 변환
- LINQ 남발
- Regex 남발
- 무제한 Channel
- UI thread 직접 write
- 큰 object graph 생성
- 매 frame마다 전체 layout 재계산
```

## 8.3 권장 구조

```text
ConPTY Read
    ↓
ArrayPool Buffer
    ↓
PipeReader
    ↓
Bounded Channel
    ↓
Frame Coalescer
    ↓
Client Stream
    ↓
xterm.js write
```

## 8.4 Backpressure 정책

| 모드 | 설명 | 용도 |
|---|---|---|
| Lossless | 모든 출력 보존 | transcript 정확성 중요 시 |
| UI Coalescing | UI 전송을 frame 단위로 합침 | 기본 실시간 표시 |
| Ring Buffer | 최근 N MB만 보존 | scrollback 용량 제한 |
| Drop UI Frames | 저장은 유지, UI update 일부 생략 | 폭주 출력 대응 |

MVP 기본값:

```text
실시간 UI: frame coalescing
scrollback: ring buffer
transcript: configurable max size
```

## 8.5 트레이드오프 점검

| 선택 | 장점 | 단점 | 결정 |
|---|---|---|---|
| 모든 output 저장 | 분석 최고 | 디스크/메모리 증가 | 제한 저장 |
| UI drop 허용 | 반응성 좋음 | 순간 출력 누락 가능 | 렌더링만 coalesce |
| byte-first 처리 | allocation 적음 | 구현 복잡 | 채택 |
| string-first 처리 | 구현 쉬움 | 성능 나쁨 | 제외 |

# 9. 보안 / 권한 설계

Agent Workspace Terminal은 agent가 터미널과 파일 시스템을 만질 수 있기 때문에 보안 설계가 핵심이다.

## 9.1 Permission Profile

```yaml
permissionProfiles:
  readOnly:
    readFiles: allow
    writeFiles: deny
    executeCommands: deny
    network: deny

  safeDev:
    readFiles: allow
    writeFiles: ask
    executeCommands: ask
    network: ask
    deniedCommands:
      - rm -rf
      - del /s /q
      - format
      - powershell Invoke-Expression

  trustedLocal:
    readFiles: allow
    writeFiles: allow
    executeCommands: ask
    network: allow
```

## 9.2 위험 명령 감지 대상

```text
- delete / recursive delete
- format
- registry modification
- credential access
- network exfiltration
- package publish
- git push --force
- secret file read
- shell eval / Invoke-Expression
```

## 9.3 Secret Redaction 대상

```text
OPENAI_API_KEY
ANTHROPIC_API_KEY
GITHUB_TOKEN
AZURE_*
AWS_*
.env
*.pem
id_rsa
```

## 9.4 Approval UX

```text
Agent requests:

Command:
dotnet test

Reason:
Verify fix for UserServiceTests failure.

Risk:
Low

[Approve once] [Always allow in this workspace] [Reject]
```

## 9.5 트레이드오프 점검

| 선택 | 장점 | 단점 | 결정 |
|---|---|---|---|
| 기본 자동 실행 | 편함 | 위험 큼 | 제외 |
| ask-before-write | 안전 | 약간 번거로움 | 기본값 |
| command allowlist | 안전 | 설정 필요 | 채택 |
| MCP 자유 실행 | 확장성 큼 | 보안 리스크 | 제한 실행 |

# 10. 관찰성 / 로그 / 디버깅 설계

## 10.1 수집 이벤트

```text
- pane created / closed
- process started / exited
- command executed
- workflow started / completed / failed
- approval requested / approved / rejected
- agent request / response summary
- test failed / passed
- build failed / passed
- memory pressure
- dropped UI frames
```

## 10.2 내부 이벤트와 OpenTelemetry 매핑

| 내부 이벤트 | OTel 매핑 |
|---|---|
| WorkflowStarted | Activity |
| AgentRequest | Activity |
| CommandExecuted | Activity + Log |
| PaneOutputDropped | Metric |
| ApprovalRequested | Log |
| TestFailed | Event |

## 10.3 저장 정책

| 데이터 | 기본 저장 | 비고 |
|---|---:|---|
| workflow event | 예 | 디버깅 필수 |
| approval history | 예 | 감사/편의 목적 |
| command history | 예 | workspace 단위 |
| full agent prompt/response | 선택 | 개인정보/보안 고려 |
| raw terminal bytes | 제한 | 최근 N MB |
| text transcript | 선택 | 검색/분석용 |

## 10.4 트레이드오프 점검

| 선택 | 장점 | 단점 | 결정 |
|---|---|---|---|
| 자체 로그만 | 단순 | 외부 분석 한계 | 기본 저장 |
| OpenTelemetry | 표준 도구 연동 | 설정 복잡 | 선택 기능 |
| 모든 agent 내용 저장 | 디버깅 좋음 | 개인정보/보안 | 요약 중심 저장 |

# 11. 프로젝트 구조

```text
AgentWorkspaceTerminal/
 ├─ src/
 │   ├─ AgentWorkspace.Abstractions/
 │   │   ├─ Ids/
 │   │   ├─ Events/
 │   │   ├─ Contracts/
 │   │   └─ Permissions/
 │   │
 │   ├─ AgentWorkspace.ConPTY/
 │   │   ├─ Native/
 │   │   ├─ PseudoConsole.cs
 │   │   ├─ PseudoConsoleProcess.cs
 │   │   └─ PipePump.cs
 │   │
 │   ├─ AgentWorkspace.Server/
 │   │   ├─ SessionManager.cs
 │   │   ├─ WorkspaceManager.cs
 │   │   ├─ PaneManager.cs
 │   │   ├─ WorkflowEngine.cs
 │   │   ├─ AgentRuntime/
 │   │   ├─ Policy/
 │   │   └─ Storage/
 │   │
 │   ├─ AgentWorkspace.Client.WinUI/
 │   │   ├─ MainWindow.xaml
 │   │   ├─ PaneGrid/
 │   │   ├─ WebTerminal/
 │   │   ├─ CommandPalette/
 │   │   └─ AgentTrace/
 │   │
 │   ├─ AgentWorkspace.Cli/
 │   │   └─ Program.cs
 │   │
 │   └─ AgentWorkspace.Tests/
 │
 ├─ web/
 │   └─ terminal/
 │       ├─ xterm-host.ts
 │       ├─ bridge.ts
 │       └─ styles.css
 │
 ├─ schemas/
 │   ├─ workspace.schema.json
 │   └─ workflow.schema.json
 │
 └─ docs/
     ├─ architecture.md
     ├─ security.md
     └─ workflow.md
```

# 12. MVP 개발 순서

## MVP-1: 단일 터미널

목표:

```text
WinUI 앱에서 pwsh.exe 실행
ConPTY output을 xterm.js에 표시
xterm.js input을 ConPTY에 전달
resize 지원
```

완료 기준:

```text
pwsh 실행 가능
dir / git / dotnet 명령 정상 출력
Ctrl+C 동작
한글 입력 가능
창 resize 시 깨짐 없음
```

## MVP-2: Pane 분할

목표:

```text
horizontal split
vertical split
pane focus 이동
pane close
pane별 cwd/command
```

완료 기준:

```text
하나의 window에서 2~4개 pane 실행 가능
각 pane에 독립 shell/process 연결
focus 이동과 resize 안정 동작
```

## MVP-3: Server 분리

목표:

```text
Client 종료 후에도 Server와 terminal process 유지
Client 재실행 후 attach
workspace layout 복구
```

완료 기준:

```text
client를 닫아도 pwsh/dotnet process 유지
재실행 후 기존 session에 attach 가능
기존 pane output 계속 표시
```

## MVP-4: Workspace Template

목표:

```text
YAML/JSON으로 workspace 시작
dotnet 개발용 template 제공
```

완료 기준:

```text
awt open workspace.yaml
정의된 pane들이 cwd/env/command 기준으로 자동 실행
```

## MVP-5: Agent Pane

목표:

```text
Claude/Codex/Gemini CLI를 pane에서 실행
Agent pane transcript 수집
Agent Trace Panel 기본 표시
```

완료 기준:

```text
agent pane 생성 가능
agent 요청/응답 요약 이벤트 생성
agent pane 출력이 terminal과 trace panel 양쪽에서 관찰 가능
```

## MVP-6: Workflow Engine

목표:

```text
dotnet test 실패 감지
최근 로그 수집
agent에게 분석 요청
사용자 승인 후 명령 실행
```

완료 기준:

```text
test_failed 이벤트 발생
agent plan 생성
approval dialog 표시
승인 시 dotnet test 재실행
```

## MVP-7: Policy / Approval

목표:

```text
위험 명령 감지
파일 쓰기 승인
명령 실행 승인
secret redaction
```

완료 기준:

```text
위험 명령 차단 또는 승인 요청
민감 값 redaction
permission profile별 정책 적용
```

# 13. 최종 설계 결정 요약

## 13.1 기술 스택

| 영역 | 선택 |
|---|---|
| Runtime | .NET 10 LTS |
| UI | WinUI 3 + Windows App SDK |
| Terminal Renderer | WebView2 + xterm.js |
| Terminal Host | ConPTY |
| IPC Control Plane | gRPC over Named Pipe |
| IPC Data Plane | Raw Named Pipe / stream |
| Storage | SQLite + JSON/YAML templates |
| Workflow | Event-driven engine |
| Agent | CLI Adapter first, API/MCP later |
| Observability | 내부 event log + optional OpenTelemetry |
| Security | ask-before-write / ask-before-execute 기본값 |

## 13.2 설계 원칙

1. Client는 화면이고 Server가 세션을 소유한다.
2. Control Plane과 Terminal Data Plane을 분리한다.
3. terminal output hot path에서는 string 변환을 최소화한다.
4. 초기 renderer는 xterm.js로 빠르게 검증한다.
5. Agent는 직접 권한을 갖지 않고 Policy Engine을 거친다.
6. Workflow는 자동화보다 승인 가능한 실행 계획을 우선한다.
7. 터미널 스크래핑이 아니라 구조화된 이벤트를 축적한다.
8. 완전 자동화보다 복구 가능성, 감사 가능성, 사용자 통제권을 우선한다.

# 14. 구현 착수 체크리스트

## 14.1 기술 검증 Spike

| Spike | 검증 내용 |
|---|---|
| ConPTY Spike | pwsh 실행, input/output, resize, Ctrl+C |
| WebView2/xterm Spike | byte stream write, 한글 입력, scrollback |
| NamedPipe Spike | server/client attach, reconnect |
| Pane Split Spike | layout tree, resize propagation |
| Transcript Spike | output tap, line decoder, ring buffer |
| Workflow Spike | test_failed event -> approval -> command 실행 |

## 14.2 우선순위

```text
1. ConPTY 단일 pane
2. xterm.js 렌더링
3. pane split
4. server detach/attach
5. workspace template
6. agent pane
7. workflow engine
8. policy/approval
9. observability
10. native renderer 검토
```

# 15. 참고 자료

1. Microsoft Learn - CreatePseudoConsole: https://learn.microsoft.com/en-us/windows/console/createpseudoconsole
2. Microsoft Learn - Creating a Pseudoconsole Session: https://learn.microsoft.com/en-us/windows/console/creating-a-pseudoconsole-session
3. Microsoft Learn - WinUI 3: https://learn.microsoft.com/en-us/windows/apps/winui/winui3/
4. Microsoft Learn - WebView2: https://learn.microsoft.com/en-us/microsoft-edge/webview2/
5. xterm.js GitHub Repository: https://github.com/xtermjs/xterm.js
6. Microsoft DevBlogs - Announcing .NET 10: https://devblogs.microsoft.com/dotnet/announcing-dotnet-10/
7. Microsoft Learn - gRPC inter-process communication with named pipes: https://learn.microsoft.com/en-us/aspnet/core/grpc/interprocess-namedpipes
8. Microsoft Learn - System.IO.Pipelines: https://learn.microsoft.com/en-us/dotnet/standard/io/pipelines
9. Model Context Protocol Documentation: https://modelcontextprotocol.io/docs/getting-started/intro
10. Microsoft Learn - Observability with OpenTelemetry in .NET: https://learn.microsoft.com/en-us/dotnet/core/diagnostics/observability-with-otel

# 16. 결론

Agent Workspace Terminal은 단순한 Windows용 tmux clone이 아니라, **개발 작업 상태를 지속적으로 유지하고 AI agent와 워크플로우를 결합하는 작업공간 런타임**으로 설계하는 것이 가장 가치가 크다.

초기 구현은 반드시 작게 시작해야 한다.

```text
ConPTY 단일 pane
→ xterm.js 렌더링
→ pane split
→ server detach/attach
→ workspace template
→ agent pane
→ workflow engine
→ policy/approval
```

이 순서가 기술 리스크를 가장 작게 만들고, 동시에 제품 차별성을 단계적으로 확보할 수 있는 경로다.
