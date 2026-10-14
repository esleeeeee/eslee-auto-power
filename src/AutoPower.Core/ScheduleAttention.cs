namespace AutoPower.Core;

/// <summary>ExecutionHistory.EventType values that callers outside the writer need to recognize.</summary>
public static class HistoryEventTypes
{
    public const string ScheduleCreated = "ScheduleCreated";
    public const string ScheduleUpdated = "ScheduleUpdated";
    public const string ScheduleDeleted = "ScheduleDeleted";
    public const string ScheduleSkipped = "ScheduleSkipped";
    public const string ScheduleMissed = "ScheduleMissed";
    public const string PowerTransitionFailed = "PowerTransitionFailed";
    public const string PowerTransitionResultUnknown = "PowerTransitionResultUnknown";
    public const string FollowUpProgram = "FollowUpProgram";
    public const string Snoozed = "Snoozed";
    public const string RecurrenceAdvanced = "RecurrenceAdvanced";
    public const string RecurrenceAdvanceFailed = "RecurrenceAdvanceFailed";
    public const string SchedulesPaused = "SchedulesPaused";
    public const string SchedulesResumed = "SchedulesResumed";
    public const string ScheduleResumeConflict = "ScheduleResumeConflict";
}

public sealed record AttentionSummary(
    int FailureCount,
    int MissedCount,
    int ResultUnknownCount,
    int RecurrenceAdvanceFailureCount,
    DateTimeOffset? LatestUtc)
{
    public static AttentionSummary None { get; } = new(0, 0, 0, 0, null);

    public int Total => FailureCount + MissedCount + ResultUnknownCount + RecurrenceAdvanceFailureCount;

    public bool HasAttention => Total > 0;
}

public static class AttentionSummaryPolicy
{
    /// <summary>
    /// Counts runs that need the user's attention after <paramref name="lastAcknowledgedUtc"/> (all rows when
    /// null): failed power transitions and failed follow-up program launches, missed schedules, power
    /// transitions whose result could not be proven, and recurring series that could not be advanced.
    /// </summary>
    public static AttentionSummary Summarize(
        IEnumerable<ExecutionHistory> history,
        DateTimeOffset? lastAcknowledgedUtc)
    {
        ArgumentNullException.ThrowIfNull(history);
        int failures = 0, missed = 0, unknown = 0, advanceFailures = 0;
        DateTimeOffset? latest = null;
        foreach (var item in history)
        {
            if (lastAcknowledgedUtc is DateTimeOffset acknowledged && item.ActualTimeUtc <= acknowledged)
            {
                continue;
            }

            switch (Classify(item))
            {
                case AttentionKind.Failure:
                    failures++;
                    break;
                case AttentionKind.Missed:
                    missed++;
                    break;
                case AttentionKind.ResultUnknown:
                    unknown++;
                    break;
                case AttentionKind.RecurrenceAdvanceFailure:
                    advanceFailures++;
                    break;
                default:
                    continue;
            }

            if (latest is null || item.ActualTimeUtc > latest)
            {
                latest = item.ActualTimeUtc;
            }
        }

        return new AttentionSummary(failures, missed, unknown, advanceFailures, latest);
    }

    public static bool NeedsAttention(ExecutionHistory item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return Classify(item) != AttentionKind.None;
    }

    private static AttentionKind Classify(ExecutionHistory item) => item.EventType switch
    {
        HistoryEventTypes.PowerTransitionFailed => AttentionKind.Failure,
        HistoryEventTypes.FollowUpProgram when item.Result == ResultKind.Failure => AttentionKind.Failure,
        HistoryEventTypes.ScheduleMissed => AttentionKind.Missed,
        HistoryEventTypes.PowerTransitionResultUnknown => AttentionKind.ResultUnknown,
        HistoryEventTypes.RecurrenceAdvanceFailed => AttentionKind.RecurrenceAdvanceFailure,
        _ => AttentionKind.None
    };

    private enum AttentionKind
    {
        None,
        Failure,
        Missed,
        ResultUnknown,
        RecurrenceAdvanceFailure
    }
}
