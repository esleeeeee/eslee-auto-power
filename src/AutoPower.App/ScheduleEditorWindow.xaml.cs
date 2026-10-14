using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using AutoPower.App.Controls;
using AutoPower.Core;
using AutoPower.Windows;
using MessageBox = AutoPower.App.LocalizedMessageBox;

namespace AutoPower.App;

public partial class ScheduleEditorWindow : ThemedWindow
{
    private readonly PowerSchedule? _existing;
    private readonly bool _duplicate;
    private readonly PowerCapabilitySnapshot _capability;
    private readonly Dictionary<string, CompatibilityResult> _compatibility;
    private readonly IReadOnlyList<PowerSchedule> _schedules;
    private readonly bool _initialized;

    public ScheduleEditorWindow(
        PowerSchedule? existing,
        PowerCapabilitySnapshot capability,
        IReadOnlyList<CompatibilityResult> compatibility,
        IReadOnlyList<PowerSchedule> schedules,
        bool duplicate = false)
    {
        InitializeComponent();
        _existing = existing;
        _duplicate = duplicate && existing is not null;
        _capability = capability;
        _compatibility = compatibility.ToDictionary(item => item.Capability, StringComparer.Ordinal);
        _schedules = schedules;
        DataContext = this;
        DateInput.DisplayDateStart = DateTime.Today;
        Programs.CollectionChanged += (_, _) => UpdateProgramsEmptyState();

        WakeModeInput.ItemsSource = new[]
        {
            new ChoiceOption<WakeModePreference>(WakeModePreference.Automatic, AppText.T("자동 선택 (추천)")),
            new ChoiceOption<WakeModePreference>(WakeModePreference.SleepS3, AppText.T("절전 (S3)")),
            new ChoiceOption<WakeModePreference>(WakeModePreference.HibernateS4, AppText.T("최대 절전 (S4)"))
        };

        if (existing is null)
        {
            SetPlannedTime(ScheduleTimePolicy.NewScheduleDefault(DateTime.Now));
            ActionWake.IsChecked = true;
            WakeModeInput.SelectedValue = WakeModePreference.Automatic;
            OneTimeAutoLogonCheck.IsChecked = true;
        }
        else
        {
            Heading.Text = AppText.T(_duplicate
                ? "예약 복제"
                : PowerSchedulePolicy.IsRemovedSchedule(existing.ActionType)
                    ? "이전 전원 예약 전환"
                    : "예약 수정");
            // A recurring occurrence may have been snoozed; editing starts from the series anchor time
            // so that renaming a snoozed occurrence does not move every later occurrence.
            SetPlannedTime(existing.Recurrence is { } rule && RecurrencePolicy.IsUsable(rule)
                ? RecurrencePolicy.FirstOccurrence(rule, existing.ScheduledLocalDateTime.Date + rule.TimeOfDay, DateTime.Now)
                : existing.ScheduledLocalDateTime);
            ActionButton(ToEditorAction(existing.ActionType)).IsChecked = true;
            WakeModeInput.SelectedValue = existing.ActionType switch
            {
                PowerActionType.WakeFromSleep => WakeModePreference.SleepS3,
                PowerActionType.WakeFromHibernate => WakeModePreference.HibernateS4,
                _ => WakeModePreference.Automatic
            };
            OneTimeAutoLogonCheck.IsChecked = existing.OneTimeAutoLogonEnabled;
            NameInput.Text = existing.Name ?? string.Empty;
            LoadRecurrence(existing.Recurrence);

            foreach (var item in existing.FollowUpPrograms.OrderBy(program => program.SortOrder))
            {
                Programs.Add(new ProgramDraft(
                    _duplicate ? Guid.NewGuid() : item.Id,
                    item.ExecutablePath,
                    item.DelayAfterDesktopReadyMinutes,
                    item.Arguments,
                    item.WorkingDirectory,
                    item.RunElevated));
            }
        }

        Title = Heading.Text;
        _initialized = true;
        UpdateProgramsEmptyState();
        UpdateActionPanels();
        UpdateRepeatPanel();
    }

    public ObservableCollection<ProgramDraft> Programs { get; } = [];
    public PowerSchedule? ResultSchedule { get; private set; }

    private EditorAction SelectedAction =>
        ActionShutdown.IsChecked == true ? EditorAction.Shutdown :
        ActionHibernate.IsChecked == true ? EditorAction.Hibernate :
        ActionSleep.IsChecked == true ? EditorAction.Sleep :
        EditorAction.AutomaticStart;

    private System.Windows.Controls.RadioButton ActionButton(EditorAction action) => action switch
    {
        EditorAction.Shutdown => ActionShutdown,
        EditorAction.Hibernate => ActionHibernate,
        EditorAction.Sleep => ActionSleep,
        _ => ActionWake
    };

