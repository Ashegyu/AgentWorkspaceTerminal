---
title: "Agent Workspace Terminal — 실제 설계 (Refined Design)"
subtitle: "원안 검토 후 트레이드오프를 반영한 구체 설계"
date: "2026-04-29"
status: "draft-v1"
supersedes: "Agent_Workspace_Terminal_Design.md (원안)"
---

# 0. 이 문서의 위치

원안(`Agent_Workspace_Terminal_Design.md`)은 *방향성 설계*다. 본 문서는 그 방향을 유지하되, 검토 과정에서 드러난 누락·과잉·위험을 정리해 **실제 코드 작성을 시작할 수 있는 수준의 결정**을 박는다.

원안과의 핵심 차이는 다음과 같다.

| 영역 | 원안 | 본 설계 | 이유 |
|---|---|---|---|
| 프로세스 구성 | 4개 exe 처음부터 분리 | MVP-1~2 단일 프로세스, MVP-3에서 Server 분리 | 초기 분산 비용이 검증을 막는다 |
| Workflow DSL | YAML DSL을 MVP부터 | 코드 hardcode → 패턴 누적 후 DSL | 표현력 미정 상태의 DSL은 부채 |
| Scrollback 정책 | "최근 N MB만" (수치 없음) | pane당 raw 8MB + text 20k 라인, configurable | 실측 가능한 기준 필요 |
| Secret Redaction | 화이트리스트 키 이름 | 패턴 + 엔트로피 기반 + 확장 가능 룰셋 | 키 이름만으로는 누락 큼 |
| Approval | 매 액션 ask | plan 단위 batch approval + 단일 액션 fallback | agent loop가 친화적이 됨 |
| Renderer | WebView2 + xterm.js | **단일 WebView2 + 다중 xterm.js 인스턴스** | 메모리 비용 1/N |
| 자식 프로세스 | 미정 | **Windows Job Object**로 pane별 묶음 | 좀비 방지 |
| 성능 목표 | 정성적 | 정량 목표 박음 (§8) | 회귀 감지 가능 |

---

# 1. 시스템 경계 (재정의)

## 1.1 Out of Scope (명시)

다음은 **하지 않는다**. 의도적 미구현이며 향후 검토 대상도 아니다(현 단계 기준).

- 원격 SSH/컨테이너 attach
- 멀티 유저 동시 협업
- tmux 단축키/명령 호환
- copy-mode (선택/검색은 xterm.js 기본 기능에 위임)
- 자체 VT parser (장기 native renderer 시점에만 재논의)
- IDE 수준 코드 편집 기능
- 자동 코드 commit / push (agent가 절대 직접 안 함)

## 1.2 Single-binary Definition of MVP-1

MVP-1은 **그 자체로** 사용 가능한 Windows 터미널이어야 한다. agent가 없어도 daily driver 가능 수준.

완료 기준 (재정의):

- pwsh / cmd / wsl.exe / git-bash 모두 실행
- IME(한/영, MS-IME) 정상, 조합 중 ESC 안 깨짐
- emoji 4-byte UTF-8, CJK width 2 정상
- Ctrl+C / Ctrl+Break 동작
- resize 시 ConPTY 갱신 누락 없음 (1초 100회 resize stress)
- Command Palette (`Ctrl+Shift+P`) 작동: `New Tab`, `Split Right`, `Split Down`, `Close Pane`, `Focus N`

---

# 2. 아키텍처 결정 (ADR 형식 요약)

각 결정에 대해 **선택 / 거절안 / 사유 / 되돌릴 수 있는 시점**을 명시한다.

## ADR-001 Server / Client 분리는 MVP-3에 도입한다

- **선택**: MVP-1~2는 단일 `AgentWorkspace.App.exe`. 내부적으로는 Server/Client 인터페이스를 통과시키되 in-process로 wire-up.
- **거절안**: 처음부터 두 exe로 시작.
- **사유**: ConPTY/xterm.js 검증 단계에서 IPC를 디버깅하는 비용이 가치 대비 큼. 단, 인터페이스(`IPaneHost`, `ISessionStore`, `IControlChannel`) 분리는 MVP-1부터 강제.
- **되돌릴 수 있는 시점**: MVP-3 시작 시 `IControlChannel` 구현체를 in-process → NamedPipe로 교체.

