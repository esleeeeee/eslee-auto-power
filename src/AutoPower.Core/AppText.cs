using System.Globalization;

namespace AutoPower.Core;

public static class AppText
{
#if APP_LANGUAGE_EN
    public const bool IsEnglish = true;
    public const string LanguageCode = "en";
#else
    public const bool IsEnglish = false;
    public const string LanguageCode = "ko";
#endif

    public static CultureInfo Culture => CultureInfo.GetCultureInfo(IsEnglish ? "en-US" : "ko-KR");

    public static void ApplyCulture()
    {
        CultureInfo.CurrentCulture = Culture;
        CultureInfo.CurrentUICulture = Culture;
        CultureInfo.DefaultThreadCurrentCulture = Culture;
        CultureInfo.DefaultThreadCurrentUICulture = Culture;
    }

    public static string T(string text)
    {
#if APP_LANGUAGE_EN
        foreach (var (korean, english) in ExactTranslations)
        {
            if (string.Equals(text, korean, StringComparison.Ordinal))
            {
                return english;
            }
        }

        var result = text;
        foreach (var (korean, english) in ExactTranslations)
        {
            if (korean.Length >= 8)
            {
                result = result.Replace(korean, english, StringComparison.Ordinal);
            }
        }

        foreach (var (korean, english) in PhraseTranslations)
        {
            result = result.Replace(korean, english, StringComparison.Ordinal);
        }

        return result;
#else
        return text;
#endif
    }

