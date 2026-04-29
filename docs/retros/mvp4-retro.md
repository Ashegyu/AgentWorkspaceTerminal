# Retro — MVP-4 (Day 20–25)

작성일: 2026-04-30
대상: DESIGN.md §ADR-011 Day 20–25에 해당하는 작업.

## 1. 무엇을 만들었나

6일치 작업의 결과물:

| Layer | Artefact |
|---|---|
| Schema | `schemas/workspace.schema.json` — YAML workspace template 검증 스키마 |
| Abstractions | `WorkspaceTemplate`, `PaneTemplate`, `LayoutNodeTemplate`, `PaneRefTemplate`, `SplitNodeTemplate` 도메인 모델 |
| Loader | `YamlTemplateLoader.LoadAsync` — YAML → `WorkspaceTemplate` 파싱 (YamlDotNet) |
| Runner | `TemplateRunner.RunAsync` — template → pane spawn 시퀀스, `SlotToPaneId` 매핑 반환 |
| Serializer | `WorkspaceTemplateSerializer.Serialize/SaveAsync` — `WorkspaceTemplate` → YAML 직렬화 |
| App | `Workspace.SaveSnapshotAsync` — DFS pane 수집 → slotMap 자동 할당 → YAML 저장 |
| Command Palette | "Open Template…" + "Save Snapshot…" — WPF `OpenFileDialog`/`SaveFileDialog` 통합 |
| Tests | `TemplateRoundtripTests` 8개 + `WorkspaceSnapshotTests` 6개 → **168 자동 / 2 quarantine** |

git diff 70bfd5a e76b492 기준: 22 files changed, +1847 / -52 LOC.

## 2. 잘 된 것

### 2.1 Schema-first → 구현 순서가 명확

`workspace.schema.json` 먼저 정의한 덕에 YAML 필드 이름, 타입, 필수 여부가 코드보다 앞에 확정됐다.
`YamlTemplateLoader` → `TemplateRunner` → `WorkspaceTemplateSerializer` 순서의 의존성 방향이
스키마 문서에서 바로 읽혔다. 불필요한 설계 논의 없이 구현 진입.

### 2.2 `WorkspaceTemplateSerializer` — OmitNull으로 최소한의 YAML 출력

`DefaultValuesHandling.OmitNull` 설정 하나로 null 필드 전부 제외. 저장된 YAML이 사람이 읽기
편한 최소 형식으로 유지됐다. 이후 사용자가 파일을 수동 편집하거나 VCS diff를 볼 때 노이즈 없음.

### 2.3 `TemplateRunner` → `TemplateRunResult` 값 반환

runner가 layout + `SlotToPaneId` 딕셔너리를 함께 반환해, 호출자가 "어느 슬롯이 어느 pane"을 
즉시 알 수 있게 됐다. 이 설계가 나중에 `MainWindow.RestoreFromTemplateAsync`에서
focus 설정까지 한 번에 처리할 수 있는 토대가 됐다.

### 2.4 Roundtrip 테스트가 두 레이어에서 각각 검증

