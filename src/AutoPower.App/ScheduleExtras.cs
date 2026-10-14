using AutoPower.Core;

namespace AutoPower.App;

/// <summary>
/// UI accessors for schedule name, recurrence and the attention summary.
/// </summary>
internal static class ScheduleExtras
{
    public static string? Name(PowerSchedule schedule) => schedule.Name;

    public static string? RepeatText(PowerSchedule schedule) =>
        schedule.Recurrence is { } rule ? RecurrencePolicy.Describe(rule, AppText.LanguageCode) : null;

    /// <summary>Occurrences of one recurring series share the series id; others use their own id.</summary>
    public static Guid? SeriesKey(PowerSchedule schedule) => schedule.SeriesId ?? schedule.Id;

    public static (string? Text, DateTimeOffset? LatestUtc) Attention(
        IReadOnlyList<ExecutionHistory> history,
        DateTimeOffset? acknowledgedUtc)
    {
        var summary = AttentionSummaryPolicy.Summarize(history, acknowledgedUtc);
        if (!summary.HasAttention)
        {
            return (null, summary.LatestUtc);
        }

        var parts = new List<string>();
        if (summary.FailureCount > 0)
        {
            parts.Add(AppText.F("실패 {0}건", summary.FailureCount));
        }

        if (summary.MissedCount > 0)
        {
            parts.Add(AppText.F("놓친 예약 {0}건", summary.MissedCount));
        }

        if (summary.ResultUnknownCount > 0)
        {
            parts.Add(AppText.F("결과 확인 불가 {0}건", summary.ResultUnknownCount));
        }

        if (summary.RecurrenceAdvanceFailureCount > 0)
        {
            parts.Add(AppText.F("반복 준비 실패 {0}건", summary.RecurrenceAdvanceFailureCount));
        }

        return (AppText.F("확인이 필요한 실행 기록이 있습니다: {0}", string.Join(", ", parts)), summary.LatestUtc);
    }
}
