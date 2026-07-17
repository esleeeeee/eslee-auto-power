using AutoPower.Windows;
using Microsoft.Win32;

namespace AutoPower.Tests;

[TestClass]
public sealed class AutologonManagerTests
{
    [TestMethod]
    public void BareMicrosoftEmailNormalizesForWindowsLogon()
    {
        Assert.AreEqual(@"MicrosoftAccount\person@example.com", WindowsCredentialIdentity.Normalize(" person@example.com "));
        var identity = WindowsCredentialIdentity.Split("person@example.com");
        Assert.AreEqual("MicrosoftAccount", identity.Domain);
        Assert.AreEqual("person@example.com", identity.User);
    }

    [TestMethod]
    public void ExplicitMicrosoftIdentityIsPreserved()
    {
        Assert.AreEqual(@"MicrosoftAccount\person@example.com",
            WindowsCredentialIdentity.Normalize(@"MicrosoftAccount\person@example.com"));
    }

    [TestMethod]
    public void BareLocalUserNormalizesToMachineAccount()
    {
        Assert.AreEqual($@"{Environment.MachineName}\localuser", WindowsCredentialIdentity.Normalize("localuser"));
    }

    [TestMethod]
    public void MalformedQualifiedIdentityIsRejected()
    {
        Assert.ThrowsExactly<ArgumentException>(() => WindowsCredentialIdentity.Normalize(@"MicrosoftAccount\"));
    }

    [TestMethod]
    public void ArmAndCleanupRestorePreviousSettings()
    {
        using var fixture = new Fixture();
        var armed = fixture.Manager.Arm(fixture.ScheduleId, DateTime.Now.AddHours(1));
        var cleaned = fixture.Manager.Cleanup();
        Assert.AreEqual(AutologonJournalState.Armed, armed.State);
        Assert.AreEqual(AutologonJournalState.Cleaned, cleaned?.State);
        Assert.AreEqual(1, fixture.System.EnableCalls);
        Assert.AreEqual(1, fixture.System.DisableCalls);
    }

    [TestMethod]
    public void ExistingWindowsAutologonBlocksArmWithoutChanges()
    {
        using var fixture = new Fixture();
        fixture.System.ExistingEnabled = true;
        Assert.ThrowsExactly<InvalidOperationException>(() => fixture.Manager.Arm(fixture.ScheduleId, DateTime.Now.AddHours(1)));
        Assert.AreEqual(0, fixture.System.EnableCalls);
        Assert.IsNull(fixture.Journal.Read());
    }

    [TestMethod]
    public void ArmFailureRollsBackAndRemovesJournal()
    {
        using var fixture = new Fixture();
        fixture.System.FailEnable = true;
        Assert.ThrowsExactly<InvalidOperationException>(() => fixture.Manager.Arm(fixture.ScheduleId, DateTime.Now.AddHours(1)));
        Assert.AreEqual(1, fixture.System.DisableCalls);
        Assert.IsNull(fixture.Journal.Read());
    }

    [TestMethod]
    public void CleanupRetriesOnceThenSucceeds()
    {
        using var fixture = new Fixture();
        fixture.Manager.Arm(fixture.ScheduleId, DateTime.Now.AddHours(1));
        fixture.System.CleanupFailuresRemaining = 1;
        var result = fixture.Manager.Cleanup();
        Assert.AreEqual(AutologonJournalState.Cleaned, result?.State);
        Assert.AreEqual(2, fixture.System.DisableCalls);
    }

    [TestMethod]
    public void RepeatedCleanupFailureLeavesSecurityRecoveryJournal()
    {
        using var fixture = new Fixture();
        fixture.Manager.Arm(fixture.ScheduleId, DateTime.Now.AddHours(1));
        fixture.System.CleanupFailuresRemaining = 2;
        Assert.ThrowsExactly<InvalidOperationException>(() => fixture.Manager.Cleanup());
        Assert.AreEqual(AutologonJournalState.CleanupFailed, fixture.Journal.Read()?.State);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "AutoPower.Tests", Guid.NewGuid().ToString("N"));
        public Fixture()
        {
            Directory.CreateDirectory(_directory);
            Journal = new MemoryJournal();
            System = new FakeAutologonSystem();
            Manager = new AutologonManager(new FakeCredentialStore(), System, Journal, new TechnicalLogger(_directory));
        }
        public Guid ScheduleId { get; } = Guid.NewGuid();
        public MemoryJournal Journal { get; }
        public FakeAutologonSystem System { get; }
        public AutologonManager Manager { get; }
        public void Dispose() => Directory.Delete(_directory, true);
    }

    private sealed class FakeCredentialStore : IProtectedCredentialStore
    {
        public void Save(string userName, string password) { }
        public StoredCredential? Read() => new(@"PC\user", "not-logged");
        public bool Exists() => true;
        public void Delete() { }
    }

    internal sealed class MemoryJournal : IAutologonJournalStore
    {
        private AutologonJournal? _journal;
        public AutologonJournal? Read() => _journal;
        public void Write(AutologonJournal journal) => _journal = journal;
        public void Delete() => _journal = null;
    }

    internal sealed class FakeAutologonSystem : IAutologonSystem
    {
        public bool IsAdministrator => true;
        public bool ExistingEnabled { get; set; }
        public bool FailEnable { get; set; }
        public int CleanupFailuresRemaining { get; set; }
        public int EnableCalls { get; private set; }
        public int DisableCalls { get; private set; }
        public bool ExistingAutologonIsEnabled() => ExistingEnabled;
        public bool PlaintextDefaultPasswordExists() => false;
        public bool LsaDefaultPasswordExists() => false;
        public AutologonRegistrySnapshot CaptureRegistrySnapshot() => new(Missing(), Missing(), Missing(), Missing());
        public void ValidateCredential(StoredCredential credential) { }
        public void EnableTemporaryAutologon(StoredCredential credential)
        {
            EnableCalls++;
            if (FailEnable) throw new InvalidOperationException("injected arm failure");
        }
        public void DisableTemporaryAutologon(AutologonRegistrySnapshot previousRegistry)
        {
            DisableCalls++;
            if (CleanupFailuresRemaining-- > 0) throw new InvalidOperationException("injected cleanup failure");
        }
        public bool IsRestored(AutologonRegistrySnapshot previousRegistry) => true;
        private static RegistrySnapshotValue Missing() => new(false, null, RegistryValueKind.None);
    }
}
