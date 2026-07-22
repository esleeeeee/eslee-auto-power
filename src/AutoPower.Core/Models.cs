namespace AutoPower.Core;

public enum PowerActionType
{
    PowerOn = 0,
    Shutdown = 1,
    Hibernate = 2,
    Sleep = 3,
    WakeFromSleep = 4,
    WakeFromHibernate = 5
}

public enum WakeModePreference
{
    Automatic,
    SleepS3,
    HibernateS4
}

public static class PowerSchedulePolicy
{
    public static bool IsWakeSchedule(PowerActionType action) =>
        action is PowerActionType.WakeFromSleep or PowerActionType.WakeFromHibernate;

    public static bool IsRemovedSchedule(PowerActionType action) =>
        action == PowerActionType.PowerOn;

    public static bool IsLowPowerEntry(PowerActionType action) =>
        action is PowerActionType.Sleep or PowerActionType.Hibernate;

    public static PowerActionType ResolveWakeAction(
        WakeModePreference preference,
        bool s3Available,
        bool s4Available,
        bool hibernateEnabled,
        CapabilityStatus? s3TestStatus = null,
        CapabilityStatus? s4TestStatus = null)
    {
        var s4Usable = s4Available && hibernateEnabled;
        var s3AutomaticCandidate = s3Available && s3TestStatus != CapabilityStatus.UnsupportedOrFailed;
        var s4AutomaticCandidate = s4Usable && s4TestStatus != CapabilityStatus.UnsupportedOrFailed;
        return preference switch
        {
            WakeModePreference.SleepS3 when s3Available => PowerActionType.WakeFromSleep,
            WakeModePreference.SleepS3 => throw new InvalidOperationException("현재 시스템에서는 S3 절전 자동 시작을 사용할 수 없습니다."),
            WakeModePreference.HibernateS4 when s4Usable => PowerActionType.WakeFromHibernate,
            WakeModePreference.HibernateS4 => throw new InvalidOperationException(
                "현재 시스템에서는 S4 최대 절전 자동 시작을 사용할 수 없습니다. Windows 최대 절전 활성화 상태를 확인하세요."),
            WakeModePreference.Automatic when s4AutomaticCandidate && s4TestStatus == CapabilityStatus.Confirmed => PowerActionType.WakeFromHibernate,
            WakeModePreference.Automatic when s3AutomaticCandidate && s3TestStatus == CapabilityStatus.Confirmed => PowerActionType.WakeFromSleep,
            WakeModePreference.Automatic when s4AutomaticCandidate => PowerActionType.WakeFromHibernate,
            WakeModePreference.Automatic when s3AutomaticCandidate => PowerActionType.WakeFromSleep,
            WakeModePreference.Automatic => throw new InvalidOperationException(
                "자동 선택에 사용할 수 있는 S3 절전 또는 S4 최대 절전 경로가 없습니다. 호환성 결과와 Windows 최대 절전 활성화 상태를 확인하세요."),
            _ => throw new ArgumentOutOfRangeException(nameof(preference))
        };
    }

    public static PowerActionType RequiredLowPowerState(PowerActionType wakeAction) => wakeAction switch
    {
        PowerActionType.WakeFromSleep => PowerActionType.Sleep,
        PowerActionType.WakeFromHibernate => PowerActionType.Hibernate,
        _ => throw new ArgumentOutOfRangeException(nameof(wakeAction), "S3 또는 S4 깨우기 예약이 아닙니다.")
    };

    public static string WakeStateName(PowerActionType wakeAction) => wakeAction switch
    {
        PowerActionType.WakeFromSleep => "S3 절전",
        PowerActionType.WakeFromHibernate => "S4 최대 절전",
        _ => throw new ArgumentOutOfRangeException(nameof(wakeAction), "S3 또는 S4 깨우기 예약이 아닙니다.")
    };
}

public enum ScheduleStatus
{
    Pending,
    Completed,
    Failed,
    Skipped,
    Missed,
    Disabled
}

public enum ResultKind
{
    Information,
    Success,
    Failure,
    Skipped,
    Warning
}

public enum CapabilityStatus
{
    Confirmed,
    NeedsPhysicalTest,
    Unknown,
    UnsupportedOrFailed
}

public static class CompatibilityCapabilities
{
    public const string S3Wake = "S3Wake";
    public const string S4Wake = "S4Wake";
    public const string OneTimeAutoLogon = "OneTimeAutoLogon";
}

public static class ScheduleTimePolicy
{
    public static DateTime NewScheduleDefault(DateTime openedAtLocal) =>
        NormalizeToMinute(openedAtLocal);

    public static DateTime QuickShutdown(DateTime clickedAtLocal, int hours)
    {
        if (hours is not (1 or 2))
        {
            throw new ArgumentOutOfRangeException(nameof(hours), "빠른 완전 종료 예약은 1시간 또는 2시간만 지원합니다.");
        }

        return NormalizeToMinute(clickedAtLocal.AddHours(hours));
    }

    private static DateTime NormalizeToMinute(DateTime value) =>
        new(value.Year, value.Month, value.Day, value.Hour, value.Minute, 0, DateTimeKind.Unspecified);
}

public enum PendingOperationType
{
    RegisterSchedule,
    UpdateSchedule,
    RemoveSchedule,
    ArmAutologon,
    DisarmAutologon,
    Reconcile,
    Cleanup
}

public enum PendingOperationState
{
    Pending,
    Compensating,
    Completed,
    Failed
}

public sealed record FollowUpProgram(
    Guid Id,
    Guid ScheduleId,
    string ExecutablePath,
    int DelayAfterDesktopReadyMinutes,
    string? Arguments,
    string? WorkingDirectory,
    bool RunElevated,
    int SortOrder);

public sealed record PowerSchedule(
    Guid Id,
    DateTime ScheduledLocalDateTime,
    PowerActionType ActionType,
    bool IsEnabled,
    bool OneTimeAutoLogonEnabled,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    ScheduleStatus Status,
    IReadOnlyList<FollowUpProgram> FollowUpPrograms)
{
    public static PowerSchedule Create(
        DateTime scheduledLocalDateTime,
        PowerActionType actionType,
        bool oneTimeAutoLogonEnabled = false,
        IReadOnlyList<FollowUpProgram>? followUpPrograms = null)
    {
        var now = DateTimeOffset.UtcNow;
        var id = Guid.NewGuid();
        var programs = (followUpPrograms ?? [])
            .Select((program, index) => program with { ScheduleId = id, SortOrder = index })
            .ToArray();
        return new PowerSchedule(
            id,
            DateTime.SpecifyKind(scheduledLocalDateTime, DateTimeKind.Unspecified),
            actionType,
            true,
            oneTimeAutoLogonEnabled,
            now,
            now,
            ScheduleStatus.Pending,
            programs);
    }
}

public sealed record ExecutionHistory(
    long Id,
    Guid? ScheduleId,
    string EventType,
    ResultKind Result,
    DateTime? PlannedLocalTime,
    DateTimeOffset ActualTimeUtc,
    string UserReadableMessage,
    string? ErrorCode);

public sealed record CompatibilityResult(
    string Capability,
    CapabilityStatus Status,
    DateTimeOffset TestedAtUtc,
    string Detail);

public sealed record PendingOperation(
    Guid Id,
    PendingOperationType Type,
    Guid? ScheduleId,
    PendingOperationState State,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    string Detail);

public sealed record ValidationIssue(string Code, string Message);

public sealed record ValidationResult(IReadOnlyList<ValidationIssue> Issues)
{
    public bool IsValid => Issues.Count == 0;
    public static ValidationResult Success { get; } = new([]);
}
