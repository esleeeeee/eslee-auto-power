# 아키텍처

## 프로세스

- `AutoPower.App.exe`: 일반 사용자 권한 WPF UI, 트레이, 경고, 예약/기록/설정 화면
- `AutoPower.Helper.exe`: `requireAdministrator`; 명시된 명령만 처리하는 Task Scheduler·전원·LSA·복구 Helper
- `AutoPower.Agent.exe`: 사용자 로그인 세션에서 바탕화면 준비 판정과 후속 프로그램 실행

Windows Service는 사용하지 않습니다.

## 예약 저장 순서

1. 도메인 검증
2. `PendingOperations`에 journal 저장
3. SQLite 예약 저장
4. UAC Helper가 앱 전용 Task Scheduler 작업 등록
5. 성공 journal 완료
6. 실패 시 새 DB 예약 삭제 또는 이전 DB 예약 복원

Helper의 Task 등록은 예약별 기존 앱 작업을 먼저 제거한 뒤 새 작업을 만들며, 중간 오류 시 해당 예약의 앱 작업을 다시 제거합니다. 다음 로그인 시 SYSTEM `startup-reconcile` 작업이 DB의 미래 예약을 기준으로 작업을 재구성하고 앱 소유 고아 작업을 삭제합니다.

## S5 Wake와 S5 Shutdown의 경계

- 앱 기반 자동 시작은 S3 절전과 S4 최대 절전 Wake만 제공한다.
- 기존 `PowerOn=0` 값은 DB 호환을 위해 보존하지만 화면에서 숨기고 비활성화한다. S5 자동 부팅 provider는 자동 시작 경로에 연결하지 않는다.
- `Shutdown=1`은 예약 시각에 PC를 S5로 완전히 끄는 별도 동작이며 정상 지원한다. S5에서 다시 켜는 기능과 혼동하지 않는다.
- 업그레이드 첫 실행의 이전 기능 정리는 `PowerOn` 작업과 이전 임시 보안 상태만 대상으로 하며, Shutdown DB 값과 예약은 보존한다.

## Wake

- `PowerActionType.WakeFromSleep`과 `WakeFromHibernate`를 DB 값 4와 5로 분리. 기존 0~3 값은 이전 DB 호환을 위해 고정
- UI에서는 S3/S4를 하나의 `자동 시작 예약`으로 제공하고 `자동 선택(추천) / 절전(S3) / 최대 절전(S4)`을 고른다. 저장 시 자동 선택을 검증된 실제 테스트 결과와 현재 전원 capability로 구체적인 Wake 타입에 해석하며, 실기 실패 경로는 자동 후보에서 제외한다. 기존 DB enum과 작업 등록 구조는 유지된다.
- S3/S4: SYSTEM `WakeToRun` 작업이 PC를 깨우고 사용자 세션 Agent가 Resume 준비를 확인
- 예약 저장 직후 사용자가 동의하거나 메인 화면에서 같은 전원 상태의 버튼을 누르면 Helper가 작업 등록을 재검증하고 실제 S3/S4 상태로 진입
- 메인 화면의 `지금 S3 절전`, `지금 S4 최대 절전`은 가까운 일치 예약이 있으면 해당 Wake 작업을 다시 확인한 뒤 진입하고, 예약이 없으면 확인 후 즉시 해당 상태로 전환한다.
- S3/S4 실기 테스트: 전용 임시 Wake 예약 → Helper가 실제 절전/최대 절전 진입 → 예약 시각 근처 재개 판정 → 사용자 무개입 확인 → capability와 실행 기록 저장
- S4가 Windows에서 비활성화된 경우 앱은 설정을 변경하지 않고 `powercfg /hibernate on` 수동 활성화 안내만 제공
- 버튼에서 선택한 전원 상태와 다음 예약의 Wake 상태가 다르면 경고하며, 사용자가 명시적으로 선택하기 전에는 전환하지 않는다.

## 예약 전원 동작

