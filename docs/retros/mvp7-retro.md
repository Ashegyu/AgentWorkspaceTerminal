# Retro — MVP-7 (Day 44–52)

작성일: 2026-04-30
대상: DESIGN.md §ADR-014 Day 44–52에 해당하는 작업.

## 1. 무엇을 만들었나

9일치 작업의 결과물:

| Layer | Artefact |
|---|---|
| Abstractions / Policy | `PolicyVerdict` (Allow/AskUser/Deny), `Risk` (Low..Critical), `PolicyLevel` (ReadOnly/SafeDev/TrustedLocal), `ProposedAction` DU (`ReadFile`, `ExecuteCommand`, `WriteFile`, `DeletePath`, `NetworkCall`, `InvokeMcpTool`), `PolicyDecision` (Verdict, Reason, Risk, RequireIndividualApproval), `PolicyContext`, `IPolicyEngine.EvaluateAsync` |
| Abstractions / Redaction | `IRedactionEngine.Redact(string)` |
| Core / Policy | `PolicyEngine` (5 action types × 3 levels 매트릭스 + workspace 외부 경로 처리), `BlacklistRule` (regex + Risk + Reason), `Blacklists.SafeDev` (50개 hard-deny 패턴), `WhitelistRule`, `Whitelists.TrustedLocal` (13개 안전 검사 명령), `PassThroughPolicyEngine`, `ActionRequestPolicyMapper` (Bash/Read/Write/Edit/MultiEdit/WebFetch/WebSearch → ProposedAction) |
| Core / Redaction | `RedactionRule` (regex → placeholder), `RegexRedactionEngine` (14개 기본 룰: env 토큰 5종, 형태 기반 secret 4종, Bearer/JWT 2종, PRIVATE KEY 블록, home 경로 2종) |
| Agents.Claude | `StreamJsonParser` 확장 — `tool_use.input` JSON 추출 + `Clone()` (parent doc 해제 안전) |
| Workflows | `WorkflowContext` 시그니처 확장 (+ IPolicyEngine, PolicyContext), `WorkflowEngine` ctor 옵션 인자, `FixDotnetTestsWorkflow` PolicyRouting (Denied/AutoApprove/Queue) 3-state 분기, `ActionRequestEvent.Input` (`JsonElement?`) 추가 |
| App.Wpf | `MainWindow` `WorkflowEngine` 생성에 실제 `PolicyEngine(SafeDev) + WorkspaceRoot=CurrentDirectory` 주입 |
| Tests | `PolicyEngineTests` (~64), `ActionRequestPolicyMapperTests` (11), `RegexRedactionEngineTests` (22), `FixDotnetTestsWorkflowPolicyTests` (5), `RecordingApprovalGateway` |

총 자동 테스트: **326개** (Day 43 기준 211개 + 신규 115개 / 회귀 0).

## 2. 잘 된 것

### 2.1 Option B 채택을 명시적으로 결정

ADR-014 entry decision 시점에 advisor가 "engine만 만들고 통합 안 하면 사용자는 보호되지 않는다"고 지적해 Option B (engine + 매퍼 + workflow 통합) 를 명시적으로 채택했다. 결과적으로 `rm -rf /` 같은 패턴이 실제 Claude stream-json 흐름에서 차단된다. ADR-014 본문에 "Option B 채택" 한 단락을 추가해 미래의 본인이 헛갈리지 않도록 명시.

### 2.2 `ProposedAction` DU + ActionRequestPolicyMapper 분리

`ActionRequestEvent`(에이전트 측 raw event)와 `ProposedAction`(정책이 이해하는 구조화 타입)을 분리하고, 이 사이를 `ActionRequestPolicyMapper`가 best-effort 매핑한다. 
결과: 정책 엔진은 stream-json 포맷을 모르고 (`Bash` → `ExecuteCommand` 만 알고), 매퍼는 정책 룰을 모른다 (`tool_use.input.command` 추출만). 두 레이어가 독립적으로 진화한다.

### 2.3 50-rule 블랙리스트 100% 매칭 검증

