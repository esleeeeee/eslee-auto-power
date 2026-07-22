using AutoPower.Core;
using AutoPower.Windows;

namespace AutoPower.Tests;

[TestClass]
public sealed class PowerTransitionRecoveryTests
{
    [TestMethod]
    public async Task BeginPersistsJournalAndExecutionStartedBeforePowerCall()
    {
        await using var database = await SqliteStoreTests.TestDatabase.CreateAsync();
        var schedule = PowerSchedule.Create(DateTime.Now.AddMinutes(1), PowerActionType.Hibernate);
        await database.Store.SaveScheduleAsync(schedule);

        var operation = await database.Store.TryBeginPowerTransitionAsync(schedule, "최대 절전 실행 시작");

        Assert.IsNotNull(operation);
        var loaded = await database.Store.GetScheduleAsync(schedule.Id);
        Assert.IsNotNull(loaded);
        Assert.IsFalse(loaded.IsEnabled);
        Assert.AreEqual(ScheduleStatus.PendingPowerTransition, loaded.Status);
        var pending = (await database.Store.GetIncompleteOperationsAsync()).Single();
        Assert.AreEqual(PendingOperationType.PowerTransition, pending.Type);
        Assert.AreEqual(schedule.Id, pending.ScheduleId);
        var history = await database.Store.GetHistoryAsync();
        Assert.HasCount(1, history);
        Assert.AreEqual("ExecutionStarted", history[0].EventType);
    }

    [TestMethod]
    public async Task DuplicateInvocationCannotConsumeTheSameScheduleTwice()
    {
        await using var database = await SqliteStoreTests.TestDatabase.CreateAsync();
        var schedule = PowerSchedule.Create(DateTime.Now.AddMinutes(1), PowerActionType.Sleep);
        await database.Store.SaveScheduleAsync(schedule);

        var first = await database.Store.TryBeginPowerTransitionAsync(schedule, "절전 실행 시작");
        var second = await database.Store.TryBeginPowerTransitionAsync(schedule, "중복 실행");

        Assert.IsNotNull(first);
        Assert.IsNull(second);
        Assert.HasCount(1, (await database.Store.GetHistoryAsync()).Where(item => item.EventType == "ExecutionStarted"));
        Assert.HasCount(1, (await database.Store.GetIncompleteOperationsAsync()).Where(item => item.Type == PendingOperationType.PowerTransition));
    }

    [TestMethod]
    public async Task ProcessInterruptionImmediatelyBeforeHibernateBecomesResultUnknown()
    {
        await using var fixture = await RecoveryFixture.CreateAsync(PowerActionType.Hibernate);
        await fixture.BeginAsync();
        fixture.Evidence.Value = NoEvidence(PowerTaskSnapshot.Missing);

        await fixture.ReconcileFromNewProcessAsync();

        var schedule = await fixture.Database.Store.GetScheduleAsync(fixture.Schedule.Id);
        Assert.AreEqual(ScheduleStatus.ResultUnknown, schedule?.Status);
        Assert.IsTrue((await fixture.Database.Store.GetHistoryAsync()).Any(item => item.EventType == "PowerTransitionResultUnknown"));
    }

    [TestMethod]
    public async Task HibernateResumeCompletesPendingOperationAndCreatesHistory()
    {
        await using var fixture = await RecoveryFixture.CreateAsync(PowerActionType.Hibernate);
        await fixture.BeginAsync();
        fixture.Evidence.Value = CompletedEvidence(fixture.NowUtc.AddHours(-1), fixture.NowUtc);

        await fixture.ReconcileFromNewProcessAsync();

        var schedule = await fixture.Database.Store.GetScheduleAsync(fixture.Schedule.Id);
        Assert.AreEqual(ScheduleStatus.Completed, schedule?.Status);
        Assert.IsEmpty(await fixture.Database.Store.GetIncompleteOperationsAsync());
        var history = await fixture.Database.Store.GetHistoryAsync();
        Assert.IsTrue(history.Any(item => item.EventType == "ExecutionStarted"));
        Assert.IsTrue(history.Any(item => item.EventType == "PowerTransitionRecovered" && item.Result == ResultKind.Success));
    }

    [TestMethod]
    public async Task ClosingAppBeforeResumeStillRecoversOnNextStartup()
    {
        await using var fixture = await RecoveryFixture.CreateAsync(PowerActionType.Sleep);
        await fixture.BeginAsync();

        fixture.Evidence.Value = CompletedEvidence(fixture.NowUtc.AddHours(-1), fixture.NowUtc);
        await fixture.ReconcileFromNewProcessAsync();

        Assert.AreEqual(ScheduleStatus.Completed,
            (await fixture.Database.Store.GetScheduleAsync(fixture.Schedule.Id))?.Status);
    }

