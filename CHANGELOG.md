# 변경 이력 / Changelog

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