    public static string F(string format, params object?[] args) =>
        string.Format(Culture, T(format), args);

#if APP_LANGUAGE_EN
    private static readonly (string Korean, string English)[] ExactTranslations =
    [
        ("eslee Auto Power 정보", "About eslee Auto Power"),
        ("Windows 11용 S3/S4 예약 깨우기 및 전원 자동화", "Scheduled S3/S4 wake and power automation for Windows 11"),
        ("운영 체제", "Operating system"),
        ("실행 환경", "Runtime"),
        ("앱 데이터", "App data"),
        ("진단 로그", "Diagnostic logs"),
        ("개인정보 및 동작 방식", "Privacy and operation"),
        ("예약, 실행 기록과 로그인 자격 증명은 이 PC의 Windows 보호 저장소와 로컬 앱 데이터에만 보관됩니다. 앱은 외부 서버로 사용 정보를 전송하지 않습니다.", "Schedules, history, and sign-in credentials stay in Windows-protected storage and local app data on this PC. The app does not send usage information to external servers."),
        ("S3 절전 또는 S4 최대 절전에서 예약 시각에 세션을 복귀시키며, 완전 종료(S5) 자동 부팅은 앱 제어 기능으로 제공하지 않습니다.", "The app resumes your session from S3 sleep or S4 hibernation at the scheduled time. App-controlled startup from a full S5 shutdown is not provided."),
        ("진단 로그 폴더 열기", "Open diagnostic log folder"),
        ("확인", "OK"),
        ("eslee Auto Power 종료", "Exit eslee Auto Power"),
        ("eslee Auto Power를 종료하시겠습니까?", "Exit eslee Auto Power?"),
        ("등록된 전원 예약은 계속 유지되지만 일부 알림과 실시간 상태 표시가 제한될 수 있습니다.", "Registered power schedules remain active, but some notifications and live status updates may be limited."),
        ("다시 표시하지 않기", "Do not show again"),
        ("취소", "Cancel"),
        ("앱 종료", "Exit app"),
        ("Windows 11 전원 자동화", "Windows 11 power automation"),
        ("예약", "Schedules"),
        ("기록", "History"),
        ("설정", "Settings"),
        ("호환성", "Compatibility"),
        ("정보", "About"),
        ("다음 예약", "Next schedule"),
        ("통합 자동 시작 예약과 전원 동작", "Unified scheduled wake and power actions"),
        ("자동 시작 예약과 전원 상태 전환", "Scheduled wake and power-state transition"),
        ("S3/S4 버튼을 누르면 같은 방식의 자동 시작 예약을 준비한 뒤 PC를 해당 전원 상태로 전환합니다. 예약이 없으면 바로 전환합니다.", "An S3/S4 button prepares the nearest matching wake schedule, then puts the PC into that power state. If no matching schedule exists, it switches immediately."),
        ("지금 S3 절전", "Enter S3 sleep now"),
        ("지금 S4 최대 절전", "Enter S4 hibernation now"),
        ("+ 새 예약", "+ New schedule"),
        ("수정", "Edit"),
        ("삭제", "Delete"),
        ("날짜", "Date"),
        ("시간", "Time"),
        ("예약 종류", "Schedule type"),
        ("상태", "Status"),
        ("세션", "Session"),
        ("후속 프로그램", "Follow-up programs"),
        ("전체 기록 삭제", "Clear all history"),
        ("시각", "Time"),
        ("구분", "Event"),
        ("결과", "Result"),
        ("내용", "Details"),
        ("Windows 시작", "Windows startup"),
        ("Windows 로그인 시 eslee Auto Power 시작", "Start eslee Auto Power at Windows sign-in"),
        ("끄면 일부 알림, 후속 프로그램 실행 상태 표시 및 복구 기능이 제한될 수 있습니다. 등록된 전원 작업 자체는 유지됩니다.", "Turning this off may limit notifications, follow-up program status, and recovery. Registered Windows power tasks remain active."),
        ("Windows 시작, 로그인 정보 및 진단 로그", "Windows startup, sign-in credentials, and diagnostic logs"),
        ("S3/S4 앱 관리형 1회 자동 로그인", "App-managed one-time sign-in for S3/S4"),
        ("자동 시작 예약에서 1회 자동 로그인을 선택할 때 사용할 Windows 계정을 등록하고 실제 로그온 자격 증명을 검사합니다. PIN이 아니라 Windows 계정 암호를 입력하세요. 암호는 Windows 보호 저장소에만 보관됩니다.", "Register the Windows account used by one-time sign-in schedules and validate it through the Windows logon API. Enter the account password, not a PIN. The password is stored only in Windows-protected storage."),
        ("등록 상태 확인 중…", "Checking registration…"),
        ("Windows 사용자 이름", "Windows user name"),
        ("Windows 계정 암호", "Windows account password"),
        ("Microsoft 계정은 메일 주소만 입력해도 MicrosoftAccount\\메일주소 형식으로 자동 변환됩니다. 로컬 계정은 PC이름\\사용자 형식이며, Windows Hello PIN은 사용할 수 없습니다.", "A Microsoft account email is converted automatically to MicrosoftAccount\\email. For a local account, use PCNAME\\user. Windows Hello PINs are not supported."),
        ("저장 및 검증", "Save and validate"),
        ("저장된 로그인 테스트", "Test saved sign-in"),
        ("등록 정보 제거", "Remove credentials"),
        ("다시 진단", "Run diagnostics again"),
        ("기능", "Capability"),
        ("세부 정보", "Details"),
        ("전원 자동 깨우기 실제 테스트", "Real wake tests"),
        ("MVP 자동 시작 경로인 S3 절전과 S4 최대 절전의 WakeToRun 동작을 직접 검증합니다.", "Test Windows WakeToRun directly for the supported S3 sleep and S4 hibernation wake paths."),
        ("S3 절전 테스트", "Test S3 sleep"),
        ("S4 최대 절전 테스트", "Test S4 hibernation"),
        ("후속 프로그램 추가", "Add follow-up program"),
        ("실제 바탕화면 준비 완료 시점 T0를 기준으로 실행합니다.", "Programs run relative to T0, when the desktop is actually ready."),
        ("프로그램 경로", "Program path"),
        ("찾아보기", "Browse"),
        ("바탕화면 준비 후 N분 (0 이상)", "N minutes after desktop ready (0 or more)"),
        ("고급 옵션", "Advanced options"),
        ("실행 인수", "Arguments"),
        ("작업 폴더", "Working directory"),
        ("관리자 권한으로 실행", "Run as administrator"),
        ("선택하면 예약 저장 시 승인된 Windows 작업을 미리 등록합니다. 실제 실행 시에는 UAC 확인창 없이 현재 사용자 데스크톱에서 최고 권한으로 시작됩니다. 신뢰할 수 있는 프로그램에만 사용하세요.", "When selected, an approved Windows task is registered while saving the schedule. At run time it starts with highest privileges on the current user's desktop without a UAC prompt. Use this only for trusted programs."),
        ("추가", "Add"),
        ("새 예약", "New schedule"),
        ("자동 시작 예약 또는 지정 시각의 전원 동작을 설정합니다.", "Create a scheduled wake or a power action at a specific time."),
        ("시간 (HH:mm)", "Time (HH:mm)"),
        ("동작", "Action"),
        ("자동 시작 방식", "Wake mode"),
        ("자동 선택은 실제 테스트에서 확인된 방식을 우선합니다. 실패로 기록된 방식은 제외하며, 나머지가 모두 미검증이면 사용 가능한 S4 최대 절전을 먼저 선택하고 S3 절전으로 대체합니다.", "Automatic mode prefers a path confirmed by a real test and excludes failed paths. If every remaining path is unverified, it prefers available S4 hibernation and falls back to S3 sleep."),
        ("앱 관리형 1회 자동 로그인", "App-managed one-time sign-in"),
        ("선택하면 설정에서 등록·검증한 Windows 계정을 확인한 뒤, 다음 S3/S4 복귀 1회에 한해 잠금 화면을 건너뜁니다. 복귀 즉시 원래의 '절전 모드 해제 시 로그인 요구' 설정으로 되돌립니다. PIN은 저장하지 않으며 Windows 암호는 보호 저장소에만 보관됩니다. 예약 전에 PC를 일찍 깨우는 경우에도 잠금 없이 열릴 수 있습니다.", "The app validates the Windows account saved in Settings and skips the lock screen for the next S3/S4 resume only. It restores the original 'require sign-in on wake' setting immediately after resume. PINs are never stored; the Windows password stays in protected storage. If the PC wakes early, the session may also open without a lock screen."),
        ("복귀 후 실행", "Run after resume"),
        ("+ 추가", "+ Add"),
        ("프로그램", "Program"),
        ("T0 + N분", "T0 + N min"),
        ("권한", "Privilege"),
        ("선택 항목 제거", "Remove selected"),
        ("후속 프로그램은 예약 시각이 아니라 실제 Resume 준비 완료 T0를 기준으로 실행합니다. 0분과 같은 지연 시간의 여러 프로그램도 허용합니다.", "Follow-up programs run relative to the actual resume-ready time T0, not the scheduled time. Multiple programs may use the same delay, including 0 minutes."),
        ("예약한 절전 또는 최대 절전 동작은 정확히 5분 전에 경고합니다. 저장하지 않은 작업이 없도록 미리 확인하세요.", "A warning appears exactly five minutes before a scheduled sleep or hibernation action. Save your work in advance."),
        ("저장", "Save"),
        ("예약 실행 전 확인", "Confirm scheduled action"),
        ("저장되지 않은 데이터가 손실될 수 있습니다.", "Unsaved data may be lost."),
        ("창을 닫거나 30초 동안 응답하지 않으면 원래 예약 시각을 그대로 유지합니다.", "Closing this window or not responding for 30 seconds keeps the original schedule."),
        ("이번 예약 건너뛰기", "Skip this schedule"),
        ("eslee Auto Power 시작", "Start eslee Auto Power"),
        ("Windows 11 전원 자동화를 시작합니다", "Start Windows 11 power automation"),
        ("이 앱은 하나의 자동 시작 예약에서 자동·S3 절전·S4 최대 절전 방식을 선택하고, 예약 시각에 Windows 사용자 세션을 깨운 뒤 Resume 준비 확인 후 후속 프로그램을 실행합니다. 필요한 예약에는 앱 관리형 1회 자동 로그인을 선택할 수 있습니다. 외부 서버로 정보를 전송하지 않습니다.", "Choose automatic, S3 sleep, or S4 hibernation for each wake schedule. At the scheduled time, the app wakes the Windows session, confirms that resume is ready, and runs follow-up programs. One-time app-managed sign-in is optional. No information is sent to external servers."),
        ("메인 화면의 S3 절전 및 S4 최대 절전 버튼으로 원하는 전원 상태에 즉시 진입할 수 있습니다. 자동 시작 예약이 있으면 같은 전원 방식으로 준비하세요.", "Use the S3 sleep and S4 hibernation buttons on the main screen to enter a power state immediately. For a wake schedule, prepare the PC with the matching state."),
        ("나중에", "Later"),
        ("진단 결과 보기", "View diagnostics"),
        ("준비됨", "Ready"),
        ("없음", "None"),
        ("자동 시작 (S3)", "Scheduled wake (S3)"),
        ("자동 시작 (S4)", "Scheduled wake (S4)"),
        ("완전 종료", "Full shutdown"),
        ("최대 절전", "Hibernate"),
        ("절전", "Sleep"),
        ("이전 전원 동작", "Legacy power action"),
        ("1회 자동 로그인", "One-time sign-in"),
        ("로그인 필요", "Sign-in required"),
        ("성공", "Success"),
        ("실패", "Failure"),
        ("건너뜀", "Skipped"),
        ("경고", "Warning"),
        ("절전(S3) 후 자동 깨우기", "Scheduled wake from S3 sleep"),
        ("최대 절전(S4) 후 자동 깨우기", "Scheduled wake from S4 hibernation"),
        ("사용 가능 / 검증 완료", "Available / validated"),
        ("설정 필요 / 검증 전", "Setup required / not validated"),
        ("상태 확인 실패", "Status check failed"),
        ("설정에 저장된 Windows 계정이 실제 로그온 API 검증을 통과했습니다.", "The Windows account saved in Settings passed validation through the actual logon API."),
        ("설정에서 Windows 계정 암호를 저장·검증한 뒤 저장된 로그인 테스트로 확인할 수 있습니다.", "Save and validate a Windows account password in Settings, then confirm it with the saved sign-in test."),
        ("Windows 보호 저장소의 자동 로그인 등록 상태를 확인하지 못했습니다. 진단 로그를 확인하세요.", "The one-time sign-in registration state could not be read from Windows-protected storage. Check the diagnostic logs."),
        ("지원 확인됨", "Confirmed supported"),
        ("지원 가능성 있음 / 실제 테스트 필요", "Potentially supported / real test required"),
        ("미지원 또는 테스트 실패", "Unsupported or test failed"),
        ("확인 불가", "Unable to determine"),
        ("자동 시작 예약", "Scheduled wake"),
        ("예약 시각에 완전 종료", "Full shutdown at scheduled time"),
        ("예약 시각에 최대 절전 진입", "Enter hibernation at scheduled time"),
        ("예약 시각에 절전 진입", "Enter sleep at scheduled time"),
        ("자동 선택 (추천)", "Automatic (recommended)"),
        ("절전 (S3)", "Sleep (S3)"),
        ("최대 절전 (S4)", "Hibernation (S4)"),
        ("관리자", "Administrator"),
        ("일반", "Standard"),
        ("앱 열기", "Open app"),
        ("모든 예약 일시 중지", "Pause all schedules"),
        ("다음 예약: 확인 중", "Next schedule: checking"),
        ("프로그램 (*.exe)|*.exe", "Programs (*.exe)|*.exe"),
        ("작업 폴더 선택", "Select working directory"),
        ("등록된 Windows 로그인 정보가 없습니다.", "No Windows sign-in credentials are registered."),
        ("등록 상태를 확인하지 못했습니다.", "Could not check registration status."),
        ("Windows 사용자 이름과 계정 암호를 모두 입력하세요. PIN은 사용할 수 없습니다.", "Enter both the Windows user name and account password. A PIN cannot be used."),
        ("먼저 Windows 로그인 자격 증명을 저장하세요.", "Save Windows sign-in credentials first."),
        ("올바른 날짜, 시간(HH:mm), 동작을 입력하세요.", "Enter a valid date, time (HH:mm), and action."),
        ("자동 시작 방식을 선택하세요.", "Select a wake mode."),
        ("수정할 예약을 선택하세요.", "Select a schedule to edit."),
        ("모든 실행 기록을 삭제하시겠습니까?", "Clear all execution history?"),
        ("작업 폴더가 존재하지 않습니다.", "The working directory does not exist."),
        ("존재하는 EXE 전체 경로와 0 이상의 분 값을 입력하세요.", "Enter the full path of an existing EXE and a delay of 0 minutes or more."),
        ("PC가 이미 켜져 있습니다", "The PC is already awake"),
        ("지금 시작", "Start now"),
        ("예약 시각에는 저장된 절전 또는 최대 절전 동작을 실행합니다.", "The saved sleep or hibernation action will run at the scheduled time."),
        ("지금 최대 절전", "Hibernate now"),
        ("지금 절전", "Sleep now"),
        ("원래대로 진행 (30)", "Keep original schedule (30)"),
        ("지원하지 않는 이전 전원 동작", "Unsupported legacy power action"),
        ("S3 절전 깨우기", "Wake from S3 sleep"),
        ("S4 최대 절전 깨우기", "Wake from S4 hibernation"),
        ("eslee Auto Power 시작 오류", "eslee Auto Power startup error"),
        ("알 수 없음", "Unknown"),
        ("실제 테스트 성공이 확인된 경로입니다.", "This path is confirmed by a successful real test."),
        ("이 방식의 이전 실제 테스트가 실패했습니다. 직접 선택해 다시 시험할 수 있지만 성공은 보장되지 않습니다.", "A previous real test of this path failed. You may select and retest it, but success is not guaranteed."),
        ("아직 실제 테스트 성공이 확인되지 않은 경로입니다. 호환성 화면에서 먼저 테스트하는 것을 권장합니다.", "This path has not yet passed a real test. Testing it from Compatibility first is recommended."),
        ("현재 시스템에서는 S3 절전 자동 시작을 사용할 수 없습니다.", "Scheduled wake from S3 sleep is not available on this system."),
        ("현재 시스템에서는 S4 최대 절전 자동 시작을 사용할 수 없습니다. Windows 최대 절전 활성화 상태를 확인하세요.", "Scheduled wake from S4 hibernation is not available. Check whether Windows hibernation is enabled."),
        ("자동 선택에 사용할 수 있는 S3 절전 또는 S4 최대 절전 경로가 없습니다. 호환성 결과와 Windows 최대 절전 활성화 상태를 확인하세요.", "No S3 sleep or S4 hibernation path is available for automatic selection. Check Compatibility and the Windows hibernation setting."),
        ("S3 또는 S4 깨우기 예약이 아닙니다.", "This is not an S3 or S4 wake schedule."),
        ("예약 시각은 현재보다 이후여야 합니다.", "The scheduled time must be in the future."),
        ("이전 버전의 앱 기반 완전 종료 자동 부팅 예약은 더 이상 사용할 수 없습니다. 자동 시작, 완전 종료, 최대 절전 또는 절전을 선택하세요.", "The legacy app-controlled startup from full shutdown is no longer available. Select scheduled wake, full shutdown, hibernation, or sleep."),
        ("1회 자동 로그인과 후속 프로그램은 S3/S4 예약 깨우기에서만 사용할 수 있습니다.", "One-time sign-in and follow-up programs are available only for S3/S4 wake schedules."),
        ("후속 프로그램 지연 시간은 0분 이상이어야 합니다.", "A follow-up program delay must be 0 minutes or more."),
        ("후속 프로그램은 올바른 전체 경로를 사용해야 합니다.", "A follow-up program must use a valid full path."),
        ("같은 시각에 서로 다른 전원 동작을 예약할 수 없습니다.", "Different power actions cannot be scheduled for the same time."),
        ("완전히 동일한 예약이 이미 있습니다.", "An identical schedule already exists."),
        ("전원 진입 예약과 깨우기 예약의 상태가 일치하지 않습니다. S3 절전은 S3 깨우기, S4 최대 절전은 S4 깨우기와 연결하세요.", "The enter-power-state schedule does not match the wake schedule. Pair S3 sleep with S3 wake and S4 hibernation with S4 wake."),
        ("예약을 저장하는 중…", "Saving schedule…"),
        ("모든 미래 예약을 일시 중지했습니다.", "All future schedules have been paused."),
        ("앱이 시스템 트레이에서 계속 실행됩니다.", "The app continues running in the system tray."),
        ("완료·실패·건너뜀·놓침 및 복구 기록", "Completed, failed, skipped, missed, and recovery history"),
        ("이 PC에서 실제로 사용할 수 있는 기능 진단", "Diagnose capabilities available on this PC"),
        ("앱 관리형 1회 자동 로그인을 사용하려면 설정에서 Windows 사용자 이름과 계정 암호를 먼저 저장·검증해야 합니다.", "To use app-managed one-time sign-in, save and validate the Windows user name and account password in Settings first."),
        ("예약 실행이 임박했습니다. 이미 Windows 또는 시스템 전원 설정에 작업이 준비되었을 수 있습니다. 변경 사항을 적용하시겠습니까?", "The scheduled action is approaching, so a Windows task or system power action may already be prepared. Apply the changes?"),
        ("S4 최대 절전 깨우기를 준비하려면 Windows 최대 절전 기능을 사용자가 먼저 활성화해야 합니다. 관리자 터미널에서 'powercfg /hibernate on'을 실행하고 다시 진단하세요.", "To prepare S4 hibernation wake, first enable Windows hibernation by running 'powercfg /hibernate on' in an elevated terminal, then run diagnostics again."),
        ("실행이 임박하여 이미 Windows 또는 시스템 전원 설정에 작업이 준비되었을 수 있습니다. 계속하시겠습니까?", "The action is approaching, so a Windows task or system power action may already be prepared. Continue?"),
        ("선택한 예약을 삭제하시겠습니까?", "Delete the selected schedule?"),
        ("Windows 계정과 암호를 실제 로그온 API로 검증하고 보호 저장소에 등록했습니다.", "The Windows account and password were validated through the Windows logon API and stored in protected storage."),
        ("저장된 Windows 계정과 암호로 실제 로그온 자격 증명 검증에 성공했습니다.", "The saved Windows account and password passed actual logon credential validation."),
        ("앱이 보호 저장소에 등록한 Windows 로그인 정보를 제거하시겠습니까?", "Remove the Windows sign-in credentials stored by the app?"),
        ("현재 시스템 펌웨어와 Windows 전원 정보에서 S3 절전을 지원하지 않아 실제 테스트를 시작할 수 없습니다.", "A real test cannot start because the system firmware and Windows power data do not report S3 sleep support."),
        ("현재 시스템 펌웨어가 S4 최대 절전 상태를 제공하지 않습니다.", "The system firmware does not provide the S4 hibernation state."),
        ("펌웨어는 S4를 지원하지만 Windows 최대 절전 기능이 현재 비활성화되어 있습니다.", "The firmware supports S4, but Windows hibernation is currently disabled."),
        ("S3/S4 테스트에서 잠금 화면을 건너뛰려면 설정에서 Windows 로그인 정보를 먼저 '저장 및 검증'해야 합니다.", "To skip the lock screen during an S3/S4 test, first use 'Save and validate' for the Windows sign-in credentials in Settings."),
        ("예약 시각의 재개가 자동 깨우기로 확인되지 않았습니다.", "The resume near the scheduled time was not confirmed as an automatic wake."),
        ("이전 버전의 전원 작업 또는 보안 설정을 정리하지 못했습니다. 다음 앱 시작 때 관리자 권한 정리를 다시 시도합니다. 자세한 원인은 진단 로그에서 확인할 수 있습니다.", "Legacy power tasks or security settings could not be cleaned up. Administrator cleanup will be attempted again at the next app start. See diagnostic logs for details."),
        ("S3/S4 1회 자동 로그인에서 사용한 임시 로그인 요구 설정을 복원하지 못했습니다. 관리자 권한 요청을 승인한 뒤 앱을 다시 실행하세요. 자세한 내용은 진단 로그에서 확인할 수 있습니다.", "The temporary sign-in requirement used for one-time S3/S4 sign-in could not be restored. Approve the administrator request and restart the app. See diagnostic logs for details."),
        ("지원하지 않는 예약 동작입니다.", "This schedule action is not supported."),
        ("이 PC는 S3 절전을 지원합니다. Task Scheduler WakeToRun 실제 테스트가 필요합니다.", "This PC supports S3 sleep. A real Task Scheduler WakeToRun test is required."),
        ("이 PC의 펌웨어가 S3 절전을 제공하지 않습니다.", "This PC's firmware does not provide S3 sleep."),
        ("이 PC의 펌웨어가 최대 절전을 제공하지 않습니다.", "This PC's firmware does not provide hibernation."),
        ("펌웨어는 S4를 제공하지만 Windows 최대 절전이 현재 비활성화되어 있습니다.", "The firmware provides S4, but Windows hibernation is currently disabled."),
        ("최대 절전이 활성화되어 있습니다. WakeToRun 실제 테스트가 필요합니다.", "Hibernation is enabled. A real WakeToRun test is required."),
        ("빠른 설정", "Quick setup"),
        ("버튼을 누른 현재 로컬 시각을 기준으로 날짜와 시간을 채웁니다. 이후에도 직접 수정할 수 있습니다.", "These buttons fill the date and time from the current local time when clicked. You can still edit them afterward."),
        ("1시간 뒤", "In 1 hour"),
        ("2시간 뒤", "In 2 hours"),
        ("예약한 전원 동작은 정확히 5분 전에 경고합니다. 저장하지 않은 작업이 없도록 미리 확인하세요.", "A warning appears exactly five minutes before a scheduled power action. Save your work in advance."),
        ("완전 종료 예약은 정확히 5분 전에 경고합니다. 완전히 종료된 PC는 앱의 자동 시작 예약으로 다시 켤 수 없습니다.", "A warning appears exactly five minutes before a full shutdown. This app cannot turn a fully shut-down PC back on for a scheduled wake."),
        ("완전 종료 예약 확인", "Confirm full-shutdown schedule"),
        ("완전 종료 전에 확인하세요", "Check before full shutdown"),
        ("예약 시각에는 정상 종료를 먼저 시도하고, 30초 안에 끝나지 않으면 강제 종료합니다. 완전히 종료된 뒤에는 앱의 자동 시작 예약으로 PC를 다시 켤 수 없습니다.", "At the scheduled time, the app first requests a graceful shutdown and forces shutdown if it has not completed within 30 seconds. After full shutdown, this app cannot turn the PC back on for a scheduled wake."),
        ("지금 완전 종료", "Shut down now")
    ];

