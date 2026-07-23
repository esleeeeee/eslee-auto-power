# v1.0.3 검증 결과

검증일: 2026-07-23

## 변경 범위

- 완전 종료에서만 보이던 빠른 예약을 최대 절전과 절전에도 표시
- 세 전원 동작에서 공통 `1시간 뒤`, `2시간 뒤` 문구 사용
- 자동 시작 예약에서는 빠른 전원 전환 UI 숨김
- 버튼 클릭 시각의 로컬 날짜·시간만 갱신하고 기존 validation과 저장 경로 유지
- 선택 동작에 따라 접근성 이름, 도움말과 툴팁을 동적으로 갱신

현재 설치본, `%ProgramData%` 운영 DB와 실제 Task Scheduler 예약은 변경하거나 덮어쓰지 않았다. 빌드, UI 자동화와 게시본 스모크 테스트는 모두 격리된 `ESLEE_AUTOPOWER_DATA_ROOT`를 사용했다.

## 정책 자동 테스트

- 완전 종료·최대 절전·절전의 1시간 및 2시간 계산
- 선택한 `PowerActionType` 유지
- 자동 시작에 해당하는 S3/S4 Wake 타입은 빠른 전원 전환 대상에서 제외
- 자정 넘김
- 월말 및 연말 전환
- 윤년 2월 29일 전환
- 초와 밀리초를 0으로 정규화
- 지원하지 않는 시간 간격 거부
- 기존 전원 예약과 v1.0.2 durable reconciliation 회귀 테스트 유지

## 실제 WPF UI 자동화

한국어와 영어 Debug 실제 창, 한국어와 영어 Release 게시본 기동을 격리된 데이터 폴더에서 확인했다.

- 자동 시작 선택: 빠른 설정 버튼 숨김
- 완전 종료 선택: 두 버튼 표시 및 `1시간 뒤` 계산
- 최대 절전 선택: 두 버튼 표시, 선택 동작과 기존 날짜·시간 유지
- 절전 선택: 두 버튼 표시 및 `2시간 뒤` 계산
- 버튼 클릭 뒤 선택된 동작이 바뀌지 않음
- 빠른 설정 뒤 DatePicker와 시간 입력이 read-only로 바뀌지 않음
- 시간 값을 직접 수정할 수 있음
- 선택 동작에 따라 버튼 접근성 이름이 달라짐
- 기본 720×700 DIP 창에서 버튼, 설명, 하단 저장 버튼이 잘리지 않음
- `WrapPanel`, 세로 `ScrollViewer`와 WPF DIP 레이아웃을 사용하므로 100%·125%·150%에서 동일한 논리 크기로 배치됨
- 실제 Windows 125% 배율의 900×875 물리 픽셀 창에서 한국어·영어 캡처 확인

캡처:

- `artifacts/ui-v1.0.3-quick-power/quick-power-shutdown-1.0.3.png`
- `artifacts/ui-v1.0.3-quick-power/quick-power-hibernate-1.0.3.png`
- `artifacts/ui-v1.0.3-quick-power/quick-power-sleep-1.0.3.png`
- `artifacts/ui-v1.0.3-quick-power-en/quick-power-hibernate-1.0.3-en.png`

## 빌드와 검사

- 한국어 Release 빌드: 경고 0, 오류 0
- 영어 Release 빌드: 경고 0, 오류 0
- 한국어 MSTest: 92/92 통과
- 영어 MSTest: 92/92 통과
- NuGet 직접·전이 취약 패키지: 0건
- 한국어·영어 self-contained 게시본: 창 표시 및 응답 상태 정상
- 게시본 ProductVersion: `1.0.3+f195dc4c6e63d41084c193b8e173226255781f61`
- 게시본 FileVersion: `1.0.3.0`
- 추적 파일 개인정보 패턴 검사: 사용자명·로컬 사용자 경로 없음

## GitHub 공식 릴리스 설치 파일

- 한국어: `eslee-auto-power-v1.0.3-ko-setup.exe`
  - SHA-256: `570FF7107EF993B9D487AADCBF63C1CCE7AF79332CE42DE5A09AEE7130AE10FC`
- 영어: `eslee-auto-power-v1.0.3-en-setup.exe`
  - SHA-256: `778CE271A7D7FD6DFF08CA239E3164CCE261BF81AB17241DCA7C8A5123D5DB42`

GitHub Actions 공개 자산의 digest와 동봉된 `.sha256` 파일을 대조했으며 두 값이 일치한다.