- `Shutdown`, `Hibernate`, `Sleep`을 각각 완전 종료·최대 절전·절전으로 제공한다.
- 새 예약의 최초 시각은 편집기를 연 현재 로컬 시각을 분 단위로 정규화한 값이다. 기존 예약 수정에서는 저장 시각을 그대로 사용한다.
- Shutdown, Hibernate, Sleep 선택에서는 공통 1시간·2시간 빠른 설정을 제공하고 자동 시작에서는 숨긴다. 클릭 시점의 로컬 시각에서 계산해 기존 날짜·시간 입력만 갱신하므로 동작 종류와 기존 저장 경로는 바뀌지 않는다.
- 세 동작 모두 5분 전 사용자 세션 경고와 예약 시각 SYSTEM `execute-power` 작업을 등록한다.
- `execute-power`는 전원 API를 호출하기 전에 한 SQLite 트랜잭션으로 `ExecutionStarted`, `PendingOperations.PowerTransition`, 예약의 `PendingPowerTransition/비활성` 상태를 먼저 기록한다.
- durable 기록이 commit된 뒤 현재 `power-{scheduleId}` Task를 비활성화한다. 동일 Task가 다시 호출돼도 DB 조건부 전이가 실패하므로 같은 1회성 예약을 두 번 실행할 수 없다.
- S3/S4에서는 복귀 뒤 Helper가 Kernel-Power 187/42와 Power-Troubleshooter 1을 확인한 후에만 Completed로 확정한다. Helper가 중단되면 다음 앱 시작 또는 SYSTEM reconciliation이 journal, Task 결과, Windows 이벤트를 다시 대조한다.
- 과거 Pending/PendingPowerTransition 예약은 증거에 따라 Completed, Failed, Missed, ResultUnknown 중 하나로 정리한다. 과거라는 사실이나 Task 결과 0만으로 성공 처리하지 않는다.
- `StartWhenAvailable=false`, `MultipleInstances=IgnoreNew`, 예약 시각 + 2분 EndBoundary를 함께 적용해 놓친 전원 Task가 나중에 실행되지 않게 한다.
- Shutdown은 `SeShutdownPrivilege`를 명시적으로 활성화·검증한 뒤 `InitiateShutdownW`를 호출한다. primary는 `SHUTDOWN_POWEROFF | SHUTDOWN_FORCE_SELF | SHUTDOWN_FORCE_OTHERS`, 30초 grace, `Application Maintenance (Planned)` reason을 사용하고 `SHUTDOWN_HYBRID`는 사용하지 않는다. 동시에 30초 뒤 `force-shutdown` SYSTEM fallback을 등록한다. primary 거부 시 schedule은 `PendingPowerTransition`, journal은 `Compensating`으로 남겨 fallback을 보존한다. fallback은 grace 0으로 즉시 요청하고 이미 예약된 종료에만 `SHUTDOWN_GRACE_OVERRIDE`를 제한적으로 재시도한다. fallback까지 거부된 경우에만 Failed이며, 접수된 종료의 완료 여부는 다음 부팅에서 User32 1074와 종료/부팅 이벤트로 확정한다.
- Shutdown 뒤에 활성 Wake 예약이 있으면 저장 단계와 5분 전 경고에서 S5 상태로는 다시 켤 수 없음을 알린다. 사용자는 다음 Wake가 요구하는 S3/S4 상태로 전환하거나 완전 종료를 명시적으로 계속할 수 있다.

## S3/S4 앱 관리형 1회 자동 로그인

1. 설정에서 Windows 사용자 이름과 암호를 입력하면 사용자 Credential Manager 사본과 관리자 Helper의 앱 전용 LSA 보호 사본을 함께 갱신한다.
2. `저장된 로그인 테스트`는 LSA 보호 사본을 읽고 `LogonUserW`로 실제 로그온 자격 증명을 재검증한다. PIN은 저장하거나 사용하지 않는다.
3. 예약의 `OneTimeAutoLogonEnabled`를 사용자가 명시적으로 선택하면 준비 직전에 저장 자격 증명을 다시 검증한다.
4. 관리자 Helper가 현재 전원 구성표 GUID와 `CONSOLELOCK`의 AC/DC 값을 journal에 먼저 저장한다.
5. 다음 복귀 1회에 한해 AC/DC의 절전 해제 로그인 요구를 0으로 적용하고 read-back으로 검증한다.
6. S3/S4에 진입한다. 예약 시각의 SYSTEM Wake 작업과 원래 Helper의 재개 경로가 모두 복원을 시도할 수 있으며, 전역 mutex와 멱등 처리로 한 번만 완료한다.
7. 복귀 즉시 원래 AC/DC 값을 되돌리고 read-back으로 검증한 뒤 성공 기록을 남긴다.
8. Helper가 중단되면 다음 Wake 작업, 앱 시작, 로그인 무결성 검사 또는 제거 과정에서 journal을 이용해 복구한다.

이 기능은 S3/S4에서 기존 사용자 세션의 잠금 화면을 한 번 건너뛰기 위한 기능이다. 부팅용 Winlogon `AutoAdminLogon`은 사용하지 않는다. Windows 암호는 보호 저장소에만 보관하고 PIN은 저장하지 않는다. 예약 전에 사용자가 PC를 일찍 깨우면 그 복귀에도 잠금이 적용되지 않을 수 있음을 UI에서 경고한다.

## 후속 프로그램

- S3/S4 예약 깨우기에만 종속
- Explorer shell과 현재 프로세스가 같은 활성 콘솔 사용자 세션에 있으면 잠금 화면 여부와 관계없이 Resume 준비 완료 T0
- `T0 + N분`, N은 0 이상
- 동일 N은 병렬 처리
- 실행 파일 전체 경로로 이미 실행 중인지 비교
- 프로그램 하나의 실패가 다른 프로그램을 중단하지 않음
- 자동 재시도 없음; 실행 시도 journal로 중복 방지
