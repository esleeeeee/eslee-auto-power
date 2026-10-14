using AutoPower.App;
using AutoPower.Core;

namespace AutoPower.Tests;

[TestClass]
[DoNotParallelize]
public sealed class UiTextTests
{
    private AppLanguagePreference _previousLanguage;

    [TestInitialize]
    public void UseKorean()
    {
        _previousLanguage = AppText.Preference;
        AppText.SetLanguage(AppLanguagePreference.Korean);
    }

    [TestCleanup]
    public void RestoreLanguage() => AppText.SetLanguage(_previousLanguage);

    [TestMethod]
    [DataRow(0, 30, "30분 남음", "30 min left")]
    [DataRow(2, 0, "2시간 남음", "2 h left")]
    [DataRow(12, 19, "12시간 19분 남음", "12 h 19 min left")]
    [DataRow(49, 0, "2일 1시간 남음", "2 d 1 h left")]
    public void RemainingRoundsUpToTheMinuteInBothLanguages(int hours, int minutes, string korean, string english)
    {
        var now = new DateTime(2030, 5, 1, 10, 0, 20);
        var target = now.AddHours(hours).AddMinutes(minutes).AddSeconds(-20);

        Assert.AreEqual(korean, UiText.Remaining(target, now));
        AppText.SetLanguage(AppLanguagePreference.English);
        Assert.AreEqual(english, UiText.Remaining(target, now));
    }

    [TestMethod]
    public void RemainingForPastTimeSaysStartingSoon()
    {
        var now = new DateTime(2030, 5, 1, 10, 0, 0);
        Assert.AreEqual("곧 실행", UiText.Remaining(now.AddSeconds(-1), now));
    }

    [TestMethod]
    public void WhenUsesTodayAndTomorrowLabels()
    {
        var now = new DateTime(2030, 5, 1, 22, 0, 0);
        Assert.AreEqual("오늘 23:30", UiText.When(new DateTime(2030, 5, 1, 23, 30, 0), now));
        Assert.AreEqual("내일 07:00", UiText.When(new DateTime(2030, 5, 2, 7, 0, 0), now));
        AppText.SetLanguage(AppLanguagePreference.English);
        Assert.AreEqual("Tomorrow 07:00", UiText.When(new DateTime(2030, 5, 2, 7, 0, 0), now));
    }

    [TestMethod]
    public void PausedRecurringOccurrenceInThePastShowsItsNextSlot()
    {
        var past = DateTime.Now.AddDays(-2);
        var rule = RecurrenceRule.CreateDaily(past.TimeOfDay);
        var paused = PowerSchedule.Create(past, PowerActionType.Shutdown, recurrence: rule) with
        {
            IsEnabled = false,
            Status = ScheduleStatus.Disabled
        };

        var row = new ScheduleRow(paused);

        Assert.IsGreaterThan(DateTime.Now, row.DisplayTime);
        Assert.AreEqual(rule.TimeOfDay.Hours, row.DisplayTime.Hour);
        Assert.AreEqual(rule.TimeOfDay.Minutes, row.DisplayTime.Minute);
        Assert.IsTrue(row.IsRecurring);
    }

    [TestMethod]
    public void ScheduleRowReadsNameRepeatAndSeriesForScreenReaders()
    {
        var schedule = PowerSchedule.Create(
            DateTime.Now.AddDays(1),
            PowerActionType.Hibernate,
            name: "퇴근 후",
            recurrence: RecurrenceRule.CreateWeekdays(new TimeSpan(23, 0, 0)));

        var row = new ScheduleRow(schedule);

        Assert.AreEqual("퇴근 후", row.TitleText);
        Assert.AreEqual(schedule.SeriesId, row.SeriesKey);
        StringAssert.Contains(row.ToString(), "퇴근 후");
        StringAssert.Contains(row.ToString(), "평일");
    }

    [TestMethod]
    public void SubstringTranslationPrefersTheLongestKey()
    {
        AppText.SetLanguage(AppLanguagePreference.English);

        // "모두 다시 켜기" (8 chars) is part of the longer tray entry; the longer pair must win.
        Assert.AreEqual("Turn all paused schedules back on", AppText.T("꺼 둔 예약 모두 다시 켜기"));
        Assert.AreEqual("Future schedules: 3", AppText.F("미래 예약 {0}개", 3));
    }

    [TestMethod]
    public void AttentionTextSummarizesProblemsSinceAcknowledgement()
    {
        var now = DateTimeOffset.UtcNow;
        var history = new[]
        {
            new ExecutionHistory(1, null, HistoryEventTypes.PowerTransitionFailed, ResultKind.Failure, null, now.AddHours(-1), "실패", null),
            new ExecutionHistory(2, null, HistoryEventTypes.ScheduleMissed, ResultKind.Warning, null, now.AddHours(-2), "놓침", null),
            new ExecutionHistory(3, null, HistoryEventTypes.ScheduleMissed, ResultKind.Warning, null, now.AddDays(-3), "오래된 기록", null)
        };

        var (text, latest) = ScheduleExtras.Attention(history, now.AddDays(-1));

        Assert.IsNotNull(text);
        StringAssert.Contains(text, "실패 1건");
        StringAssert.Contains(text, "놓친 예약 1건");
        Assert.AreEqual(now.AddHours(-1), latest);
        Assert.IsNull(ScheduleExtras.Attention(history, now).Text);
    }
}
