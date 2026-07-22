# v1.0.2 검증 결과

검증일: 2026-07-23

## 실제 장애 증거

- 운영 설치본: v1.0.0, 실행 파일 커밋 `04a4f5e`
- 예약 ID: `829318b2-cc26-4cc5-b583-474815c20ea3`
- DB: 2026-07-23 01:00 최대 절전, `PowerAction` 시작 기록 존재, 당시 성공·실패 확정 기록 없음
- 기술 로그: 2026-07-22 22:50:41 예약 Task 등록 확인; 00:55~01:10 별도 Helper 오류 없음
- Task Scheduler Operational 채널: 비활성 상태라 과거 `power-*` 실행 이벤트 없음
- 실행 뒤 `power-*` Task: 이미 없어 XML, LastRunTime, LastTaskResult, NextRunTime 사후 조회 불가
- 남은 경고 Task: LastRunTime 2026-07-23 00:55, LastTaskResult `0x00000000`, NextRunTime 없음
- Windows Kernel-Power 187: 01:00:00 `AutoPower.Helper.exe`가 SetSuspendState 호출
- Windows Kernel-Power 42: 01:00:03 `TargetState=5`, `EffectiveState=5`, Application API 사유로 S4 진입
- Windows Power-Troubleshooter 1: SleepTime 01:00:00, WakeTime 06:35:17, TargetState/EffectiveState 5
- 앱 프로세스 시작 시각: 전날 20:09. S4 동안 종료되지 않고 살아 있어 복귀 뒤 기존 트레이 창이 오래된 화면 컬렉션을 계속 표시함

원본 `%ProgramData%` DB, 현재 설치 파일과 실제 Task는 읽기 전용으로 조사했다. 복구 스모크 테스트는 운영 DB의 별도 복사본과 격리된 `ESLEE_AUTOPOWER_DATA_ROOT`에서만 수행했다.

## 수정 검증

- `ExecutionStarted`, power-transition pending journal, `PendingPowerTransition`을 단일 SQLite 트랜잭션으로 저장
- durable commit 뒤 `power-*` Task 소비, 이후에만 Sleep/Hibernate/Shutdown 호출
- 정상 S3/S4 복귀 시 Windows 전원 이벤트 확인 후 `PowerTransitionCompleted`
- Helper 중단 시 앱 시작/활성화 reconciliation으로 Completed, Failed, Missed, ResultUnknown 확정
- legacy v1.0.0처럼 선행 Completed 처리된 예약도 실제 이벤트가 있어야 `PowerTransitionRecovered` 성공 기록 생성
- `StartWhenAvailable=false`, `MultipleInstances=IgnoreNew`, EndBoundary=예약 시각+2분 확인
- Resume 뒤 트레이 창 재활성화 시 DB·실행 기록 새로고침

운영 DB 복사본 스모크 결과:

- 01:00 Helper 호출 + S4 진입 + 06:35 복귀를 모두 대조
- ScheduleStatus `Completed`, IsEnabled `false` 유지
- `PowerTransitionRecovered / Success` 생성
- 원본 운영 DB에는 새 기록이나 상태 변경이 생기지 않음

## 자동화 결과

- 한국어 Release 빌드: 경고 0, 오류 0
- 영어 Release 빌드: 경고 0, 오류 0
- 한국어 MSTest: 84/84 통과
- 영어 MSTest: 84/84 통과
- NuGet 직접·전이 취약 패키지: 0건
- 게시본 ProductVersion/FileVersion: 1.0.2 / 1.0.2.0

추가된 주요 시나리오:

- 최대 절전 호출 직전 프로세스 중단
- 최대 절전 후 복귀
- 복귀 전에 앱이 종료되고 다음 앱 시작에서 복구
- 실행된 예약의 미래 목록 제거와 실행 기록 생성
- 과거 예약 지연 실행 차단
- Task 실행/결과 기록 누락 조합
- Task 자체 미실행
- 동일 예약 중복 실행 및 중복 terminal history 방지
- Windows 증거 접근 실패 시 상태를 임의로 덮어쓰지 않음

## 설치 파일

- 한국어: `artifacts/installer/eslee-auto-power-v1.0.2-ko-setup.exe`
  - SHA-256: `03CB75D71330BDD4110918455E0644512B9D2AAF8B3603AB99A3585DCBBDD9A0`
- 영어: `artifacts/installer/eslee-auto-power-v1.0.2-en-setup.exe`
  - SHA-256: `A49BE58CB6F95954BAF9A3485285FE129E540124C708D86FB5741F5A9499104F`

실제 PC를 다시 절전·최대 절전·완전 종료시키는 파괴적 통합 테스트와 현재 설치본 덮어쓰기는 수행하지 않았다. 실제 당일 S4 이벤트와 운영 DB 복사본으로 recovery 판정을 검증했다.
