# CLAUDE.md

이 저장소에서 작업할 때 참고할 지침입니다.

## 프로젝트 개요

`eslee Auto Power`는 Windows 11에서 완전 종료·최대 절전·절전을 예약하고, S3/S4 상태의 PC를 예약 시각에 깨워 후속 프로그램을 실행하는 앱입니다.
구조와 전원 전환 설계는 `docs/architecture.md`를 먼저 읽으세요. 문서보다 코드가 우선입니다.

| 프로젝트 | 역할 |
|---|---|
| `AutoPower.App` | WPF UI(일반 권한), 트레이, 5분 전 경고 창 |
| `AutoPower.Core` | 모델, 검증, 반복 규칙, 표시 문자열(`AppText`) |
| `AutoPower.Data` | SQLite 저장소(예약·기록·설정·저널) |
| `AutoPower.Windows` | Task Scheduler, 전원 API, 자격 증명, 복구 |
| `AutoPower.Helper` | `requireAdministrator` 도우미. 명시된 명령만 처리 |
| `AutoPower.Agent` | 사용자 세션에서 바탕화면 준비 확인과 후속 프로그램 실행 |

## 빌드와 테스트

```bash
dotnet build AutoPower.sln -c Release
```

```bash
dotnet test tests/AutoPower.Tests/AutoPower.Tests.csproj -c Release --no-build
```

- 타깃은 `net10.0-windows`(Core/Data는 `net10.0`)이고 `TreatWarningsAsErrors`가 켜져 있습니다.
- 테스트는 실제 Task Scheduler·레지스트리·전원 API를 건드리지 않습니다. 새 테스트도 가짜 객체와 임시 SQLite 파일만 쓰세요.
- SQLite 테스트에서 `SqliteConnection.ClearAllPools()`를 쓰지 마세요. 병렬로 도는 다른 테스트의 연결까지 닫습니다. `SqliteStore.ReleasePooledConnections()`나 `TestDatabase.ReleasePools(path)`를 쓰세요.
- 설치 파일: `scripts/Build-Release.ps1 -Version 2.0.0` → `artifacts/installer/eslee-auto-power-v<버전>-setup.exe` 하나(한국어/영어 공용). Inno Setup 6의 `Languages\Korean.isl`이 필요합니다. 스크립트는 ASCII로 유지하세요.

## 전원 전환·예약 규칙 (변경 금지)

- 1회성 전원 전환의 내구성 상태 기계(전원 API 호출 전 `ExecutionStarted` + `PendingOperations` + `PendingPowerTransition`을 한 트랜잭션으로 기록, 증거 기반 복구)를 약하게 만들지 마세요.
- 반복 예약은 1회성 예약 행의 사슬입니다. 한 시리즈(`SeriesId`)에는 살아 있는(Pending/Disabled) 행이 최대 하나입니다. 다음 회차 생성은 `SqliteStore.TryAdvanceSeriesAsync`의 단일 쓰기 트랜잭션으로만 하세요.
- 작업 등록은 관리자/SYSTEM 도우미 경로에서만 합니다. 일반 권한 앱의 복구(`canRepairSystem: false`)는 작업을 등록하거나 시리즈를 진행하지 않습니다.
- 설정은 SQLite `AppSettings`의 키 단위 값입니다(`ui-theme`, `ui-language` 등). 파일을 통째로 읽고-고치고-쓰는 설정 저장을 새로 만들지 마세요.

## UI 규칙

- 모든 창은 `Controls/ThemedWindow`를 상속합니다. 라이트/다크 팔레트는 `Themes/Light.xaml`, `Themes/Dark.xaml`이고, 컨트롤 스타일은 `Themes/Controls.xaml`입니다(eslee Folder Locker v2.0.0과 같은 토큰).
- 색은 팔레트 키를 `DynamicResource`로만 참조합니다. 창 XAML이나 코드에 색 값을 직접 쓰지 마세요.
- `System.Windows.MessageBox` 대신 `AppDialog` 또는 `LocalizedMessageBox`(테마가 적용된 `MessageDialog`를 씁니다)를 사용합니다.
- 블러·아크릴·큰 그림자·긴 애니메이션은 쓰지 않습니다. `App.OnStartup`의 `RenderMode.SoftwareOnly`는 모니터를 끈 원격 접속에서 흰 창을 막기 위한 것이므로 지우지 마세요.
- 토글 스위치는 `Click`이 아니라 `Checked`/`Unchecked`에서 저장합니다. 아이콘 버튼에는 `AutomationProperties.Name`을 붙이고, 목록 항목과 콤보 옵션은 `ToString()`이 읽을 수 있는 이름을 돌려주게 하세요.
- 메인 앱은 데이터 위치별 named mutex로 단일 인스턴스를 유지합니다. 경고 창(`warning --schedule`)은 예외입니다.

## 언어

- 한 빌드에서 한국어/영어를 고릅니다. 설정 키 `ui-language`(`system`/`ko`/`en`), 다시 시작하면 적용됩니다. App·Helper·Agent가 시작할 때 각각 적용합니다.
- 표시 문자열은 한국어 원문으로 쓰고 `AppText.T`/`AppText.F`로 번역합니다. 새 한국어 문구를 추가하면 `AppText.cs`의 `ExactTranslations`에 영어 쌍을 함께 넣으세요. XAML의 정적 문구는 `WpfLocalizer`가 번역합니다.
- 데이터에 연결된 `Run`이 들어 있는 `TextBlock`은 번역기가 건너뜁니다. 이 규칙을 깨면 영어 화면에서 값이 고정됩니다.

## eslee Tray Folder 연동

- 파이프 프로토콜 v1과 실행 파일 이름(`AutoPower.App.exe`)을 유지하세요. 기존 메뉴 액션 id(`quick-shutdown-1h`, `quick-shutdown-2h`, `next-schedule`, `open-app`, `new-schedule`, `pause-all`, `exit-app`)는 바꾸지 마세요.
- v2.0.0에서 추가한 id: `quick-{shutdown|hibernate|sleep}-{30m|1h|2h|3h}`(기존 두 개 제외), `resume-all`. 프로토콜 v1에는 하위 메뉴가 없어 Hosted 메뉴에서는 같은 단계에 나열합니다.

## 화면 확인과 테스트 인스턴스

- Debug 빌드의 `AutoPower.App.exe --ui-capture <폴더> [light|dark] [ko|en]`은 예시 데이터를 넣고 모든 창을 화면 밖에서 그려 PNG로 저장합니다. 반드시 `ESLEE_AUTOPOWER_DATA_ROOT`(임시 폴더)와 `ESLEE_AUTOPOWER_NO_STARTUP=1`을 지정하세요. 트레이·파이프·작업 스케줄러·도우미는 쓰지 않습니다.
- 테스트용 인스턴스로 예약을 만들지 마세요. Task Scheduler 폴더 `\eslee\AutoPower\`와 HKCU Run 값 `eslee Auto Power`는 실제 설치본과 공유되며, SYSTEM 정리 작업이 DB에 없는 작업을 지울 수 있습니다.

## 개인정보

- 커밋·문서·스크린샷에 개인 경로, 사용자 이름, SID, 이메일을 넣지 마세요. 테스트 값은 `person@example.com` 같은 예시만 씁니다.
- `docs/current-machine-verification.md`, `docs/s5-rtc-investigation.md`는 이 PC의 펌웨어 정보가 있어 gitignore 대상입니다.
