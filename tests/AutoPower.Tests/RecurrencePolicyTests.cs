using AutoPower.Core;

namespace AutoPower.Tests;

[TestClass]
public sealed class RecurrencePolicyTests
{
    private static readonly TimeSpan Eleven = new(23, 0, 0);

    [TestMethod]
    public void DailyReturnsTheSameDayWhenTheAnchorIsStillAhead()
    {
        var next = RecurrencePolicy.NextOccurrence(RecurrenceRule.CreateDaily(Eleven), new DateTime(2026, 10, 14, 9, 30, 0));

        Assert.AreEqual(new DateTime(2026, 10, 14, 23, 0, 0), next);
        Assert.AreEqual(DateTimeKind.Unspecified, next.Kind);
    }

    [TestMethod]
    public void DailyIsStrictlyAfterTheGivenTime()
    {
        var next = RecurrencePolicy.NextOccurrence(RecurrenceRule.CreateDaily(Eleven), new DateTime(2026, 10, 14, 23, 0, 0));

        Assert.AreEqual(new DateTime(2026, 10, 15, 23, 0, 0), next);
    }

    [TestMethod]
    public void WeekdaysSkipFromFridayToMonday()
    {
        var friday = new DateTime(2026, 10, 16, 23, 0, 0);
        Assert.AreEqual(DayOfWeek.Friday, friday.DayOfWeek);

        var next = RecurrencePolicy.NextOccurrence(RecurrenceRule.CreateWeekdays(Eleven), friday);

        Assert.AreEqual(new DateTime(2026, 10, 19, 23, 0, 0), next);
        Assert.AreEqual(DayOfWeek.Monday, next.DayOfWeek);
    }

    [TestMethod]
    public void WeekdaysFromSaturdayMorningGoToMonday()
    {
        var next = RecurrencePolicy.NextOccurrence(RecurrenceRule.CreateWeekdays(Eleven), new DateTime(2026, 10, 17, 8, 0, 0));

        Assert.AreEqual(new DateTime(2026, 10, 19, 23, 0, 0), next);
    }

    [TestMethod]
    public void SelectedDaysUseOnlyThoseDays()
    {
        var rule = RecurrenceRule.CreateWeekly(RecurrenceDays.Monday | RecurrenceDays.Wednesday, Eleven);
        var wednesday = new DateTime(2026, 10, 14, 23, 0, 0);
        Assert.AreEqual(DayOfWeek.Wednesday, wednesday.DayOfWeek);

        Assert.AreEqual(new DateTime(2026, 10, 19, 23, 0, 0), RecurrencePolicy.NextOccurrence(rule, wednesday));
        Assert.AreEqual(new DateTime(2026, 10, 19, 23, 0, 0), RecurrencePolicy.NextOccurrence(rule, new DateTime(2026, 10, 19, 9, 0, 0)));
        Assert.AreEqual(new DateTime(2026, 10, 21, 23, 0, 0), RecurrencePolicy.NextOccurrence(rule, new DateTime(2026, 10, 19, 23, 0, 0)));
    }

    [TestMethod]
    public void MonthAndYearRollOver()
    {
        var daily = RecurrenceRule.CreateDaily(new TimeSpan(6, 0, 0));
        Assert.AreEqual(new DateTime(2026, 11, 1, 6, 0, 0), RecurrencePolicy.NextOccurrence(daily, new DateTime(2026, 10, 31, 6, 0, 0)));
        Assert.AreEqual(new DateTime(2027, 1, 1, 6, 0, 0), RecurrencePolicy.NextOccurrence(daily, new DateTime(2026, 12, 31, 7, 0, 0)));

        var lastFriday = new DateTime(2027, 12, 31, 22, 0, 0);
        Assert.AreEqual(DayOfWeek.Friday, lastFriday.DayOfWeek);
        Assert.AreEqual(new DateTime(2028, 1, 3, 7, 0, 0),
            RecurrencePolicy.NextOccurrence(RecurrenceRule.CreateWeekdays(new TimeSpan(7, 0, 0)), lastFriday));
    }

    [TestMethod]
    public void LeapDayIsAnOrdinaryCalendarDay()
    {
        var daily = RecurrenceRule.CreateDaily(new TimeSpan(7, 0, 0));
        Assert.AreEqual(new DateTime(2028, 2, 29, 7, 0, 0), RecurrencePolicy.NextOccurrence(daily, new DateTime(2028, 2, 28, 23, 30, 0)));
        Assert.AreEqual(new DateTime(2028, 3, 1, 7, 0, 0), RecurrencePolicy.NextOccurrence(daily, new DateTime(2028, 2, 29, 7, 0, 0)));

        var tuesdays = RecurrenceRule.CreateWeekly(RecurrenceDays.Tuesday, new TimeSpan(7, 0, 0));
        Assert.AreEqual(new DateTime(2028, 2, 29, 7, 0, 0), RecurrencePolicy.NextOccurrence(tuesdays, new DateTime(2028, 2, 22, 7, 0, 0)));
    }

