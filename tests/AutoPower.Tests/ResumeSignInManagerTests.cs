using AutoPower.Windows;

namespace AutoPower.Tests;

[TestClass]
public sealed class ResumeSignInManagerTests
{
    [TestMethod]
    public void ArmAndRestorePreserveTheExactAcAndDcValues()
    {
        using var fixture = new Fixture(ac: 1, dc: 0);

        var armed = fixture.Manager.Arm(Guid.NewGuid(), DateTime.Now.AddHours(1));

        Assert.AreEqual(ResumeSignInJournalState.Armed, armed.State);
        Assert.AreEqual(0u, fixture.Settings.CurrentAc);
        Assert.AreEqual(0u, fixture.Settings.CurrentDc);

        var restored = fixture.Manager.Restore(armed.ScheduleId);

        Assert.AreEqual(ResumeSignInJournalState.Restored, restored?.State);
        Assert.AreEqual(1u, fixture.Settings.CurrentAc);
        Assert.AreEqual(0u, fixture.Settings.CurrentDc);
    }

    [TestMethod]
    public void ArmFailureRollsBackAndDeletesTheJournal()
    {
        using var fixture = new Fixture(ac: 1, dc: 1);
        fixture.Settings.FailDisable = true;

        Assert.ThrowsExactly<InvalidOperationException>(() =>
            fixture.Manager.Arm(Guid.NewGuid(), DateTime.Now.AddHours(1)));

        Assert.AreEqual(1u, fixture.Settings.CurrentAc);
        Assert.AreEqual(1u, fixture.Settings.CurrentDc);
        Assert.IsNull(fixture.Journal.Read());
    }

    [TestMethod]
    public void RestoreRetriesOnceAndThenSucceeds()
    {
        using var fixture = new Fixture(ac: 1, dc: 1);
        var armed = fixture.Manager.Arm(Guid.NewGuid(), DateTime.Now.AddHours(1));
        fixture.Settings.RestoreFailuresRemaining = 1;

        var restored = fixture.Manager.Restore(armed.ScheduleId);

        Assert.AreEqual(ResumeSignInJournalState.Restored, restored?.State);
        Assert.AreEqual(1u, fixture.Settings.CurrentAc);
        Assert.AreEqual(1u, fixture.Settings.CurrentDc);
    }

    [TestMethod]
    public void RepeatedRestoreFailureLeavesARecoveryJournal()
    {
        using var fixture = new Fixture(ac: 1, dc: 1);
        var armed = fixture.Manager.Arm(Guid.NewGuid(), DateTime.Now.AddHours(1));
        fixture.Settings.RestoreFailuresRemaining = 2;

        Assert.ThrowsExactly<InvalidOperationException>(() => fixture.Manager.Restore(armed.ScheduleId));

        Assert.AreEqual(ResumeSignInJournalState.RestoreFailed, fixture.Journal.Read()?.State);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _logDirectory;

        public Fixture(uint ac, uint dc)
        {
            Settings = new FakeSettings(ac, dc);
            Journal = new MemoryJournal();
            _logDirectory = Path.Combine(Path.GetTempPath(), "AutoPowerTests", Guid.NewGuid().ToString("N"));
            Manager = new ResumeSignInManager(Settings, Journal, new TechnicalLogger(_logDirectory));
        }

        public FakeSettings Settings { get; }
        public MemoryJournal Journal { get; }
        public ResumeSignInManager Manager { get; }

        public void Dispose()
        {
            if (Directory.Exists(_logDirectory))
            {
                Directory.Delete(_logDirectory, true);
            }
        }
    }

    private sealed class MemoryJournal : IResumeSignInJournalStore
    {
        private ResumeSignInJournal? _value;
        public ResumeSignInJournal? Read() => _value;
        public void Write(ResumeSignInJournal journal) => _value = journal;
        public void Delete() => _value = null;
    }

    private sealed class FakeSettings(uint ac, uint dc) : IResumeSignInSettings
    {
        private readonly ResumeSignInSnapshot _snapshot = new(Guid.NewGuid(), ac, dc);
        public uint CurrentAc { get; private set; } = ac;
        public uint CurrentDc { get; private set; } = dc;
        public bool FailDisable { get; set; }
        public int RestoreFailuresRemaining { get; set; }

        public ResumeSignInSnapshot Capture() => _snapshot;

        public void SetRequireSignIn(ResumeSignInSnapshot snapshot, bool required)
        {
            CurrentAc = required ? 1u : 0u;
            CurrentDc = required ? 1u : 0u;
            if (FailDisable)
            {
                throw new InvalidOperationException("injected disable failure");
            }
        }

        public bool IsRequireSignInDisabled(ResumeSignInSnapshot snapshot) =>
            CurrentAc == 0 && CurrentDc == 0;

        public void Restore(ResumeSignInSnapshot snapshot)
        {
            if (RestoreFailuresRemaining > 0)
            {
                RestoreFailuresRemaining--;
                throw new InvalidOperationException("injected restore failure");
            }

            CurrentAc = snapshot.AcValue;
            CurrentDc = snapshot.DcValue;
        }

        public bool IsRestored(ResumeSignInSnapshot snapshot) =>
            CurrentAc == snapshot.AcValue && CurrentDc == snapshot.DcValue;
    }
}
