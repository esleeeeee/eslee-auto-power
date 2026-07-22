using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using AutoPower.Core;

namespace AutoPower.App;

public sealed class MainViewModel : INotifyPropertyChanged
{
    private ScheduleRow? _selectedSchedule;
    private string _statusMessage = "준비됨";
    private string? _securityWarning;

    public ObservableCollection<ScheduleRow> Schedules { get; } = [];
    public ObservableCollection<HistoryRow> History { get; } = [];
    public ObservableCollection<CompatibilityRow> Compatibility { get; } = [];

    public ScheduleRow? SelectedSchedule
    {
        get => _selectedSchedule;
        set { _selectedSchedule = value; OnPropertyChanged(); }
    }

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

    public string NextScheduleSummary => Schedules.FirstOrDefault(row =>
        row.Schedule.IsEnabled && row.Schedule.Status == ScheduleStatus.Pending)?.Summary ?? AppText.T("없음");

    public PowerSchedule? NextPendingWakeSchedule => Schedules
        .Select(row => row.Schedule)
        .FirstOrDefault(schedule =>
            schedule.IsEnabled &&
            schedule.Status == ScheduleStatus.Pending &&
            PowerSchedulePolicy.IsWakeSchedule(schedule.ActionType));

    public async Task RefreshAsync()
    {
        var selectedId = SelectedSchedule?.Schedule.Id;
        var allSchedules = await AppServices.Store.GetAllSchedulesAsync();
        var hiddenScheduleIds = allSchedules
            .Where(schedule => PowerSchedulePolicy.IsRemovedSchedule(schedule.ActionType))
            .Select(schedule => schedule.Id)
            .ToHashSet();
        var schedules = allSchedules
            .Where(schedule => schedule.ScheduledLocalDateTime > DateTime.Now &&
                               schedule.Status is ScheduleStatus.Pending or ScheduleStatus.Disabled &&
                               !PowerSchedulePolicy.IsRemovedSchedule(schedule.ActionType))
            .OrderBy(schedule => schedule.ScheduledLocalDateTime);
        Schedules.Clear();
        foreach (var schedule in schedules)
        {
            Schedules.Add(new ScheduleRow(schedule));
        }

        SelectedSchedule = Schedules.FirstOrDefault(row => row.Schedule.Id == selectedId);
        History.Clear();
        foreach (var item in (await AppServices.Store.GetHistoryAsync()).Where(item =>
                     !(item.ScheduleId is Guid id && hiddenScheduleIds.Contains(id)) &&
                     !IsRemovedFeatureHistory(item.EventType)))
        {
            History.Add(new HistoryRow(item));
        }

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
        StatusMessage = Schedules.Count == 0 ? "미래 예약이 없습니다." : $"미래 예약 {Schedules.Count}개";
        OnPropertyChanged(nameof(NextScheduleSummary));
        OnPropertyChanged(nameof(NextPendingWakeSchedule));
    }

    private static bool IsRemovedFeatureHistory(string eventType) =>
        eventType.Contains("FirmwareRtc", StringComparison.OrdinalIgnoreCase) ||
        eventType.Contains("S5", StringComparison.OrdinalIgnoreCase) ||
        eventType.Contains("ShutdownGuard", StringComparison.OrdinalIgnoreCase);

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public sealed record ScheduleRow(PowerSchedule Schedule)
{
    public string DateText => Schedule.ScheduledLocalDateTime.ToString("yyyy.MM.dd (ddd)", System.Globalization.CultureInfo.CurrentCulture);
    public string TimeText => Schedule.ScheduledLocalDateTime.ToString("HH:mm", System.Globalization.CultureInfo.CurrentCulture);
    public string ActionText => AppText.T(Schedule.ActionType switch
    {
        PowerActionType.WakeFromSleep => "자동 시작 (S3)",
        PowerActionType.WakeFromHibernate => "자동 시작 (S4)",
        PowerActionType.Shutdown => "완전 종료",
        PowerActionType.Hibernate => "최대 절전",
        PowerActionType.Sleep => "절전",
        _ => "이전 전원 동작"
    });
    public string EnabledText => Schedule.IsEnabled ? "ON" : "OFF";
    public string SessionText => AppText.T(PowerSchedulePolicy.IsWakeSchedule(Schedule.ActionType)
        ? Schedule.OneTimeAutoLogonEnabled ? "1회 자동 로그인" : "로그인 필요"
        : "—");
    public string ProgramsText => PowerSchedulePolicy.IsWakeSchedule(Schedule.ActionType)
        ? AppText.IsEnglish ? $"{Schedule.FollowUpPrograms.Count}" : $"{Schedule.FollowUpPrograms.Count}개"
        : "—";
    public string Summary => $"{Schedule.ScheduledLocalDateTime:MM.dd HH:mm} {ActionText}";
}

public sealed record HistoryRow(ExecutionHistory History)
{
    public string TimeText => History.ActualTimeUtc.ToLocalTime().ToString("yyyy.MM.dd HH:mm:ss", System.Globalization.CultureInfo.CurrentCulture);
    public string ResultText => AppText.T(History.Result switch
    {
        ResultKind.Success => "성공",
        ResultKind.Failure => "실패",
        ResultKind.Skipped => "건너뜀",
        ResultKind.Warning => "경고",
        _ => "정보"
    });
    public string DetailText => AppText.T(History.UserReadableMessage);
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
}
