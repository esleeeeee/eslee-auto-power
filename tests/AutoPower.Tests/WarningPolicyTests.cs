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
    }
}
