using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using AutoPower.Core;
using AutoPower.Windows;
using MessageBox = AutoPower.App.LocalizedMessageBox;

namespace AutoPower.App;

public partial class ScheduleEditorWindow : Window
{
    private readonly PowerSchedule? _existing;
    private readonly PowerCapabilitySnapshot _capability;
    private readonly Dictionary<string, CompatibilityResult> _compatibility;
    private readonly IReadOnlyList<PowerSchedule> _schedules;

    public ScheduleEditorWindow(
        PowerSchedule? existing,
        PowerCapabilitySnapshot capability,
        IReadOnlyList<CompatibilityResult> compatibility,
        IReadOnlyList<PowerSchedule> schedules)
    {
        InitializeComponent();
        _existing = existing;
        _capability = capability;
        _compatibility = compatibility.ToDictionary(item => item.Capability, StringComparer.Ordinal);
        _schedules = schedules;
        DataContext = this;
        DateInput.DisplayDateStart = DateTime.Today;

        ActionInput.ItemsSource = new[]
        {
            new ActionOption(AppText.T("자동 시작 예약"), EditorAction.AutomaticStart),
            new ActionOption(AppText.T("예약 시각에 완전 종료"), EditorAction.Shutdown),
            new ActionOption(AppText.T("예약 시각에 최대 절전 진입"), EditorAction.Hibernate),
            new ActionOption(AppText.T("예약 시각에 절전 진입"), EditorAction.Sleep)
        };
        WakeModeInput.ItemsSource = new[]
        {
            new WakeModeOption(AppText.T("자동 선택 (추천)"), WakeModePreference.Automatic),
            new WakeModeOption(AppText.T("절전 (S3)"), WakeModePreference.SleepS3),
            new WakeModeOption(AppText.T("최대 절전 (S4)"), WakeModePreference.HibernateS4)
        };

        if (existing is null)
        {
            SetPlannedTime(ScheduleTimePolicy.NewScheduleDefault(DateTime.Now));
            ActionInput.SelectedValue = EditorAction.AutomaticStart;
            WakeModeInput.SelectedValue = WakeModePreference.Automatic;
            OneTimeAutoLogonCheck.IsChecked = true;
        }
        else
        {
            Heading.Text = AppText.T(PowerSchedulePolicy.IsRemovedSchedule(existing.ActionType)
                ? "이전 전원 예약 전환"
                : "예약 수정");
            DateInput.SelectedDate = existing.ScheduledLocalDateTime.Date;
            TimeInput.Text = existing.ScheduledLocalDateTime.ToString("HH:mm", CultureInfo.InvariantCulture);
            ActionInput.SelectedValue = ToEditorAction(existing.ActionType);
            WakeModeInput.SelectedValue = existing.ActionType switch
            {
                PowerActionType.WakeFromSleep => WakeModePreference.SleepS3,
                PowerActionType.WakeFromHibernate => WakeModePreference.HibernateS4,
                _ => WakeModePreference.Automatic
            };
            OneTimeAutoLogonCheck.IsChecked = existing.OneTimeAutoLogonEnabled;

            foreach (var item in existing.FollowUpPrograms.OrderBy(program => program.SortOrder))
            {
                Programs.Add(new ProgramDraft(item.Id, item.ExecutablePath, item.DelayAfterDesktopReadyMinutes,
                    item.Arguments, item.WorkingDirectory, item.RunElevated));
            }
        }

        UpdateActionPanels();
    }

    public ObservableCollection<ProgramDraft> Programs { get; } = [];
    public PowerSchedule? ResultSchedule { get; private set; }

    private void ActionInput_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateActionPanels();

    private void WakeModeInput_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateWakeModeNotice();