xUnit `[Theory]` + 50 `[InlineData]`로 모든 룰이 적어도 하나의 입력에 매칭됨을 강제했다. 추가로 `Blacklists.SafeDev.Count == 50` 단언 + 카테고리 cross-level 검증 (블랙리스트는 ReadOnly/SafeDev/TrustedLocal 모두에서 deny). Day 51 한 번에 0 false negative 달성.

### 2.4 Defense-in-depth: blacklist > whitelist > level

`PolicyEngine.EvaluateExecute` 평가 순서를 blacklist → whitelist → level 로 두고, 화이트리스트에 `^rm` 같은 위험 패턴이 있어도 `rm -rf /`는 블랙리스트가 먼저 잡는 것을 단위 테스트로 명문화. "사용자가 화이트리스트에 실수로 위험 패턴을 넣어도 안전" 보장.

### 2.5 `JsonElement.Clone()` 메모리 안전성

`StreamJsonParser`는 `using var doc = JsonDocument.Parse(...)`로 doc을 즉시 해제한다. `tool_use.input`을 `JsonElement?`로 보존하려면 parent doc 라이프타임에서 떼어내야 한다. `inp.Clone()` 한 줄로 해결. 이 함정을 작성 시점에 알고 있었기에 디버깅 0회.

### 2.6 14개 기본 redaction 룰 — DESIGN.md §9.3 100% 커버

`OPENAI_API_KEY`/`ANTHROPIC_API_KEY`/`GITHUB_TOKEN`/`AZURE_*`/`AWS_*` env 토큰 5종 + 형태 기반 (`sk-...`, `sk-ant-...`, `ghp_...`, `AKIA...`) 4종 + Bearer/JWT 2종 + PRIVATE KEY 블록 + 절대 home 경로 2종 = 14개. DESIGN.md §9.3에 나열된 모든 카테고리를 1개 이상의 룰로 커버.

## 3. 잘 안 된 것

### 3.1 `ActionRequestEvent.Input` 후방 호환성 — 수동 점검 필요

기존 `ActionRequestEvent` 인스턴스 생성 시점에는 `Input = null` 기본값이 자동 적용된다. 그러나 `tool_use.input` 추출 후 mapper가 nullish input을 어떻게 처리하는지는 매퍼별로 다르다 (`MapBash`는 null → null 리턴). 다음에 새로운 매퍼를 추가할 때 nullable 처리를 빠뜨릴 위험이 있다.
행동: MVP-8 또는 별도 슬롯에서 `ActionRequestPolicyMapper`에 nullable 처리를 강제하는 헬퍼 메서드 도입.

### 3.2 `ApprovalDialog` UI에 Risk / Reason 미표시

`PolicyDecision.Reason`과 `Risk`는 결정 단계에는 채워지지만, 사용자에게 보여줄 ApprovalDialog는 여전히 "Approve All / Deny All" 버튼만 있고 각 액션의 Risk/Reason을 표시하지 않는다.
행동: UX 이터레이션 슬롯에서 ApprovalDialog에 risk badge + reason tooltip 추가.

### 3.3 `RequireIndividualApproval` 미적용

`PolicyDecision.RequireIndividualApproval = true`인 액션 (e.g. `rm -rf` Critical) 이 들어와도 워크플로는 batch approval 흐름에 그대로 합친다. 즉, "개별 재확인" 의도가 UI 흐름까지 반영되지 않는다.
행동: ApprovalDialog 개선과 함께 RequireIndividualApproval 액션은 별도 모달로 분리.

### 3.4 `IRedactionEngine` 미통합 — 엔진은 있지만 호출처 없음

`RegexRedactionEngine`이 만들어졌고 단위 테스트 22개가 통과하지만, 실제 `TranscriptSink.AppendAsync` / `AgentTraceViewModel.Append` / `AgentMessageEvent` 표시 어디에도 redact 호출이 없다. 즉 엔진은 ready지만 wire-up 0회.
행동: MVP-8 또는 별도 슬롯에서 `TranscriptSink` 생성 시 `IRedactionEngine` 주입 + JSONL append 직전 redact.

### 3.5 `WhitelistRule` — 정규식 작성 부담