    private static readonly (string Korean, string English)[] PhraseTranslations =
    [
        ("S3/S4 1회 자동 로그인의 임시 로그인 요구 설정이 아직 복원되지 않았습니다. 관리자 권한 복구가 필요합니다.", "The temporary sign-in requirement used for one-time S3/S4 sign-in has not been restored. Administrator recovery is required."),
        ("이전 버전의 보안 설정을 해제하지 못했습니다. 관리자 권한 정리가 필요합니다.", "A legacy security setting could not be removed. Administrator cleanup is required."),
        ("미래 예약이 없습니다.", "There are no future schedules."),
        ("미래 예약 ", "Future schedules: "),
        ("등록됨: ", "Registered: "),
        ("다음 예약: ", "Next schedule: "),
        ("버전 ", "Version "),
        (" 상태로 전환하는 중…", " state transition in progress…"),
        (" 예약을 준비하는 중…", " schedule is being prepared…"),
        (" 예약 준비", " schedule preparation"),
        (" 테스트 실패", " test failed"),
        (" 테스트", " test"),
        (" 결과 확인", " result confirmation"),
        (" 테스트 최종 확인", " final test confirmation"),
        ("자동 깨우기 실기 테스트", "real wake test"),
        ("S3 절전", "S3 sleep"),
        ("S4 최대 절전", "S4 hibernation"),
        ("예약 시각", "scheduled time"),
        ("후속 프로그램", "follow-up programs"),
        ("완전 종료", "full shutdown"),
        ("자동 시작 예약은 PC를 다시 켤 수 없습니다.", "the scheduled wake cannot turn the PC back on."),
        ("사용자 세션", "user session"),
        ("자동 깨우기", "scheduled wake"),
        ("실기 테스트", "real test"),
        ("로그인 정보", "sign-in credentials"),
        ("예약 삭제", "Delete schedule"),
        ("기록 삭제", "Clear history"),
        ("예약을 저장할 수 없습니다", "Unable to save schedule"),
        ("자동 시작 방식 확인", "Confirm wake mode"),
        ("실행 임박 경고", "Action approaching"),
        ("이전 버전 설정 정리", "Legacy settings cleanup"),
        ("1회 자동 로그인 복구 필요", "One-time sign-in recovery required"),
        ("1회 자동 로그인 정보 필요", "One-time sign-in credentials required"),
        ("로그인 검증 성공", "Sign-in validation succeeded"),
        ("로그인 테스트 성공", "Sign-in test succeeded"),
        ("로그인 정보 제거", "Remove sign-in credentials"),
        ("현재 시스템은 S3 절전을 지원하지 않습니다.", "This system does not support S3 sleep."),
        ("Windows 최대 절전 기능", "Windows hibernation"),
        ("관리자 터미널", "an elevated terminal"),
        ("다시 진단", "Run diagnostics again"),
        ("저장하지 않은 작업을 먼저 저장하세요.", "Save any unsaved work first."),
        ("계속하시겠습니까?", "Continue?"),
        ("선택한 예약을 삭제하시겠습니까?", "Delete the selected schedule?"),
        ("개를 실행할 방법을 선택하세요.", " programs should run."),
        ("동작 실행을 시작했습니다.", " action execution started."),
        ("전원 전환과 복귀 증거를 확인해 완료 처리했습니다.", " power transition and resume evidence was verified and marked complete."),
        ("실행은 시작됐지만 완료 여부를 입증할 증거가 부족합니다.", " execution started, but there is not enough evidence to prove completion."),
        ("작업이 오류 결과로 종료되어 실패 처리했습니다.", " task ended with an error result and was marked failed."),
        ("전원 전환 명령이 실패했습니다.", " power transition command failed."),
        ("상태에서 복귀한 Windows 전원 이벤트를 확인해 완료 처리했습니다.", " resume was verified from Windows power events and marked complete."),
        ("미실행 — 예약 시각에 전원 작업이 실행된 증거가 없습니다.", "Missed — there is no evidence that the power task ran at the scheduled time."),
        ("사용자 선택으로 ", "User selected ")
    ];
#endif
}
