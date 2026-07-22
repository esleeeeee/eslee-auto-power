# eslee Auto Power

> Windows 11에서 S3 절전·S4 최대 절전 상태의 PC를 예약 시각에 깨우고, 완전 종료·최대 절전·절전 예약과 세션 복귀 후 프로그램 실행까지 관리하는 데스크톱 자동화 도구입니다.

[최신 한국어판 다운로드](https://github.com/esleeeeee/eslee-auto-power/releases/latest) · [English installer](https://github.com/esleeeeee/eslee-auto-power/releases/latest)

문서 언어: **한국어** · [English](README.en.md)

## 무엇을 해결하나요?

Windows 작업 스케줄러의 `WakeToRun`만 등록해 두면 PC가 어떤 상태에 있어야 하는지, 잠금 화면과 후속 프로그램은 어떻게 처리할지 사용자가 따로 관리해야 합니다. eslee Auto Power는 이 과정을 한 예약으로 묶습니다.

- 예약 시각과 `자동 / S3 / S4` 깨우기 방식을 선택합니다.
- 메인 화면의 버튼으로 PC를 예약과 같은 절전 상태에 넣습니다.
- Windows가 예약 시각에 세션을 깨우면 실제 바탕화면 준비 시점 `T0`를 확인합니다.
- `T0 + N분` 기준으로 후속 프로그램을 실행합니다.
- 필요한 경우 다음 복귀 한 번만 잠금 화면을 건너뛰도록 준비하고 원래 설정을 복원합니다.
- 지정 시각에 완전 종료, 최대 절전 또는 절전으로 전환할 수도 있습니다.

## 전원 상태별 지원 범위

| 전원 상태 | 앱에서 예약 깨우기 | 예약 시각에 상태 전환 | 설명 |
|---|---:|---:|---|
| S3 절전 | 지원 | 지원 | 펌웨어와 Windows에서 S3를 제공하는 PC에서 사용합니다. |
| S4 최대 절전 | 지원 | 지원 | Windows 최대 절전 기능이 활성화된 PC에서 사용합니다. |
| S5 완전 종료 | 미지원 | 지원 | 앱이 PC를 완전히 끄는 예약은 가능하지만, 완전히 꺼진 PC를 다시 켜지는 못합니다. |

완전 종료 후 자동 부팅은 제품 기능으로 표시하거나 우회 구현하지 않습니다. 이 앱으로 자동 시작하려면 PC를 **S3 절전 또는 S4 최대 절전** 상태로 두어야 합니다.

## 빠른 시작

1. [Releases](https://github.com/esleeeeee/eslee-auto-power/releases/latest)에서 `eslee-auto-power-v1.0.2-ko-setup.exe`를 내려받아 설치합니다.
2. `호환성`에서 S3/S4 지원 상태를 확인하고 가능한 전원 방식의 2분 실기 테스트를 진행합니다.
3. `설정`에서 Windows 계정 암호를 저장·검증합니다. Windows Hello PIN은 사용할 수 없습니다.
4. `+ 새 예약`에서 자동 시작 또는 완전 종료·최대 절전·절전 동작을 선택합니다. 새 예약의 날짜와 시각은 화면을 연 현재 시각으로 시작합니다.
5. 메인 화면에서 예약과 같은 `지금 S3 절전` 또는 `지금 S4 최대 절전`을 누릅니다.

절전이나 최대 절전으로 전환하기 전에 저장하지 않은 작업을 먼저 저장하세요.

## 완전 종료 예약

`완전 종료`는 PC를 끄는 S5 동작이며, S5 상태에서 PC를 다시 켜는 기능과는 별개입니다.

- 완전 종료를 선택한 경우에만 `1시간 뒤 종료`, `2시간 뒤 종료` 빠른 버튼이 표시됩니다. 버튼을 실제로 누른 현재 로컬 시각을 기준으로 날짜와 시간이 채워지며 이후 직접 수정할 수 있습니다.
- 정확히 5분 전에 저장하지 않은 작업을 확인할 수 있는 경고가 표시됩니다.
- 예약 시각에는 정상 종료를 먼저 요청하고, 30초 안에 완료되지 않으면 강제 종료 fallback을 실행합니다.
- 이후 활성 자동 시작 예약이 있으면 완전 종료로는 해당 예약이 PC를 다시 켤 수 없다고 안내합니다. 5분 전 경고에서 다음 예약에 맞는 S3/S4 상태로 전환하거나, 경고를 확인하고 완전 종료를 계속할 수 있습니다.

장시간 대기 뒤 자동 시작에는 최대 절전, 짧은 대기에는 절전을 권장합니다.

## 작동 원리

```mermaid
flowchart LR
    A["예약 저장"] --> B["Windows WakeToRun 작업 등록"]
    B --> C["S3 절전 또는 S4 최대 절전 진입"]
    C --> D["예약 시각에 Windows가 깨움"]
    D --> E["사용자 세션과 Explorer 준비 확인"]
    E --> F["T0 확정"]
    F --> G["T0 + N분 후속 프로그램 실행"]
```

절전·최대 절전·완전 종료 예약은 전원 명령보다 먼저 `ExecutionStarted`, pending journal, `PendingPowerTransition` 상태를 DB에 저장하고 해당 1회성 Task를 소비합니다. S3/S4 복귀 또는 다음 앱 시작 때 Windows 전원 이벤트와 Task 결과를 대조해 완료·실패·미실행·결과 불명 중 하나로 확정합니다. 따라서 과거 예약을 단순히 성공으로 간주하거나 늦게 다시 실행하지 않습니다.

앱은 역할을 세 프로세스로 나눕니다.

- `AutoPower.App.exe`: 일반 사용자 권한 UI, 트레이, 예약·기록·설정 화면
- `AutoPower.Helper.exe`: 사용자가 승인한 관리자 권한으로 전원 전환과 앱 전용 작업 등록
- `AutoPower.Agent.exe`: 사용자 세션에서 Resume 준비 확인과 후속 프로그램 실행

Task Scheduler에는 앱 전용 `\eslee\AutoPower\` 폴더만 사용하며, 앱이 소유하지 않은 작업은 변경하지 않습니다. 자세한 구조는 [아키텍처 문서](docs/architecture.md)에 정리되어 있습니다.

## 앱 관리형 1회 로그인

S3/S4 복귀 뒤에도 Windows 설정에 따라 잠금 화면에서 암호 입력을 요구할 수 있습니다. 예약의 `앱 관리형 1회 자동 로그인`을 선택하면 다음 복귀 한 번에 한해 Windows의 `절전 모드 해제 시 로그인 요구` 값을 임시로 해제합니다.

- Windows 계정 암호는 Credential Manager와 앱 전용 LSA 보호 저장소에 보관됩니다.
- Microsoft 계정은 이메일 주소만 입력해도 계정 형식으로 변환합니다.
- Windows Hello PIN은 저장하거나 사용하지 않습니다.
- 복귀 후 원래 AC/DC 로그인 요구 값을 다시 적용하고 검증합니다.
- 앱이나 Helper가 중단되어도 다음 실행·로그인·제거 과정에서 journal을 이용해 복구를 재시도합니다.

이 기능을 켠 뒤 예약 전에 PC를 직접 깨우면 그 복귀도 잠금 없이 열릴 수 있습니다. 물리적으로 안전한 PC에서만 사용하세요.

## 후속 프로그램과 관리자 권한

후속 프로그램은 예약 시각이 아니라 바탕화면 준비 완료 시점 `T0`를 기준으로 실행됩니다. 같은 지연 시간의 여러 프로그램도 등록할 수 있으며, 하나가 실패해도 나머지는 계속 처리됩니다.

관리자 권한이 필요한 프로그램은 고급 옵션에서 `관리자 권한으로 실행`을 선택하세요. 예약 저장 시 해당 실행 파일을 위한 최고 권한 작업을 미리 등록하므로 실제 Resume 시 UAC 확인창을 기다리지 않습니다. 신뢰할 수 있는 프로그램에만 사용해야 합니다.

## 호환성과 주의사항

- 운영 체제: Windows 11 x64
- S3는 PC 펌웨어가 해당 상태를 제공해야 합니다.
- S4는 펌웨어 지원과 Windows 최대 절전 활성화가 모두 필요합니다.
- 앱은 사용자 동의 없이 최대 절전을 활성화하지 않습니다. 필요한 경우 관리자 터미널의 `powercfg /hibernate on`을 안내합니다.
- UEFI 전원 정책, Wake Timer 설정, 제조사 펌웨어에 따라 실제 깨우기 결과가 달라질 수 있습니다.
- 배포 파일은 현재 코드 서명 인증서로 서명되지 않았으므로 Windows SmartScreen 경고가 표시될 수 있습니다.

호환성 화면은 기능을 추정만으로 확정하지 않습니다. S3/S4 실기 테스트가 성공한 경로만 `지원 확인됨`으로 기록하며, 앱 관리형 1회 자동 로그인은 설정의 자격 증명 검증 여부를 함께 표시합니다.

## 로컬 데이터와 개인정보

예약, 실행 기록, 복구 journal과 진단 로그는 `%ProgramData%\eslee\AutoPower` 아래에 저장됩니다. 로그인 암호는 평문 DB나 로그에 기록하지 않으며 Windows 보호 저장소를 사용합니다. 앱에는 분석·광고·외부 서버 전송 코드가 없습니다.

진단 로그에는 전원 작업 결과, 오류 코드, 실행 파일 경로처럼 문제 해결에 필요한 로컬 정보가 포함될 수 있습니다. 공개 이슈에 첨부하기 전 직접 내용을 확인하세요. 자세한 내용은 [개인정보 안내](PRIVACY.md)를 참고하세요.

## 개발 빌드

필요 환경:

- Windows 11 x64
- .NET SDK 10.0.301
- 설치 프로그램 생성 시 Inno Setup 6

```powershell
dotnet restore .\AutoPower.sln
dotnet build .\AutoPower.sln -c Release
dotnet test .\tests\AutoPower.Tests\AutoPower.Tests.csproj -c Release
```

한국어·영어 설치 파일을 함께 만들려면:

```powershell
.\scripts\Build-Release.ps1 -Version 1.0.2
```

언어별 단일 빌드는 `-p:AppLanguage=ko` 또는 `-p:AppLanguage=en`을 사용합니다. 릴리스 스크립트는 self-contained x64 설치 파일 두 개와 SHA-256 파일을 `artifacts\installer`에 만듭니다.

## 프로젝트 구조

```text
src/AutoPower.App       WPF UI와 트레이
src/AutoPower.Core      예약 모델, 정책, 검증, 언어 카탈로그
src/AutoPower.Data      SQLite 저장소
src/AutoPower.Windows   Task Scheduler, 전원, 자격 증명, 복구
src/AutoPower.Helper    관리자 권한 명령 실행기
src/AutoPower.Agent     Resume 및 후속 프로그램 처리
tests/AutoPower.Tests   자동 테스트
installer               Inno Setup 정의
```

## 문서와 지원

- [변경 이력](CHANGELOG.md)
- [검증 결과](docs/verification.md)
- [보안 정책](SECURITY.md)
- [개인정보 안내](PRIVACY.md)
- [GitHub Issues](https://github.com/esleeeeee/eslee-auto-power/issues)

## 라이선스

[MIT License](LICENSE)