- `TemplateRoundtripTests` (TestData/*.yaml → runner): 로더+러너 파이프라인 검증
- `WorkspaceSnapshotTests` (live Workspace → SaveSnapshotAsync → YamlTemplateLoader): end-to-end 검증

두 레이어가 독립 테스트 클래스에 분리되어 실패 지점이 명확하다.

### 2.5 `FakeChannel : IControlChannel, IDataChannel` 패턴

`PaneSession` 생성자가 두 인터페이스를 모두 요구하는 상황에서 단일 Fake로 두 인터페이스를
동시에 구현하는 방식은 테스트 보일러플레이트를 최소화하면서 실제 파이프를 열지 않는다.
`WorkspaceLifecycleTests`와 `WorkspaceSnapshotTests`가 동일 패턴을 공유.

### 2.6 ADR-011의 Day-by-day 계획을 Day 25에 완주

원래 Day 26 완료 예상. Day 25에 모든 MVP-4 기능이 완성되어 Day 26가 오롯이 레트로/ADR 작업에
집중할 수 있게 됐다.

## 3. 잘 안 된 것

### 3.1 `PaneSession.LastStartOptions` null 처리 묵시적

`SaveSnapshotAsync`에서 세션이 아직 시작되지 않은 pane에 대해 `LastStartOptions`가 null이면
`"cmd"` 기본값으로 fallback한다. 이 동작이 테스트에서는 커버되지 않고 코드 주석도 없다.
행동: MVP-5 또는 MVP-8에서 `PaneSession`이 미시작 상태임을 알리는 명시적 상태 플래그 추가 검토.

### 3.2 `YamlTemplateLoader` 에러 메시지가 약함

invalid YAML이나 스키마 위반 시 `YamlDotNet` 기본 exception이 그대로 노출된다. 사용자가
터미널 로그에서 원인을 파악하기 어렵다.
행동: MVP-6 이전에 `WorkspaceTemplateException` 래퍼로 감싸고 파일명+라인 포함 메시지 제공.

### 3.3 `TemplateRunner`가 pane spawn 실패 시 부분 롤백 없음

첫 번째 pane이 시작된 후 두 번째 pane spawn이 실패하면 첫 번째 pane이 고아 상태로 남는다.
현재는 FakeChannel이라 테스트에서 발생하지 않지만, 실제 Daemon 연결 시 문제가 될 수 있다.
행동: `TemplateRunner`에 all-or-nothing 트랜잭션 패턴 추가 (MVP-6 또는 MVP-8).

### 3.4 Command Palette에서 "Open Template" 후 layout 애니메이션 없음

WPF `BinaryLayoutManager` 교체 시 기존 pane이 사라지고 새 pane이 나타나는 동작이 즉각적이다.
시각적 피드백 없이 레이아웃이 전환되어 사용자 혼란 가능.
행동: MVP-5 UI 작업 시점에 fade / slide 트랜지션 검토.

## 4. ADR 회고

| ADR | 결정 | 결과 (Day 25) | 유지/수정 |
|---|---|---|---|
| ADR-011 | Day 20–26 MVP-4 계획 | Day 25에 완주 (1일 절약) | **완료** |
| ADR-003 개정 | `AWT2` 바이너리 프레임 (gRPC 폐기) | MVP-4에서 Client 레이어 재사용 시 문제 없음 | **유지** |

## 5. MVP-4 완료 기준 자체 평가

| 기준 | 상태 |
|---|---|
| `workspace.schema.json` validate 통과하는 YAML → pane 자동 시작 E2E 검증 | ✓ `TemplateRoundtripTests` 8개 |
| snapshot 저장 후 reload 시 동일 레이아웃 복구 (pane 수 + ratio 오차 ≤ 0.01) | ✓ `WorkspaceSnapshotTests` 6개 (ratio precision 10 자리) |
| 기존 자동 테스트 101개 회귀 0 | ✓ 168개 전부 통과 (67개 신규 추가) |
| "Open Template…" + "Save Snapshot…" Command Palette 통합 | ✓ MainWindow.xaml.cs Day 24+25 |

**MVP-4 완료 선언.**

## 6. Open questions (MVP-5로 이관)

- `ClaudeAdapter`: `claude --output-format stream-json` 출력 파싱 전략 — line-delimited JSON vs. length-prefixed?
- `AgentTrace` UI 컴포넌트: WPF `ItemsControl` + `ObservableCollection`? 아니면 WebView2 안에 React?
- transcript 저장 경로: `%LOCALAPPDATA%\AgentWorkspace\transcripts\{sessionId}\{timestamp}.jsonl`?
- cancel 동작: `CancellationToken` + SIGINT to ConPTY? 아니면 별도 RPC?
- `PaneSession.LastStartOptions` null 처리 — MVP-5 진입 전에 명시적 상태로 변경?

---

## 결론

MVP-4 Workspace Template은 Day 25에 예정보다 1일 일찍 완료. 핵심 성과:
1. schema-first 접근으로 구현 순서 혼선 없이 6일 완주.
2. `TemplateRunner` + `WorkspaceTemplateSerializer` 두 방향 파이프라인 완성 — snapshot/restore가 완전 roundtrip.
3. 168개 자동 테스트 (회귀 0, 기존 101 → 168 +67).

**MVP-5 진입을 권고**. 자세한 진입 plan은 [DESIGN.md ADR-012](../../DESIGN.md#adr-012).