## ADR-002 Terminal Renderer는 단일 WebView2 호스트, 다중 xterm.js 인스턴스

- **선택**: 한 개의 `WebView2` 컨트롤이 SPA를 호스팅, 그 안에 pane 수만큼 `Terminal` 인스턴스(xterm.js).
- **거절안**: pane마다 별도 WebView2.
- **사유**: WebView2 인스턴스당 100MB 이상 RSS, 4-pane 시 400MB+. 단일 호스트면 < 200MB 유지 가능.
- **리스크**: pane focus/IME 격리. xterm.js의 `Terminal.focus()` 와 hidden input element를 pane별로 분리하고, container DOM에서 pointer-events 격리.

## ADR-003 Control Plane = gRPC over Named Pipe / Data Plane = Raw Bidirectional Pipe

- **선택**: 본문대로.
- **인증**: Server 시작 시 16-byte token 생성 → `%LOCALAPPDATA%\AgentWorkspace\session.token`(ACL: 현재 사용자 RW only). Client 접속 시 token 제시. Data Plane handshake에서 재검증.
- **거절안**: TCP localhost.
- **사유**: ACL 기반 격리가 단순. 다른 사용자의 프로세스가 우연히 접속하는 상황 차단.

## ADR-004 ConPTY Ownership = SessionDaemon

- **선택**: ConPTY handle은 항상 "Daemon role"이 소유. MVP-1~2에서는 Daemon이 App 내부 component, MVP-3부터 별도 process(`AgentWorkspace.Daemon.exe`).
- **자식 프로세스 정리**: pane마다 **Win32 Job Object** 생성 + `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE`. pane 종료 시 자식 트리 보장 종료. Daemon 자체에도 outer Job Object를 두고 `JOB_OBJECT_LIMIT_BREAKAWAY_OK` 전략 결정 필요.
- **Daemon 비정상 종료 시**: 모든 ConPTY 자식 손실 → 사용자에게 명시적 알림. "session persistence"는 *Daemon이 살아 있는 한*에 한정한다고 문서화.

## ADR-005 Workflow는 코드 우선, DSL은 사용 패턴 누적 후

- **선택**: MVP-6에서 workflow는 `IWorkflow` 인터페이스 + C# 구현으로 hardcode.
- **DSL 도입 조건**: workflow 5종 이상 누적 + 사용자 변형 요구 3건 이상.
- **사유**: 미숙한 DSL은 곧 부채. Pulumi, Bicep도 처음엔 코드.

## ADR-006 Approval 모델은 Plan-level Batch + Per-action Fallback

- **선택**:
  1. Agent가 `Plan { actions: [a1..aN] }` 제출
  2. UI는 plan을 한 화면에 보여주고 "Approve plan / Approve subset / Reject" 제공
  3. 위험 액션(파일 삭제, 외부 실행, network)은 plan 승인이어도 **개별 재확인** 강제
- **거절안**: 매 액션 ask only.
- **사유**: agent loop 친화. 단, 위험 액션은 회피 불가하게 강제.

## ADR-007 Secret Redaction = Pattern + Entropy + Configurable Rules

- **엔진**: 다음을 OR로 적용
  - 키 이름 매칭(`*_API_KEY`, `*_TOKEN`, `*_SECRET`, `password=`)
  - 정규식 매칭(AWS access key `AKIA[0-9A-Z]{16}`, Slack `xox[baprs]-`, GitHub `ghp_`, OpenAI `sk-`, Anthropic `sk-ant-`, JWT 3-part base64url, PEM block)
  - Shannon entropy: 길이 ≥ 32 + entropy ≥ 4.5 → 후보, 사용자 confirm 후 redact
- **적용 지점**: (1) agent에 보내는 context packet 직전 (2) transcript 저장 시 (3) UI 표시 시 (toggleable mask)
- **사용자 룰**: `~/.agentworkspace/redaction.yaml` 에서 추가 패턴/제외 가능

## ADR-009 UI Framework = WPF (MVP-2 끝까지 잠정)

