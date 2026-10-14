using System.Globalization;

namespace AutoPower.Core;

public enum RecurrenceKind
{
    None = 0,
    Daily = 1,
    Weekdays = 2,
    Weekly = 3
}

[Flags]
public enum RecurrenceDays
{
    None = 0,
    Sunday = 1 << 0,
    Monday = 1 << 1,
    Tuesday = 1 << 2,
    Wednesday = 1 << 3,
    Thursday = 1 << 4,
    Friday = 1 << 5,
    Saturday = 1 << 6,
    Weekdays = Monday | Tuesday | Wednesday | Thursday | Friday,
    All = Weekdays | Saturday | Sunday
}

/// <summary>
/// Series rule: repeat kind, allowed days (used by <see cref="RecurrenceKind.Weekly"/>) and the anchor
/// local wall-clock time of day. Snoozing an occurrence never changes the anchor.
/// </summary>
public sealed record RecurrenceRule(RecurrenceKind Kind, RecurrenceDays Days, TimeSpan TimeOfDay)
{
    public static RecurrenceRule CreateDaily(TimeSpan timeOfDay) =>
        new(RecurrenceKind.Daily, RecurrenceDays.All, timeOfDay);

    public static RecurrenceRule CreateWeekdays(TimeSpan timeOfDay) =>
        new(RecurrenceKind.Weekdays, RecurrenceDays.Weekdays, timeOfDay);

    public static RecurrenceRule CreateWeekly(RecurrenceDays days, TimeSpan timeOfDay) =>
        new(RecurrenceKind.Weekly, days & RecurrenceDays.All, timeOfDay);

    public RecurrenceDays EffectiveDays => Kind switch
    {
        RecurrenceKind.Daily => RecurrenceDays.All,
        RecurrenceKind.Weekdays => RecurrenceDays.Weekdays,
        RecurrenceKind.Weekly => Days & RecurrenceDays.All,
        _ => RecurrenceDays.None
    };

    public bool AllowsDay(DayOfWeek day) => (EffectiveDays & RecurrencePolicy.ToFlag(day)) != 0;
}

public static class RecurrencePolicy
{
    /// <summary>A generated or re-enabled occurrence must be at least this far in the future.</summary>
    public static TimeSpan MinimumLeadTime { get; } = TimeSpan.FromMinutes(1);

    private static readonly DayOfWeek[] MondayFirst =
    [
        DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday,
        DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday
    ];

    public static RecurrenceDays ToFlag(DayOfWeek day) => (RecurrenceDays)(1 << (int)day);

    public static bool IsUsable(RecurrenceRule? rule) =>
        rule is not null &&
        Enum.IsDefined(rule.Kind) &&
        rule.Kind != RecurrenceKind.None &&
        rule.EffectiveDays != RecurrenceDays.None &&
        rule.TimeOfDay >= TimeSpan.Zero &&
        rule.TimeOfDay < TimeSpan.FromDays(1);

    /// <summary>
    /// Returns the first local wall-clock time strictly after <paramref name="afterLocal"/> that falls on an
    /// allowed day at the rule's anchor time. Times are unspecified-kind local values, like every stored
    /// schedule time, so the calculation is calendar arithmetic and DST-agnostic.
    /// </summary>
    public static DateTime NextOccurrence(RecurrenceRule rule, DateTime afterLocal)
    {
        ArgumentNullException.ThrowIfNull(rule);
        if (!IsUsable(rule))
        {
            throw new ArgumentException("반복 규칙이 올바르지 않습니다.", nameof(rule));
        }

        var after = DateTime.SpecifyKind(afterLocal, DateTimeKind.Unspecified);
        var candidate = after.Date + rule.TimeOfDay;
        if (candidate <= after)
        {
            candidate = candidate.AddDays(1);
        }

        for (var offset = 0; offset < 7; offset++)
        {
            if (rule.AllowsDay(candidate.DayOfWeek))
            {
                return candidate;
            }

            candidate = candidate.AddDays(1);
        }

        throw new InvalidOperationException("반복 규칙에 허용된 요일이 없습니다.");
    }

    /// <summary>
    /// Next occurrence of a series after <paramref name="occurrenceLocal"/>, never earlier than the minimum
    /// lead time from now. A snoozed occurrence still yields the next slot at the anchor time.
    /// </summary>
    public static DateTime NextOccurrenceAfter(RecurrenceRule rule, DateTime occurrenceLocal, DateTime nowLocal)
    {
        var occurrence = DateTime.SpecifyKind(occurrenceLocal, DateTimeKind.Unspecified);
        var earliest = DateTime.SpecifyKind(nowLocal, DateTimeKind.Unspecified) + MinimumLeadTime;
        return NextOccurrence(rule, occurrence > earliest ? occurrence : earliest);
    }

    /// <summary>
    /// First occurrence for a new or edited series: the first allowed slot at or after the desired time,
    /// or the next slot after now plus the minimum lead time when the desired slot is too close or past.
    /// </summary>
    public static DateTime FirstOccurrence(RecurrenceRule rule, DateTime desiredLocal, DateTime nowLocal)
    {
        var onOrAfter = NextOccurrence(rule, DateTime.SpecifyKind(desiredLocal, DateTimeKind.Unspecified).AddTicks(-1));
        var earliest = DateTime.SpecifyKind(nowLocal, DateTimeKind.Unspecified) + MinimumLeadTime;
        return onOrAfter > earliest ? onOrAfter : NextOccurrence(rule, earliest);
    }

    /// <summary>Allowed days of a rule, Monday first.</summary>
    public static IReadOnlyList<DayOfWeek> OrderedDays(RecurrenceRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        return MondayFirst.Where(rule.AllowsDay).ToArray();
    }

    /// <summary>
    /// Short display text such as "매일 23:00" or "Every Mon, Wed at 23:00". Pass "en" for English;
    /// any other language code returns Korean. A null rule describes a one-time schedule.
    /// </summary>
    public static string Describe(RecurrenceRule? rule, string languageCode = "ko")
    {
        var english = string.Equals(languageCode, "en", StringComparison.OrdinalIgnoreCase);
        if (rule is null || rule.Kind == RecurrenceKind.None)
        {
            return english ? "Does not repeat" : "반복 안 함";
        }

        var time = rule.TimeOfDay.ToString(@"hh\:mm", CultureInfo.InvariantCulture);
        return rule.Kind switch
        {
            RecurrenceKind.Daily => english ? $"Every day at {time}" : $"매일 {time}",
            RecurrenceKind.Weekdays => english ? $"Weekdays (Mon–Fri) at {time}" : $"평일(월–금) {time}",
            _ => english
                ? $"Every {string.Join(", ", OrderedDays(rule).Select(day => EnglishDay(day)))} at {time}"
                : $"매주 {string.Join("·", OrderedDays(rule).Select(day => KoreanDay(day)))} {time}"
        };
    }

    private static string KoreanDay(DayOfWeek day) => day switch
    {
        DayOfWeek.Monday => "월",
        DayOfWeek.Tuesday => "화",
        DayOfWeek.Wednesday => "수",
        DayOfWeek.Thursday => "목",
        DayOfWeek.Friday => "금",
        DayOfWeek.Saturday => "토",
        _ => "일"
    };

    private static string EnglishDay(DayOfWeek day) =>
        CultureInfo.InvariantCulture.DateTimeFormat.GetAbbreviatedDayName(day);
}
