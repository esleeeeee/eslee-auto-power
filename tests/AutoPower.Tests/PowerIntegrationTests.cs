using AutoPower.Core;
using AutoPower.Windows;

namespace AutoPower.Tests;

[TestClass]
public sealed class PowerIntegrationTests
{
    [TestMethod]
    public void NativePowerCapabilityLayoutMatchesCurrentWindowsSdk()
    {
        PowerCapabilityDetector.ValidateNativePowerCapabilitiesLayout();

        Assert.AreEqual(84, PowerCapabilityDetector.NativePowerCapabilitiesSize);
        Assert.AreEqual(64, PowerCapabilityDetector.NativePowerCapabilitiesOffset("AcOnLineWake"));
        Assert.AreEqual(72, PowerCapabilityDetector.NativePowerCapabilitiesOffset("RtcWake"));
    }

    [TestMethod]
    public void GetPwrCapabilitiesReturnsAValidSnapshotOnWindows()
    {
        var snapshot = PowerCapabilityDetector.Detect();

        Assert.IsFalse(string.IsNullOrWhiteSpace(snapshot.WindowsVersion));
        CollectionAssert.AreEquivalent(
            new[]
            {
                AutoPower.Core.CompatibilityCapabilities.S3Wake,
                AutoPower.Core.CompatibilityCapabilities.S4Wake
            },
            snapshot.Results.Select(result => result.Capability).ToArray());
    }

    [TestMethod]
    public void CurrentResumeSignInPowerSettingCanBeReadWithoutMutation()
    {
        var snapshot = new WindowsResumeSignInSettings().Capture();

        Assert.AreNotEqual(Guid.Empty, snapshot.SchemeGuid);
        Assert.IsTrue(snapshot.AcValue is 0 or 1);
        Assert.IsTrue(snapshot.DcValue is 0 or 1);
    }

    [TestMethod]
    public void PhysicalSleepWakeTestUsesTwoMinuteDelay()
    {
        var now = new DateTime(2026, 7, 17, 12, 34, 56, DateTimeKind.Local);

        var wake = SleepWakeTestPolicy.CreateWakeTime(now);

        Assert.AreEqual(TimeSpan.FromMinutes(2), wake - DateTime.SpecifyKind(now, DateTimeKind.Unspecified));
    }

    [TestMethod]
    [DataRow(SleepWakeTestTarget.S3, PowerActionType.WakeFromSleep)]
    [DataRow(SleepWakeTestTarget.S4, PowerActionType.WakeFromHibernate)]
    public void PhysicalSleepWakeTestAlwaysArmsOneTimeAutoLogon(
        SleepWakeTestTarget target,
        PowerActionType expectedAction)
    {
        var wake = DateTime.Now.AddMinutes(2);

        var schedule = SleepWakeTestPolicy.CreateSchedule(target, wake);

        Assert.AreEqual(expectedAction, schedule.ActionType);
        Assert.IsTrue(schedule.OneTimeAutoLogonEnabled);
    }

    [TestMethod]
    public void ResumeSignInRestorationWaitsPastTheWakeRaceWindow()
    {
        Assert.IsGreaterThanOrEqualTo(TimeSpan.FromSeconds(15), ResumeSignInPolicy.RestoreDelayAfterResume);
    }

    [TestMethod]
    public async Task ShutdownUsesGracefulControllerWhileLegacyPowerOnRemainsBlocked()
    {
        var shutdown = new FakeShutdownController();
        var executor = new PowerActionExecutor(shutdown);

        Assert.ThrowsExactly<InvalidOperationException>(() => executor.ExecuteAsync(PowerActionType.PowerOn));
        await executor.ExecuteAsync(PowerActionType.Shutdown);
        Assert.AreEqual(1, shutdown.GracefulCount);
        Assert.AreEqual(0, shutdown.ForceCount);
    }

    [TestMethod]
    public void ShutdownCommandsSeparateGracefulAndForcedFallback()
    {
        CollectionAssert.Contains(WindowsShutdownController.CreateArguments(force: false).ToArray(), "/soft");
        CollectionAssert.DoesNotContain(WindowsShutdownController.CreateArguments(force: false).ToArray(), "/f");
        CollectionAssert.Contains(WindowsShutdownController.CreateArguments(force: true).ToArray(), "/f");
        CollectionAssert.DoesNotContain(WindowsShutdownController.CreateArguments(force: true).ToArray(), "/soft");
        Assert.AreEqual(TimeSpan.FromSeconds(30), WarningPolicy.ShutdownGracePeriod);
    }