- **선택**: `AgentWorkspace.App.Wpf` (`.NET 10 WPF`) + `Microsoft.Web.WebView2.Wpf`.
- **거절안**: WinUI 3.
- **사유**: 본 환경(.NET 10.0.103, Windows 11 26200)에서 `dotnet workload install` 으로 즉시 사용 가능한 WinUI 3 / Windows App SDK 템플릿이 없음. §11 미해결 항목에서 "WinUI 3 ecosystem 미성숙 시 WPF fallback" 을 명시했고, 그 조건이 Day 3 첫 시도에 트리거됨.
- **유지되는 결정**: ADR-002(단일 WebView2 + 다중 xterm.js), ADR-003(IPC), ADR-004(Daemon)는 변경 없음. WPF는 `Microsoft.Web.WebView2.Wpf` 를 통해 동일한 WebView2 호스팅을 지원.
- **재평가**: MVP-2 종료 시점에 (1) Windows App SDK가 환경에 안정적으로 들어왔는지 (2) WPF에서 budget(§ADR-008) 충족이 확인됐는지를 보고 결정.
- **되돌릴 수 있는 시점**: WinUI 3 환경이 갖춰지면 `AgentWorkspace.App.WinUI` 추가 후 점진적 cut-over. 모든 비-UI 컨테이너(ConPTY/ Storage/Workflow)는 UI 프레임워크 무관하게 작성되어 있으므로 교체 비용은 UI XAML 수준에 한정.

## ADR-008 Performance Budget (정량 목표)

| 측정 항목 | 목표 (p95) | 측정 방법 |
|---|---:|---|
| 키 입력 → 화면 echo | ≤ 50ms | xterm.js timestamp diff |
| ConPTY read → client write | ≤ 5ms | Activity span |
| 4-pane workspace idle RSS | ≤ 500MB | `Process.WorkingSet64` |
| pane 1개 idle RSS 증가분 | ≤ 30MB | A/B 측정 |
| 1MB burst output 표시 완료 | ≤ 250ms | benchmark harness |
| GC Gen2 / 분 (idle) | ≤ 1 | dotnet-counters |
| Job-Object 종료 시 좀비 자식 | 0개 | tasklist diff |

이 수치는 회귀 가드. CI에서 측정 + 임계 초과 시 빌드 fail.

---

# 3. 컴포넌트 명세 (인터페이스 우선)

설계 단계에서는 인터페이스만 박는다. 구현은 MVP 단계별로 채운다.

## 3.1 PTY 계층

```csharp
public interface IPseudoTerminal : IAsyncDisposable
{
    PaneId Id { get; }
    PaneState State { get; }                                  // Created / Running / Exited / Faulted

    ValueTask StartAsync(PaneStartOptions opts, CancellationToken ct);
    ValueTask WriteAsync(ReadOnlyMemory<byte> input, CancellationToken ct);
    ValueTask ResizeAsync(short cols, short rows, CancellationToken ct);

    // single subscriber per call; multi-fan-out은 PaneOutputBroadcaster가 담당
    IAsyncEnumerable<PtyChunk> ReadAsync(CancellationToken ct);

    ValueTask SignalAsync(PtySignal signal, CancellationToken ct); // CtrlC, CtrlBreak
    ValueTask KillAsync(KillMode mode, CancellationToken ct);
}

public readonly record struct PtyChunk(
    ReadOnlyMemory<byte> Data,        // ArrayPool-rented; 소비자가 lifetime 책임
    long SequenceId,
    DateTimeOffset MonotonicTime);
```

`PtyChunk.Data`는 ArrayPool 렌트. consumer는 `Return` 시점을 명확히. `PaneOutputBroadcaster`가 **유일한 owner**고, 그 뒤로는 `ReadOnlyMemory<byte>` 복사본 또는 ref-counted slice를 fan-out.

## 3.2 Pane Output Broadcaster

```csharp
public interface IPaneOutputBroadcaster
{
    void Subscribe(PaneId pane, IPaneOutputSink sink);   // 다중 sink
    void Unsubscribe(PaneId pane, IPaneOutputSink sink);
}

public interface IPaneOutputSink
{
    // back-pressure: false 반환 시 broadcaster는 다음 frame부터 drop 통계 증가
    ValueTask<bool> WriteAsync(PaneId pane, PtyFrame frame, CancellationToken ct);
}
```

