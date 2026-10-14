using AutoPower.Core;

namespace AutoPower.Tests;

[TestClass]
public sealed class ScheduleModelTests
{
    private static readonly DateTime Now = new(2030, 1, 1, 12, 0, 0);

    [TestMethod]
    public void NameIsTrimmedAndBlankBecomesNull()
    {
        Assert.AreEqual("야간 종료", PowerSchedule.Create(Now.AddHours(1), PowerActionType.Shutdown, name: "  야간 종료 ").Name);
        Assert.IsNull(PowerSchedule.Create(Now.AddHours(1), PowerActionType.Shutdown, name: "   ").Name);
        Assert.IsNull((PowerSchedule.Create(Now.AddHours(1), PowerActionType.Shutdown) with { Name = "" }).Name);
    }

    [TestMethod]
    public void NameLongerThanFortyCharactersIsRejected()
    {
        var exact = PowerSchedule.Create(Now.AddHours(1), PowerActionType.Sleep, name: new string('가', 40));
        var tooLong = exact with { Name = new string('가', 41) };

        Assert.IsTrue(ScheduleValidator.Validate(exact, Now, []).IsValid);
        Assert.IsTrue(ScheduleValidator.Validate(tooLong, Now, []).Issues.Any(issue => issue.Code == "name-too-long"));
    }

    [TestMethod]
    public void RecurringCreateGetsSeriesAndNoneRuleIsNormalizedAway()
    {
        var recurring = PowerSchedule.Create(Now.AddHours(1), PowerActionType.Shutdown,
            recurrence: RecurrenceRule.CreateDaily(Now.AddHours(1).TimeOfDay));
        var oneTime = PowerSchedule.Create(Now.AddHours(1), PowerActionType.Shutdown,
            recurrence: new RecurrenceRule(RecurrenceKind.None, RecurrenceDays.All, TimeSpan.Zero));

        Assert.IsTrue(recurring.IsRecurring);
        Assert.IsNotNull(recurring.SeriesId);
        Assert.IsFalse(oneTime.IsRecurring);
        Assert.IsNull(oneTime.Recurrence);
        Assert.IsNull(oneTime.SeriesId);
    }

    [TestMethod]
    public void WeeklyRecurrenceNeedsAtLeastOneDay()
    {
        var schedule = PowerSchedule.Create(Now.AddHours(1), PowerActionType.Hibernate,
            recurrence: RecurrenceRule.CreateWeekly(RecurrenceDays.None, new TimeSpan(13, 0, 0)));

        Assert.IsTrue(ScheduleValidator.Validate(schedule, Now, []).Issues.Any(issue => issue.Code == "recurrence-days"));
    }

    [TestMethod]
    public void RecurrenceIsAllowedForPowerTransitionsAndWakeButNotRemovedAction()
    {
        var rule = RecurrenceRule.CreateWeekdays(new TimeSpan(13, 0, 0));
        foreach (var action in new[]
                 {
                     PowerActionType.Shutdown, PowerActionType.Hibernate, PowerActionType.Sleep,
                     PowerActionType.WakeFromSleep, PowerActionType.WakeFromHibernate
                 })
        {
            Assert.IsTrue(ScheduleValidator.Validate(PowerSchedule.Create(Now.AddHours(1), action, recurrence: rule), Now, []).IsValid, action.ToString());
        }

        var removed = PowerSchedule.Create(Now.AddHours(1), PowerActionType.PowerOn, recurrence: rule) with { IsEnabled = false };
        Assert.IsTrue(ScheduleValidator.Validate(removed, Now, []).Issues.Any(issue => issue.Code == "recurrence-action"));
    }

    [TestMethod]
    public void InvalidRecurrenceTimeIsRejected()
    {
        var schedule = PowerSchedule.Create(Now.AddHours(1), PowerActionType.Sleep,
            recurrence: RecurrenceRule.CreateDaily(TimeSpan.FromHours(25)));

        Assert.IsTrue(ScheduleValidator.Validate(schedule, Now, []).Issues.Any(issue => issue.Code == "recurrence-invalid"));
    }

