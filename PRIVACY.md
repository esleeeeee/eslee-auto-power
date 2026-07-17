# 개인정보 안내 / Privacy

## 한국어

eslee Auto Power는 로컬에서만 동작합니다. 분석, 광고, 계정 서버, 자동 오류 전송 기능이 없으며 외부 서버로 예약이나 사용 정보를 보내지 않습니다.

로컬에 저장되는 정보:

- `%ProgramData%\eslee\AutoPower\autopower.db`: 예약, 후속 프로그램 설정, 실행 기록, 호환성 결과
- `%ProgramData%\eslee\AutoPower\logs`: 진단 로그
- 같은 폴더의 journal 파일: 예약 저장·전원 복귀·로그인 설정 복구 상태
- Windows Credential Manager와 앱 전용 LSA 보호 저장소: 사용자가 직접 등록한 Windows 계정 이름과 암호

암호는 SQLite DB나 진단 로그에 평문으로 기록하지 않습니다. 진단 로그에는 오류 코드, 작업 이름, 실행 파일 경로, Windows 전원 상태처럼 문제 해결에 필요한 로컬 정보가 포함될 수 있습니다. 공개 이슈에 공유하기 전에 반드시 직접 검토하세요.

앱 제거 시 앱 전용 Task Scheduler 작업과 로컬 상태를 정리합니다. 사용자가 별도로 보관한 로그 사본이나 GitHub에 직접 올린 첨부물은 앱이 삭제할 수 없습니다.

## English

eslee Auto Power operates locally. It has no analytics, advertising, account server, or automatic crash upload, and does not send schedules or usage data to external servers.

Local data includes schedules, follow-up program settings, history, compatibility results, diagnostics, and recovery journals under `%ProgramData%\eslee\AutoPower`. Windows credentials supplied by the user are kept in Windows Credential Manager and an app-specific LSA protected secret, never in the plain-text SQLite database or diagnostic logs.

Logs may contain troubleshooting details such as error codes, task names, executable paths, and Windows power state. Review them before sharing them publicly.