기본 sink:

- `XtermSink` — Client에게 frame 전송 (drop 허용, UI coalescing)
- `ScrollbackSink` — ring buffer, drop 금지
- `TextTapSink` — VT 제거 후 라인 디코더, agent/workflow event 추출 (drop 금지)
- `TranscriptSink` — opt-in, append-only 파일

drop 정책은 sink가 결정. broadcaster는 `false` 받으면 해당 sink의 `Drop(PaneId, int frames)`을 호출하고 통계만 누적.

## 3.3 Layout

```csharp
public abstract record LayoutNode(LayoutId Id);
public sealed record PaneNode(LayoutId Id, PaneId Pane) : LayoutNode(Id);
public sealed record SplitNode(
    LayoutId Id,
    SplitDirection Direction,
    double Ratio,                // 0..1, child A 비율
    LayoutNode A,
    LayoutNode B) : LayoutNode(Id);

public interface ILayoutManager
{
    LayoutSnapshot Current { get; }
    LayoutSnapshot Split(PaneId target, SplitDirection dir, double ratio = 0.5);
    LayoutSnapshot Close(PaneId target);
    LayoutSnapshot SetRatio(LayoutId split, double ratio);
    LayoutSnapshot Focus(PaneId target);
}
```

binary tree만, 3-way split은 split의 split으로.

## 3.4 Session / Workspace

```csharp
public interface ISessionStore
{
    ValueTask<SessionId> CreateAsync(WorkspaceTemplate? template, CancellationToken ct);
    ValueTask<Session> AttachAsync(SessionId id, CancellationToken ct);
    ValueTask<IReadOnlyList<SessionInfo>> ListAsync(CancellationToken ct);
    ValueTask DetachAsync(SessionId id, CancellationToken ct);
    ValueTask SaveLayoutAsync(SessionId id, LayoutSnapshot layout, CancellationToken ct);
}
```

**저장 매체**:
- `~/.agentworkspace/sessions.db` (SQLite): session 메타, layout, command history, approval log
- `~/.agentworkspace/transcripts/<session>/<pane>/<rotated>.log`: text transcript (1 파일당 8MB rotation, 7일 retention 기본)
- `~/.agentworkspace/templates/*.yaml`: 사용자 편집용 워크스페이스 템플릿

**저장하지 않는 것**: pane raw scrollback (메모리 ring buffer만), agent prompt/response 전문 (opt-in 시에만 별도 파일).

## 3.5 Agent Adapter

```csharp
public interface IAgentAdapter
{
    AgentId Id { get; }
    AgentCapabilities Capabilities { get; }   // StructuredOutput, Cancellable, StreamingPlan

    ValueTask<AgentSession> StartSessionAsync(AgentSessionOptions opts, CancellationToken ct);
}

public interface IAgentSession : IAsyncDisposable
{
    IAsyncEnumerable<AgentEvent> Events { get; }                      // PlanProposed, ActionRequest, Message, Done
    ValueTask SendAsync(AgentMessage msg, CancellationToken ct);
    ValueTask CancelAsync(CancellationToken ct);                      // SIGINT-ish
}
```

**MVP-5 어댑터 매트릭스**:

| Agent | 어댑터 전략 | 구조화 출력 | Cancel |
|---|---|---|---|
| Claude Code | `claude --output-format stream-json` 파이프 | ✅ JSON lines | SIGINT via ConPTY |
| Codex | CLI raw + heuristic line parser | △ | SIGINT |
| Gemini | CLI raw + heuristic | △ | SIGINT |
| Generic | 사용자 정의 regex 룰 | ❌ | best-effort |

구조화 출력을 지원하지 않는 agent는 capability 표기로 workflow가 쓸 수 있는 기능을 제한.

## 3.6 Policy Engine