    [TestMethod]
    public async Task ExecutedOneTimeScheduleDisappearsFromFutureList()
    {
        await using var fixture = await RecoveryFixture.CreateAsync(PowerActionType.Hibernate);
        await fixture.BeginAsync();
        fixture.Evidence.Value = CompletedEvidence(fixture.NowUtc.AddHours(-1), fixture.NowUtc);
        await fixture.ReconcileFromNewProcessAsync();

        var future = await fixture.Database.Store.GetFutureSchedulesAsync(fixture.NowUtc.ToLocalTime().DateTime);

        Assert.IsFalse(future.Any(item => item.Id == fixture.Schedule.Id));
    }

    [TestMethod]
    public async Task PastScheduleIsMissedAndItsTaskIsConsumedInsteadOfRunningLate()
    {
        await using var fixture = await RecoveryFixture.CreateAsync(PowerActionType.Sleep);
        fixture.Evidence.Value = NoEvidence(PowerTaskSnapshot.Missing);

        await fixture.ReconcileFromNewProcessAsync(canRepairSystem: true);

        Assert.AreEqual(ScheduleStatus.Missed,
            (await fixture.Database.Store.GetScheduleAsync(fixture.Schedule.Id))?.Status);
        Assert.AreEqual(1, fixture.Registrar.ConsumeCount);
        Assert.AreEqual(1, fixture.Registrar.RemoveCount);
        Assert.AreEqual(0, fixture.Registrar.RegisterCount);
    }

    [TestMethod]
    public async Task TaskRanWithoutPowerOrHistoryEvidenceBecomesResultUnknown()
    {
        await using var fixture = await RecoveryFixture.CreateAsync(PowerActionType.Hibernate);
        fixture.Evidence.Value = NoEvidence(new PowerTaskSnapshot(
            true,
            false,
            false,
            fixture.Schedule.ScheduledLocalDateTime,
            0,
            null));

        await fixture.ReconcileFromNewProcessAsync();

        Assert.AreEqual(ScheduleStatus.ResultUnknown,
            (await fixture.Database.Store.GetScheduleAsync(fixture.Schedule.Id))?.Status);
    }

    [TestMethod]
    public async Task TaskThatNeverRanIsNotReportedAsSuccess()
    {
        await using var fixture = await RecoveryFixture.CreateAsync(PowerActionType.Hibernate);
        fixture.Evidence.Value = NoEvidence(new PowerTaskSnapshot(
            true,
            true,
            false,
            null,
            0x00041303,
            null));

        await fixture.ReconcileFromNewProcessAsync();

        Assert.AreEqual(ScheduleStatus.Missed,
            (await fixture.Database.Store.GetScheduleAsync(fixture.Schedule.Id))?.Status);
    }

    [TestMethod]
    public async Task NonZeroTaskResultIsRecoveredAsFailure()
    {
        await using var fixture = await RecoveryFixture.CreateAsync(PowerActionType.Shutdown);
        fixture.Evidence.Value = NoEvidence(new PowerTaskSnapshot(
            true,
            false,
            false,
            fixture.Schedule.ScheduledLocalDateTime,
            unchecked((int)0x80004005),
            null));

        await fixture.ReconcileFromNewProcessAsync();

        Assert.AreEqual(ScheduleStatus.Failed,
            (await fixture.Database.Store.GetScheduleAsync(fixture.Schedule.Id))?.Status);
        Assert.IsTrue((await fixture.Database.Store.GetHistoryAsync()).Any(item => item.EventType == "PowerTransitionFailed"));
    }

    [TestMethod]
    public async Task RepeatedReconciliationDoesNotDuplicateTerminalHistory()
    {
        await using var fixture = await RecoveryFixture.CreateAsync(PowerActionType.Sleep);
        await fixture.BeginAsync();
        fixture.Evidence.Value = CompletedEvidence(fixture.NowUtc.AddHours(-1), fixture.NowUtc);

        await fixture.ReconcileFromNewProcessAsync();
        await fixture.ReconcileFromNewProcessAsync();

        Assert.HasCount(1, (await fixture.Database.Store.GetHistoryAsync()).Where(item => item.EventType == "PowerTransitionRecovered"));
    }

    [TestMethod]
    public async Task LegacyPremarkedCompletedScheduleIsValidatedAgainstPowerEvidence()
    {
        await using var fixture = await RecoveryFixture.CreateAsync(PowerActionType.Hibernate);
        await fixture.Database.Store.UpdateScheduleStateAsync(fixture.Schedule.Id, false, ScheduleStatus.Completed);
        await fixture.Database.Store.AddHistoryAsync(
            fixture.Schedule.Id,
            "PowerAction",
            ResultKind.Information,
            fixture.Schedule.ScheduledLocalDateTime,
            "최대 절전 동작을 시작했습니다.");
        fixture.Evidence.Value = CompletedEvidence(fixture.NowUtc.AddHours(-1), fixture.NowUtc);

        await fixture.ReconcileFromNewProcessAsync();

        Assert.AreEqual(ScheduleStatus.Completed,
            (await fixture.Database.Store.GetScheduleAsync(fixture.Schedule.Id))?.Status);
        Assert.IsTrue((await fixture.Database.Store.GetHistoryAsync()).Any(item => item.EventType == "PowerTransitionRecovered"));
    }