    [TestMethod]
    public void S3WakeTestTransitionsFromPreparedToConfirmedCandidate()
    {
        var directory = Path.Combine(Path.GetTempPath(), "AutoPowerTests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "sleep-wake-state.json");
        try
        {
            var wake = DateTime.SpecifyKind(DateTime.Now.AddMinutes(4), DateTimeKind.Unspecified);
            var store = new SleepWakeTestStateStore(path);
            store.Prepare(Guid.NewGuid(), SleepWakeTestTarget.S3, wake);
            store.MarkTransitionRequested();

            var candidate = store.EvaluateResume(ToLocalOffset(wake.AddSeconds(20)));
            var confirmed = store.ConfirmAutomaticResume();

            Assert.AreEqual(SleepWakeTestStage.CandidateResumeObserved, candidate?.Stage);
            Assert.AreEqual(SleepWakeTestStage.Confirmed, confirmed.Stage);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, true);
            }
        }
    }

    [TestMethod]
    public void SleepWakeTestRejectsResumeBeforeScheduledWindow()
    {
        var directory = Path.Combine(Path.GetTempPath(), "AutoPowerTests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "sleep-wake-state.json");
        try
        {
            var wake = DateTime.SpecifyKind(DateTime.Now.AddMinutes(4), DateTimeKind.Unspecified);
            var store = new SleepWakeTestStateStore(path);
            store.Prepare(Guid.NewGuid(), SleepWakeTestTarget.S3, wake);
            store.MarkTransitionRequested();

            var result = store.EvaluateResume(ToLocalOffset(wake.AddMinutes(-2)));

            Assert.AreEqual(SleepWakeTestStage.Failed, result?.Stage);
            StringAssert.Contains(result?.Detail, "예약 시각 전에");
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, true);
            }
        }
    }

    [TestMethod]
    public void SleepWakeTestRejectsResumeAfterFiveMinuteWindow()
    {
        var directory = Path.Combine(Path.GetTempPath(), "AutoPowerTests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "sleep-wake-state.json");
        try
        {
            var wake = DateTime.SpecifyKind(DateTime.Now.AddMinutes(4), DateTimeKind.Unspecified);
            var store = new SleepWakeTestStateStore(path);
            store.Prepare(Guid.NewGuid(), SleepWakeTestTarget.S4, wake);
            store.MarkTransitionRequested();

            var result = store.EvaluateResume(ToLocalOffset(wake.AddMinutes(6)));

            Assert.AreEqual(SleepWakeTestStage.Failed, result?.Stage);
            StringAssert.Contains(result?.Detail, "5분 이내");
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, true);
            }
        }
    }

    [TestMethod]
    public void ActiveSleepWakeTestCannotBeOverwritten()
    {
        var directory = Path.Combine(Path.GetTempPath(), "AutoPowerTests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "sleep-wake-state.json");
        try
        {
            var store = new SleepWakeTestStateStore(path);
            store.Prepare(Guid.NewGuid(), SleepWakeTestTarget.S3, DateTime.Now.AddMinutes(4));

            Assert.ThrowsExactly<InvalidOperationException>(() =>
                store.Prepare(Guid.NewGuid(), SleepWakeTestTarget.S4, DateTime.Now.AddMinutes(5)));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, true);
            }
        }
    }

    private static DateTimeOffset ToLocalOffset(DateTime local) =>
        new(local, TimeZoneInfo.Local.GetUtcOffset(local));

    private sealed class FakeShutdownController : IShutdownController
    {
        public int GracefulCount { get; private set; }
        public int ForceCount { get; private set; }

        public Task StartGracefulShutdownAsync(CancellationToken cancellationToken = default)
        {
            GracefulCount++;
            return Task.CompletedTask;
        }

        public Task ForceShutdownAsync(CancellationToken cancellationToken = default)
        {
            ForceCount++;
            return Task.CompletedTask;
        }
    }
}
