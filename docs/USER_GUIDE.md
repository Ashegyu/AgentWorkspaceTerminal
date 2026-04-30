# Agent Workspace Terminal — 사용 가이드

MVP-8 + 모든 maintenance 슬롯이 끝난 시점(2026-04-30) 기준의 사용자 매뉴얼.
설계 의도/배경은 [DESIGN.md](../DESIGN.md), 빌드/구조 개요는 [README.md](../README.md) 참조.

이 문서는 **이미 빌드된 앱을 어떻게 쓰는가**에 집중한다.

---

## 목차

1. [설치 & 첫 실행](#1-설치--첫-실행)
2. [기본 조작](#2-기본-조작)
3. [Command Palette — 명령 카탈로그](#3-command-palette--명령-카탈로그)
4. [Workspace 템플릿 (MVP-4)](#4-workspace-템플릿-mvp-4)
5. [AI 에이전트 (MVP-5)](#5-ai-에이전트-mvp-5)
6. [워크플로 (MVP-6)](#6-워크플로-mvp-6)
7. [Policy & 승인 (MVP-7 + Slot 4)](#7-policy--승인-mvp-7--slot-4)
8. [세션 영속성 & 데몬 라이프사이클](#8-세션-영속성--데몬-라이프사이클)
9. [성능 측정 도구 (PerfProbe CLI)](#9-성능-측정-도구-perfprobe-cli)
10. [BenchmarkDotNet 정밀 측정](#10-benchmarkdotnet-정밀-측정)
11. [문제 해결](#11-문제-해결)
12. [파일 위치 요약](#12-파일-위치-요약)

---

## 1. 설치 & 첫 실행

### 요구사항
- Windows 10 1809+ (ConPTY 지원)
- .NET 10 SDK (검증: 10.0.103)
- Microsoft Edge **WebView2 Runtime** (Windows 11 기본 포함)

### 빌드
```pwsh
dotnet build AgentWorkspaceTerminal.slnx -c Release
```

### 실행
```pwsh
dotnet run --project src/AgentWorkspace.App.Wpf
```

App 시작 시 자동으로:
1. `%LOCALAPPDATA%\AgentWorkspace\session.token` 을 읽어 데몬에 attach 시도.
2. 살아 있는 데몬이 없으면 동봉된 `awtd.exe` 를 spawn (App.Wpf bin 폴더에 ProjectReference 로 복사됨).
3. 셸 검색 우선순위: `pwsh.exe` → `powershell.exe` → `cmd.exe`.

App 창을 닫아도 daemon 은 살아 있으므로, 다음 실행 시 같은 pane 들이 그대로 attach 됨 (ADR-010).

---

## 2. 기본 조작

| 키 | 동작 |
|---|---|
| `Ctrl+Shift+P` | Command Palette 열기 |
| `Esc` | Command Palette 닫기 |
| 마우스 클릭 (pane) | 해당 pane 에 포커스 이동 |
| 셸 입력 | xterm.js → bridge.js → daemon → ConPTY → 자식 셸 |
| `Ctrl+C` (셸 안) | 자식 프로세스 인터럽트 (셸이 처리, 일반 동작) |

> **Tip — 폰트 크기**: 팔레트의 `Increase Font Size` / `Decrease Font Size` 가 +1/-1 px 단위로 동작. 8~36 px 범위로 클램프된다.

---

## 3. Command Palette — 명령 카탈로그

`Ctrl+Shift+P` 후 검색어를 입력하면 fuzzy match. 각 명령은 description + keyword 로 검색됨.

### 셸 제어 (MVP-1)
| 명령 | 동작 |
|---|---|
| **Restart Shell** | 포커스된 pane 의 자식 트리를 죽이고 새 셸 기동 |
| **Send Ctrl+C** | 포커스된 pane 의 foreground 프로세스에 SIGINT 전달 |
| **Clear Terminal** | 포커스된 pane 의 화면만 클리어 (scrollback 유지) |
| **Increase / Decrease Font Size** | 포커스된 pane 의 폰트 ±1 px |

### 레이아웃 (MVP-2)
| 명령 | 동작 |
|---|---|
| **Split Right** | 포커스 pane 오른쪽에 새 pane (horizontal split) |
| **Split Down** | 포커스 pane 아래에 새 pane (vertical split) |
| **Close Pane** | 포커스 pane 종료 (마지막 1개면 거부) |
| **Focus Next Pane** | 다음 pane 으로 포커스 순환 |
| **Focus Previous Pane** | 이전 pane 으로 포커스 순환 |

### 워크스페이스 템플릿 (MVP-4)
| 명령 | 동작 |
|---|---|
| **Open Template…** | YAML 템플릿을 파일 다이얼로그로 골라 현재 레이아웃을 교체 |
| **Save Snapshot…** | 현재 레이아웃 + pane 명령을 YAML 로 저장 |

### AI 에이전트 (MVP-5)
| 명령 | 동작 |
|---|---|
| **Ask Agent…** | 새 pane 에 Claude Code 세션 기동 (Claude CLI 가 PATH 에 있어야 함) |

### 워크플로 (MVP-6)
| 명령 | 동작 |
|---|---|
| **Summarize Session…** | 가장 최근 에이전트 transcript 를 Claude 로 요약 |

### 운영 / 측정 (Slot 3)
| 명령 | 동작 |
|---|---|
| **Dump Echo Latency Samples…** | bridge.js 에 누적된 키→렌더 round-trip 샘플(최대 500개)을 `awt-perfprobe echo-latency` 로 자동 pipe; status bar 에 `p95=X.Xms` 표시 |

---

## 4. Workspace 템플릿 (MVP-4)

YAML 1.0 schema. 예시:

```yaml
# schemas/examples/basic.yaml
name: Basic Dev
description: Editor left, shell right — a minimal two-pane setup.
version: 1.0.0

panes:
  - id: editor
    command: cmd
    args: [/d, /k, echo editor pane]
  - id: shell
    command: cmd
    args: [/d, /k]

layout:
  split: horizontal
  ratio: 0.65
  a:
    pane: editor
  b:
    pane: shell

focus: editor
```

- `panes`: 각 pane 의 child process. `command` + `args`.
- `layout`: 트리. `pane: <id>` (leaf) 또는 `split: horizontal|vertical` + `ratio` + `a` + `b` (internal).
- `focus`: 초기 포커스 pane id.
- 스키마: [schemas/workspace.schema.json](../schemas/workspace.schema.json). 더 많은 예: [schemas/examples/three-pane.yaml](../schemas/examples/three-pane.yaml).

**Save Snapshot…** 으로 현재 상태를 그대로 저장하면 다음 세션에서 같은 구성을 재현할 수 있다.

---

## 5. AI 에이전트 (MVP-5)

### 사전 준비
- Claude Code CLI(`claude`) 가 PATH 에 있어야 함.
- 기본 모델은 `ClaudeAdapter` 가 결정 (보통 sonnet 4.6).

### 사용
1. `Ctrl+Shift+P` → **Ask Agent…**
2. 입력 다이얼로그에 첫 prompt 입력.
3. 새 pane 이 열리면서:
   - 위쪽: ConPTY 로 실행되는 `claude` 자식 프로세스 (xterm)
   - **AgentTrace**: 모델/도구 호출/결과 이벤트가 ObservableCollection 으로 표시
4. transcript 는 JSONL 로 `~/.agentworkspace/transcripts/<session-id>.jsonl` 에 append.

### Transcript 활용
- **Summarize Session…** 명령이 가장 최근 transcript 를 자동으로 가져다 Claude 로 요약. 결과는 `~/.agentworkspace/summaries.jsonl` 에 append.
- 외부 도구로 직접 분석하려면 `~/.agentworkspace/transcripts/` 디렉터리 직접 참조.

---

## 6. 워크플로 (MVP-6)

워크플로는 **트리거 → 컨텍스트 → 결과** 구조. `WorkflowEngine` 이 모든 등록된 `IWorkflow` 를 순회하며 `CanHandle` 로 매칭.

### 내장 워크플로

| 워크플로 | 트리거 | 동작 |
|---|---|---|
| `SummarizeSessionWorkflow` | `ManualTrigger("Summarize Session")` 또는 `SessionDetachedTrigger` | transcript JSONL 읽고 요약 생성 |
| `ExplainBuildErrorWorkflow` | `BuildFailedTrigger(projectPath, logText)` | dotnet build 로그를 Claude 로 분석, 가능한 원인/수정안 제시 |
| `FixDotnetTestsWorkflow` | `TestFailedTrigger(projectPath, logText)` | 실패한 테스트를 보고 수정 시도 (Approval 필수) |

### 트리거 종류
```csharp
ManualTrigger(string WorkflowName, string? Argument = null)
TestFailedTrigger(string ProjectPath, string LogText)
BuildFailedTrigger(string ProjectPath, string LogText)
SessionDetachedTrigger(string SessionId, string TranscriptPath)
```

### 새 워크플로 추가
ADR-016 기준으로는 **4번째 hardcoded 워크플로 요청**이 들어오면 그때 DSL 검토. 그 전까지는 `IWorkflow` 직접 구현 후 DI 등록.

---

## 7. Policy & 승인 (MVP-7 + Slot 4)

### Policy 레벨
| 레벨 | 의미 |
|---|---|
| `SafeDev` | 기본값. 모든 destructive 명령은 사용자 승인 필요 |
| `TrustedLocal` | whitelist 명령은 자동 허용 (read-only `git status`, `kubectl get` 등) |

### 평가 순서 (defense-in-depth)
1. **Built-in blacklist** (코드 내장) — `rm -rf /`, `format`, `del /f /s` 등 critical 명령.
2. **User blacklist** (yaml) — 사용자 추가.
3. **Built-in whitelist** + **User whitelist** — `TrustedLocal` 일 때만 적용.
4. 매치 없음 → `AskUser` (승인 다이얼로그).

> **중요: blacklist 가 항상 whitelist 를 이긴다.** 사용자가 실수로 `rm` 을 whitelist 해도 built-in blacklist 가 막음.

### User policy 파일 (Slot 4)
경로: `%USERPROFILE%\.agentworkspace\policies.yaml`. 샘플: [docs/policies.example.yaml](policies.example.yaml).

```yaml
version: 1

blacklist:
  - pattern: "^deploy-prod"
    mode: regex            # regex (default) | prefix | glob
    risk: high             # low | medium | high | critical
    reason: "Production deploy must be initiated outside the agent."

whitelist:
  - pattern: "kubectl get"
    mode: prefix
    reason: "Read-only kubernetes lookup."

  - pattern: "docker ps*"
    mode: glob
    reason: "Listing containers is read-only."
```

- **Validation 규칙**:
  - `version` 은 `1` 만 허용.
  - `pattern`, `reason` 필수.
  - `mode` 누락 → `regex` (기본).
  - `risk` 누락 → `high` (안전한 기본값).
  - 알 수 없는 mode/risk → 즉시 `UserPolicyConfigException`.
- 파싱 실패 시 App 은 **built-in 룰만으로 fallback** (조용히 폴백하지 않고 stderr 에 path-prefixed 메시지 표시).
- 변경사항은 App 재시작 시 반영 (현재 hot-reload 미지원).

### 승인 다이얼로그
- `AskUser` 결과가 나오면 `ApprovalDialog` 가 뜸. 명령, 위험도, 사유를 표시.
- **Approve / Deny** + 옵션 *"이 세션 동안 비슷한 명령 자동 승인"* (in-memory only, 영속화 X).

---

## 8. 세션 영속성 & 데몬 라이프사이클

### 컴포넌트 구조
```
App.exe (WPF, WebView2)  ──NamedPipe──►  awtd.exe (daemon)
                                          │
                                          ├─ ConPTY × N (각 pane 의 자식 셸)
                                          ├─ SqliteSessionStore (~/.agentworkspace/sessions.db)
                                          └─ RpcDispatcher (control + data + store)
```

- 데몬은 사용자별 격리: pipe 이름 `agentworkspace.control.{user-sid}`, 동시 connection 최대 4.
- Bearer token: `%LOCALAPPDATA%\AgentWorkspace\session.token` (32-char base64, 현재 사용자만 FullControl ACL).
- App 종료 ≠ 데몬 종료. 데몬을 직접 종료하려면 콘솔에서 `Ctrl+C` 또는 `awtd.exe` 프로세스 종료.

### 데몬 직접 실행 (디버깅)
```pwsh
dotnet run --project src/AgentWorkspace.Daemon
```

기동 시 출력:
```
[awtd] listening on \\.\pipe\agentworkspace.control.S-1-5-21-...
[awtd] session token written to C:\Users\<user>\AppData\Local\AgentWorkspace\session.token
[awtd] press Ctrl+C to stop.
```

### 세션 DB
- 위치: `~/.agentworkspace/sessions.db` (SQLite WAL).
- 데몬이 owner. 클라이언트는 RPC (`Store.Get*`/`Store.Save*`) 로만 접근.
- 백업: 데몬 정지 후 파일 복사. WAL 을 정리하려면 `PRAGMA wal_checkpoint(FULL)`.

---

## 9. 성능 측정 도구 (PerfProbe CLI)

ADR-008 의 7개 운영 메트릭 중 BDN 으로 못 잡는 것을 측정. 빌드 후 `src/AgentWorkspace.PerfProbe/bin/Release/net10.0-windows/awt-perfprobe.exe`.

### Sub-commands

#### `echo-latency` — 키 입력→화면 echo p95 (#1)
입력 ms 배열을 받아 percentile 계산만 수행. `--input <file>` (한 줄에 한 ms 값) 또는 stdin.
```pwsh
awt-perfprobe echo-latency --input samples.txt
# {"p50":12.4,"p95":31.7,"max":58.0,"n":487}
```
보통 직접 호출보다 App.Wpf 팔레트의 **Dump Echo Latency Samples…** 를 통해 자동으로 호출됨.

#### `rss` — daemon-floor idle RSS (#3, #4)
WPF/WebView2 없이 데몬 + ConPTY 만으로 N개 pane 을 돌렸을 때의 RSS 측정.
```pwsh
awt-perfprobe rss --panes 4
awt-perfprobe rss --panes 1
```
CI gate 가 자동 호출. ADR-008 천장: 4-pane ≤ 500MB, 1-pane delta ≤ 30MB.

#### `rss-full` — full-stack RSS (Slot 1)
WPF + WebView2 까지 합친 실제 사용 환경. App.Wpf 가 이미 떠 있어야 함:
```pwsh
$pid = (Get-Process AgentWorkspace.App.Wpf).Id
awt-perfprobe rss-full --pid $pid --warmup-sec 3 --sample-sec 5
```
출력: 프로세스명별 breakdown + min/peak/p50/p95. CI 자동화 불가 (PID 가 환경에 따라 다름) — 사람이 측정 후 `baseline.json` 의 `fourPaneIdleRssFullMb` 채움.

#### `gc-idle` — GC Gen2/min idle (#6)
```pwsh
awt-perfprobe gc-idle --duration-sec 60
# {"gen2PerMin":0.0,"durationSec":60,"gen2Total":0}
```
ADR-008 천장: ≤ 1 collection/min.

#### `zombies` — Job-Object 좀비 자식 (#7)
```pwsh
awt-perfprobe zombies --panes 4 --settle-ms 500
# {"zombies":0,"panes":4,"settleMs":500}
```
ConPTY 자식 트리를 KillMode.Force 로 죽인 뒤 settle 시간 후 PID 가 reaped 됐는지 확인. ADR-008: 0 마리.

### CI Gate
[`.github/workflows/perf-gate.yml`](../.github/workflows/perf-gate.yml) → [`scripts/Test-PerfBudget.ps1`](../scripts/Test-PerfBudget.ps1).
매 push/PR 마다 4개 메트릭(rss 1-pane / rss 4-pane / gc-idle / zombies)을 측정하고 다음 둘 다 만족해야 통과:
1. **회귀 게이트**: `baseline.json` × 1.5
2. **하드 천장**: ADR-008 절대값

자세한 매트릭스는 [docs/perf-budget.md](perf-budget.md).

---

## 10. BenchmarkDotNet 정밀 측정

`src/AgentWorkspace.Benchmarks/`. perf-sensitive 변경 후 명시적으로:

```pwsh
# 모든 벤치 (수십 분)
dotnet run -c Release --project src/AgentWorkspace.Benchmarks -- --filter '*'

# 특정 부분만
dotnet run -c Release --project src/AgentWorkspace.Benchmarks -- --filter '*Layout*'
dotnet run -c Release --project src/AgentWorkspace.Benchmarks -- --filter '*PolicyEval*'

# 빠른 smoke run
dotnet run -c Release --project src/AgentWorkspace.Benchmarks -- --job short --filter '*'
```

### MVP-8 추가 벤치 (Day 59)
| 클래스 | 측정 |
|---|---|
| `Mvp8/PolicyEvalBench` | 50-rule miss / earlyHit / lateHit |
| `Mvp8/RedactionEvalBench` | 14-rule plain / tokenHit / 1KB bulk |
| `Mvp8/PtyReadWriteBench` | 64KB read→write cycle p95 |
| `Mvp8/BurstRenderBench` | 1MB burst (128 × 8KB) |

### 야간 BDN (Slot 2)
[`.github/workflows/bdn-nightly.yml`](../.github/workflows/bdn-nightly.yml) 가 매일 05:17 UTC 에 ShortRun 으로 4개 메트릭을 측정하고 `baseline.json × 1.5` 회귀 게이트로 알림. 결과는 14일간 artifact 보존.

---

## 11. 문제 해결

### App 이 시작은 되는데 pane 이 비어 있음
1. `%LOCALAPPDATA%\AgentWorkspace\session.token` 이 있는지 확인.
2. `awtd.exe` 가 살아 있는지 (`tasklist | findstr awtd`).
3. 둘 다 있는데도 안 되면 token 파일 삭제 후 App 재시작 — 새 데몬을 spawn.

### "데몬 spawn 실패" 메시지
- App.Wpf bin 폴더에 `awtd.exe` 가 복사됐는지 확인. ProjectReference 가 깨졌을 가능성.
- `dotnet build -c Release` 다시.

### Claude CLI 가 안 뜸
- `where claude` 로 PATH 확인.
- Claude Code 가 별도 인증을 요구하면 먼저 별도 셸에서 `claude` 한 번 실행하여 인증.

### Policy 가 적용 안 됨
- `~/.agentworkspace/policies.yaml` 위치 확인 (틸드 확장 안 되는 환경에서는 절대 경로 사용).
- 파싱 에러 시 stderr 에 `path: rule[N].field ...` 형태 메시지가 뜸 — 그대로 보고 수정.
- 변경 후 App 재시작 필요 (hot-reload 없음).

### Echo latency 가 비현실적으로 높음
- 키를 길게 누르면 autorepeat 가 `pendingInputTs` 를 덮어써서 p95 가 부풀려짐. 의도적으로 ≥ 50ms 간격 으로 띄엄띄엄 입력해야 깨끗한 측정. (`web/terminal/bridge.js` 의 caveat 주석 참조.)

### CI perf-gate 가 첫 실행에서 실패
- 보통 `setup-dotnet@v4 + dotnet-version: '10.0.x'` 가 .NET 10 GA 등록 전이라 발생. `bdn-nightly.yml` 도 같은 패턴이라 동시에 패치해야 함.
- baseline 자체가 너무 엄격할 가능성도 있음 — baseline 갱신 PR 로 대응.

### `awt-spike` 셸 데모
ConPTY 단독 동작 확인용 진단 도구. 본 앱과는 무관:
```pwsh
dotnet run --project src/AgentWorkspace.Spike.Console
# Ctrl+C — 자식에 SIGINT
# Ctrl+] — spike 종료
```

---

## 12. 파일 위치 요약

### 사용자 데이터
| 경로 | 내용 |
|---|---|
| `%LOCALAPPDATA%\AgentWorkspace\session.token` | 데몬 bearer token (파일 ACL: 사용자만) |
| `~/.agentworkspace/sessions.db` | SQLite 세션 DB (WAL) |
| `~/.agentworkspace/transcripts/<id>.jsonl` | 에이전트 transcript (append-only) |
| `~/.agentworkspace/summaries.jsonl` | Summarize 결과 모음 |
| `~/.agentworkspace/policies.yaml` | 사용자 policy 추가 (선택) |

### 빌드 산출물
| 경로 | 설명 |
|---|---|
| `src/AgentWorkspace.App.Wpf/bin/Release/net10.0-windows/AgentWorkspace.App.exe` | 메인 GUI |
| `src/AgentWorkspace.App.Wpf/bin/Release/net10.0-windows/awtd.exe` | 데몬 (ProjectReference 로 함께 복사) |
| `src/AgentWorkspace.PerfProbe/bin/Release/net10.0-windows/awt-perfprobe.exe` | 성능 측정 CLI |
| `src/AgentWorkspace.Spike.Console/bin/Release/net10.0/awt-spike.exe` | ConPTY 진단 CLI |

### 문서
| 경로 | 내용 |
|---|---|
| [DESIGN.md](../DESIGN.md) | 설계 결정 + 모든 ADR |
| [docs/perf-budget.md](perf-budget.md) | ADR-008 측정 매트릭스 |
| [docs/manual-test-matrix.md](manual-test-matrix.md) | 회귀 시 사람이 돌리는 시나리오 |
| [docs/retros/](retros/) | MVP 별 회고 |
| [docs/policies.example.yaml](policies.example.yaml) | User policy 샘플 |
| [schemas/workspace.schema.json](../schemas/workspace.schema.json) | 워크스페이스 템플릿 schema |

---

## 부록 A: 자주 쓰는 단축 흐름

### "어제 작업 이어서"
1. App 실행 → 자동으로 어제 레이아웃 복원 (데몬이 살아 있으면 즉시, 죽었으면 sessions.db 에서 복구).
2. 이전 에이전트 세션도 pane 째 살아 있음.

### "에이전트한테 빌드 에러 물어보기"
1. 셸 pane 에서 `dotnet build` 실행 후 에러 발생.
2. `Ctrl+Shift+P` → **Ask Agent…** 후 빌드 로그 붙여넣기.
3. 또는 코드에서 직접 `WorkflowEngine.RunAsync(new BuildFailedTrigger(projectPath, logText))` 호출 (현재는 코드 경로만, 팔레트 노출은 4번째 워크플로 요청 시 검토).

### "최근 세션 요약해서 보고서 만들기"
1. 에이전트 작업 끝.
2. `Ctrl+Shift+P` → **Summarize Session…**
3. `~/.agentworkspace/summaries.jsonl` 에 결과 누적 — 외부 스크립트로 가공.

### "프로덕션 명령 실수 막기"
1. `~/.agentworkspace/policies.yaml` 에 prod 패턴을 blacklist 추가.
2. App 재시작.
3. Built-in blacklist 가 항상 whitelist 를 이기므로 안전.
