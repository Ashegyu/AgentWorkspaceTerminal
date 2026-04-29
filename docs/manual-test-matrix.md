# Manual Test Matrix — MVP-1

자동화하기 어려운 영역(IME, font shaping, terminal cell rendering)을 사람 눈으로 검증하는 표.
각 항목은 DESIGN §1.2의 MVP-1 완료 기준에 1:1로 매핑된다.

> **언제 돌리나** — MVP-1을 닫을 때 + WPF UI 코드 또는 `web/terminal/*` 변경 시.
> **어떻게 기록하나** — 결과 칸을 PASS / FAIL / N/A 로 채우고, FAIL은 본 문서 하단 "Known Failures" 섹션에 추가.

## 0. 사전 준비

```pwsh
dotnet run --project src/AgentWorkspace.App.Wpf
```

기본 셸 검색 우선순위: `pwsh.exe` → `powershell.exe` → `cmd.exe`. 매트릭스를 셸별로 한 번씩 통과시키는 것이 이상적이지만, MVP-1에서는 **최소 `pwsh.exe`** 만 통과하면 된다.

## 1. 셸 호환성

| # | 검사 | 절차 | 결과 |
|---|---|---|---|
| 1.1 | pwsh 시작 | 앱 실행 후 `Get-Date` 입력 | |
| 1.2 | cmd 시작 | shell 검색이 cmd로 떨어지도록 PATH 조정 후 `dir` | |
| 1.3 | wsl 시작 (선택) | command line으로 `wsl.exe` 직접 지정 후 `uname` | |
| 1.4 | git-bash 시작 (선택) | command line으로 `bash.exe` 지정 후 `pwd` | |

## 2. 입력·표시: ASCII / 제어 키

| # | 검사 | 절차 | 결과 |
|---|---|---|---|
| 2.1 | ASCII 입력 | `echo hello`, 결과가 `hello` | |
| 2.2 | Backspace | `abc<BS><BS><BS>def` → `def` | |
| 2.3 | Tab 자동완성 | `cd Doc<Tab>` → `cd Documents` | |
| 2.4 | 화살표 키 | 이전 명령 호출(`Up`), 좌우 커서 이동 | |
| 2.5 | Ctrl+L | 화면이 지워짐 | |
| 2.6 | Ctrl+C | 5초 sleep 등을 띄우고 Ctrl+C 시 즉시 중단 | |
| 2.7 | Ctrl+Break | (선택) 동일하게 중단 | |

## 3. CJK · Unicode

| # | 검사 | 절차 | 결과 |
|---|---|---|---|
| 3.1 | 한글 출력 | `echo 한글출력테스트` → 글자 깨짐/누락 없음 | |
| 3.2 | 한글 width | 표 그리기(`-`+`├`+`├`)에서 한글 cell width 2 정상 | |
| 3.3 | 중국어 | `echo 中文测试` 깨짐 없음 | |
| 3.4 | 일본어 | `echo こんにちは世界` 깨짐 없음 | |
| 3.5 | emoji 1 (BMP) | `echo ✨` 정상 | |
| 3.6 | emoji 2 (4-byte UTF-8) | `echo 🎉🚀` 정상 | |
| 3.7 | combining mark | `é` (`é`) 정상 | |

## 4. IME (한글 MS-IME)

| # | 검사 | 절차 | 결과 |
|---|---|---|---|
| 4.1 | 조합 시작 | 한/영 키로 한글 모드 → "ㅎ" 입력 시 미완성 글자 표시 | |
| 4.2 | 조합 완료 | "ㅎ→하→한" 까지 조합 후 Enter, 완성된 "한" 만 셸에 전달 | |
| 4.3 | 조합 중 ESC | 조합 중 ESC → 조합 취소, 셸은 ESC 시퀀스 안 받음 | |
| 4.4 | 조합 중 화살표 | 조합 중 ←/→ 입력 → 조합 깨지지 않음 | |
| 4.5 | 조합 중 Backspace | 조합 중 BS → 조합 한 글자 지움, 셸 입력 줄은 변동 없음 | |

## 5. 렌더링·layout

| # | 검사 | 절차 | 결과 |
|---|---|---|---|
| 5.1 | 창 크기 변경 | 창을 천천히 늘리고 줄임. 글자 잘림/줄바꿈 깨짐 없음 | |
| 5.2 | resize stress | 창을 최대화 ↔ 최소 크기로 빠르게 10초간 토글. 깨짐 없음 | |
| 5.3 | 큰 출력 | `Get-ChildItem -Recurse C:\Windows` 같이 수만 줄 출력 후 멈춤 없음 | |
| 5.4 | scrollback | 위 5.3 직후 PgUp/마우스 휠로 위로 스크롤 | |
| 5.5 | URL 렌더 | `https://example.com` 출력 → 클릭 가능한 링크로 표시 | |

## 6. 라이프사이클

| # | 검사 | 절차 | 결과 |
|---|---|---|---|
| 6.1 | exit 정상 종료 | `exit` 입력 → "[process exited with code 0]" 표시 | |
| 6.2 | 창 닫기 | 셸 실행 중 창 X → 자식 프로세스 트리(예: pwsh + 자식) 모두 정리 (작업 관리자 확인) | |
| 6.3 | crash log 비어있음 | `bin/Debug/net10.0-windows/agentworkspace-crash.log` 없음 | |

---

## Known Failures

자동화 테스트가 quarantine한 항목:

- `EchoHello_OutputContainsExpectedString`: ConPTY가 짧은 자식 출력의 cell-grid diff를 emit하지 않는 환경 이슈. 화면 출력 여부는 본 매트릭스 §3.1로 대체 검증.
- `InteractiveSession_EchoesUserInputBack`: 같은 cell-grid emit 이슈. 본 매트릭스 §2.1 / §3.1로 대체 검증.

신규 매뉴얼 실패 발견 시 여기에 추가하고, 환경/재현경로/우회 방법을 함께 적는다.
