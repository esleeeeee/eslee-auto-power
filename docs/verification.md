# v1.0.4 검증 결과

검증일: 2026-07-29

## 변경 범위

- `shutdown.exe /s /soft` 호출을 Windows `InitiateShutdownW` 기반 구현으로 교체
- `SeShutdownPrivilege` 활성화 및 `ERROR_NOT_ALL_ASSIGNED` 명시적 검사
- 정상 종료 요청과 제한시간 후 fallback을 durable pending journal에 단계별 기록
- primary가 거절되더라도 fallback 예약과 pending 상태를 유지
- fallback까지 거절된 경우에만 최종 실패로 확정
- Win32 반환 코드, symbolic name, flags, reason, grace period, privilege 결과와 실행 문맥을 기술 로그에 기록

현재 설치본, `%ProgramData%` 운영 DB, 실제 Task Scheduler 예약에는 접근하거나 변경하지 않았다. 실제 종료·재부팅·절전·최대 절전 명령도 실행하지 않았다.

## 종료 경로 정적 검증

- 실행 코드에서 `shutdown.exe`, `/soft`, `Process.Start` 기반 종료 경로 제거 확인
- primary 요청:
  - API: `InitiateShutdownW`
  - grace period: 30초
  - flags: `SHUTDOWN_FORCE_OTHERS | SHUTDOWN_FORCE_SELF | SHUTDOWN_POWEROFF`
  - reason: `SHTDN_REASON_FLAG_PLANNED | SHTDN_REASON_MAJOR_APPLICATION | SHTDN_REASON_MINOR_MAINTENANCE`
- fallback 요청:
  - API: `InitiateShutdownW`
  - grace period: 0초
  - 기본 flags: primary와 동일
  - 예약된 종료가 이미 있을 때 `ERROR_SHUTDOWN_IS_SCHEDULED`를 확인하고 `SHUTDOWN_GRACE_OVERRIDE`로 한 번 재시도
- `ERROR_SHUTDOWN_IN_PROGRESS`와 이미 예약된 종료는 수락 또는 진행 중 증거로 처리
- 네이티브 DWORD 반환값을 프로세스 종료 코드나 HRESULT로 변환하지 않고 그대로 보존

## fake native API 자동 테스트

- primary 성공
- Win32 오류 `1`, `5`, `21`, `87`, `1115`, `1190`, `1191` 및 알 수 없는 코드
- raw native error code 보존
- privilege 활성화 성공
- `AdjustTokenPrivileges` 성공 반환 후 `ERROR_NOT_ALL_ASSIGNED`
- privilege API 자체 실패
- fallback grace override 재시도
- 종료 진행 중 및 예약됨 판정
- 로그에 사용자명, SID, 암호 등 민감정보가 포함되지 않음
- primary 거절 시 fallback 유지 및 `Compensating` 상태
- fallback 수락 시 reconciliation 대기
- fallback 거절 시에만 최종 `Failed`
- 실패한 pending operation이 `Completed`로 오표기되지 않음
- 기존 S3/S4, 빠른 전원 예약, durable reconciliation 회귀 테스트

## 빌드·테스트 결과

- 한국어 Release 빌드: 경고 0, 오류 0
- 영어 Release 빌드: 경고 0, 오류 0
- 한국어 MSTest: 115/115 통과
- 영어 MSTest: 115/115 통과
- NuGet 직접·전이 취약 패키지: 0건
- 게시본 FileVersion: `1.0.4.0`
- 게시본 ProductVersion: `1.0.4+396fce738c16796f45c197fb4d31aa63aaf02a53`
- 테스트는 fake native API와 격리된 임시 데이터 경로만 사용

## 로컬 설치 파일

- 한국어: `artifacts/installer/eslee-auto-power-v1.0.4-ko-setup.exe`
  - SHA-256: `B427604C61A5017693E45B69AC4BD53254FAE31F62CA7A1CE00EEB42DCDD40B6`
- 영어: `artifacts/installer/eslee-auto-power-v1.0.4-en-setup.exe`
  - SHA-256: `E8A943AA81E7E268A258DE235309458F8742D4B4C87D097FFC05BD17DE64EF89`

## GitHub 공식 릴리스 설치 파일

- GitHub Actions: `30450592460` 성공
- 정식 릴리스: `v1.0.4` (Draft 아님, Prerelease 아님)
- 한국어: `eslee-auto-power-v1.0.4-ko-setup.exe`
  - SHA-256: `C74310212AFF49F1C5398EF9A3D758E3EEEC00480D2B68B623EDDD9497547B34`
- 영어: `eslee-auto-power-v1.0.4-en-setup.exe`
  - SHA-256: `2D12EC865B6E40C7EB681CA3CEDE61F3291EB38337E16B226E0E287CFAA7219B`

공식 설치 파일을 다시 내려받아 계산한 SHA-256, 동봉된 `.sha256` 내용과 GitHub asset digest가 양쪽 언어 모두 일치함을 확인했다. 설치 파일의 FileVersion은 `1.0.4.0`, ProductVersion은 `1.0.4`다.

## 운영 환경 보호 확인

- 현재 설치본: 변경하지 않음
- 운영 DB: 읽기·쓰기 모두 수행하지 않음
- 운영 Task Scheduler 작업: 읽기·쓰기 모두 수행하지 않음
- 실제 전원 동작: 실행하지 않음