```csharp
public interface IPolicyEngine
{
    ValueTask<PolicyDecision> EvaluateAsync(ProposedAction action, AgentContext ctx, CancellationToken ct);
}

public abstract record ProposedAction;
public sealed record ExecuteCommand(string Cmd, string[] Args, string Cwd, IReadOnlyDictionary<string,string> Env) : ProposedAction;
public sealed record WriteFile(string Path, ReadOnlyMemory<byte> Content, FileWriteMode Mode) : ProposedAction;
public sealed record DeletePath(string Path, bool Recursive) : ProposedAction;
public sealed record NetworkCall(Uri Url, string Method) : ProposedAction;
public sealed record InvokeMcpTool(string ServerId, string ToolName, JsonElement Args) : ProposedAction;

public sealed record PolicyDecision(
    PolicyVerdict Verdict,         // Allow / AskUser / Deny
    string Reason,
    Risk Risk,                     // Low / Medium / High / Critical
    bool RequireIndividualApproval // batch 승인이어도 개별 재확인
);
```

위험도 산정:
- `DeletePath(recursive=true)` → Critical (항상 개별 재확인)
- `ExecuteCommand` cmd가 `format`, `del /s`, `rm -rf`, `Invoke-Expression` 포함 → Critical
- `NetworkCall` 외부 도메인 → High (deny-by-default profile에서는 deny)
- `WriteFile` 워크스페이스 외부 → High
- `WriteFile` 워크스페이스 내부 + 텍스트 → Low/Medium

## 3.7 Workflow Engine

```csharp
public interface IWorkflow
{
    WorkflowId Id { get; }
    WorkflowTrigger Trigger { get; }
    ValueTask<WorkflowResult> RunAsync(WorkflowContext ctx, CancellationToken ct);
}
```

MVP에서는 다음 3개를 hardcode:
1. `FixDotnetTestsWorkflow` — `TestFailedEvent` → 로그 수집 → agent plan → batch approval → 재실행
2. `ExplainBuildErrorWorkflow` — `BuildFailedEvent` → 로그 요약만 (실행 없음, 위험 0)
3. `SummarizeSessionWorkflow` — `SessionDetached` → transcript 요약 저장

step 실패 시 정책: **abort + log + user notification 기본**, retry는 step이 자체적으로 옵트인.

---

# 4. Hot Path: Terminal I/O

```
ConPTY pipe ── PipeReader ──┐
                            ▼
                   [VT-Boundary Aware Splitter]   ← partial escape 보존
                            ▼
                   PtyChunk(ArrayPool)
                            ▼
                   PaneOutputBroadcaster
                   ┌────────┼────────┬────────┐
                XtermSink Scrollback TextTap Transcript
                (drop OK) (no drop) (no drop) (opt-in)
```

**규칙**:
- ConPTY → Splitter 까지: zero allocation (PipeReader buffer 재사용)
- Splitter는 ESC sequence가 buffer 경계에서 끊겼는지 확인하고 다음 read까지 대기. state machine 4 byte 짜리.
- Broadcaster는 frame 단위로 전달 (frame = 16ms tick 또는 64KB 누적, 둘 중 먼저).
- XtermSink는 frame을 base64로 인코딩하지 않음 — Raw Pipe로 binary 전송, JS 측 `postMessage` transferable로 받음.

**Resize 직렬화**: pane별 actor 1개 (System.Threading.Channels). write/resize/kill 모두 같은 channel에 enqueue. ConPTY API는 thread-safe하지 않으므로 직렬화 필수.

**Backpressure**:
- XtermSink의 client가 느리면: `frames_dropped_total{pane}` 증가. UI 상단에 "Output coalesced" 인디케이터 표시.
- TextTap이 느리면: ring buffer 가득 차면 가장 오래된 라인 drop, `text_lines_dropped_total{pane}` 증가. workflow 입장에서 hint로 사용 가능.

---

# 5. UI 결정

## 5.1 화면 layout (재확정)

- 상단: 워크스페이스 / 브랜치 / agent 상태 / `Ctrl+Shift+P`
- 중앙: pane grid (xterm.js)
- 우측: Agent Trace **collapsible**, 활동 시 자동 펼침, idle 5분 후 자동 접힘
- 하단: Event Timeline (build/test/git/workflow)

## 5.2 Command Palette는 MVP-1 필수

명령:
- `New Tab`, `Close Tab`
- `Split Right`, `Split Down`, `Close Pane`, `Focus N`, `Zoom Pane`
- `Run Command…` (cwd/env override 가능)
- `Save Snapshot`, `Restore Snapshot`
- 이후 MVP-5: `Ask Agent…`, `Run Workflow: …`

