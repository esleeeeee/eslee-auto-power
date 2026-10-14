using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using AutoPower.App.Controls;
using AutoPower.App.Services;
using AutoPower.Core;
using AutoPower.Windows;
using MessageBox = AutoPower.App.LocalizedMessageBox;

namespace AutoPower.App;

public partial class MainWindow : ThemedWindow
{
    private readonly MainViewModel _viewModel = new();
    private TrayService? _tray;
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(30) };
    private bool _suppressToggle;
    private bool _evaluatingSleepWakeTest;
    private bool _refreshingAfterActivation;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _viewModel;
        Navigation.ItemsSource = new[]
        {
            new NavItem(Glyphs.Calendar, AppText.T("예약")),
            new NavItem(Glyphs.History, AppText.T("기록")),
            new NavItem(Glyphs.Diagnostic, AppText.T("호환성"))
        };
        Navigation.SelectedIndex = 0;
        VersionFooter.Text = AppText.F("버전 {0}", SettingsWindow.GetDisplayVersion());
        HistorySearchBox.Tag = AppText.T("내용이나 구분으로 검색");
        _clock.Tick += (_, _) => Tick();
        _clock.Start();
        PreviewKeyDown += MainWindow_PreviewKeyDown;
        Loaded += MainWindow_Loaded;
        Activated += MainWindow_Activated;
        Closing += MainWindow_Closing;
    }

    public bool AllowClose { get; set; }
    public string NextScheduleSummary => _viewModel.NextScheduleSummary;
    public string TrayTooltip => _viewModel.TrayTooltip;
    public bool HasPausedSchedules => _viewModel.HasPausedSchedules;

    public void AttachTray(TrayService tray) => _tray = tray;

    public void ShowFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    public void ShowNewSchedule() => _ = ShowEditorAsync(null);

    public Task RefreshFromTrayAsync() => RefreshAsync();

    public async Task PauseAllAsync()
    {
        try
        {
            await AppServices.Coordinator.PauseAllAsync();
            _tray?.ShowBalloon("eslee Auto Power", "모든 미래 예약을 일시 중지했습니다.");
            await RefreshAsync();
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
        if (await AppServices.Store.GetSettingAsync("welcome-shown") is null)
        {
            var welcome = new WelcomeWindow { Owner = this };
            var showCompatibility = welcome.ShowDialog() == true;
            await AppServices.Store.SetSettingAsync("welcome-shown", "1");
            if (showCompatibility)
            {
                Navigation.SelectedIndex = 2;
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
            _ = ShowTrayHintOnceAsync();
        }
    }

    private async void MainWindow_Activated(object? sender, EventArgs e)
    {
        if (!IsLoaded || _refreshingAfterActivation || AppServices.CaptureMode)
        {
            return;
        }

        _refreshingAfterActivation = true;
        try
        {
            await AppServices.ReconcilePowerTransitionsAsync();
            await RefreshAsync();
        }
        catch (Exception error)
        {
            AppServices.Logger.Error("power-transition.activation-refresh-failed", error);
        }
        finally
        {
            _refreshingAfterActivation = false;
        }
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

    private async Task ShowEditorAsync(PowerSchedule? existing, bool duplicate = false)
    {
        PowerCapabilitySnapshot capability;
        IReadOnlyList<CompatibilityResult> compatibility;
        IReadOnlyList<PowerSchedule> schedules;
        try
        {
            capability = PowerCapabilityDetector.Detect();
            compatibility = await AppServices.Store.GetCompatibilityAsync();
            schedules = await AppServices.Store.GetAllSchedulesAsync();
        }
        catch (Exception error)
        {
            ShowError(error);
            return;
        }

        var editor = new ScheduleEditorWindow(existing, capability, compatibility, schedules, duplicate) { Owner = this };
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
            OpenSettings();
            return;
        }

        if (existing is not null && !duplicate && ScheduleValidator.NeedsImminentWarning(existing, DateTime.Now))
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
                MessageBox.Show(string.Join(Environment.NewLine, validation.Issues.Select(issue => "• " + AppText.T(issue.Message))),
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
        _suppressToggle = false;
        _tray?.UpdateStatus();
    }

    private void Tick()
    {
        _viewModel.Tick();
        _tray?.UpdateStatus();
    }

    private static void ShowError(Exception error) => AppDialog.ShowError(null, error);

    private async Task ShowTrayHintOnceAsync()
    {
        try
        {
            if (await AppServices.Store.GetSettingAsync("tray-hint-shown") is null)
            {
                _tray?.ShowBalloon("eslee Auto Power", "앱이 시스템 트레이에서 계속 실행됩니다.");
                await AppServices.Store.SetSettingAsync("tray-hint-shown", "1");
            }
        }
        catch (Exception error)
        {
            AppServices.Logger.Error("tray.hint-failed", error);
        }
    }

    // ----- Navigation -----

    private void Navigation_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var index = Navigation.SelectedIndex;
        if (index < 0)
        {
            return;
        }

        SchedulesPage.Visibility = index == 0 ? Visibility.Visible : Visibility.Collapsed;
        HistoryPage.Visibility = index == 1 ? Visibility.Visible : Visibility.Collapsed;
        CompatibilityPage.Visibility = index == 2 ? Visibility.Visible : Visibility.Collapsed;
    }

    internal void SelectPageForCapture(int index) => Navigation.SelectedIndex = index;

    private void Settings_Click(object sender, RoutedEventArgs e) => OpenSettings();

    private void OpenSettings()
    {
        var existing = System.Windows.Application.Current.Windows.OfType<SettingsWindow>().FirstOrDefault();
        if (existing is not null)
        {
            existing.Activate();
            return;
        }

        new SettingsWindow { Owner = this }.ShowDialog();
    }

    private void MainWindow_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.N && Keyboard.Modifiers == ModifierKeys.Control)
        {
            ShowNewSchedule();
            e.Handled = true;
        }
        else if (e.Key == Key.F5)
        {
            _ = RefreshAsync();
            e.Handled = true;
        }
    }

    // ----- Schedule list -----

    private void ScheduleList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source &&
            ItemsControl.ContainerFromElement(ScheduleList, source) is ListBoxItem { DataContext: ScheduleRow row })
        {
            _ = ShowEditorAsync(row.Schedule);
        }
    }

    private void ScheduleList_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Delete)
        {
            Delete_Click(sender, e);
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && _viewModel.SelectedSchedule is { } row)
        {
            _ = ShowEditorAsync(row.Schedule);
            e.Handled = true;
        }
    }

    // Saved from Checked/Unchecked rather than Click so that a change made through
    // UI Automation (screen readers) is stored as well. Rows are recreated on refresh;
    // the binding then raises Checked with the stored value, which is ignored here.
    private async void ScheduleToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressToggle)
        {
            return;
        }

        if (sender is not System.Windows.Controls.CheckBox { DataContext: ScheduleRow row } toggle)
        {
            // A toggle from a list item that a refresh has already replaced.
            AppServices.Logger.Warning("schedule.toggle-detached", (sender as FrameworkElement)?.DataContext?.GetType().Name ?? "null");
            return;
        }

        var enable = toggle.IsChecked == true;
        if (enable == row.Schedule.IsEnabled)
        {
            return;
        }

        AppServices.Logger.Information("schedule.toggle-requested", $"schedule={row.Schedule.Id:D};enable={enable}");
        if (AppServices.CaptureMode)
        {
            return;
        }

        _suppressToggle = true;
        try
        {
            await AppServices.Coordinator.SetEnabledAsync(row.Schedule.Id, enable);
        }
        catch (Exception error)
        {
            ShowError(error);
        }
        finally
        {
            await RefreshAsync();
        }
    }

    private static ScheduleRow? RowOf(object sender) => (sender as FrameworkElement)?.DataContext as ScheduleRow;

    private void SelectRow(object sender)
    {
        if (RowOf(sender) is { } row)
        {
            _viewModel.SelectedSchedule = row;
        }
    }

    private void EditMenu_Click(object sender, RoutedEventArgs e)
    {
        SelectRow(sender);
        Edit_Click(sender, e);
    }

    private void DuplicateMenu_Click(object sender, RoutedEventArgs e)
    {
        SelectRow(sender);
        Duplicate_Click(sender, e);
    }

    private void ToggleMenu_Click(object sender, RoutedEventArgs e)
    {
        SelectRow(sender);
        Toggle_Click(sender, e);
    }

    private void DeleteMenu_Click(object sender, RoutedEventArgs e)
    {
        SelectRow(sender);
        Delete_Click(sender, e);
    }

    private void ScheduleHistoryMenu_Click(object sender, RoutedEventArgs e)
    {
        SelectRow(sender);
        ScheduleHistory_Click(sender, e);
    }

    private async void Duplicate_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectedSchedule is { } row)
        {
            await ShowEditorAsync(row.Schedule, duplicate: true);
        }
    }

    private void ScheduleHistory_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectedSchedule is not { } row)
        {
            return;
        }

        _viewModel.HistoryScheduleFilter = row;
        Navigation.SelectedIndex = 1;
    }

    // ----- More menu -----

    private void More_Click(object sender, RoutedEventArgs e)
    {
        PauseAllMenu.IsEnabled = _viewModel.HasActiveSchedules;
        ResumeAllMenu.IsEnabled = _viewModel.HasPausedSchedules;
        MoreMenu.PlacementTarget = MoreButton;
        MoreMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        MoreMenu.IsOpen = true;
    }

    private async void PauseAll_Click(object sender, RoutedEventArgs e) => await PauseAllAsync();

    private async void ResumeAll_Click(object sender, RoutedEventArgs e) => await ResumeAllAsync();

    public async Task ResumeAllAsync()
    {
        try
        {
            await AppServices.Coordinator.ResumeAllAsync();
            _tray?.ShowBalloon("eslee Auto Power", "꺼 둔 미래 예약을 모두 다시 켰습니다.");
        }
        catch (Exception error)
        {
            ShowError(error);
        }
        finally
        {
            await RefreshAsync();
        }
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();

    // ----- Attention banner -----

    private async void AttentionHistory_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.HistoryScheduleFilter = null;
        FilterAll.IsChecked = true;
        Navigation.SelectedIndex = 1;
        await AcknowledgeAttentionAsync();
    }

    private async void DismissAttention_Click(object sender, RoutedEventArgs e) => await AcknowledgeAttentionAsync();

    private async Task AcknowledgeAttentionAsync()
    {
        try
        {
            await _viewModel.AcknowledgeAttentionAsync();
        }
        catch (Exception error)
        {
            AppServices.Logger.Error("attention.acknowledge-failed", error);
        }
    }

    // ----- History page -----

    private void HistoryFilter_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.RadioButton { Tag: HistoryResultFilter filter })
        {
            _viewModel.HistoryResultFilter = filter;
        }
    }

    private void ClearHistoryScheduleFilter_Click(object sender, RoutedEventArgs e) => _viewModel.HistoryScheduleFilter = null;
}

/// <summary>Navigation pane entry; <see cref="ToString"/> gives screen readers its name.</summary>
public sealed record NavItem(string Glyph, string Text)
{
    public override string ToString() => Text;
}

