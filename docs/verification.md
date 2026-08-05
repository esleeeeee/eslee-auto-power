# v1.0.5 검증 결과

검증일: 2026-08-05

## 변경 범위

- 트레이 최상위 메뉴 `1시간 후 완전 종료`, `2시간 후 완전 종료`로 한 번의 메뉴 클릭 만에 완전 종료 예약 생성
- 한국어·영어 README를 일반 사용자 중심 구조로 전면 개편하고 실제 앱 화면 스크린샷(메인·새 예약·호환성) 추가
- 수정 창 복귀 후 선택된 예약 행이 읽을 수 없게 표시되던 DataGrid 선택 대비 문제 수정
- 예약 저장 성공 후 화면 새로고침만 실패한 경우 예약 생성 실패로 잘못 안내하던 트레이 알림 수정

현재 설치본, `%ProgramData%` 운영 DB, 실제 Task Scheduler 예약에는 접근하거나 변경하지 않았다. 실제 종료·재부팅·절전·최대 절전 명령도 실행하지 않았다. 수동 UI 검증과 README 스크린샷 캡처는 `ESLEE_AUTOPOWER_DATA_ROOT`로 격리한 별도 테스트 DB만 사용했다.

## 트레이 빠른 완전 종료 검증

- 트레이 컨트롤러는 기존 `ScheduleCoordinator.SaveAsync` 파이프라인(검증 → SQLite → Helper Task 등록 → 기록)만 사용하고 SQLite·Task Scheduler·전원 API를 직접 호출하지 않음
- Interlocked 실행 gate가 성공, 유효성 실패, 저장 예외, 새로고침 예외 모든 경로에서 finally로 해제됨
- 저장 성공 후 새로고침 실패 시: 재저장·Task 재등록 없음, 예약 생성 실패로 표시하지 않음, 예약 저장 성공과 새로고침 실패를 정확히 1회 알림, 예외는 `tray.quick-shutdown.refresh-failed`로 기술 로그에 기록
- 임시 SQLite DB + FakeHelper 기준: 새로고침 실패 주입 후 DB 예약 정확히 1건, Helper `register-schedule` 호출 정확히 1회, 이후 메뉴 재사용 가능(gate 고착 없음)

## 선택 행 스타일 검증

- `DataGridCell`의 투명 배경·테두리 setter가 WPF 기본 테마의 SystemColors 선택 트리거를 차단
- `DataGridRow` 트리거로 hover(#2A323D), 선택 활성(#2D5F9A), 선택 비활성(#3A4656) 상태를 명시하고, 비활성 MultiTrigger가 선택 트리거 뒤에 오도록 순서 고정
- 전경 `TextBrush`(#F3F6F9) 대비: 일반 행 14.75:1, 교대 행 13.69:1, hover 11.94:1, 선택 활성 6.02:1, 선택 비활성 8.84:1 — 모든 상태 WCAG 4.5:1 이상을 자동 테스트로 검증
- 실제 App.xaml 리소스를 로드해 브러시 존재, 트리거 구조와 순서, 셀 차단 setter, 대비 계산을 검사하는 테스트 4개 추가
- 사용자가 실제 UI에서 선택, 수정 창 복귀, 포커스 이동, OFF 행, 기록·호환성 탭을 직접 확인함

## 빌드·테스트 결과

- 한국어 Release 빌드: 경고 0, 오류 0
- 영어 Release 빌드: 경고 0, 오류 0
- 한국어 MSTest: 135/135 통과
- 영어 MSTest: 135/135 통과
- NuGet 직접·전이 취약 패키지: 0건
- 게시본 FileVersion: `1.0.5.0`
- 게시본 ProductVersion: `1.0.5+6798ad279b5340f007b60acb10ee2d0affe8b00d`
- 테스트는 임시 SQLite DB, FakeHelper, fake refresh callback과 격리된 임시 데이터 경로만 사용

## 로컬 설치 파일

- 한국어: `artifacts/installer/eslee-auto-power-v1.0.5-ko-setup.exe`
  - SHA-256: `CB5F3C7A589E6D5048B5EF675B3AAF9CC77D8225CCC9DE611A4B4163ECE17841`
- 영어: `artifacts/installer/eslee-auto-power-v1.0.5-en-setup.exe`
  - SHA-256: `99A9EC48468AA4653E4FE004D7CF033547111248047C4B3CD94E5EE8B7A2766E`

## GitHub 공식 릴리스 설치 파일

- GitHub Actions: `31006645238` 성공
- 정식 릴리스: `v1.0.5` (Draft 아님, Prerelease 아님, Latest 지정)
- 한국어: `eslee-auto-power-v1.0.5-ko-setup.exe`
  - SHA-256: `8DCD65F4307137A0A9299F9D6CD58A2087F6DC9AF41C72E7B5CA59AC868FF646`
- 영어: `eslee-auto-power-v1.0.5-en-setup.exe`
  - SHA-256: `9C688B221688755E28810007D544DD9187B3F53629E9CE461CC22A56BB0AD6A0`

공식 설치 파일을 다시 내려받아 계산한 SHA-256, 동봉된 `.sha256` 내용과 GitHub asset digest가 양쪽 언어 모두 일치함을 확인했다.

## 운영 환경 보호 확인

- 현재 설치본: 변경하지 않음
- 운영 DB: 읽기·쓰기 모두 수행하지 않음
- 운영 Task Scheduler 작업: 읽기·쓰기 모두 수행하지 않음
- 실제 전원 동작: 실행하지 않음