## 5.3 Approval Dialog

```
Plan from: coder (Claude Code)
Goal: Fix UserServiceTests failure

Actions (3):
  [✓] 1. read   src/UserService.cs                          [Low]
  [✓] 2. write  src/UserService.cs (+12 −4)   [diff ▾]      [Medium]
  [!] 3. exec   dotnet test                                  [Low]

[Approve plan]  [Approve selected]  [Reject]
                                ↑
                   체크박스 토글로 부분 승인
```

위험도 Critical 액션은 plan 승인이어도 실행 직전 다시 모달.

---

# 6. 보안 결정 (요약)

- **기본 profile = `safeDev`**: read allow / write ask / exec ask / network ask
- **위험 명령 블랙리스트** (실행 전 매칭):
  - `rm -rf`, `del /s /q`, `format`, `mkfs`
  - `Invoke-Expression`, `IEX`, `iex `, `cmd /c %`
  - `git push --force`, `git push -f`, `git push --force-with-lease` (force-with-lease는 warn)
  - `curl … | sh`, `iwr … | iex`
  - Registry: `reg delete`, `Remove-Item HKLM:`
- **Argument-aware parsing**: shell command를 파싱해 토큰 단위로 표시. `dotnet test; rm -rf /` 같은 chain을 감지.
- **Secret redaction**: §ADR-007. 적용 지점 3곳 모두에서 동일 엔진 호출.

---

# 7. 관찰성

`System.Diagnostics.Activity` + `Meter` 기반. OpenTelemetry exporter는 **opt-in**.

**Always-on metrics** (low cardinality):
- `agentworkspace_pane_count`
- `agentworkspace_frames_emitted_total{pane}` / `frames_dropped_total{pane}`
- `agentworkspace_text_lines_total{pane}` / `text_lines_dropped_total{pane}`
- `agentworkspace_workflow_runs_total{workflow,result}`
- `agentworkspace_approval_total{verdict}`
- `agentworkspace_input_echo_latency_ms` (histogram)

**Always-on logs** (텍스트, 7일 rotation):
- 워크플로우 시작/완료/실패
- 승인 요청/승인/거절 (액션 요약 포함)
- pane 시작/종료 (cmd/cwd/exit code)

**Opt-in**:
- Agent prompt/response 전문
- raw VT bytes (디버깅용 capture mode)

---

# 8. MVP 단계 (개정)

| # | 이름 | 핵심 산출물 | 완료 기준 (수치) |
|---|---|---|---|
| 1 | Single Pane Terminal | ConPTY + WebView2 + xterm.js + Command Palette | §1.2 통과, 입력 echo p95 ≤ 50ms |
| 2 | Pane Split & Layout | binary layout tree, focus, resize | 4-pane 안정, idle RSS ≤ 500MB |
| 3 | Daemon Split | `Daemon.exe` 분리, NamedPipe attach/detach | client kill 후 attach 시 출력 연속성 유지 |
| 4 | Workspace Template | YAML template, snapshot/restore | template 로드 후 모든 pane 자동 시작 |
| 5 | Agent Pane | ClaudeAdapter (stream-json), AgentTrace UI | plan 표시, cancel 정상, transcript 저장 |
| 6 | Workflow Engine v1 | 3개 hardcoded workflow + ApprovalUI | fix-dotnet-tests end-to-end 시연 |
| 7 | Policy + Redaction | safeDev/readOnly/trustedLocal, redaction engine | 위험 명령 블랙리스트 100% catch (테스트셋 50개) |
| 8 | Performance Hardening | benchmark harness + CI gate | §ADR-008 budget 전체 충족 |
| 9 | (옵션) DSL / Native Renderer | 패턴 누적 후 결정 | — |

각 MVP는 **그 자체로 사용 가능**한 상태로 마감. half-done 금지.

---

# 9. 프로젝트 레이아웃 (개정)

