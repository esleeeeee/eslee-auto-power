# 보안 정책 / Security Policy

## 지원 버전

현재 최신 `1.x` 릴리스를 지원합니다. 보안 수정이 필요한 경우 최신 패치 릴리스로 제공됩니다.

## 취약점 제보

암호, 토큰, 진단 로그, 실제 Windows 사용자 이름과 같은 민감한 정보를 공개 GitHub Issue에 올리지 마세요. 먼저 저장소 소유자에게 비공개 채널로 재현 조건과 영향을 전달해 주세요. 공개 이슈가 필요한 일반 버그라면 개인정보를 제거한 최소 재현 정보만 사용하세요.

## 보안 경계

- 관리자 Helper는 명시된 앱 명령만 처리합니다.
- Task Scheduler 변경은 앱 전용 폴더로 제한됩니다.
- Windows 계정 암호는 보호 저장소에만 보관됩니다.
- 관리자 권한 후속 프로그램은 사용자가 예약에 명시적으로 추가한 실행 파일에 대해서만 사전 등록됩니다.

## English

The latest `1.x` release is supported. Do not post passwords, tokens, diagnostic logs, or real Windows account names in a public issue. Report security-sensitive reproduction details privately to the repository owner first. The elevated helper accepts only defined app commands, task changes are scoped to the app-owned folder, and credentials remain in Windows-protected storage.