    [TestMethod]
    public void DuplicateAsNewCreatesAFreshEnabledCopy()
    {
        var originalId = Guid.NewGuid();
        var program = new FollowUpProgram(Guid.NewGuid(), originalId, @"C:\Apps\OBS.exe", 2, "--x", null, true, 0);
        var original = new PowerSchedule(originalId, Now.AddHours(1), PowerActionType.WakeFromSleep, false, true,
            DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(-1), ScheduleStatus.Disabled, [program])
        {
            Name = "아침 녹화",
            Recurrence = RecurrenceRule.CreateWeekdays(Now.AddHours(1).TimeOfDay),
            SeriesId = Guid.NewGuid()
        };
        var target = Now.AddDays(1).AddHours(3);

        var copy = original.DuplicateAsNew(target);

        Assert.AreNotEqual(original.Id, copy.Id);
        Assert.AreEqual(target, copy.ScheduledLocalDateTime);
        Assert.IsTrue(copy.IsEnabled);
        Assert.AreEqual(ScheduleStatus.Pending, copy.Status);
        Assert.AreEqual("아침 녹화", copy.Name);
        Assert.IsTrue(copy.OneTimeAutoLogonEnabled);
        Assert.AreEqual(RecurrenceKind.Weekdays, copy.Recurrence?.Kind);
        Assert.AreEqual(target.TimeOfDay, copy.Recurrence?.TimeOfDay);
        Assert.IsNotNull(copy.SeriesId);
        Assert.AreNotEqual(original.SeriesId, copy.SeriesId);
        Assert.HasCount(1, copy.FollowUpPrograms);
        Assert.AreNotEqual(program.Id, copy.FollowUpPrograms[0].Id);
        Assert.AreEqual(copy.Id, copy.FollowUpPrograms[0].ScheduleId);
        Assert.AreEqual(program.ExecutablePath, copy.FollowUpPrograms[0].ExecutablePath);
        Assert.IsTrue(copy.FollowUpPrograms[0].RunElevated);
    }

    [TestMethod]
    public void DuplicateOfOneTimeScheduleStaysOneTime()
    {
        var copy = PowerSchedule.Create(Now.AddHours(1), PowerActionType.Shutdown, name: "종료").DuplicateAsNew(Now.AddHours(2));

        Assert.IsNull(copy.Recurrence);
        Assert.IsNull(copy.SeriesId);
        Assert.AreEqual("종료", copy.Name);
    }

    [TestMethod]
    public void AttentionSummaryCountsOnlyUnacknowledgedProblemRuns()
    {
        var acknowledged = new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var history = new[]
        {
            History("PowerTransitionFailed", ResultKind.Failure, acknowledged.AddMinutes(-1)),
            History("PowerTransitionFailed", ResultKind.Failure, acknowledged.AddMinutes(1)),
            History("FollowUpProgram", ResultKind.Failure, acknowledged.AddMinutes(2)),
            History("FollowUpProgram", ResultKind.Success, acknowledged.AddMinutes(3)),
            History("ScheduleMissed", ResultKind.Warning, acknowledged.AddMinutes(4)),
            History("PowerTransitionResultUnknown", ResultKind.Warning, acknowledged.AddMinutes(5)),
            History("RecurrenceAdvanceFailed", ResultKind.Warning, acknowledged.AddMinutes(7)),
            History("ShutdownPrimaryRejected", ResultKind.Warning, acknowledged.AddMinutes(9)),
            History("ScheduleCreated", ResultKind.Success, acknowledged.AddMinutes(10))
        };

        var summary = AttentionSummaryPolicy.Summarize(history, acknowledged);

        Assert.AreEqual(2, summary.FailureCount);
        Assert.AreEqual(1, summary.MissedCount);
        Assert.AreEqual(1, summary.ResultUnknownCount);
        Assert.AreEqual(1, summary.RecurrenceAdvanceFailureCount);
        Assert.AreEqual(5, summary.Total);
        Assert.IsTrue(summary.HasAttention);
        Assert.AreEqual(acknowledged.AddMinutes(7), summary.LatestUtc);
        Assert.AreEqual(6, AttentionSummaryPolicy.Summarize(history, null).Total);
    }

    [TestMethod]
    public void AttentionSummaryIsEmptyWhenEverythingWasAcknowledged()
    {
        var at = new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);

        var summary = AttentionSummaryPolicy.Summarize([History("ScheduleMissed", ResultKind.Warning, at)], at);

        Assert.IsFalse(summary.HasAttention);
        Assert.IsNull(summary.LatestUtc);
        Assert.AreEqual(AttentionSummary.None, summary);
    }

    private static ExecutionHistory History(string eventType, ResultKind result, DateTimeOffset at) =>
        new(0, Guid.NewGuid(), eventType, result, null, at, "test", null);
}
