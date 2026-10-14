using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows.Data;
using AutoPower.App.Controls;
using AutoPower.Core;

namespace AutoPower.App;

public enum HistoryResultFilter
{
    All,
    Success,
    Failure,
    Skipped,
    Warning,
    Information
}

public sealed class MainViewModel : INotifyPropertyChanged
{
    internal const string AttentionAcknowledgedKey = "attention-acknowledged-utc";

    private ScheduleRow? _selectedSchedule;
    private string _statusMessage = "준비됨";
    private string? _securityWarning;
    private string _historySearch = string.Empty;
    private HistoryResultFilter _historyResultFilter = HistoryResultFilter.All;
    private ScheduleRow? _historyScheduleFilter;
    private string? _attentionText;
    private DateTimeOffset? _attentionLatestUtc;
    private DateTime _now = DateTime.Now;

    public MainViewModel()
    {
        HistoryView = CollectionViewSource.GetDefaultView(History);
        HistoryView.Filter = item => item is HistoryRow row && MatchesHistoryFilter(row);
    }

    public ObservableCollection<ScheduleRow> Schedules { get; } = [];
    public ObservableCollection<HistoryRow> History { get; } = [];
    public ICollectionView HistoryView { get; }
    public ObservableCollection<HistoryRow> SelectedScheduleHistory { get; } = [];
    public ObservableCollection<CompatibilityRow> Compatibility { get; } = [];

