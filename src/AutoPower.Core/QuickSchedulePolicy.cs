namespace AutoPower.Core;

/// <summary>Delays offered by the tray quick-schedule menu.</summary>
public static class QuickSchedulePolicy
{
    public static IReadOnlyList<int> SupportedMinutes { get; } = [30, 60, 120, 180];

    public static bool IsSupportedAction(PowerActionType action) => PowerSchedulePolicy.IsPowerTransition(action);

    /// <summary>The click time plus <paramref name="minutes"/>, normalized to the minute.</summary>
    public static DateTime At(DateTime clickedAtLocal, int minutes)
    {
        if (!SupportedMinutes.Contains(minutes))
        {
            throw new ArgumentOutOfRangeException(nameof(minutes), "빠른 예약은 30분, 1시간, 2시간, 3시간만 지원합니다.");
        }

        var value = clickedAtLocal.AddMinutes(minutes);
        return new DateTime(value.Year, value.Month, value.Day, value.Hour, value.Minute, 0, DateTimeKind.Unspecified);
    }
}
