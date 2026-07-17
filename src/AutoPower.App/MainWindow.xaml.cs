using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using AutoPower.Core;
using AutoPower.Windows;
using MessageBox = AutoPower.App.LocalizedMessageBox;

namespace AutoPower.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel = new();
    private TrayService? _tray;
    private bool _loadingSettings;
    private bool _evaluatingSleepWakeTest;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _viewModel;
        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;
    }

    public bool AllowClose { get; set; }
    public string NextScheduleSummary => _viewModel.NextScheduleSummary;

    public void AttachTray(TrayService tray) => _tray = tray;

    public void ShowFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    public void ShowNewSchedule() => _ = ShowEditorAsync(null);

    public async Task PauseAllAsync()
    {
        try
        {
            await AppServices.Helper.RunAsync(["pause-all"]);
            _tray?.ShowBalloon("eslee Auto Power", "모든 미래 예약을 일시 중지했습니다.");
            await _viewModel.RefreshAsync();
        }
        catch (Exception error)
        {
            ShowError(error);
        }
    }

    public void RequestExit()
    {
        if (string.Equals(AppServices.Store.GetSettingAsync("hide-exit-warning").GetAwaiter().GetResult(), "1", StringComparison.Ordinal))
        {
            ((App)System.Windows.Application.Current).ExitApplication();
            return;
        }

        var dialog = new ExitConfirmationWindow { Owner = this };
        if (dialog.ShowDialog() == true)
        {
            if (dialog.DoNotShowAgain)
            {
                _ = AppServices.Store.SetSettingAsync("hide-exit-warning", "1");
            }

            ((App)System.Windows.Application.Current).ExitApplication();
        }
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        await RecoverResumeSignInAsync();
        await RetireRemovedPowerFeaturesAsync();
        await RefreshAsync();
        RefreshCredentialUi();
        if (await AppServices.Store.GetSettingAsync("welcome-shown") is null)
        {
            var welcome = new WelcomeWindow { Owner = this };
            var showCompatibility = welcome.ShowDialog() == true;
            await AppServices.Store.SetSettingAsync("welcome-shown", "1");
            if (showCompatibility)
            {
                SelectPage(3, "호환성", "이 PC에서 실제로 사용할 수 있는 기능 진단");
            }
        }

        await EvaluateSleepWakeTestAsync();
    }

    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (!AllowClose)
        {
            e.Cancel = true;
            Hide();
            _tray?.ShowBalloon("eslee Auto Power", "앱이 시스템 트레이에서 계속 실행됩니다.");
        }
    }

    private void Reservations_Click(object sender, RoutedEventArgs e) => SelectPage(0, "예약", "통합 자동 시작 예약과 전원 동작");
    private void History_Click(object sender, RoutedEventArgs e) => SelectPage(1, "기록", "완료·실패·건너뜀·놓침 및 복구 기록");
    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        RefreshCredentialUi();
        SelectPage(2, "설정", "Windows 시작, 로그인 정보 및 진단 로그");
    }
    private void Compatibility_Click(object sender, RoutedEventArgs e) => SelectPage(3, "호환성", "이 PC에서 실제로 사용할 수 있는 기능 진단");
    private void About_Click(object sender, RoutedEventArgs e) => new AboutWindow { Owner = this }.ShowDialog();

    private void SelectPage(int index, string title, string subtitle)
    {
        Pages.SelectedIndex = index;
        PageTitle.Text = AppText.T(title);
        PageSubtitle.Text = AppText.T(subtitle);
    }

    private void New_Click(object sender, RoutedEventArgs e) => ShowNewSchedule();

    private async void EnterS3_Click(object sender, RoutedEventArgs e) =>
        await EnterLowPowerStateAsync(PowerActionType.Sleep);

    private async void EnterS4_Click(object sender, RoutedEventArgs e) =>
        await EnterLowPowerStateAsync(PowerActionType.Hibernate);

    private async Task EnterLowPowerStateAsync(PowerActionType state)
    {
        try
        {
            var snapshot = PowerCapabilityDetector.Detect();
            if (state == PowerActionType.Sleep && !snapshot.S3Available)
            {
                MessageBox.Show("현재 시스템은 S3 절전을 지원하지 않습니다.", "S3 절전",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (state == PowerActionType.Hibernate && (!snapshot.S4Available || !snapshot.HibernateEnabled))
            {
                MessageBox.Show(
                    "S4 최대 절전을 사용하려면 Windows 최대 절전 기능이 활성화되어 있어야 합니다. 관리자 터미널에서 'powercfg /hibernate on'을 실행한 뒤 다시 진단하세요. 앱은 이 설정을 자동으로 변경하지 않습니다.",
                    "S4 최대 절전",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            var selected = _viewModel.SelectedSchedule?.Schedule;
            var candidate = IsWakeReady(selected) ? selected : _viewModel.NextPendingWakeSchedule;
            if (candidate is not null && PowerSchedulePolicy.RequiredLowPowerState(candidate.ActionType) == state)
            {
                await PrepareWakeScheduleAsync(candidate);
                return;
            }

            var stateName = AppText.T(state == PowerActionType.Sleep ? "S3 절전" : "S4 최대 절전");
            if (candidate is not null)
            {
                var required = AppText.T(PowerSchedulePolicy.WakeStateName(candidate.ActionType));
                var mismatchMessage = AppText.IsEnglish
                    ? $"The nearest wake schedule requires {required}. Entering {stateName} now will not prepare that schedule.\n\nEnter {stateName} without preparing a schedule?"
                    : $"가장 가까운 자동 시작 예약은 {required} 방식입니다. 지금 {stateName}으로 전환하면 해당 예약을 준비하지 않습니다.\n\n예약 없이 {stateName}으로 전환하시겠습니까?";
                if (MessageBox.Show(
                        mismatchMessage,
                        "자동 시작 방식 확인",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Warning,
                        MessageBoxResult.No) != MessageBoxResult.Yes)
                {
                    return;
                }
            }
            else if (MessageBox.Show(
                         AppText.IsEnglish
                             ? $"Enter {stateName} now. Save any unsaved work first. Continue?"
                             : $"지금 {stateName} 상태로 전환합니다. 저장하지 않은 작업을 먼저 저장하세요. 계속하시겠습니까?",
                         stateName,
                         MessageBoxButton.YesNo,
                         MessageBoxImage.Warning,
                         MessageBoxResult.No) != MessageBoxResult.Yes)
            {
                return;
            }

            _viewModel.StatusMessage = $"{stateName} 상태로 전환하는 중…";
            await AppServices.Helper.RunAsync(["enter-low-power", "--state", state == PowerActionType.Sleep ? "s3" : "s4"]);
            await RefreshAsync();
        }
        catch (Exception error)
        {
            ShowError(error);
            await RefreshAsync();
        }
    }

    private static bool IsWakeReady(PowerSchedule? schedule) =>
        schedule is not null &&
        PowerSchedulePolicy.IsWakeSchedule(schedule.ActionType) &&
        schedule.IsEnabled &&
        schedule.Status == ScheduleStatus.Pending &&
        schedule.ScheduledLocalDateTime >= DateTime.Now + TimeSpan.FromMinutes(1);

    private async void Edit_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectedSchedule is null)
        {
            MessageBox.Show("수정할 예약을 선택하세요.", "eslee Auto Power", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        await ShowEditorAsync(_viewModel.SelectedSchedule.Schedule);
    }

    private async Task ShowEditorAsync(PowerSchedule? existing)
    {
        PowerCapabilitySnapshot capability;
        IReadOnlyList<CompatibilityResult> compatibility;
        try
        {
            capability = PowerCapabilityDetector.Detect();
            compatibility = await AppServices.Store.GetCompatibilityAsync();
        }
        catch (Exception error)
        {
            ShowError(error);
            return;
        }

        var editor = new ScheduleEditorWindow(existing, capability, compatibility) { Owner = this };
        if (editor.ShowDialog() != true || editor.ResultSchedule is null)
        {
            return;
        }

        if (editor.ResultSchedule.OneTimeAutoLogonEnabled && !AppServices.Credentials.Exists())
        {
            MessageBox.Show(
                "앱 관리형 1회 자동 로그인을 사용하려면 설정에서 Windows 사용자 이름과 계정 암호를 먼저 저장·검증해야 합니다.",
                "로그인 정보 필요",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            Settings_Click(this, new RoutedEventArgs());
            return;
        }

        if (existing is not null && ScheduleValidator.NeedsImminentWarning(existing, DateTime.Now))
        {
            var answer = MessageBox.Show(
                "예약 실행이 임박했습니다. 이미 Windows 또는 시스템 전원 설정에 작업이 준비되었을 수 있습니다. 변경 사항을 적용하시겠습니까?",
                "실행 임박 경고", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (answer != MessageBoxResult.Yes)
            {
                return;
            }
        }

        try
        {
            _viewModel.StatusMessage = "예약을 저장하는 중…";
            var validation = await AppServices.Coordinator.SaveAsync(editor.ResultSchedule);
            if (!validation.IsValid)
            {
                MessageBox.Show(string.Join(Environment.NewLine, validation.Issues.Select(issue => "• " + issue.Message)),
                    "예약을 저장할 수 없습니다", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            await RefreshAsync();
            if (PowerSchedulePolicy.IsWakeSchedule(editor.ResultSchedule.ActionType) &&
                editor.ResultSchedule.ScheduledLocalDateTime > DateTime.Now + TimeSpan.FromMinutes(1) &&
                MessageBox.Show(
                    AppText.IsEnglish
                        ? $"The wake schedule was registered using {AppText.T(PowerSchedulePolicy.WakeStateName(editor.ResultSchedule.ActionType))}.\n\nPrepare it and enter the matching power state now? You can also use the matching button on the main screen later."
                        : $"자동 시작 예약을 등록했습니다. 선택된 전원 방식은 {PowerSchedulePolicy.WakeStateName(editor.ResultSchedule.ActionType)}입니다.\n\n지금 이 예약을 준비하고 해당 전원 상태로 전환하시겠습니까? 나중에 메인 화면에서 같은 전원 상태의 버튼을 눌러도 됩니다.",
                    "자동 시작 예약 등록 완료",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question) == MessageBoxResult.Yes)
            {
                await PrepareWakeScheduleAsync(editor.ResultSchedule);
            }
        }
        catch (Exception error)
        {
            ShowError(error);
        }
    }

    private async Task PrepareWakeScheduleAsync(PowerSchedule schedule)
    {
        try
        {
            if (!PowerSchedulePolicy.IsWakeSchedule(schedule.ActionType))
            {
                throw new InvalidOperationException("선택한 항목은 S3/S4 깨우기 예약이 아닙니다.");
            }

            if (!schedule.IsEnabled || schedule.Status != ScheduleStatus.Pending ||
                schedule.ScheduledLocalDateTime < DateTime.Now + TimeSpan.FromMinutes(1))
            {
                throw new InvalidOperationException("1분 이상 남은 활성 깨우기 예약만 준비할 수 있습니다.");
            }

            var snapshot = PowerCapabilityDetector.Detect();
            if (schedule.ActionType == PowerActionType.WakeFromSleep && !snapshot.S3Available)
            {
                throw new InvalidOperationException("현재 시스템은 S3 절전을 지원하지 않습니다.");
            }

            if (schedule.ActionType == PowerActionType.WakeFromHibernate && (!snapshot.S4Available || !snapshot.HibernateEnabled))
            {
                MessageBox.Show(
                    "S4 최대 절전 깨우기를 준비하려면 Windows 최대 절전 기능을 사용자가 먼저 활성화해야 합니다. 관리자 터미널에서 'powercfg /hibernate on'을 실행하고 다시 진단하세요.",
                    "S4 예약 준비",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            var stateName = AppText.T(PowerSchedulePolicy.WakeStateName(schedule.ActionType));
            var autoLogonNotice = schedule.OneTimeAutoLogonEnabled
                ? AppText.IsEnglish
                    ? "\n\nApp-managed one-time sign-in is enabled. The lock-screen requirement is temporarily disabled until the next resume, so waking the PC early may also open the session without sign-in."
                    : "\n\n앱 관리형 1회 자동 로그인이 켜져 있습니다. 다음 복귀까지 잠금 화면 요구가 임시로 해제되므로, 예약 전에 PC를 일찍 깨우는 경우에도 로그인 없이 열릴 수 있습니다."
                : string.Empty;
            var preparationMessage = AppText.IsEnglish
                ? $"After verifying the registered wake task for {schedule.ScheduledLocalDateTime:MMM dd HH:mm}, the PC will enter {stateName} now.\n\nSave any unsaved work first.{autoLogonNotice}\n\nContinue?"
                : $"{schedule.ScheduledLocalDateTime:MM월 dd일 HH:mm}에 자동으로 깨우도록 등록 상태를 확인한 뒤 지금 {stateName} 상태로 전환합니다.\n\n저장하지 않은 작업을 먼저 저장하세요.{autoLogonNotice}\n\n계속하시겠습니까?";
            if (MessageBox.Show(
                    preparationMessage,
                    $"{stateName} 예약 준비",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning,
                    MessageBoxResult.No) != MessageBoxResult.Yes)
            {
                return;
            }

            _viewModel.StatusMessage = $"{stateName} 예약을 준비하는 중…";
            await AppServices.Helper.RunAsync(["prepare-wake", "--schedule", schedule.Id.ToString("D")]);
            await RefreshAsync();
            if (DateTime.Now < schedule.ScheduledLocalDateTime - TimeSpan.FromMinutes(1))
            {
                MessageBox.Show(
                    AppText.IsEnglish
                        ? $"The PC resumed from {stateName} before the scheduled time. The schedule is still active. To use scheduled wake, press the matching power-state button again on the main screen."
                        : $"예약 시각 전에 {stateName} 상태에서 복귀했습니다. 예약은 아직 활성 상태입니다. 자동 시작을 원하면 메인 화면에서 같은 전원 상태의 버튼을 다시 누르세요.",
                    "예약 시각 전 복귀",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            else
            {
                _tray?.ShowBalloon(
                    "예약 깨우기 복귀",
                    AppText.IsEnglish
                        ? $"The user session resumed from {stateName}. Follow-up programs linked to the schedule will run after the resume-ready check."
                        : $"{stateName} 상태에서 사용자 세션으로 복귀했습니다. 예약 시각에 연결된 후속 프로그램은 Resume 준비 확인 후 처리됩니다.");
            }
        }
        catch (Exception error)
        {
            await RecoverResumeSignInAsync();
            ShowError(error);
            await RefreshAsync();
        }
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        var selected = _viewModel.SelectedSchedule?.Schedule;
        if (selected is null)
        {
            return;
        }

        var text = ScheduleValidator.NeedsImminentWarning(selected, DateTime.Now)
            ? "실행이 임박하여 이미 Windows 또는 시스템 전원 설정에 작업이 준비되었을 수 있습니다. 계속하시겠습니까?"
            : "선택한 예약을 삭제하시겠습니까?";
        if (MessageBox.Show(text, "예약 삭제", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            await AppServices.Coordinator.DeleteAsync(selected.Id);
            await RefreshAsync();
        }
        catch (Exception error)
        {
            ShowError(error);
        }
    }

    private async void Toggle_Click(object sender, RoutedEventArgs e)
    {
        var selected = _viewModel.SelectedSchedule?.Schedule;
        if (selected is null)
        {
            return;
        }

        try
        {
            await AppServices.Coordinator.SetEnabledAsync(selected.Id, !selected.IsEnabled);
            await RefreshAsync();
        }
        catch (Exception error)
        {
            ShowError(error);
        }
    }

    private async void ClearHistory_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show("모든 실행 기록을 삭제하시겠습니까?", "기록 삭제", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
        {
            await AppServices.Store.ClearHistoryAsync();
            await RefreshAsync();
        }
    }

    private void StartupCheck_Click(object sender, RoutedEventArgs e)
    {
        if (_loadingSettings)
        {
            return;
        }

        try
        {
            StartupManager.SetEnabled(StartupCheck.IsChecked == true, AppServices.Layout.AppPath);
        }
        catch (Exception error)
        {
            ShowError(error);
        }
    }

    private void OpenLogs_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(AppPaths.LogDirectory);
        Process.Start(new ProcessStartInfo("explorer.exe") { UseShellExecute = false, ArgumentList = { AppPaths.LogDirectory } })?.Dispose();
    }

    private async void SaveCredential_Click(object sender, RoutedEventArgs e)
    {
        var userNameInput = CredentialUserNameInput.Text.Trim();
        var password = CredentialPasswordInput.Password;
        if (string.IsNullOrWhiteSpace(userNameInput) || string.IsNullOrWhiteSpace(password))
        {
            MessageBox.Show("Windows 사용자 이름과 계정 암호를 모두 입력하세요. PIN은 사용할 수 없습니다.",
                "1회 자동 로그인", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var previous = AppServices.Credentials.Read();
        string? transferPath = null;
        try
        {
            var userName = WindowsCredentialIdentity.Normalize(userNameInput);
            AppServices.Credentials.Save(userName, password);
            transferPath = CredentialTransferFile.Create(userName, password);
            await AppServices.Helper.RunAsync(["sync-credential", "--file", transferPath]);
            transferPath = null;
            RefreshCredentialUi();
            MessageBox.Show(
                "Windows 계정과 암호를 실제 로그온 API로 검증하고 보호 저장소에 등록했습니다.",
                "로그인 검증 성공",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception error)
        {
            try
            {
                if (previous is null)
                {
                    AppServices.Credentials.Delete();
                }
                else
                {
                    AppServices.Credentials.Save(previous.UserName, previous.Password);
                }
            }
            catch (Exception rollbackError)
            {
                AppServices.Logger.Error("credential.user-copy-rollback-failed", rollbackError);
            }

            ShowError(error);
        }
        finally
        {
            CredentialTransferFile.DeleteIfPresent(transferPath);
            CredentialPasswordInput.Clear();
            RefreshCredentialUi();
        }
    }

    private async void TestCredential_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!AppServices.Credentials.Exists())
            {
                throw new InvalidOperationException("먼저 Windows 로그인 자격 증명을 저장하세요.");
            }

            await AppServices.Helper.RunAsync(["test-credential"]);
            MessageBox.Show(
                "저장된 Windows 계정과 암호로 실제 로그온 자격 증명 검증에 성공했습니다.",
                "로그인 테스트 성공",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception error)
        {
            ShowError(error);
        }
        finally
        {
            CredentialPasswordInput.Clear();
            RefreshCredentialUi();
        }
    }

    private async void RemoveCredential_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(
                "앱이 보호 저장소에 등록한 Windows 로그인 정보를 제거하시겠습니까?",
                "로그인 정보 제거",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No) != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            await AppServices.Helper.RunAsync(["delete-credential"]);
            AppServices.Credentials.Delete();
            CredentialPasswordInput.Clear();
            RefreshCredentialUi();
        }
        catch (Exception error)
        {
            ShowError(error);
        }
    }

    private void RefreshCredentialUi()
    {
        try
        {
            var credential = AppServices.Credentials.Read();
            if (credential is null)
            {
                CredentialStatusText.Text = AppText.T("등록된 Windows 로그인 정보가 없습니다.");
                if (string.IsNullOrWhiteSpace(CredentialUserNameInput.Text))
                {
                    CredentialUserNameInput.Text = TaskSchedulerService.CurrentUserName();
                }
            }
            else
            {
                CredentialStatusText.Text = AppText.T($"등록됨: {credential.UserName}");
                CredentialUserNameInput.Text = credential.UserName;
            }
        }
        catch (Exception error)
        {
            CredentialStatusText.Text = AppText.T("등록 상태를 확인하지 못했습니다.");
            AppServices.Logger.Error("credential.status-read-failed", error);
        }
    }

    private async void Diagnose_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var snapshot = PowerCapabilityDetector.Detect();
            await SaveProbeResultsPreservingPhysicalTestsAsync(snapshot.Results);

            await RefreshAsync();
        }
        catch (Exception error)
        {
            ShowError(error);
        }
    }

    private async void S3Test_Click(object sender, RoutedEventArgs e) =>
        await StartSleepWakeTestAsync(SleepWakeTestTarget.S3);

    private async void S4Test_Click(object sender, RoutedEventArgs e) =>
        await StartSleepWakeTestAsync(SleepWakeTestTarget.S4);

    private async Task StartSleepWakeTestAsync(SleepWakeTestTarget target)
    {
        try
        {
            EnsureNoWakeTestInProgress();
            var snapshot = PowerCapabilityDetector.Detect();
            await SaveProbeResultsPreservingPhysicalTestsAsync(snapshot.Results);
            if (target == SleepWakeTestTarget.S3 && !snapshot.S3Available)
            {
                MessageBox.Show(
                    "현재 시스템 펌웨어와 Windows 전원 정보에서 S3 절전을 지원하지 않아 실제 테스트를 시작할 수 없습니다.",
                    "S3 절전 테스트",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            if (target == SleepWakeTestTarget.S4 && (!snapshot.S4Available || !snapshot.HibernateEnabled))
            {
                var reason = AppText.T(!snapshot.S4Available
                    ? "현재 시스템 펌웨어가 S4 최대 절전 상태를 제공하지 않습니다."
                    : "펌웨어는 S4를 지원하지만 Windows 최대 절전 기능이 현재 비활성화되어 있습니다.");
                MessageBox.Show(
                    AppText.IsEnglish
                        ? $"{reason}\n\nTo test it, enable hibernation by running 'powercfg /hibernate on' in an elevated terminal, then choose 'Run diagnostics again' in the app. The app never changes this setting without your consent."
                        : $"{reason}\n\n테스트하려면 사용자가 관리자 터미널에서 'powercfg /hibernate on'을 실행해 최대 절전을 활성화한 뒤 앱의 '다시 진단'을 눌러야 합니다. 앱은 사용자 동의 없이 이 설정을 변경하지 않습니다.",
                    "S4 최대 절전 테스트",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            if (!AppServices.Credentials.Exists())
            {
                MessageBox.Show(
                    "S3/S4 테스트에서 잠금 화면을 건너뛰려면 설정에서 Windows 로그인 정보를 먼저 '저장 및 검증'해야 합니다.",
                    "1회 자동 로그인 정보 필요",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            var targetName = AppText.T(SleepWakeTestStateStore.TargetName(target));
            var testStartMessage = AppText.IsEnglish
                ? $"Start a real {targetName} scheduled-wake test.\n\nThe saved Windows credentials will be validated again, a wake task will be created for exactly 2 minutes from now, and the PC will enter {targetName}. This resume skips the lock screen once and restores the original sign-in requirement after the desktop is ready.\n\nSave any unsaved work first. Continue?"
                : $"{targetName} 자동 깨우기 실기 테스트를 시작합니다.\n\n등록된 Windows 로그인 정보를 다시 검증하고, 정확히 2분 뒤 Wake 예약을 만든 후 PC를 실제 {targetName} 상태로 전환합니다. 이번 복귀에서는 잠금 화면을 건너뛰고 원래 로그인 요구 설정은 데스크톱 복귀 후 자동 복원합니다.\n\n저장하지 않은 작업을 먼저 저장하세요. 계속하시겠습니까?";
            if (MessageBox.Show(
                    testStartMessage,
                    $"{targetName} 테스트",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning) != MessageBoxResult.Yes)
            {
                return;
            }

            var wake = SleepWakeTestPolicy.CreateWakeTime(DateTime.Now);
            if (MessageBox.Show(
                    AppText.IsEnglish
                        ? $"The test wake time is {wake:HH:mm}. Enter {targetName} now?"
                        : $"테스트 Wake 시각은 {wake:HH:mm}입니다. 지금 {targetName} 상태로 전환하시겠습니까?",
                    $"{targetName} 테스트 최종 확인",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning) != MessageBoxResult.Yes)
            {
                return;
            }

            var schedule = SleepWakeTestPolicy.CreateSchedule(target, wake);
            var allSchedules = await AppServices.Store.GetAllSchedulesAsync();
            var validation = ScheduleValidator.Validate(schedule, DateTime.Now, allSchedules);
            if (!validation.IsValid)
            {
                throw new InvalidOperationException(string.Join(Environment.NewLine, validation.Issues.Select(issue => issue.Message)));
            }

            await AppServices.Store.SaveScheduleAsync(schedule);
            await AppServices.Store.AddHistoryAsync(
                schedule.Id,
                target == SleepWakeTestTarget.S3 ? "S3WakeTest" : "S4WakeTest",
                ResultKind.Information,
                wake,
                $"{targetName} 실기 테스트용 임시 Wake 예약을 만들었습니다.");

            var stateStore = new SleepWakeTestStateStore();
            stateStore.Prepare(schedule.Id, target, wake);
            await AppServices.Helper.RunAsync([
                "run-sleep-wake-test",
                "--state",
                target == SleepWakeTestTarget.S3 ? "s3" : "s4",
                "--schedule",
                schedule.Id.ToString("D")]);
            await Task.Delay(TimeSpan.FromSeconds(2));
            await EvaluateSleepWakeTestAsync();
        }
        catch (Exception error)
        {
            var stateStore = new SleepWakeTestStateStore();
            var state = stateStore.Read();
            if (state?.Stage == SleepWakeTestStage.Prepared)
            {
                stateStore.MarkFailed("테스트 준비 또는 전원 상태 전환을 완료하지 못했습니다.");
            }

            if (state is not null)
            {
                var schedule = await AppServices.Store.GetScheduleAsync(state.ScheduleId);
                if (schedule?.Status == ScheduleStatus.Pending)
                {
                    await AppServices.Store.UpdateScheduleStateAsync(state.ScheduleId, false, ScheduleStatus.Failed);
                }
            }

            await EvaluateSleepWakeTestAsync();
            ShowError(error);
        }
    }

    private async Task EvaluateSleepWakeTestAsync()
    {
        if (_evaluatingSleepWakeTest)
        {
            return;
        }

        _evaluatingSleepWakeTest = true;
        try
        {
            var stateStore = new SleepWakeTestStateStore();
            var state = stateStore.EvaluateResume(DateTimeOffset.Now);
            if (state is null || state.Stage is SleepWakeTestStage.Prepared or SleepWakeTestStage.TransitionRequested)
            {
                return;
            }

            var targetName = AppText.T(SleepWakeTestStateStore.TargetName(state.Target));
            var capability = state.Target == SleepWakeTestTarget.S3
                ? CompatibilityCapabilities.S3Wake
                : CompatibilityCapabilities.S4Wake;
            var eventType = state.Target == SleepWakeTestTarget.S3 ? "S3WakeTest" : "S4WakeTest";
            if (state.Stage == SleepWakeTestStage.CandidateResumeObserved)
            {
                var confirmed = MessageBox.Show(
                    AppText.IsEnglish
                        ? $"The PC resumed from {targetName} near the scheduled time.\n\nDid it wake automatically from the schedule, without you using the power button, keyboard, mouse, or another method?"
                        : $"예약 시각 근처에 {targetName} 상태에서 재개되었습니다.\n\n전원 버튼, 키보드, 마우스 또는 다른 방법으로 직접 깨우지 않았고 예약에 의해 자동으로 깨어났습니까?",
                    $"{targetName} 결과 확인",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);
                if (confirmed == MessageBoxResult.Yes)
                {
                    state = stateStore.ConfirmAutomaticResume();
                    await AppServices.Store.SaveCompatibilityAsync(new CompatibilityResult(
                        capability,
                        CapabilityStatus.Confirmed,
                        DateTimeOffset.UtcNow,
                        $"{targetName} 상태에서 예약 시각 자동 깨우기를 실제 테스트로 확인했습니다."));
                    await AppServices.Store.AddHistoryAsync(
                        state.ScheduleId,
                        eventType,
                        ResultKind.Success,
                        state.RequestedWakeLocalTime,
                        $"{targetName} 예약 시각 자동 깨우기 실기 테스트에 성공했습니다.");
                }
                else
                {
                    state = stateStore.MarkFailed("예약 시각의 재개가 자동 깨우기로 확인되지 않았습니다.");
                    await SaveSleepWakeFailureAsync(state, capability, eventType);
                }
            }
            else if (state.Stage == SleepWakeTestStage.Failed)
            {
                await SaveSleepWakeFailureAsync(state, capability, eventType);
                MessageBox.Show(state.Detail, $"{targetName} 테스트 실패", MessageBoxButton.OK, MessageBoxImage.Warning);
            }

            stateStore.Clear();
            await RefreshAsync();
        }
        finally
        {
            _evaluatingSleepWakeTest = false;
        }
    }

    private static async Task SaveSleepWakeFailureAsync(
        SleepWakeTestState state,
        string capability,
        string eventType)
    {
        var targetName = SleepWakeTestStateStore.TargetName(state.Target);
        await AppServices.Store.SaveCompatibilityAsync(new CompatibilityResult(
            capability,
            CapabilityStatus.UnsupportedOrFailed,
            DateTimeOffset.UtcNow,
            state.Detail));
        await AppServices.Store.AddHistoryAsync(
            state.ScheduleId,
            eventType,
            ResultKind.Failure,
            state.RequestedWakeLocalTime,
            $"{targetName} 자동 깨우기 실기 테스트에 실패했습니다. {state.Detail}");
    }

    private static void EnsureNoWakeTestInProgress()
    {
        var sleepState = new SleepWakeTestStateStore().Read();
        if (sleepState?.Stage is SleepWakeTestStage.Prepared or
            SleepWakeTestStage.TransitionRequested or
            SleepWakeTestStage.CandidateResumeObserved)
        {
            throw new InvalidOperationException("이미 진행 중인 S3 또는 S4 Wake 테스트가 있습니다. 현재 테스트 결과를 먼저 처리하세요.");
        }

    }

    private static async Task SaveProbeResultsPreservingPhysicalTestsAsync(IEnumerable<CompatibilityResult> results)
    {
        var existing = (await AppServices.Store.GetCompatibilityAsync())
            .ToDictionary(item => item.Capability, StringComparer.Ordinal);
        foreach (var result in results)
        {
            if (existing.TryGetValue(result.Capability, out var saved) &&
                saved.Status is CapabilityStatus.Confirmed or CapabilityStatus.UnsupportedOrFailed)
            {
                continue;
            }

            await AppServices.Store.SaveCompatibilityAsync(result);
        }
    }

    private static async Task RetireRemovedPowerFeaturesAsync()
    {
        var schedules = await AppServices.Store.GetAllSchedulesAsync();
        var removedSchedules = schedules
            .Where(schedule => PowerSchedulePolicy.IsRemovedSchedule(schedule.ActionType))
            .ToArray();
        foreach (var schedule in removedSchedules.Where(schedule =>
                     schedule.IsEnabled || schedule.Status == ScheduleStatus.Pending))
        {
            await AppServices.Store.SaveScheduleAsync(schedule with
            {
                IsEnabled = false,
                OneTimeAutoLogonEnabled = false,
                Status = ScheduleStatus.Disabled,
                UpdatedAtUtc = DateTimeOffset.UtcNow
            });
            await AppServices.Store.AddHistoryAsync(
                schedule.Id,
                "RemovedPowerFeatureMigration",
                ResultKind.Warning,
                schedule.ScheduledLocalDateTime,
                "이 버전에서 제공하지 않는 이전 전원 예약을 비활성화했습니다. 기존 데이터는 보존했습니다.");
        }

        var cleanupCompleted = string.Equals(
            await AppServices.Store.GetSettingAsync("removed-power-features-cleanup-v3"),
            "1",
            StringComparison.Ordinal);
        if (!cleanupCompleted)
        {
            try
            {
                await AppServices.Helper.RunAsync(["cleanup-removed-features"]);
                await AppServices.Store.SetSettingAsync("removed-power-features-cleanup-v3", "1");
            }
            catch (Exception error)
            {
                AppServices.Logger.Warning("removed-power-features.cleanup-pending", error.Message);
                MessageBox.Show(
                    "이전 버전의 전원 작업 또는 보안 설정을 정리하지 못했습니다. 다음 앱 시작 때 관리자 권한 정리를 다시 시도합니다. 자세한 원인은 진단 로그에서 확인할 수 있습니다.",
                    "이전 버전 설정 정리",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }
    }

    private static async Task RecoverResumeSignInAsync()
    {
        try
        {
            var journal = new JsonResumeSignInJournalStore().Read();
            if (journal is null || journal.State == ResumeSignInJournalState.Restored)
            {
                return;
            }

            await AppServices.Helper.RunAsync(["restore-resume-signin"]);
        }
        catch (Exception error)
        {
            AppServices.Logger.Error("resume-signin.recovery-failed", error);
            MessageBox.Show(
                "S3/S4 1회 자동 로그인에서 사용한 임시 로그인 요구 설정을 복원하지 못했습니다. 관리자 권한 요청을 승인한 뒤 앱을 다시 실행하세요. 자세한 내용은 진단 로그에서 확인할 수 있습니다.",
                "1회 자동 로그인 복구 필요",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private async Task RefreshAsync()
    {
        await _viewModel.RefreshAsync();
        _loadingSettings = true;
        StartupCheck.IsChecked = StartupManager.IsEnabled;
        _loadingSettings = false;
    }

    private static void ShowError(Exception error) =>
        MessageBox.Show(error.Message, "eslee Auto Power", MessageBoxButton.OK, MessageBoxImage.Error);
}