    [TestMethod]
    public async Task UnavailableEventAndTaskEvidenceDoesNotOverwritePendingResult()
    {
        await using var fixture = await RecoveryFixture.CreateAsync(PowerActionType.Hibernate);
        await fixture.BeginAsync();
        fixture.Evidence.Value = new PowerTransitionEvidence(
            false,
            false,
            null,
            null,
            PowerTaskSnapshot.Missing,
            "테스트: 증거 접근 불가",
            EventLogAvailable: false);

        await fixture.ReconcileFromNewProcessAsync();

        Assert.AreEqual(ScheduleStatus.PendingPowerTransition,
            (await fixture.Database.Store.GetScheduleAsync(fixture.Schedule.Id))?.Status);
        Assert.IsFalse((await fixture.Database.Store.GetHistoryAsync()).Any(item => item.EventType == "PowerTransitionResultUnknown"));
    }

    private static PowerTransitionEvidence NoEvidence(PowerTaskSnapshot task) =>
        new(false, false, null, null, task, "테스트: 전원 이벤트 없음");

    private static PowerTransitionEvidence CompletedEvidence(DateTimeOffset entered, DateTimeOffset resumed) =>
        new(true, true, entered, resumed, PowerTaskSnapshot.Missing, "테스트: 진입 및 복귀 확인");

    private sealed class RecoveryFixture : IAsyncDisposable
    {
        private readonly string _logDirectory;

        private RecoveryFixture(
            SqliteStoreTests.TestDatabase database,
            PowerSchedule schedule,
            DateTimeOffset nowUtc,
            FakeRegistrar registrar,
            FakeEvidence evidence,
            string logDirectory)
        {
            Database = database;
            Schedule = schedule;
            NowUtc = nowUtc;
            Registrar = registrar;
            Evidence = evidence;
            _logDirectory = logDirectory;
        }

        public SqliteStoreTests.TestDatabase Database { get; }
        public PowerSchedule Schedule { get; }
        public DateTimeOffset NowUtc { get; }
        public FakeRegistrar Registrar { get; }
        public FakeEvidence Evidence { get; }

        public static async Task<RecoveryFixture> CreateAsync(PowerActionType action)
        {
            var database = await SqliteStoreTests.TestDatabase.CreateAsync();
            var now = DateTimeOffset.Now;
            var schedule = PowerSchedule.Create(now.LocalDateTime.AddHours(-1), action);
            await database.Store.SaveScheduleAsync(schedule);
            var logDirectory = Path.Combine(Path.GetTempPath(), "AutoPower.Tests", Guid.NewGuid().ToString("N"), "logs");
            return new RecoveryFixture(database, schedule, now.ToUniversalTime(), new FakeRegistrar(), new FakeEvidence(), logDirectory);
        }

        public async Task BeginAsync()
        {
            var operation = await Database.Store.TryBeginPowerTransitionAsync(
                Schedule,
                "테스트 실행 시작",
                NowUtc.AddHours(-1));
            Assert.IsNotNull(operation);
        }

        public async Task ReconcileFromNewProcessAsync(bool canRepairSystem = false)
        {
            var logger = new TechnicalLogger(_logDirectory);
            var reconciler = new PowerTransitionReconciler(
                Database.Store,
                Registrar,
                Evidence,
                logger,
                () => NowUtc);
            await reconciler.ReconcileAsync(canRepairSystem);
        }

        public async ValueTask DisposeAsync()
        {
            await Database.DisposeAsync();
            var parent = Directory.GetParent(_logDirectory)?.FullName;
            if (parent is not null && Directory.Exists(parent))
            {
                Directory.Delete(parent, true);
            }
        }
    }

    private sealed class FakeEvidence : IPowerTransitionEvidenceSource
    {
        public PowerTransitionEvidence Value { get; set; } = NoEvidence(PowerTaskSnapshot.Missing);

        public PowerTransitionEvidence Inspect(PowerSchedule schedule, DateTimeOffset startedAtUtc, DateTimeOffset nowUtc) => Value;
    }

    private sealed class FakeRegistrar : ISystemScheduleRegistrar
    {
        public int RegisterCount { get; private set; }
        public int ConsumeCount { get; private set; }
        public int RemoveCount { get; private set; }

        public void Register(PowerSchedule schedule) => RegisterCount++;
        public void Remove(Guid scheduleId) => RemoveCount++;
        public void ConsumePowerTask(Guid scheduleId) => ConsumeCount++;
        public PowerTaskSnapshot GetPowerTaskSnapshot(Guid scheduleId) => PowerTaskSnapshot.Missing;
        public IReadOnlySet<string> ListOwnedTasks() => new HashSet<string>();
        public void RegisterStartupAgent(string userName) { }
        public void RemoveAllOwnedTasks() { }
    }
}
