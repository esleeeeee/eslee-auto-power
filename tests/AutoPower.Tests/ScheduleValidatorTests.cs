using AutoPower.Core;

namespace AutoPower.Tests;

[TestClass]
public sealed class ScheduleValidatorTests
{
    [TestMethod]
    public void ExistingDatabaseActionValuesRemainStable()
    {
        var values = Enum.GetValues<PowerActionType>().ToDictionary(
            value => value.ToString(),
            value => Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture));
        CollectionAssert.AreEquivalent(
            new Dictionary<string, int>
            {
                [nameof(PowerActionType.PowerOn)] = 0,
                [nameof(PowerActionType.Shutdown)] = 1,
                [nameof(PowerActionType.Hibernate)] = 2,
                [nameof(PowerActionType.Sleep)] = 3,
                [nameof(PowerActionType.WakeFromSleep)] = 4,
                [nameof(PowerActionType.WakeFromHibernate)] = 5
            },
            values);
    }

    [TestMethod]
    public void PastScheduleIsRejected()
    {
        var now = new DateTime(2030, 1, 1, 12, 0, 0);
        var result = ScheduleValidator.Validate(Create(now.AddSeconds(-1), PowerActionType.Sleep), now, []);
        Assert.IsFalse(result.IsValid);
        Assert.IsTrue(result.Issues.Any(issue => issue.Code == "past"));
    }

    [TestMethod]
    public void ThereIsNoGeneralTwoMinuteSpacingRule()
    {
        var now = new DateTime(2030, 1, 1, 12, 0, 0);
        var first = Create(now.AddMinutes(10), PowerActionType.Sleep);
        var second = Create(now.AddMinutes(10).AddSeconds(30), PowerActionType.Hibernate);
        Assert.IsTrue(ScheduleValidator.Validate(second, now, [first]).IsValid);
    }

    [TestMethod]
    public void ConflictingActionsAtSameTimeAreRejected()
    {
        var now = new DateTime(2030, 1, 1, 12, 0, 0);
        var result = ScheduleValidator.Validate(
            Create(now.AddMinutes(10), PowerActionType.Sleep), now,
            [Create(now.AddMinutes(10), PowerActionType.WakeFromSleep)]);
        Assert.IsTrue(result.Issues.Any(issue => issue.Code == "conflicting-action"));
    }

    [TestMethod]
    public void ZeroDelayAndSameDelayProgramsAreAccepted()
    {
        var now = new DateTime(2030, 1, 1, 12, 0, 0);
        var schedule = Create(now.AddMinutes(10), PowerActionType.WakeFromSleep);
        schedule = schedule with
        {
            FollowUpPrograms =
            [
                new(Guid.NewGuid(), schedule.Id, @"C:\Apps\a.exe", 0, null, null, false, 0),
                new(Guid.NewGuid(), schedule.Id, @"C:\Apps\b.exe", 0, null, null, false, 1)
            ]
        };
        Assert.IsTrue(ScheduleValidator.Validate(schedule, now, []).IsValid);
    }

    [TestMethod]
    public void NextWakeSelectsNearestEnabledFutureS3OrS4Schedule()
    {
        var now = new DateTime(2030, 1, 1, 12, 0, 0);
        var later = Create(now.AddHours(2), PowerActionType.WakeFromHibernate);
        var nearest = Create(now.AddHours(1), PowerActionType.WakeFromSleep);
        var nonWake = Create(now.AddMinutes(5), PowerActionType.Sleep);
        var disabled = Create(now.AddMinutes(30), PowerActionType.WakeFromSleep) with { IsEnabled = false, Status = ScheduleStatus.Disabled };
        Assert.AreEqual(nearest.Id, ScheduleValidator.SelectNextWake([later, nearest, nonWake, disabled], now)?.Id);
    }

    [TestMethod]
    public void ImminentWarningIsOnlyForChangesWithinTwoMinutes()
    {
        var now = new DateTime(2030, 1, 1, 12, 0, 0);
        Assert.IsTrue(ScheduleValidator.NeedsImminentWarning(Create(now.AddSeconds(119), PowerActionType.Sleep), now));
        Assert.IsFalse(ScheduleValidator.NeedsImminentWarning(Create(now.AddMinutes(2), PowerActionType.Sleep), now));
    }

    [TestMethod]
    public void OnlyLegacyPowerOnIsRejectedAndShutdownRemainsSupported()
    {
        var now = new DateTime(2030, 1, 1, 12, 0, 0);

        var powerOn = ScheduleValidator.Validate(Create(now.AddHours(1), PowerActionType.PowerOn), now, []);
        var shutdown = ScheduleValidator.Validate(Create(now.AddHours(1), PowerActionType.Shutdown), now, []);

        Assert.IsTrue(powerOn.Issues.Any(item => item.Code == "removed-power-action"));
        Assert.IsTrue(shutdown.IsValid);
    }

    [TestMethod]
    public void NewScheduleDefaultIsValidWithoutForcingTomorrow()
    {
        var opened = new DateTime(2026, 7, 22, 23, 47, 59, 999, DateTimeKind.Local);

        var result = ScheduleTimePolicy.NewScheduleDefault(opened);

        Assert.AreEqual(new DateTime(2026, 7, 22, 23, 52, 0, DateTimeKind.Unspecified), result);
    }

    [TestMethod]
    public void NewScheduleDefaultCrossesMidnightNaturally()
    {
        var opened = new DateTime(2026, 12, 31, 23, 59, 59);
        var result = ScheduleTimePolicy.NewScheduleDefault(opened);
        Assert.AreEqual(new DateTime(2027, 1, 1, 0, 4, 0), result);
        Assert.IsTrue(ScheduleValidator.Validate(Create(result, PowerActionType.Shutdown), opened, []).IsValid);
    }

    [TestMethod]
    [DataRow(1, 2026, 7, 23, 0, 20)]
    [DataRow(2, 2026, 7, 23, 1, 20)]
    public void QuickPowerTransitionUsesClickTimeAndCrossesMidnight(
        int hours, int year, int month, int day, int hour, int minute)
    {
        var clicked = new DateTime(2026, 7, 22, 23, 20, 49, DateTimeKind.Local);

        var result = ScheduleTimePolicy.QuickPowerTransition(clicked, hours);

        Assert.AreEqual(new DateTime(year, month, day, hour, minute, 0, DateTimeKind.Unspecified), result);
    }

    [TestMethod]
    public void QuickPowerTransitionHandlesMonthYearAndLeapYearBoundaries()
    {
        Assert.AreEqual(
            new DateTime(2027, 1, 1, 0, 30, 0, DateTimeKind.Unspecified),
            ScheduleTimePolicy.QuickPowerTransition(new DateTime(2026, 12, 31, 23, 30, 45), 1));
        Assert.AreEqual(
            new DateTime(2026, 3, 1, 0, 30, 0, DateTimeKind.Unspecified),
            ScheduleTimePolicy.QuickPowerTransition(new DateTime(2026, 2, 28, 22, 30, 45), 2));
        Assert.AreEqual(
            new DateTime(2028, 2, 29, 0, 30, 0, DateTimeKind.Unspecified),
            ScheduleTimePolicy.QuickPowerTransition(new DateTime(2028, 2, 28, 23, 30, 59, 999), 1));
    }

    [TestMethod]
    [DataRow(PowerActionType.Shutdown, 1)]
    [DataRow(PowerActionType.Shutdown, 2)]
    [DataRow(PowerActionType.Hibernate, 1)]
    [DataRow(PowerActionType.Hibernate, 2)]
    [DataRow(PowerActionType.Sleep, 1)]
    [DataRow(PowerActionType.Sleep, 2)]
    public void QuickPowerTransitionKeepsSelectedActionType(PowerActionType action, int hours)
    {
        var clicked = new DateTime(2026, 7, 23, 12, 15, 58, 987, DateTimeKind.Local);

        var schedule = PowerSchedule.Create(
            ScheduleTimePolicy.QuickPowerTransition(clicked, hours),
            action);

        Assert.AreEqual(action, schedule.ActionType);
        Assert.AreEqual(new DateTime(2026, 7, 23, 12 + hours, 15, 0, DateTimeKind.Unspecified),
            schedule.ScheduledLocalDateTime);
        Assert.IsTrue(PowerSchedulePolicy.IsPowerTransition(schedule.ActionType));
    }

    [TestMethod]
    public void QuickPowerTransitionIsHiddenForAutomaticStartActions()
    {
        Assert.IsFalse(PowerSchedulePolicy.IsPowerTransition(PowerActionType.WakeFromSleep));
        Assert.IsFalse(PowerSchedulePolicy.IsPowerTransition(PowerActionType.WakeFromHibernate));
        Assert.IsTrue(PowerSchedulePolicy.IsPowerTransition(PowerActionType.Shutdown));
        Assert.IsTrue(PowerSchedulePolicy.IsPowerTransition(PowerActionType.Hibernate));
        Assert.IsTrue(PowerSchedulePolicy.IsPowerTransition(PowerActionType.Sleep));
    }

    [TestMethod]
    public void QuickPowerTransitionRejectsUnsupportedHourOffset()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            ScheduleTimePolicy.QuickPowerTransition(DateTime.Now, 3));
    }

    [TestMethod]
    public void DisabledRemovedScheduleCanRemainForStoredHistory()
    {
        var now = new DateTime(2030, 1, 1, 12, 0, 0);

        var legacy = Create(now.AddHours(1), PowerActionType.PowerOn) with
        {
            IsEnabled = false,
            Status = ScheduleStatus.Disabled
        };
        var result = ScheduleValidator.Validate(legacy, now, []);

        Assert.IsTrue(result.IsValid);
    }

    [TestMethod]
    public void LongRangeS3AndS4WakeSchedulesAreAccepted()
    {
        var now = new DateTime(2030, 1, 1, 12, 0, 0);

        Assert.IsTrue(ScheduleValidator.Validate(Create(now.AddDays(60), PowerActionType.Sleep), now, []).IsValid);
        Assert.IsTrue(ScheduleValidator.Validate(Create(now.AddDays(60), PowerActionType.Hibernate), now, []).IsValid);
        Assert.IsTrue(ScheduleValidator.Validate(Create(now.AddDays(60), PowerActionType.WakeFromSleep), now, []).IsValid);
        Assert.IsTrue(ScheduleValidator.Validate(Create(now.AddDays(60), PowerActionType.WakeFromHibernate), now, []).IsValid);
    }

    [TestMethod]
    public void WakeScheduleAcceptsOneTimeResumeAutologon()
    {
        var now = new DateTime(2030, 1, 1, 12, 0, 0);
        var schedule = Create(now.AddHours(1), PowerActionType.WakeFromSleep) with { OneTimeAutoLogonEnabled = true };

        var result = ScheduleValidator.Validate(schedule, now, []);

        Assert.IsTrue(result.IsValid);
    }

    [TestMethod]
    public void ScheduledSleepCannotFeedS4Wake()
    {
        var now = new DateTime(2030, 1, 1, 12, 0, 0);
        var sleep = Create(now.AddHours(1), PowerActionType.Sleep);
        var wake = Create(now.AddHours(2), PowerActionType.WakeFromHibernate);

        var result = ScheduleValidator.Validate(wake, now, [sleep]);

        Assert.IsTrue(result.Issues.Any(issue => issue.Code == "power-state-mismatch"));
    }

    [TestMethod]
    public void ScheduledHibernateCanFeedS4Wake()
    {
        var now = new DateTime(2030, 1, 1, 12, 0, 0);
        var hibernate = Create(now.AddHours(1), PowerActionType.Hibernate);
        var wake = Create(now.AddHours(2), PowerActionType.WakeFromHibernate);

        Assert.IsTrue(ScheduleValidator.Validate(wake, now, [hibernate]).IsValid);
    }

    [TestMethod]
    public void AutomaticWakePrefersConfirmedS3OverUntestedS4()
    {
        var action = PowerSchedulePolicy.ResolveWakeAction(
            WakeModePreference.Automatic,
            s3Available: true,
            s4Available: true,
            hibernateEnabled: true,
            s3TestStatus: CapabilityStatus.Confirmed,
            s4TestStatus: CapabilityStatus.NeedsPhysicalTest);

        Assert.AreEqual(PowerActionType.WakeFromSleep, action);
    }

    [TestMethod]
    public void AutomaticWakePrefersConfirmedS4WhenBothAreConfirmed()
    {
        var action = PowerSchedulePolicy.ResolveWakeAction(
            WakeModePreference.Automatic,
            s3Available: true,
            s4Available: true,
            hibernateEnabled: true,
            s3TestStatus: CapabilityStatus.Confirmed,
            s4TestStatus: CapabilityStatus.Confirmed);

        Assert.AreEqual(PowerActionType.WakeFromHibernate, action);
    }

    [TestMethod]
    public void AutomaticWakeFallsBackToAvailableS4ThenS3()
    {
        Assert.AreEqual(
            PowerActionType.WakeFromHibernate,
            PowerSchedulePolicy.ResolveWakeAction(
                WakeModePreference.Automatic, true, true, true));
        Assert.AreEqual(
            PowerActionType.WakeFromSleep,
            PowerSchedulePolicy.ResolveWakeAction(
                WakeModePreference.Automatic, true, true, false));
    }

    [TestMethod]
    public void AutomaticWakeExcludesFailedPhysicalTestPaths()
    {
        Assert.AreEqual(
            PowerActionType.WakeFromSleep,
            PowerSchedulePolicy.ResolveWakeAction(
                WakeModePreference.Automatic,
                s3Available: true,
                s4Available: true,
                hibernateEnabled: true,
                s3TestStatus: CapabilityStatus.NeedsPhysicalTest,
                s4TestStatus: CapabilityStatus.UnsupportedOrFailed));

        Assert.ThrowsExactly<InvalidOperationException>(() =>
            PowerSchedulePolicy.ResolveWakeAction(
                WakeModePreference.Automatic,
                s3Available: true,
                s4Available: true,
                hibernateEnabled: true,
                s3TestStatus: CapabilityStatus.UnsupportedOrFailed,
                s4TestStatus: CapabilityStatus.UnsupportedOrFailed));
    }

    [TestMethod]
    public void WakeModeResolutionRejectsUnavailableRequestedState()
    {
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            PowerSchedulePolicy.ResolveWakeAction(
                WakeModePreference.HibernateS4, true, true, false));
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            PowerSchedulePolicy.ResolveWakeAction(
                WakeModePreference.Automatic, false, false, false));
    }

    private static PowerSchedule Create(DateTime time, PowerActionType action)
    {
        var now = DateTimeOffset.UtcNow;
        return new PowerSchedule(Guid.NewGuid(), time, action, true, false, now, now, ScheduleStatus.Pending, []);
    }
}
