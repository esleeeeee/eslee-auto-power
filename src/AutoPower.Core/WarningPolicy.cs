namespace AutoPower.Core;

public enum WarningChoice
{
    Now,
    KeepOriginal,
    Skip
}
public static class WarningPolicy
{
    public static readonly TimeSpan AdvanceNotice = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan DialogCountdown = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan ShutdownGracePeriod = TimeSpan.FromSeconds(30);

    public static WarningChoice ChoiceOnTimeout => WarningChoice.KeepOriginal;
    public static WarningChoice ChoiceOnWindowClose => WarningChoice.KeepOriginal;

    public static bool CanRunShutdownFallback(PowerSchedule schedule, DateTime nowLocal) =>
        schedule.ActionType == PowerActionType.Shutdown &&
        schedule.Status == ScheduleStatus.Completed &&
        nowLocal >= schedule.ScheduledLocalDateTime - TimeSpan.FromMinutes(6) &&
        nowLocal <= schedule.ScheduledLocalDateTime + TimeSpan.FromMinutes(2);

    public static DateTime ResolveVirtualDesktopReady(
        WarningChoice choice,
        DateTime clickedAtLocal,
        DateTime scheduledLocalTime) => choice switch
        {
            WarningChoice.Now => clickedAtLocal,
            WarningChoice.KeepOriginal => scheduledLocalTime,
            WarningChoice.Skip => throw new InvalidOperationException("건너뛴 예약에는 가상 T0가 없습니다."),
            _ => throw new ArgumentOutOfRangeException(nameof(choice))
        };
}