    private void UpdateActionPanels()
    {
        if (WakeOptions is null || ActionInput.SelectedValue is not EditorAction action)
        {
            return;
        }

        var automaticStart = action == EditorAction.AutomaticStart;
        var powerAction = ToPowerTransitionAction(action);
        var shutdown = powerAction == PowerActionType.Shutdown;
        WakeOptions.Visibility = automaticStart ? Visibility.Visible : Visibility.Collapsed;
        PowerOffNotice.Visibility = automaticStart ? Visibility.Collapsed : Visibility.Visible;
        PowerShortcuts.Visibility = powerAction is null ? Visibility.Collapsed : Visibility.Visible;
        PowerOffNoticeText.Text = AppText.T(shutdown
            ? "완전 종료 예약은 정확히 5분 전에 경고합니다. 완전히 종료된 PC는 앱의 자동 시작 예약으로 다시 켤 수 없습니다."
            : "예약한 절전 또는 최대 절전 동작은 정확히 5분 전에 경고합니다. 저장하지 않은 작업이 없도록 미리 확인하세요.");
        if (powerAction is not null)
        {
            UpdateQuickPowerAccessibility(powerAction.Value);
        }

        if (automaticStart)
        {
            UpdateWakeModeNotice();
        }
    }

    private void PowerAfterOneHour_Click(object sender, RoutedEventArgs e) =>
        SetQuickPowerTime(1);

    private void PowerAfterTwoHours_Click(object sender, RoutedEventArgs e) =>
        SetQuickPowerTime(2);

    private void SetQuickPowerTime(int hours)
    {
        if (ActionInput.SelectedValue is not EditorAction action ||
            ToPowerTransitionAction(action) is null)
        {
            return;
        }

        SetPlannedTime(ScheduleTimePolicy.QuickPowerTransition(DateTime.Now, hours));
    }

    private void UpdateQuickPowerAccessibility(PowerActionType action)
    {
        var actionName = AppText.T(action switch
        {
            PowerActionType.Shutdown => "완전 종료",
            PowerActionType.Hibernate => "최대 절전",
            PowerActionType.Sleep => "절전",
            _ => throw new ArgumentOutOfRangeException(nameof(action))
        });
        SetQuickPowerAccessibility(PowerAfterOneHourButton, actionName, 1);
        SetQuickPowerAccessibility(PowerAfterTwoHoursButton, actionName, 2);
    }

    private static void SetQuickPowerAccessibility(System.Windows.Controls.Button button, string actionName, int hours)
    {
        var description = AppText.IsEnglish
            ? $"Schedule {actionName.ToLowerInvariant()} for {hours} hour{(hours == 1 ? string.Empty : "s")} from the current local time."
            : $"현재 로컬 시각에서 {hours}시간 뒤에 {actionName} 예약";
        button.ToolTip = description;
        System.Windows.Automation.AutomationProperties.SetName(button, description);
        System.Windows.Automation.AutomationProperties.SetHelpText(button, description);
    }

    private void SetPlannedTime(DateTime planned)
    {
        DateInput.SelectedDate = planned.Date;
        TimeInput.Text = planned.ToString("HH:mm", CultureInfo.InvariantCulture);
    }

    private void UpdateWakeModeNotice()
    {
        if (WakeModeNotice is null || WakeModeInput.SelectedValue is not WakeModePreference preference)
        {
            return;
        }

        try
        {
            var action = ResolveWakeAction(preference);
            var stateName = AppText.T(PowerSchedulePolicy.WakeStateName(action));
            var status = GetTestStatus(action);
            var verification = AppText.T(status switch
            {
                CapabilityStatus.Confirmed => "실제 테스트 성공이 확인된 경로입니다.",
                CapabilityStatus.UnsupportedOrFailed => "이 방식의 이전 실제 테스트가 실패했습니다. 직접 선택해 다시 시험할 수 있지만 성공은 보장되지 않습니다.",
                _ => "아직 실제 테스트 성공이 확인되지 않은 경로입니다. 호환성 화면에서 먼저 테스트하는 것을 권장합니다."
            });
            WakeModeNotice.Text = AppText.IsEnglish
                ? preference == WakeModePreference.Automatic
                    ? $"Automatic selection: {stateName}. {verification} Save the schedule, then use the matching power-state button on the main screen."
                    : $"Selected mode: {stateName}. {verification} Save the schedule and enter that state to resume at the scheduled time."
                : preference == WakeModePreference.Automatic
                    ? $"자동 선택 결과: {stateName}. {verification} 저장 후 메인 화면에서 같은 전원 상태의 버튼을 누르세요."
                    : $"선택한 방식: {stateName}. {verification} 저장 후 해당 상태로 전환해야 예약 시각에 복귀할 수 있습니다.";
        }
        catch (InvalidOperationException error)
        {
            WakeModeNotice.Text = AppText.T(error.Message);
        }
    }