    private void Action_Checked(object sender, RoutedEventArgs e) => UpdateActionPanels();

    private void WakeModeInput_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateWakeModeNotice();

    private void When_Changed(object sender, RoutedEventArgs e) => UpdateNextRunPreview();

    private void Repeat_Changed(object sender, RoutedEventArgs e) => UpdateRepeatPanel();

    private void UpdateActionPanels()
    {
        if (!_initialized)
        {
            return;
        }

        var action = SelectedAction;
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

    // ----- Quick time -----

    private void QuickTime_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string tag } ||
            !int.TryParse(tag, NumberStyles.Integer, CultureInfo.InvariantCulture, out var minutes) ||
            ToPowerTransitionAction(SelectedAction) is null)
        {
            return;
        }

        SetPlannedTime(QuickSchedulePolicy.At(DateTime.Now, minutes));
    }

    private void UpdateQuickPowerAccessibility(PowerActionType action)
    {
        var actionName = UiText.ActionName(action);
        SetQuickPowerAccessibility(PowerAfter30Button, actionName, 30);
        SetQuickPowerAccessibility(PowerAfterOneHourButton, actionName, 60);
        SetQuickPowerAccessibility(PowerAfterTwoHoursButton, actionName, 120);
        SetQuickPowerAccessibility(PowerAfter3HoursButton, actionName, 180);
    }

    private static void SetQuickPowerAccessibility(System.Windows.Controls.Button button, string actionName, int minutes)
    {
        var description = AppText.IsEnglish
            ? minutes < 60
                ? $"Schedule {actionName.ToLowerInvariant()} for {minutes} minutes from the current local time."
                : $"Schedule {actionName.ToLowerInvariant()} for {minutes / 60} hour{(minutes == 60 ? string.Empty : "s")} from the current local time."
            : minutes < 60
                ? $"현재 로컬 시각에서 {minutes}분 뒤에 {actionName} 예약"
                : $"현재 로컬 시각에서 {minutes / 60}시간 뒤에 {actionName} 예약";
        button.ToolTip = description;
        System.Windows.Automation.AutomationProperties.SetName(button, description);
        System.Windows.Automation.AutomationProperties.SetHelpText(button, description);
    }

    private void SetPlannedTime(DateTime planned)
    {
        DateInput.SelectedDate = planned.Date;
        TimeInput.Text = planned.ToString("HH:mm", CultureInfo.InvariantCulture);
    }

    private bool TryReadPlanned(out DateTime planned)
    {
        planned = default;
        if (DateInput.SelectedDate is not DateTime date ||
            !TimeSpan.TryParseExact(TimeInput.Text.Trim(), ["h\\:mm", "hh\\:mm"], CultureInfo.InvariantCulture, out var time))
        {
            return false;
        }

        planned = DateTime.SpecifyKind(date.Date + time, DateTimeKind.Unspecified);
        return true;
    }

    // ----- Recurrence -----

    private void LoadRecurrence(RecurrenceRule? rule)
    {
        switch (rule?.Kind)
        {
            case RecurrenceKind.Daily:
                RepeatDaily.IsChecked = true;
                break;
            case RecurrenceKind.Weekdays:
                RepeatWeekdays.IsChecked = true;
                break;
            case RecurrenceKind.Weekly:
                RepeatWeekly.IsChecked = true;
                foreach (var (day, toggle) in DayToggles())
                {
                    toggle.IsChecked = rule.AllowsDay(day);
                }

                break;
            default:
                RepeatNone.IsChecked = true;
                break;
        }
    }

    private IEnumerable<(DayOfWeek Day, System.Windows.Controls.Primitives.ToggleButton Toggle)> DayToggles() =>
    [
        (DayOfWeek.Monday, DayMonday),
        (DayOfWeek.Tuesday, DayTuesday),
        (DayOfWeek.Wednesday, DayWednesday),
        (DayOfWeek.Thursday, DayThursday),
        (DayOfWeek.Friday, DayFriday),
        (DayOfWeek.Saturday, DaySaturday),
        (DayOfWeek.Sunday, DaySunday)
    ];

    private void UpdateRepeatPanel()
    {
        if (!_initialized)
        {
            return;
        }

        WeekdayPanel.Visibility = RepeatWeekly.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        if (RepeatWeekly.IsChecked == true && !DayToggles().Any(item => item.Toggle.IsChecked == true) &&
            TryReadPlanned(out var planned))
        {
            // Start the day picker from the chosen date so the first occurrence stays where the user put it.
            DayToggles().First(item => item.Day == planned.DayOfWeek).Toggle.IsChecked = true;
        }

        UpdateNextRunPreview();
    }

    /// <summary>The selected rule, or null for a one-time schedule. The time of day comes from the time field.</summary>
    private RecurrenceRule? BuildRule(TimeSpan timeOfDay)
    {
        if (RepeatDaily.IsChecked == true)
        {
            return RecurrenceRule.CreateDaily(timeOfDay);
        }

        if (RepeatWeekdays.IsChecked == true)
        {
            return RecurrenceRule.CreateWeekdays(timeOfDay);
        }

        if (RepeatWeekly.IsChecked == true)
        {
            var days = DayToggles()
                .Where(item => item.Toggle.IsChecked == true)
                .Aggregate(RecurrenceDays.None, (current, item) => current | RecurrencePolicy.ToFlag(item.Day));
            return RecurrenceRule.CreateWeekly(days, timeOfDay);
        }

        return null;
    }

    private void UpdateNextRunPreview()
    {
        if (!_initialized || NextRunPreview is null)
        {
            return;
        }

        if (!TryReadPlanned(out var planned))
        {
            NextRunPreview.Text = AppText.T("날짜와 시간(HH:mm)을 입력하세요.");
            return;
        }

        var rule = BuildRule(planned.TimeOfDay);
        if (rule is null)
        {
            NextRunPreview.Text = AppText.F("한 번 실행: {0}", $"{UiText.LongDate(planned)} {planned:HH:mm}");
            return;
        }

        if (!RecurrencePolicy.IsUsable(rule))
        {
            NextRunPreview.Text = AppText.T("반복할 요일을 하나 이상 고르세요.");
            return;
        }

        var first = RecurrencePolicy.FirstOccurrence(rule, planned, DateTime.Now);
        NextRunPreview.Text = AppText.F(
            "{0} · 첫 실행: {1}",
            RecurrencePolicy.Describe(rule, AppText.LanguageCode),
            $"{UiText.LongDate(first)} {first:HH:mm}");
    }

    // ----- Wake mode -----

    private void UpdateWakeModeNotice()
    {
        if (!_initialized || WakeModeInput.SelectedValue is not WakeModePreference preference)
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

    // ----- Follow-up programs -----

    private void UpdateProgramsEmptyState() =>
        NoProgramsText.Visibility = Programs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    private void AddProgram_Click(object sender, RoutedEventArgs e)
    {
        var editor = new ProgramEditorWindow { Owner = this };
        if (editor.ShowDialog() == true && editor.Result is not null)
        {
            Programs.Add(editor.Result);
        }
    }

    private void EditProgram_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ProgramDraft selected)
        {
            return;
        }

        var editor = new ProgramEditorWindow(selected) { Owner = this };
        if (editor.ShowDialog() == true && editor.Result is not null)
        {
            var index = Programs.IndexOf(selected);
            Programs[index] = editor.Result;
        }
    }

    private void RemoveProgram_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ProgramDraft selected)
        {
            Programs.Remove(selected);
        }
    }

    // ----- Save -----

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!TryReadPlanned(out var planned))
        {
            MessageBox.Show("올바른 날짜, 시간(HH:mm), 동작을 입력하세요.", "예약",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var editorAction = SelectedAction;
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

        var rule = BuildRule(planned.TimeOfDay);
        if (rule is not null)
        {
            if (!RecurrencePolicy.IsUsable(rule))
            {
                MessageBox.Show("반복할 요일을 하나 이상 고르세요.", "반복", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            planned = RecurrencePolicy.FirstOccurrence(rule, planned, DateTime.Now);
        }

        var editing = _existing is not null && !_duplicate;
        var id = editing ? _existing!.Id : Guid.NewGuid();
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
            editing ? _existing!.IsEnabled : true,
            PowerSchedulePolicy.IsWakeSchedule(action) && OneTimeAutoLogonCheck.IsChecked == true,
            editing ? _existing!.CreatedAtUtc : now,
            now,
            editing && _existing!.Status is ScheduleStatus.Disabled ? ScheduleStatus.Disabled : ScheduleStatus.Pending,
            programs)
        {
            Name = NameInput.Text,
            Recurrence = rule,
            // Editing the live occurrence edits its series; a duplicate starts a new one.
            SeriesId = editing ? _existing!.SeriesId : null
        };
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
                _schedules.Where(schedule => schedule.Id != id),
                planned);
            var warning = ShutdownConsequence.Describe(nextWake) + "\n\n" +
                (AppText.IsEnglish ? "Save this full-shutdown schedule anyway?" : "그래도 완전 종료 예약을 저장하시겠습니까?");
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
    public string DelayText => RunElevated
        ? AppText.F("T0 + {0}분 · 관리자", DelayMinutes)
        : AppText.F("T0 + {0}분", DelayMinutes);
    public override string ToString() => $"{FileName}, {DelayText}";
}
