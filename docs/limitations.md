# 제한 사항 / Limitations

v2.0.0 기준입니다. 코드와 실제 확인 결과를 바탕으로 적었습니다.

## 전원과 펌웨어

- Windows 11 x64 전용입니다.
- 완전 종료(S5)한 PC를 앱이 다시 켤 수는 없습니다. 자동 시작은 절전(S3)과 최대 절전(S4)에서만 동작합니다.
- S3는 펌웨어가 해당 상태를 제공해야 하고, S4는 Windows 최대 절전이 켜져 있어야 합니다. 앱은 `powercfg /hibernate on`을 대신 실행하지 않습니다.
- 깨우기 결과는 펌웨어, Windows 깨우기 타이머 설정, 제조사 전원 정책에 따라 달라질 수 있습니다. 실제 S3/S4/S5 전환과 펌웨어 깨우기는 개발 PC에서 매 릴리스마다 실행해 보지 않습니다(릴리스 노트의 검증 항목 참고).

## 반복 예약

- 반복 예약은 1회성 예약을 차례로 이어 만드는 방식입니다. 다음 회차는 한 회차가 실행을 시작하거나, 건너뛰거나, 놓친 것으로 정리될 때 만들어집니다.
- PC가 꺼져 있어 회차를 놓치면, 다음 Windows 로그인 때 SYSTEM 정리 작업이 다음 회차를 만듭니다. 그 전까지는 예약 목록에 다음 회차가 보이지 않을 수 있습니다.
- 다음 회차를 자동으로 만들 때는 같은 시각의 다른 1회성 예약과 겹치는지 검사하지 않습니다.
- 일시 중지(끄기)한 반복 예약은 다시 켜기 전까지 다음 회차를 만들지 않습니다. 시간이 지난 뒤 다시 켜면 다음 회차 시각으로 옮겨집니다.
- 실행 중인 회차에서 다음 회차를 만드는 작업은 전원 동작 직전에 Windows 작업 등록 시간(보통 1초 안팎)만큼 전원 동작을 늦출 수 있습니다.
- 여러 사용자가 쓰는 PC에서 SYSTEM 정리 작업이 만든 회차의 5분 전 경고와 후속 프로그램은 마지막으로 예약을 저장한 사용자 계정에 등록됩니다.

## 미루기

- 미루기는 완전 종료·최대 절전·절전 예약에만 있습니다. 예약 시각을 바꿔 다시 등록하므로 UAC 확인이 나타날 수 있습니다.
- 5분 전 경고 창은 앱의 사용자 세션 작업이 실행해야 나타납니다. 앱의 Windows 로그인 시 자동 실행을 끄면 경고와 미루기를 쓸 수 없을 수 있습니다.

## 1회 자동 로그인

- Windows 계정 암호가 필요하며 Windows Hello PIN은 쓸 수 없습니다.
- 켜 두면 예약 전에 PC를 직접 깨운 경우에도 다음 복귀 한 번은 잠금 화면이 생략될 수 있습니다. 물리적으로 안전한 PC에서만 쓰세요.

## 언어와 업그레이드

- 언어는 설정에서 바꾸고 앱을 다시 시작하면 적용됩니다. 처음에는 Windows 표시 언어를 따릅니다(한국어면 한국어, 그 밖에는 영어).
- v1.x 영어판을 쓰던 PC라도 Windows 표시 언어가 한국어이면 업그레이드 후 한국어로 시작합니다. 설정 > 언어에서 English를 고르세요.
- 기록에 이미 저장된 v1.x 문구 일부는 영어 화면에서도 한국어로 보일 수 있습니다.

## eslee Tray Folder 연동

- 파이프 프로토콜 v1에는 하위 메뉴가 없어, Tray Folder 메뉴에서는 추가 빠른 예약을 구분선으로 묶어 한 단계에 보여 줍니다.

## 배포

- 설치 파일에 코드 서명이 없어 Windows SmartScreen 경고가 나타날 수 있습니다.
- 앱은 업데이트를 자동으로 설치하지 않습니다. 정보 창에서 새 버전을 확인하고 Release 페이지를 엽니다.
- 진단 로그에는 실행 파일 경로 같은 로컬 정보가 들어갈 수 있습니다. 공개 공유 전에 확인하세요.

---

## English summary

- Windows 11 x64 only. The app cannot power on a PC that was fully shut down (S5); scheduled wake works from S3 sleep and S4 hibernation, which depend on firmware and Windows settings. Real power transitions are not exercised on the development PC for every release.
- Recurring schedules are chains of one-time schedules. The next occurrence is created when an occurrence starts, is skipped, or is recorded as missed; after the PC was off, it appears at the next Windows sign-in. Automatic occurrences are not conflict-checked against other one-time schedules. A paused series creates nothing until turned back on, then moves to its next slot. Creating the next occurrence can delay the power action by about the time of a task registration. On multi-user PCs, occurrences created by the SYSTEM reconcile use the account that last saved a schedule for warnings and follow-ups.
- Snooze exists for shutdown, hibernate and sleep only and may show a UAC prompt. The 5-minute warning needs the app's sign-in task.
- One-time sign-in needs the Windows password (no PIN) and may skip the lock screen once even if the PC is woken early.
- The language follows Windows until changed in Settings (applies after restart). Former v1.x English-edition users on Korean Windows start in Korean after upgrading. Some history text stored by v1.x may stay Korean in English mode.
- Tray Folder protocol v1 has no submenus, so extra quick schedules appear flat with separators there.
- The installer is not code-signed (SmartScreen may warn), there is no automatic update, and diagnostic logs can contain local paths.