    private void AddProgram_Click(object sender, RoutedEventArgs e)
    {
        var editor = new ProgramEditorWindow { Owner = this };
        if (editor.ShowDialog() == true && editor.Result is not null)
        {
            Programs.Add(editor.Result);
        }
    }

    private void RemoveProgram_Click(object sender, RoutedEventArgs e)
    {
        if (ProgramsGrid.SelectedItem is ProgramDraft selected)
        {
            Programs.Remove(selected);
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (DateInput.SelectedDate is not DateTime date ||
            !TimeSpan.TryParseExact(TimeInput.Text.Trim(), ["h\\:mm", "hh\\:mm"], CultureInfo.InvariantCulture, out var time) ||
            ActionInput.SelectedValue is not EditorAction editorAction)
        {
            MessageBox.Show("올바른 날짜, 시간(HH:mm), 동작을 입력하세요.", "예약",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        PowerActionType action;
        if (editorAction == EditorAction.AutomaticStart)
        {
            if (WakeModeInput.SelectedValue is not WakeModePreference preference)
            {
                MessageBox.Show("자동 시작 방식을 선택하세요.", "자동 시작 예약",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try
            {
                action = ResolveWakeAction(preference);
            }
            catch (InvalidOperationException error)
            {
                MessageBox.Show(error.Message, "자동 시작 예약",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
        }
        else
        {
            action = ToPowerTransitionAction(editorAction)
                     ?? throw new InvalidOperationException("지원하지 않는 예약 동작입니다.");
        }

        var planned = DateTime.SpecifyKind(date.Date + time, DateTimeKind.Unspecified);
        var id = _existing?.Id ?? Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var programs = PowerSchedulePolicy.IsWakeSchedule(action)
            ? Programs.Select((item, index) => new FollowUpProgram(item.Id, id, item.ExecutablePath, item.DelayMinutes,
                string.IsNullOrWhiteSpace(item.Arguments) ? null : item.Arguments,
                string.IsNullOrWhiteSpace(item.WorkingDirectory) ? null : item.WorkingDirectory,
                item.RunElevated,
                index)).ToArray()
            : [];
        var candidate = new PowerSchedule(
            id,
            planned,
            action,
            _existing?.IsEnabled ?? true,
            PowerSchedulePolicy.IsWakeSchedule(action) && OneTimeAutoLogonCheck.IsChecked == true,
            _existing?.CreatedAtUtc ?? now,
            now,
            _existing?.Status is ScheduleStatus.Disabled ? ScheduleStatus.Disabled : ScheduleStatus.Pending,
            programs);
        var validation = ScheduleValidator.Validate(candidate, DateTime.Now, _schedules);
        if (!validation.IsValid)
        {
            MessageBox.Show(
                string.Join(Environment.NewLine, validation.Issues.Select(issue => "• " + AppText.T(issue.Message))),
                "예약을 저장할 수 없습니다",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        if (action == PowerActionType.Shutdown)
        {
            var nextWake = ScheduleValidator.SelectNextWake(
                _schedules.Where(schedule => schedule.Id != _existing?.Id),
                planned);
            var warning = nextWake is null
                ? AppText.IsEnglish
                    ? "After a full shutdown, this app cannot turn the PC back on for a scheduled wake. To use scheduled wake later, leave the PC in sleep or hibernation instead.\n\nSave this full-shutdown schedule anyway?"
                    : "완전히 종료된 PC는 앱의 자동 시작 예약으로 다시 켤 수 없습니다. 이후 자동 시작을 사용하려면 PC를 절전 또는 최대 절전 상태로 두어야 합니다.\n\n그래도 완전 종료 예약을 저장하시겠습니까?"
                : AppText.IsEnglish
                    ? $"A scheduled wake exists at {nextWake.ScheduledLocalDateTime:MMM dd HH:mm}. A full shutdown prevents that wake from turning the PC back on. Hibernation is recommended for a long wait, and sleep for a short wait.\n\nSave this full-shutdown schedule anyway?"
                    : $"{nextWake.ScheduledLocalDateTime:MM월 dd일 HH:mm}에 다음 자동 시작 예약이 있습니다. PC를 완전히 종료하면 해당 시각에 자동으로 다시 시작할 수 없습니다. 장시간 대기는 최대 절전, 짧은 대기는 절전을 권장합니다.\n\n그래도 완전 종료 예약을 저장하시겠습니까?";
            if (MessageBox.Show(
                    warning,
                    AppText.T("완전 종료 예약 확인"),
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning,
                    MessageBoxResult.No) != MessageBoxResult.Yes)
            {
                return;
            }
        }

        ResultSchedule = candidate;
        DialogResult = true;
    }

    private PowerActionType ResolveWakeAction(WakeModePreference preference) =>
        PowerSchedulePolicy.ResolveWakeAction(
            preference,
            _capability.S3Available,
            _capability.S4Available,
            _capability.HibernateEnabled,
            GetCapabilityStatus(CompatibilityCapabilities.S3Wake),
            GetCapabilityStatus(CompatibilityCapabilities.S4Wake));

    private CapabilityStatus? GetTestStatus(PowerActionType action) => action switch
    {
        PowerActionType.WakeFromSleep => GetCapabilityStatus(CompatibilityCapabilities.S3Wake),
        PowerActionType.WakeFromHibernate => GetCapabilityStatus(CompatibilityCapabilities.S4Wake),
        _ => null
    };

    private CapabilityStatus? GetCapabilityStatus(string capability) =>
        _compatibility.TryGetValue(capability, out var result) ? result.Status : null;

    private static EditorAction ToEditorAction(PowerActionType action) => action switch
    {
        PowerActionType.PowerOn or PowerActionType.WakeFromSleep or PowerActionType.WakeFromHibernate => EditorAction.AutomaticStart,
        PowerActionType.Shutdown => EditorAction.Shutdown,
        PowerActionType.Hibernate => EditorAction.Hibernate,
        PowerActionType.Sleep => EditorAction.Sleep,
        _ => throw new ArgumentOutOfRangeException(nameof(action))
    };

    private static PowerActionType? ToPowerTransitionAction(EditorAction action) => action switch
    {
        EditorAction.Shutdown => PowerActionType.Shutdown,
        EditorAction.Hibernate => PowerActionType.Hibernate,
        EditorAction.Sleep => PowerActionType.Sleep,
        EditorAction.AutomaticStart => null,
        _ => throw new ArgumentOutOfRangeException(nameof(action))
    };

    private enum EditorAction
    {
        AutomaticStart,
        Shutdown,
        Hibernate,
        Sleep
    }

    private sealed record ActionOption(string Text, EditorAction Value);
    private sealed record WakeModeOption(string Text, WakeModePreference Value);
}

public sealed record ProgramDraft(
    Guid Id,
    string ExecutablePath,
    int DelayMinutes,
    string? Arguments,
    string? WorkingDirectory,
    bool RunElevated)
{
    public string FileName => Path.GetFileName(ExecutablePath);
    public string PrivilegeText => AppText.T(RunElevated ? "관리자" : "일반");
}