    [TestMethod]
    public void WeeklyWithoutDaysIsRejected()
    {
        var rule = RecurrenceRule.CreateWeekly(RecurrenceDays.None, Eleven);

        Assert.IsFalse(RecurrencePolicy.IsUsable(rule));
        Assert.ThrowsExactly<ArgumentException>(() => RecurrencePolicy.NextOccurrence(rule, new DateTime(2026, 10, 14, 0, 0, 0)));
    }

    [TestMethod]
    public void NextOccurrenceAfterIgnoresSnoozeAndRespectsLeadTime()
    {
        var rule = RecurrenceRule.CreateDaily(Eleven);
        var snoozed = new DateTime(2026, 10, 14, 23, 30, 0);

        Assert.AreEqual(new DateTime(2026, 10, 15, 23, 0, 0),
            RecurrencePolicy.NextOccurrenceAfter(rule, snoozed, snoozed));
        Assert.AreEqual(new DateTime(2026, 10, 18, 23, 0, 0),
            RecurrencePolicy.NextOccurrenceAfter(rule, new DateTime(2026, 10, 10, 23, 0, 0), new DateTime(2026, 10, 18, 20, 0, 0)));
        Assert.AreEqual(new DateTime(2026, 10, 19, 23, 0, 0),
            RecurrencePolicy.NextOccurrenceAfter(rule, new DateTime(2026, 10, 10, 23, 0, 0), new DateTime(2026, 10, 18, 22, 59, 30)));
    }

    [TestMethod]
    public void FirstOccurrenceKeepsAnAllowedDesiredSlotAndMovesOtherwise()
    {
        var rule = RecurrenceRule.CreateWeekdays(Eleven);
        var now = new DateTime(2026, 10, 14, 12, 0, 0);

        Assert.AreEqual(new DateTime(2026, 10, 14, 23, 0, 0), RecurrencePolicy.FirstOccurrence(rule, new DateTime(2026, 10, 14, 23, 0, 0), now));
        Assert.AreEqual(new DateTime(2026, 10, 19, 23, 0, 0), RecurrencePolicy.FirstOccurrence(rule, new DateTime(2026, 10, 17, 23, 0, 0), now));
        Assert.AreEqual(new DateTime(2026, 10, 14, 23, 0, 0), RecurrencePolicy.FirstOccurrence(rule, new DateTime(2026, 10, 13, 23, 0, 0), now));
        Assert.AreEqual(new DateTime(2026, 10, 15, 23, 0, 0), RecurrencePolicy.FirstOccurrence(rule, new DateTime(2026, 10, 14, 23, 0, 0), new DateTime(2026, 10, 14, 22, 59, 30)));
    }

    [TestMethod]
    public void DescribeSupportsKoreanAndEnglish()
    {
        var rule = RecurrenceRule.CreateWeekly(RecurrenceDays.Friday | RecurrenceDays.Monday | RecurrenceDays.Sunday, new TimeSpan(7, 5, 0));

        Assert.AreEqual("매주 월·금·일 07:05", RecurrencePolicy.Describe(rule));
        Assert.AreEqual("Every Mon, Fri, Sun at 07:05", RecurrencePolicy.Describe(rule, "en"));
        Assert.AreEqual("매일 23:00", RecurrencePolicy.Describe(RecurrenceRule.CreateDaily(Eleven)));
        Assert.AreEqual("평일(월–금) 23:00", RecurrencePolicy.Describe(RecurrenceRule.CreateWeekdays(Eleven)));
        Assert.AreEqual("반복 안 함", RecurrencePolicy.Describe(null));
    }

    [TestMethod]
    public void SnoozeAddsMinutesToTheLaterOfScheduledTimeAndCurrentMinute()
    {
        var scheduled = new DateTime(2026, 10, 14, 23, 0, 0);

        Assert.AreEqual(new DateTime(2026, 10, 14, 23, 10, 0), ScheduleTimePolicy.Snooze(scheduled, new DateTime(2026, 10, 14, 22, 55, 12), 10));
        Assert.AreEqual(new DateTime(2026, 10, 14, 23, 32, 0), ScheduleTimePolicy.Snooze(scheduled, new DateTime(2026, 10, 14, 23, 1, 30), 30));
        Assert.AreEqual(new DateTime(2026, 10, 15, 0, 1, 0), ScheduleTimePolicy.Snooze(scheduled, new DateTime(2026, 10, 14, 23, 1, 0), 60));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => ScheduleTimePolicy.Snooze(scheduled, scheduled, 15));
    }
}
