# 변경 이력 / Changelog

## Unreleased

- 시스템 트레이 최상위에 `1시간 후 완전 종료`, `2시간 후 완전 종료`를 추가하고 한 번의 메뉴 클릭으로 S5 완전 종료 예약 생성
- v1.0.3의 공용 빠른 시간 정책과 기존 `ScheduleCoordinator` 저장·검증·Task 등록·기록 경로를 재사용
- 처리 중 두 메뉴를 비활성화하고 동시 클릭을 gate로 차단하며, 성공·실패를 한국어/영어 비차단 트레이 알림과 진단 로그로 표시
- 실제 전원 API나 운영 Task Scheduler를 호출하지 않는 임시 SQLite·fake Helper 회귀 테스트 추가

## Unreleased - English summary

- Adds top-level `Shut down in 1 hour` and `Shut down in 2 hours` tray commands that create an S5 shutdown schedule with one menu click
- Reuses the v1.0.3 quick-time policy and the existing coordinator validation, SQLite, Task Scheduler registration, and history pipeline
- Gates concurrent clicks, temporarily disables both commands, and reports localized success or failure through non-blocking tray notifications and technical logs
- Adds isolated SQLite and fake-helper regression coverage without invoking real power APIs or production Task Scheduler entries

## 1.0.4 - 2026-07-29

- 문서화되지 않은 `shutdown.exe /soft` 완전 종료 경로를 제거하고 `InitiateShutdownW` 직접 Win32 호출로 전환
- SYSTEM Helper에서 `SeShutdownPrivilege`를 명시적으로 활성화하고 `ERROR_NOT_ALL_ASSIGNED`를 포함해 실제 보유·활성화 결과 검증
- 30초 grace와 `SHUTDOWN_POWEROFF | SHUTDOWN_FORCE_SELF | SHUTDOWN_FORCE_OTHERS`로 계획된 S5 완전 종료 요청, hybrid 종료 제외
- native DWORD, symbolic error, `FormatMessage`, flags, reason, 호출 시각·경과 시간과 실행 보안 컨텍스트 진단 추가
- primary 거부 시 최종 실패 처리와 fallback 삭제를 막고 `Compensating + FallbackPending` 상태로 유지
- fallback 접수는 실제 종료 증거를 기다리고 fallback까지 거부된 경우에만 최종 Failed 처리
- 실패한 전원 pending operation을 Completed가 아닌 `PendingOperationState.Failed`로 저장
- 실제 전원 동작 없는 fake native API와 임시 DB 테스트로 오류·privilege·fallback·기존 S3/S4 및 빠른 예약 회귀 검증

## 1.0.4 - English summary

- Replaces the undocumented `shutdown.exe /soft` path with direct `InitiateShutdownW`
- Explicitly enables and verifies `SeShutdownPrivilege`, including the `ERROR_NOT_ALL_ASSIGNED` case
- Requests planned S5 power-off with a 30-second grace period, forced self/other sessions, and no hybrid shutdown
- Preserves native DWORD errors with symbolic names, formatted messages, flags, reason, timing, and security-context diagnostics
- Keeps the forced fallback after a rejected primary request and finalizes failure only after fallback rejection
- Stores failed power-operation journals as `PendingOperationState.Failed`
- Adds fake-native and isolated-database coverage without invoking real power transitions

## 1.0.3 - 2026-07-23

- `완전 종료` 전용이던 빠른 예약을 `최대 절전`, `절전`까지 포함한 공통 `빠른 설정`으로 확장
- 세 전원 동작에서 `1시간 뒤`, `2시간 뒤` 버튼을 같은 위치와 디자인으로 표시하고 자동 시작에서는 숨김
- 버튼 문구에서 특정 동작명을 제거하고 선택 동작에 맞는 접근성 이름과 도움말을 동적으로 제공
- 클릭한 현재 로컬 시각을 기준으로 계산하고 초·밀리초를 0으로 정규화
- 자정·월말·연말·윤년 경계, 1·2시간 계산, 선택 ActionType 유지, 자동 시작 표시 제외를 자동 테스트로 검증
- 빠른 설정 뒤 수동 편집과 동작 변경 시 날짜·시간 유지 여부를 실제 WPF UI 자동화로 검증

## 1.0.3 - English summary

- Extends the quick schedule controls from full shutdown to hibernation and sleep
- Shows `In 1 hour` and `In 2 hours` for all three power transitions while keeping them hidden for scheduled wake
- Uses action-neutral button labels with dynamic action-specific accessibility names and help text
- Calculates from the local click time, normalizes seconds and milliseconds, and preserves manual edits and date/time when the action changes
- Adds policy and real WPF UI automation coverage for visibility, action preservation, and date boundaries

## 1.0.2 - 2026-07-23

