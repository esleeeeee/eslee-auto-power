using AutoPower.Core;

namespace AutoPower.Tests;

[TestClass]
public sealed class WarningPolicyTests
{
    [TestMethod]
    public void WarningIsExactlyFiveMinutesEarly() => Assert.AreEqual(TimeSpan.FromMinutes(5), WarningPolicy.AdvanceNotice);

    [TestMethod]
    public void TimeoutAndWindowCloseKeepOriginalSchedule()
    {
        Assert.AreEqual(WarningChoice.KeepOriginal, WarningPolicy.ChoiceOnTimeout);
        Assert.AreEqual(WarningChoice.KeepOriginal, WarningPolicy.ChoiceOnWindowClose);
    }

    [TestMethod]
    public void ThirtySecondCountdownDoesNotMovePowerTime()
    {
        var scheduled = new DateTime(2030, 2, 3, 23, 0, 0);
        var clicked = scheduled.AddMinutes(-5).AddSeconds(30);
        Assert.AreEqual(scheduled, WarningPolicy.ResolveVirtualDesktopReady(WarningChoice.KeepOriginal, clicked, scheduled));
        Assert.AreEqual(TimeSpan.FromSeconds(30), WarningPolicy.DialogCountdown);
        Assert.AreEqual(TimeSpan.FromSeconds(30), WarningPolicy.ShutdownGracePeriod);
    }

    [TestMethod]
    public void ForcedShutdownFallbackOnlyAcceptsARecentlyCompletedShutdown()
    {
        var now = new DateTime(2030, 2, 3, 23, 0, 30);
        var schedule = PowerSchedule.Create(new DateTime(2030, 2, 3, 23, 0, 0), PowerActionType.Shutdown) with
        {
            IsEnabled = false,
            Status = ScheduleStatus.PendingPowerTransition
        };

        Assert.IsTrue(WarningPolicy.CanRunShutdownFallback(schedule, now));
        Assert.IsFalse(WarningPolicy.CanRunShutdownFallback(schedule with { Status = ScheduleStatus.Pending }, now));
        Assert.IsFalse(WarningPolicy.CanRunShutdownFallback(schedule with { ActionType = PowerActionType.Sleep }, now));
        Assert.IsFalse(WarningPolicy.CanRunShutdownFallback(schedule, schedule.ScheduledLocalDateTime.AddMinutes(3)));
        Assert.IsFalse(WarningPolicy.CanRunShutdownFallback(schedule, schedule.ScheduledLocalDateTime.AddMinutes(-7)));
    }
}
