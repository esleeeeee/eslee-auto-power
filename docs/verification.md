# v1.0.7 검증 결과

검증일: 2026-08-10

## 변경 범위

- 설치 폴더가 아닌 빌드 출력에서 앱을 실행하면 `AutoPower.Helper.exe`를 찾지 못해 완전 종료 예약 생성이 실패하던 문제 수정
- 정보 창에 현재 버전 표시와 GitHub 최신 정식 릴리스 비교 기반 업데이트 확인 추가

현재 설치본, `%ProgramData%` 운영 DB, 실제 Task Scheduler 예약에는 접근하거나 변경하지 않았다(운영 진단 로그는 read-only로만 분석). 실제 종료·재부팅·절전·최대 절전 명령도 실행하지 않았다.

## 완전 종료 예약 실패 root cause

- 운영 진단 로그(2026-08-10)의 `tray.quick-shutdown.failed`에 기록된 `FileNotFoundException` 파일 경로가 `src\AutoPower.App\bin\Debug\net10.0-windows\AutoPower.Helper.exe`로 확정됨
- `InstallationLayout.FromBaseDirectory`는 실행 중인 앱과 같은 폴더에서 Helper·Agent를 찾지만, App 프로젝트가 두 exe 프로젝트를 참조하지 않아 빌드 출력에는 존재하지 않았다
- 앱 내부 예약 저장, 자체 트레이 빠른 예약, Tray Folder 파이프 메뉴는 모두 동일한 `ScheduleCoordinator.SaveAsync` → `ElevatedHelperClient` 경로를 사용하므로 세 증상의 원인은 하나다
- 설치본(v1.0.4 실물 확인)과 publish 산출물에는 Helper가 항상 포함되어 있었으므로 설치 패키징 문제가 아니다
- 수정: App이 Helper·Agent 프로젝트를 참조해 모든 빌드 출력이 설치 레이아웃과 동일한 실행 파일 구성을 갖도록 변경, `InstallationLayout`이 찾는 세 실행 파일의 존재를 회귀 테스트로 고정

## 업데이트 확인 검증

- `UpdateCheckPolicy`(순수 로직): 최신 태그 비교(신규·동일·과거), `v` 접두사 유무, draft·prerelease 제외, tag 누락·비정상 JSON 실패 처리, InformationalVersion의 `+빌드메타` 제거를 자동 테스트로 검증
- `UpdateCheckService`: 네트워크 예외를 던지지 않고 실패 상태로 변환, 결과 캐시와 Updated 이벤트, 시작 시 비동기 확인 + 24시간 주기 루프, 10초 타임아웃
- 실제 GitHub `releases/latest` API 응답 형태(tag_name·draft·prerelease·html_url)를 확인해 파서 계약과 일치함을 검증
- 정보 창에서 현재 버전 표시, 상태 문구(미확인·최신·업데이트 가능·실패), 수동 확인 버튼, Release 페이지 열기 버튼 제공 — 한국어·영어 문자열 쌍 검증

## 빌드·테스트 결과

- 한국어 Release 빌드: 경고 0, 오류 0
- 영어 Release 빌드: 경고 0, 오류 0
- 한국어 MSTest: 157/157 통과
- 영어 MSTest: 157/157 통과
- NuGet 직접·전이 취약 패키지: 0건
- 게시본 FileVersion: `1.0.7.0`
- 게시본 ProductVersion: `1.0.7+257cccf4003786ef52d515c7e004e35c258e2fc4`
- publish 산출물과 App 빌드 출력 모두에 `AutoPower.App.exe`, `AutoPower.Helper.exe`, `AutoPower.Agent.exe` 포함 확인 — 설치 프로그램은 publish 폴더 전체를 `{app}`에 복사하므로 설치본에도 동일하게 포함됨

## 로컬 설치 파일

- 한국어: `artifacts/installer/eslee-auto-power-v1.0.7-ko-setup.exe`
  - SHA-256: `6D92BE4D21B15D8F74A9B1792FABC21CC192AE3690942AA93C465DFB324BEC85`
- 영어: `artifacts/installer/eslee-auto-power-v1.0.7-en-setup.exe`
  - SHA-256: `42DED4621E0DA8E5424599780BD360B288C97C4E13306C7441B99CAE0EB1280D`

GitHub Actions가 태그 소스에서 다시 빌드한 공식 릴리스 파일은 게시 후 별도로 내려받아 동봉된 `.sha256` 및 GitHub asset digest와 대조한다.

## 운영 환경 보호 확인

- 현재 설치본: 변경하지 않음
- 운영 DB: 쓰기 수행하지 않음
- 운영 진단 로그: 원인 분석을 위해 읽기만 수행, 변경·삭제 없음
- 운영 Task Scheduler 작업: 읽기·쓰기 모두 수행하지 않음
- 실제 전원 동작: 실행하지 않음