    public ScheduleRow? SelectedSchedule
    {
        get => _selectedSchedule;
        set
        {
            _selectedSchedule = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasSelectedSchedule));
            OnPropertyChanged(nameof(SelectedRemainingText));
            OnPropertyChanged(nameof(SelectedStatusText));
            RefreshSelectedScheduleHistory();
        }
    }

    public bool HasSelectedSchedule => SelectedSchedule is not null;
    public bool HasSchedules => Schedules.Count > 0;

    public string StatusMessage
    {
        get => AppText.T(_statusMessage);
        set { _statusMessage = value; OnPropertyChanged(); }
    }

    public string? SecurityWarning
    {
        get => _securityWarning is null ? null : AppText.T(_securityWarning);
        set { _securityWarning = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasSecurityWarning)); }
    }

    public bool HasSecurityWarning => !string.IsNullOrWhiteSpace(SecurityWarning);

    // ----- Next action (hero card + tray tooltip) -----

    public ScheduleRow? NextSchedule => Schedules.FirstOrDefault(row =>
        row.Schedule.IsEnabled && row.Schedule.Status == ScheduleStatus.Pending);

    public bool HasNextSchedule => NextSchedule is not null;

    public string NextScheduleSummary => NextSchedule is { } next
        ? $"{UiText.When(next.Schedule.ScheduledLocalDateTime, _now)} {next.ActionText}"
        : AppText.T("없음");

    public string NextWhenText => NextSchedule is { } next
        ? $"{UiText.When(next.Schedule.ScheduledLocalDateTime, _now)} · {next.ActionText}"
        : AppText.T("예정된 동작이 없습니다");

    public string NextDetailText => NextSchedule is { } next
        ? string.Join(" · ", new[] { UiText.Remaining(next.Schedule.ScheduledLocalDateTime, _now), next.NameText }
            .Where(part => !string.IsNullOrWhiteSpace(part)))
        : AppText.T("새 예약을 만들면 여기에서 다음 동작과 남은 시간을 확인할 수 있습니다.");

    public string NextGlyph => NextSchedule?.ActionGlyph ?? Glyphs.Clock;

    public string SelectedRemainingText => SelectedSchedule is { } row && row.Schedule.IsEnabled
        ? UiText.Remaining(row.Schedule.ScheduledLocalDateTime, _now)
        : AppText.T("꺼져 있음");

    /// <summary>"켜짐 · 2시간 남음" for an active schedule, "꺼짐" for a paused one.</summary>
    public string SelectedStatusText => SelectedSchedule is { } row
        ? row.Schedule.IsEnabled ? $"{row.EnabledText} · {SelectedRemainingText}" : row.EnabledText
        : string.Empty;

    /// <summary>Short text for the tray icon tooltip (Windows limits it to 127 characters).</summary>
    public string TrayTooltip
    {
        get
        {
            var text = NextSchedule is { } next
                ? $"eslee Auto Power\n{UiText.When(next.Schedule.ScheduledLocalDateTime, _now)} {next.ActionText} ({UiText.Remaining(next.Schedule.ScheduledLocalDateTime, _now)})"
                : "eslee Auto Power";
            return text.Length <= 127 ? text : text[..127];
        }
    }

    public PowerSchedule? NextPendingWakeSchedule => Schedules
        .Select(row => row.Schedule)
        .FirstOrDefault(schedule =>
            schedule.IsEnabled &&
            schedule.Status == ScheduleStatus.Pending &&
            PowerSchedulePolicy.IsWakeSchedule(schedule.ActionType));

    public bool HasPausedSchedules => Schedules.Any(row => !row.Schedule.IsEnabled);
    public bool HasActiveSchedules => Schedules.Any(row => row.Schedule.IsEnabled);

    // ----- Attention banner (missed / failed runs since last acknowledgement) -----

    public string? AttentionText
    {
        get => _attentionText;
        private set { _attentionText = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasAttention)); }
    }

    public bool HasAttention => !string.IsNullOrWhiteSpace(AttentionText);

    public DateTimeOffset? AttentionLatestUtc => _attentionLatestUtc;

    // ----- History filters -----

    public string HistorySearch
    {
        get => _historySearch;
        set { _historySearch = value ?? string.Empty; OnPropertyChanged(); RefreshHistoryView(); }
    }

    public HistoryResultFilter HistoryResultFilter
    {
        get => _historyResultFilter;
        set { _historyResultFilter = value; OnPropertyChanged(); RefreshHistoryView(); }
    }

    public ScheduleRow? HistoryScheduleFilter
    {
        get => _historyScheduleFilter;
        set
        {
            _historyScheduleFilter = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasHistoryScheduleFilter));
            OnPropertyChanged(nameof(HistoryScheduleFilterText));
            RefreshHistoryView();
        }
    }

    public bool HasHistoryScheduleFilter => HistoryScheduleFilter is not null;

    public string HistoryScheduleFilterText => HistoryScheduleFilter is { } row
        ? AppText.F("이 예약만: {0}", row.TitleText)
        : string.Empty;

    public bool HistoryIsEmpty => HistoryView.IsEmpty;

    /// <summary>Called every 30 seconds so countdowns stay current.</summary>
    public void Tick()
    {
        _now = DateTime.Now;
        OnPropertyChanged(nameof(NextWhenText));
        OnPropertyChanged(nameof(NextDetailText));
        OnPropertyChanged(nameof(NextScheduleSummary));
        OnPropertyChanged(nameof(SelectedRemainingText));
        OnPropertyChanged(nameof(SelectedStatusText));
        OnPropertyChanged(nameof(TrayTooltip));
    }

    public async Task RefreshAsync()
    {
        _now = DateTime.Now;
        var selectedKey = SelectedSchedule?.SeriesKey;
        var historyFilterKey = HistoryScheduleFilter?.SeriesKey;
        var allSchedules = await AppServices.Store.GetAllSchedulesAsync();
        var hiddenScheduleIds = allSchedules
            .Where(schedule => PowerSchedulePolicy.IsRemovedSchedule(schedule.ActionType))
            .Select(schedule => schedule.Id)
            .ToHashSet();
        // A paused recurring series stays listed after its paused time has passed so it can be turned
        // back on (it moves to the next slot) or deleted; otherwise it would become unreachable.
        var schedules = allSchedules
            .Where(schedule => !PowerSchedulePolicy.IsRemovedSchedule(schedule.ActionType) &&
                               ((schedule.ScheduledLocalDateTime > _now &&
                                 schedule.Status is ScheduleStatus.Pending or ScheduleStatus.Disabled) ||
                                (schedule.Status == ScheduleStatus.Disabled && RecurrencePolicy.IsUsable(schedule.Recurrence))))
            .Select(schedule => new ScheduleRow(schedule, allSchedules))
            .OrderBy(row => row.DisplayTime);
        Schedules.Clear();
        foreach (var row in schedules)
        {
            Schedules.Add(row);
        }

        History.Clear();
        var historyItems = (await AppServices.Store.GetHistoryAsync())
            .Where(item => !(item.ScheduleId is Guid id && hiddenScheduleIds.Contains(id)) &&
                           !IsRemovedFeatureHistory(item.EventType))
            .ToArray();
        var seriesOf = allSchedules.ToDictionary(schedule => schedule.Id, ScheduleExtras.SeriesKey);
        foreach (var item in historyItems)
        {
            History.Add(new HistoryRow(item, item.ScheduleId is Guid id && seriesOf.TryGetValue(id, out var key) ? key : item.ScheduleId));
        }

        SelectedSchedule = Schedules.FirstOrDefault(row => row.SeriesKey == selectedKey) ?? Schedules.FirstOrDefault();
        HistoryScheduleFilter = historyFilterKey is null ? null : Schedules.FirstOrDefault(row => row.SeriesKey == historyFilterKey);
        RefreshHistoryView();

        Compatibility.Clear();
        foreach (var item in (await AppServices.Store.GetCompatibilityAsync()).Where(item =>
                     item.Capability is CompatibilityCapabilities.S3Wake or CompatibilityCapabilities.S4Wake))
        {
            Compatibility.Add(new CompatibilityRow(item));
        }

        bool? credentialsRegistered;
        try
        {
            credentialsRegistered = AppServices.Credentials.Exists();
        }
        catch (Exception error)
        {
            credentialsRegistered = null;
            AppServices.Logger.Error("credential.compatibility-status-failed", error);
        }

        Compatibility.Add(new CompatibilityRow(new CompatibilityResult(
            CompatibilityCapabilities.OneTimeAutoLogon,
            credentialsRegistered switch
            {
                true => CapabilityStatus.Confirmed,
                false => CapabilityStatus.NeedsPhysicalTest,
                null => CapabilityStatus.Unknown
            },
            DateTimeOffset.UtcNow,
            credentialsRegistered switch
            {
                true => "설정에 저장된 Windows 계정이 실제 로그온 API 검증을 통과했습니다.",
                false => "설정에서 Windows 계정 암호를 저장·검증한 뒤 저장된 로그인 테스트로 확인할 수 있습니다.",
                null => "Windows 보호 저장소의 자동 로그인 등록 상태를 확인하지 못했습니다. 진단 로그를 확인하세요."
            })));

        var journal = new AutoPower.Windows.JsonAutologonJournalStore().Read();
        var resumeJournal = new AutoPower.Windows.JsonResumeSignInJournalStore().Read();
        SecurityWarning = resumeJournal is not null && resumeJournal.State != AutoPower.Windows.ResumeSignInJournalState.Restored
            ? "S3/S4 1회 자동 로그인의 임시 로그인 요구 설정이 아직 복원되지 않았습니다. 관리자 권한 복구가 필요합니다."
            : journal?.State is AutoPower.Windows.AutologonJournalState.CleanupFailed or AutoPower.Windows.AutologonJournalState.RollbackFailed
                ? "이전 버전의 보안 설정을 해제하지 못했습니다. 관리자 권한 정리가 필요합니다."
                : null;

        await RefreshAttentionAsync(historyItems);

        StatusMessage = Schedules.Count == 0 ? "미래 예약이 없습니다." : AppText.F("미래 예약 {0}개", Schedules.Count);
        OnPropertyChanged(nameof(HasSchedules));
        OnPropertyChanged(nameof(NextSchedule));
        OnPropertyChanged(nameof(HasNextSchedule));
        OnPropertyChanged(nameof(NextGlyph));
        OnPropertyChanged(nameof(NextPendingWakeSchedule));
        OnPropertyChanged(nameof(HasPausedSchedules));
        OnPropertyChanged(nameof(HasActiveSchedules));
        Tick();
    }

    /// <summary>Hides the banner until a newer problem is recorded.</summary>
    public async Task AcknowledgeAttentionAsync()
    {
        var stamp = _attentionLatestUtc ?? DateTimeOffset.UtcNow;
        await AppServices.Store.SetSettingAsync(AttentionAcknowledgedKey, stamp.ToString("O", CultureInfo.InvariantCulture));
        AttentionText = null;
    }

    private async Task RefreshAttentionAsync(IReadOnlyList<ExecutionHistory> history)
    {
        var stored = await AppServices.Store.GetSettingAsync(AttentionAcknowledgedKey);
        DateTimeOffset acknowledged;
        if (!DateTimeOffset.TryParse(stored, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out acknowledged))
        {
            // First run of this version (fresh install or upgrade): only problems recorded from now on
            // are announced, so years of older history do not greet the user as a warning.
            acknowledged = DateTimeOffset.UtcNow;
            await AppServices.Store.SetSettingAsync(AttentionAcknowledgedKey, acknowledged.ToString("O", CultureInfo.InvariantCulture));
        }

        var summary = ScheduleExtras.Attention(history, acknowledged);
        _attentionLatestUtc = summary.LatestUtc;
        AttentionText = summary.Text;
    }

    private void RefreshHistoryView()
    {
        HistoryView.Refresh();
        OnPropertyChanged(nameof(HistoryIsEmpty));
    }

    private void RefreshSelectedScheduleHistory()
    {
        SelectedScheduleHistory.Clear();
        if (SelectedSchedule is not { } selected)
        {
            return;
        }

        foreach (var row in History.Where(row => row.SeriesKey == selected.SeriesKey).Take(5))
        {
            SelectedScheduleHistory.Add(row);
        }
    }

    private bool MatchesHistoryFilter(HistoryRow row)
    {
        if (HistoryScheduleFilter is { } schedule && row.SeriesKey != schedule.SeriesKey)
        {
            return false;
        }

        var resultMatches = HistoryResultFilter switch
        {
            HistoryResultFilter.Success => row.History.Result == ResultKind.Success,
            HistoryResultFilter.Failure => row.History.Result == ResultKind.Failure,
            HistoryResultFilter.Skipped => row.History.Result == ResultKind.Skipped,
            HistoryResultFilter.Warning => row.History.Result == ResultKind.Warning,
            HistoryResultFilter.Information => row.History.Result == ResultKind.Information,
            _ => true
        };
        if (!resultMatches)
        {
            return false;
        }

        var search = HistorySearch.Trim();
        return search.Length == 0 ||
               row.DetailText.Contains(search, StringComparison.CurrentCultureIgnoreCase) ||
               row.EventText.Contains(search, StringComparison.CurrentCultureIgnoreCase) ||
               row.History.EventType.Contains(search, StringComparison.OrdinalIgnoreCase) ||
               row.TimeText.Contains(search, StringComparison.Ordinal);
    }

    private static bool IsRemovedFeatureHistory(string eventType) =>
        eventType.Contains("FirmwareRtc", StringComparison.OrdinalIgnoreCase) ||
        eventType.Contains("S5", StringComparison.OrdinalIgnoreCase) ||
        eventType.Contains("ShutdownGuard", StringComparison.OrdinalIgnoreCase);

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public sealed record ScheduleRow(PowerSchedule Schedule, IReadOnlyList<PowerSchedule> AllSchedules)
{
    public ScheduleRow(PowerSchedule schedule)
        : this(schedule, [schedule])
    {
    }

    /// <summary>Groups occurrences of one recurring series (the schedule id otherwise).</summary>
    public Guid? SeriesKey => ScheduleExtras.SeriesKey(Schedule);

    /// <summary>
    /// The stored time, except for a paused recurring occurrence whose time has passed: that one shows
    /// the slot it moves to when it is turned back on.
    /// </summary>
    public DateTime DisplayTime =>
        !Schedule.IsEnabled && Schedule.Recurrence is { } rule && RecurrencePolicy.IsUsable(rule) &&
        Schedule.ScheduledLocalDateTime <= DateTime.Now
            ? RecurrencePolicy.NextOccurrenceAfter(rule, Schedule.ScheduledLocalDateTime, DateTime.Now)
            : Schedule.ScheduledLocalDateTime;

    public string TimeText => DisplayTime.ToString("HH:mm", CultureInfo.InvariantCulture);
    public string DateText => UiText.DateLabel(DisplayTime);
    public string LongDateText => UiText.LongDate(DisplayTime);
    public string WhenText => UiText.When(DisplayTime, DateTime.Now);
    public string ActionText => UiText.ActionName(Schedule.ActionType);
    public string? NameText => ScheduleExtras.Name(Schedule);
    public string TitleText => NameText ?? ActionText;
    public string SubtitleText => NameText is null ? WhenText : $"{ActionText} · {WhenText}";
    public string? RepeatText => ScheduleExtras.RepeatText(Schedule);
    public bool IsRecurring => RepeatText is not null;
    public bool IsEnabled => Schedule.IsEnabled;
    public bool IsWake => PowerSchedulePolicy.IsWakeSchedule(Schedule.ActionType);
    public string EnabledText => AppText.T(Schedule.IsEnabled ? "켜짐" : "꺼짐");
    public string ToggleMenuText => AppText.T(Schedule.IsEnabled ? "끄기" : "켜기");
    public string RepeatDisplayText => RepeatText ?? AppText.T("한 번");
    public string SessionText => AppText.T(IsWake
        ? Schedule.OneTimeAutoLogonEnabled ? "1회 자동 로그인" : "로그인 필요"
        : "—");
    public string ProgramsText => IsWake
        ? Schedule.FollowUpPrograms.Count == 0 ? AppText.T("없음") : AppText.F("{0}개", Schedule.FollowUpPrograms.Count)
        : "—";
    public IReadOnlyList<ProgramDraft> Programs => Schedule.FollowUpPrograms
        .OrderBy(program => program.SortOrder)
        .Select(program => new ProgramDraft(program.Id, program.ExecutablePath, program.DelayAfterDesktopReadyMinutes, program.Arguments, program.WorkingDirectory, program.RunElevated))
        .ToArray();
    public bool HasPrograms => IsWake && Schedule.FollowUpPrograms.Count > 0;

    public string ActionGlyph => Schedule.ActionType switch
    {
        PowerActionType.Shutdown => Glyphs.Power,
        PowerActionType.Hibernate => Glyphs.Hibernate,
        PowerActionType.Sleep => Glyphs.Sleep,
        PowerActionType.WakeFromSleep or PowerActionType.WakeFromHibernate => Glyphs.Wake,
        _ => Glyphs.Clock
    };

    public string Summary => $"{DisplayTime:MM.dd HH:mm} {ActionText}";

    /// <summary>Name read by screen readers for the list item.</summary>
    public override string ToString() =>
        string.Join(", ", new[] { TitleText, WhenText, RepeatText, EnabledText }.Where(part => !string.IsNullOrWhiteSpace(part)));
}