- 예약 전원 호출 전에 `ExecutionStarted`, pending operation journal, `PendingPowerTransition` 상태를 하나의 SQLite 트랜잭션으로 먼저 저장
- 실행 중인 `power-*` 작업을 전원 호출 전에 소비하여 동일한 1회성 예약의 중복 실행 방지
- S3/S4 복귀 후 Helper가 Windows 전원 이벤트를 확인해 완료 기록을 남기고, Helper가 중단돼도 다음 앱 시작에서 복구
- 과거 전원 예약을 Task Scheduler 결과와 Kernel-Power·Power-Troubleshooter·User32 이벤트로 대조해 Completed, Failed, Missed 또는 ResultUnknown으로 정리
- 단순히 시각이 지났거나 작업 결과가 0이라는 이유만으로 성공 처리하지 않도록 변경
- `StartWhenAvailable=false`를 유지하고 일회성 Task EndBoundary를 2분 허용 범위로 축소해 놓친 전원 작업의 지연 실행 차단
- Resume 뒤 트레이에서 기존 창을 다시 열 때 DB·실행 기록을 즉시 새로고침하여 과거 ON 예약이 남아 보이던 문제 수정
- 실제 운영 DB 복사본과 2026-07-23 Windows 전원 이벤트를 이용해 01:00 S4 진입 및 06:35 복귀 복구 판정 검증

## 1.0.2 - English summary

- Persists `ExecutionStarted`, a pending-operation journal, and `PendingPowerTransition` atomically before invoking power APIs
- Consumes the one-time `power-*` task before the transition to prevent duplicate execution
- Finalizes S3/S4 after resume and recovers interrupted transitions on the next app start
- Reconciles Task Scheduler results with Windows power events instead of assuming that a past schedule succeeded
- Keeps `StartWhenAvailable=false` and limits one-time task validity to the existing two-minute tolerance
- Refreshes the tray-resident app after resume/activation so completed schedules no longer appear stale and enabled

## 1.0.1 - 2026-07-22

- S5 자동 부팅과 별개인 `완전 종료` 예약을 복구하고 기존 DB enum 값 호환 유지
- 전원 변경 예약을 완전 종료·최대 절전·절전 3종으로 제공
- 완전 종료 선택 시 클릭 시점 기준 `1시간 뒤 종료`, `2시간 뒤 종료` 빠른 버튼 추가
- 새 예약 기본 날짜·시간을 다음 날 16시에서 화면을 연 현재 로컬 시각으로 변경
- 완전 종료 5분 전 경고, 정상 종료 요청, 30초 강제 종료 fallback 추가
- 이후 자동 시작 예약이 있으면 S5 상태에서 깨울 수 없음을 저장·실행 전에 안내하고, 다음 Wake에 맞는 S3/S4 전환 선택 제공
- 호환성 화면의 기존 S3/S4 UI를 유지하면서 앱 관리형 1회 자동 로그인 검증 상태 표시
- 한국어·영어 UI 및 self-contained 설치 파일 갱신

## 1.0.1 - English summary

- Restored scheduled full shutdown while keeping app-controlled S5 startup disabled
- Added one-hour and two-hour full-shutdown shortcuts based on click time
- New schedules now start at the current local date and time
- Added a five-minute warning, graceful shutdown request, and forced fallback after 30 seconds
- Warns when full shutdown would prevent a later S3/S4 scheduled wake
- Reports one-time sign-in validation in Compatibility without adding S5 wake UI

## 1.0.0 - 2026-07-17

- S3 절전 및 S4 최대 절전 기반 예약 깨우기 정식 제공
- 자동 선택·S3·S4를 하나의 자동 시작 예약 편집 흐름으로 통합
- 메인 화면에서 즉시 S3/S4 전환 및 일치 예약 준비 지원
- 2분 뒤 S3/S4 WakeToRun 실기 테스트와 결과 기록
- 앱 관리형 S3/S4 1회 로그인, 자격 증명 실제 검증, 설정 원복 journal
- Resume 준비 시점 T0 기준 후속 프로그램 실행
- 신뢰한 후속 프로그램의 사전 승인 관리자 권한 실행
- 반응형 호환성 표, 고급 옵션 스크롤, DPI 레이아웃 보완
- 앱 아이콘, 정보 창, 자동으로 사라지는 정상 복귀 알림
- 한국어·영어 UI 및 언어별 self-contained 설치 파일
- Windows 앱 제어형 S5 완전 종료 자동 부팅을 제품 경로에서 제거

## 1.0.0 - English summary

- Scheduled wake from S3 sleep and S4 hibernation
- Unified wake-mode selection and matching power-state controls
- Two-minute real WakeToRun compatibility tests
- App-managed one-time sign-in with credential validation and journaled restoration
- Resume-ready T0 follow-up programs, including pre-approved elevated launches
- Korean and English self-contained installers
- App-controlled S5 startup intentionally removed from the product path