`Whitelists.TrustedLocal` 13개 룰 모두 regex이고, 사용자가 자기 환경에 맞춰 룰을 추가하려면 정규식을 알아야 한다. `git status -sb` 같은 변형은 룰 1개로 못 잡는 경우도 있다.
행동: 향후 yaml-기반 사용자 설정 파일 추가 시 prefix-match / glob-match 옵션 지원.

### 3.6 `WorkflowContext` 시그니처 비대화

`WorkflowContext`가 `(ExecutionId, Trigger, AgentAdapter, ApprovalGateway, PolicyEngine, PolicyContext, CancellationToken)` 7개 필드로 비대해졌다. MVP-8에서 redaction을 추가하면 8개. 향후 DI 컨테이너 도입을 검토할 시점.
행동: MVP-9 진입 시 `IServiceProvider` 또는 명시적 `WorkflowDependencies` 묶음 record 도입 검토.

## 4. ADR 회고

| ADR | 결정 | 결과 (Day 52) | 유지/수정 |
|---|---|---|---|
| ADR-014 | Day 44–52 MVP-7 계획 | Day 52에 계획대로 완주 | **완료** |
| ADR-014 (Option B 추가) | engine + mapper + workflow 통합 | 실제 stream-json 흐름에서 `rm -rf /` 차단 | **유지** |
| `IPolicyEngine.EvaluateAsync` (DESIGN.md sketch) | `ValueTask<PolicyDecision>` per-action | 326개 테스트 통과, 회귀 0 | **유지** |
| Blacklist > Whitelist > Level 평가 순서 | `PolicyEngine.EvaluateExecute` 명시 | defense-in-depth 단위 테스트로 명문화 | **유지** |
| `IRedactionEngine` Abstractions 위치 | Core 의존 없는 인터페이스 | 22개 단위 테스트, wire-up은 MVP-8 이관 | **유지** |

## 5. MVP-7 완료 기준 자체 평가

| 기준 | 상태 |
|---|---|
| `safeDev` 블랙리스트 50개 테스트셋 100% catch | ✓ `[Theory]` 50 `[InlineData]` 전수 통과 + `Count == 50` 단언 |
| `readOnly` 레벨에서 write_file / edit 액션 전면 거부 | ✓ `WriteFile_ReadOnly_Deny` + level 매트릭스 |
| `IRedactionEngine` — 절대 경로·환경변수 토큰 마스킹 테스트 20개+ | ✓ 22개 redaction 테스트 (env 5 + literal 4 + Bearer/JWT 2 + PRIVATE KEY 2 + path 2 + edge 7) |
| 기존 자동 테스트 211개 회귀 0 | ✓ 326 pass / 2 skip (quarantine) / 0 fail |

**완료**: 4개 기준 전부 충족.

## 6. Open questions (MVP-8로 이관)

- `IRedactionEngine` wire-up — `TranscriptSink` 진입점? agent message UI 표시? 둘 다?
- `RequireIndividualApproval` UI 분리 — 별도 모달? 기존 ApprovalDialog 안에서 강조?
- `ApprovalDialog` Risk/Reason 표시 — badge 색상 매핑 (Low=회색, High=주황, Critical=빨강)?
- 사용자 정의 yaml policy 파일 — 어디에 두나? `~/.agentworkspace/policies.yaml`?
- 성능 측정 — 50-rule blacklist 평가가 hot path에서 얼마나 비싼가? (regex compile 1회는 ok지만 50회 IsMatch 누적 비용)

---

## 결론

MVP-7 Policy + Redaction은 Day 52에 ADR-014 계획 그대로 완료.
핵심 성과:
1. `IPolicyEngine` + `ProposedAction` DU + 50-rule SafeDev 블랙리스트 — 실제 워크플로에서 위험 명령 차단.
2. `IRedactionEngine` + 14개 기본 redaction 룰 — DESIGN.md §9.3 100% 커버.
3. `ActionRequestPolicyMapper` + `FixDotnetTestsWorkflow` Day 50 통합 — Option B 채택 (engine 단독이 아닌 실제 보호 흐름까지).
4. 326개 자동 테스트 (회귀 0, 211 → 326 +115).

**MVP-8 Performance Hardening 진입을 권고**. 자세한 진입 plan은 DESIGN.md ADR-015.