public sealed record HistoryRow(ExecutionHistory History, Guid? SeriesKey)
{
    public HistoryRow(ExecutionHistory history)
        : this(history, history.ScheduleId)
    {
    }

    public string TimeText => History.ActualTimeUtc.ToLocalTime().ToString("yyyy.MM.dd HH:mm:ss", CultureInfo.InvariantCulture);
    public string ResultText => UiText.ResultName(History.Result);
    public string EventText => UiText.EventName(History.EventType);
    public string DetailText => AppText.T(History.UserReadableMessage);
    public ResultKind Result => History.Result;

    public override string ToString() => $"{TimeText}, {ResultText}, {EventText}, {DetailText}";
}

public sealed record CompatibilityRow(CompatibilityResult Result)
{
    public string CapabilityText => AppText.T(Result.Capability switch
    {
        CompatibilityCapabilities.S3Wake => "절전(S3) 후 자동 깨우기",
        CompatibilityCapabilities.S4Wake => "최대 절전(S4) 후 자동 깨우기",
        CompatibilityCapabilities.OneTimeAutoLogon => "앱 관리형 1회 자동 로그인",
        _ => Result.Capability
    });
    public string StatusText => AppText.T(Result.Capability == CompatibilityCapabilities.OneTimeAutoLogon
        ? Result.Status == CapabilityStatus.Confirmed
            ? "사용 가능 / 검증 완료"
            : Result.Status == CapabilityStatus.Unknown
                ? "상태 확인 실패"
                : "설정 필요 / 검증 전"
        : Result.Status switch
        {
            CapabilityStatus.Confirmed => "지원 확인됨",
            CapabilityStatus.NeedsPhysicalTest => "지원 가능성 있음 / 실제 테스트 필요",
            CapabilityStatus.UnsupportedOrFailed => "미지원 또는 테스트 실패",
            _ => "확인 불가"
        });
    public string DetailText => AppText.T(Result.Detail);
    public CapabilityStatus Status => Result.Status;

    public string Glyph => Result.Capability switch
    {
        CompatibilityCapabilities.S3Wake => Glyphs.Sleep,
        CompatibilityCapabilities.S4Wake => Glyphs.Hibernate,
        CompatibilityCapabilities.OneTimeAutoLogon => Glyphs.Contact,
        _ => Glyphs.Diagnostic
    };

    public override string ToString() => $"{CapabilityText}, {StatusText}";
}