```
AgentWorkspaceTerminal/
 ├─ src/
 │  ├─ AgentWorkspace.Abstractions/    # IPseudoTerminal, IAgentAdapter, IPolicyEngine 등
 │  ├─ AgentWorkspace.ConPTY/          # Native interop, Job Object, PipePump
 │  ├─ AgentWorkspace.Core/            # Session/Layout/Workflow/Policy 구현
 │  ├─ AgentWorkspace.Agents.Claude/   # stream-json adapter
 │  ├─ AgentWorkspace.Agents.Generic/  # CLI heuristic
 │  ├─ AgentWorkspace.Storage/         # SQLite, transcript files
 │  ├─ AgentWorkspace.Daemon/          # MVP-3에서 분리되는 host
 │  ├─ AgentWorkspace.App.WinUI/       # Client UI (MVP-1~2는 in-process Daemon 호스팅)
 │  ├─ AgentWorkspace.Cli/             # `awt` 명령
 │  └─ AgentWorkspace.Tests/
 │     ├─ Unit/
 │     ├─ Integration/
 │     └─ Benchmarks/                  # BenchmarkDotNet, perf budget gate
 ├─ web/terminal/                      # 단일 SPA: 다중 xterm.js 인스턴스
 ├─ schemas/                           # workspace.schema.json, workflow.schema.json
 ├─ rules/redaction.default.yaml
 └─ docs/
```

---

# 10. 첫 두 주 작업 계획 (concrete)

| Day | 작업 |
|---|---|
| 1–2 | repo skeleton, `Abstractions` + `ConPTY` 패키지, ConPTY 단일 spike (콘솔 앱) |
| 3–4 | WinUI 3 shell + WebView2 + xterm.js 단일 인스턴스, Raw byte 송수신 |
| 5–6 | IME / CJK / emoji 회귀 테스트 자동화 (Windows.UI.Input.Composition mock 또는 수동 매트릭스) |
| 7 | Command Palette 골격 (`Ctrl+Shift+P`, 명령 5개) |
| 8–9 | LayoutManager + 다중 xterm.js 인스턴스 (단일 WebView2) |
| 10 | Job Object 도입, 자식 프로세스 좀비 회귀 테스트 |
| 11–12 | benchmark harness (BenchmarkDotNet) + 입력 echo / burst output 테스트 |
| 13 | session SQLite schema + layout 저장/복구 |
| 14 | 회고 + MVP-3 진입 결정 (Daemon 분리 시점) |

---

# 11. 미해결 항목 (의도적으로 남김)

- **WinUI 3 vs WPF**: WinUI 3 ecosystem 미성숙 시 WPF로 fallback 가능성. MVP-2 종료 시점에 재평가.
- **Native renderer 진입 조건**: WebView2 RSS가 budget 초과하거나, native VT control 요구가 누적되면 시작.
- **MCP**: MVP-7 이후. Policy Engine을 통과하는 tool invocation 모델 채택 예정 (§3.6의 `InvokeMcpTool`).
- **Multi-agent 동시성**: 두 agent가 같은 파일 수정 제안 시 → Approval UI에서 충돌 표시 + diff merge 유도. 알고리즘 미정.
- **Agent context 중복 문제**: Server 측 context와 agent CLI 자체 context의 중복 누적을 어떻게 측정/관리할지 — 측정 도구부터 만들어야 결정 가능.

---

# 12. 결론

원안의 방향(persistent terminal + agent runtime + workflow)은 유지한다. 본 설계는 다음을 추가했다.

1. **분리는 늦게, 인터페이스는 일찍** — 단일 프로세스로 시작하되 Server/Client 경계는 처음부터 코드로 강제.
2. **수치로 박힌 budget** — 입력 latency 50ms, 4-pane 500MB, 자식 좀비 0.
3. **Approval은 plan 단위, 위험 액션은 개별 재확인** — agent loop 친화 + 안전 양립.
4. **Renderer는 단일 WebView2 + 다중 xterm.js** — 메모리 비용 1/N.
5. **Job Object로 자식 프로세스 정리 보장**.
6. **Workflow는 코드 우선, DSL은 누적 후** — 미숙한 DSL 부채 회피.
7. **Secret redaction = pattern + entropy + 사용자 룰**.
8. **각 MVP는 단독 사용 가능** — half-done 금지.

다음 단계는 **MVP-1 day 1**: `AgentWorkspace.Abstractions` + `AgentWorkspace.ConPTY` 패키지를 만들고 ConPTY spike를 콘솔 앱에서 검증한다.
