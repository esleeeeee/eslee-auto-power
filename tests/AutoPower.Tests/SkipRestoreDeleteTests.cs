using AutoPower.Core;
using AutoPower.Data;
using AutoPower.Windows;

namespace AutoPower.Tests;

[TestClass]
public sealed class SkipRestoreDeleteTests
{
    [TestMethod]
    public async Task RestoringASkippedOneTimeShutdownMakesItPendingAgainAndIsIdempotent()
    {
        await using var fixture = await RecurringSeriesServiceTests.SeriesFixture.CreateAsync();
        var store = fixture.Database.Store;
        var shutdown = PowerSchedule.Create(fixture.Now.AddMinutes(4), PowerActionType.Shutdown);
        await store.SaveScheduleAsync(shutdown);
        var operations = Operations(fixture);
        await operations.SkipAsync(shutdown.Id);
        Assert.AreEqual(ScheduleStatus.Skipped, (await store.GetScheduleAsync(shutdown.Id))?.Status);

        var restored = await operations.RestoreSkippedAsync(shutdown.Id);
        var again = await operations.RestoreSkippedAsync(shutdown.Id);

        var loaded = await store.GetScheduleAsync(shutdown.Id);
        Assert.AreEqual(ScheduleStatus.Pending, loaded?.Status);
        Assert.IsTrue(loaded?.IsEnabled);
        Assert.AreEqual(ScheduleStatus.Pending, restored.Status);
        Assert.AreEqual(shutdown.Id, again.Id);
        CollectionAssert.AreEqual(new[] { shutdown.Id, shutdown.Id }, fixture.Registrar.Registered.ToArray());
        Assert.HasCount(1, (await store.GetHistoryAsync()).Where(item => item.EventType == HistoryEventTypes.ScheduleRestored));
        Assert.HasCount(1, await store.GetAllSchedulesAsync());
    }

    [TestMethod]
    public async Task RestoringASkippedRecurringOccurrenceRemovesTheOccurrenceTheSkipCreated()
    {
        await using var fixture = await RecurringSeriesServiceTests.SeriesFixture.CreateAsync();
        var store = fixture.Database.Store;
        var time = fixture.Now.AddMinutes(4);
        var shutdown = PowerSchedule.Create(time, PowerActionType.Shutdown, recurrence: RecurrenceRule.CreateDaily(time.TimeOfDay));
        await store.SaveScheduleAsync(shutdown);
        var operations = Operations(fixture);
        await operations.SkipAsync(shutdown.Id);
        var advanced = (await store.GetAllSchedulesAsync()).Single(item => item.Id != shutdown.Id);
        Assert.AreEqual(ScheduleStatus.Pending, advanced.Status);

        await operations.RestoreSkippedAsync(shutdown.Id);

        var series = (await store.GetAllSchedulesAsync()).Where(item => item.SeriesId == shutdown.SeriesId).ToArray();
        Assert.HasCount(1, series);
        Assert.AreEqual(shutdown.Id, series[0].Id);
        Assert.AreEqual(ScheduleStatus.Pending, series[0].Status);
        Assert.Contains(advanced.Id, fixture.Registrar.Removed);
        Assert.AreEqual(shutdown.Id, fixture.Registrar.Registered.Last());
    }

    [TestMethod]
    public async Task RestoreRefusesChangedTooCloseOrMissingSchedules()
    {
        await using var fixture = await RecurringSeriesServiceTests.SeriesFixture.CreateAsync();
        var store = fixture.Database.Store;
        var operations = Operations(fixture);
        var tooClose = PowerSchedule.Create(fixture.Now.AddSeconds(40), PowerActionType.Sleep) with
        {
            IsEnabled = false,
            Status = ScheduleStatus.Skipped
        };
        var inFlight = PowerSchedule.Create(fixture.Now.AddMinutes(10), PowerActionType.Shutdown) with
        {
            IsEnabled = false,
            Status = ScheduleStatus.PendingPowerTransition
        };
        var disabled = PowerSchedule.Create(fixture.Now.AddMinutes(20), PowerActionType.Hibernate) with
        {
            IsEnabled = false,
            Status = ScheduleStatus.Disabled
        };
        foreach (var schedule in new[] { tooClose, inFlight, disabled })
        {
            await store.SaveScheduleAsync(schedule);
        }

        var tooCloseError = await Assert.ThrowsExactlyAsync<ScheduleStateConflictException>(() => operations.RestoreSkippedAsync(tooClose.Id));
        var changedError = await Assert.ThrowsExactlyAsync<ScheduleStateConflictException>(() => operations.RestoreSkippedAsync(inFlight.Id));
        await Assert.ThrowsExactlyAsync<ScheduleStateConflictException>(() => operations.RestoreSkippedAsync(disabled.Id));
        await Assert.ThrowsExactlyAsync<KeyNotFoundException>(() => operations.RestoreSkippedAsync(Guid.NewGuid()));

        Assert.AreEqual("예약 시각이 지났거나 너무 가까워 건너뛴 예약을 다시 켤 수 없습니다.", tooCloseError.Message);
        Assert.AreEqual("건너뛴 예약만 다시 켤 수 있습니다. 예약 상태가 이미 바뀌었습니다.", changedError.Message);
        Assert.AreEqual(ScheduleStatus.PendingPowerTransition, (await store.GetScheduleAsync(inFlight.Id))?.Status);
        Assert.AreEqual(ScheduleStatus.Skipped, (await store.GetScheduleAsync(tooClose.Id))?.Status);
        Assert.IsEmpty(fixture.Registrar.Registered);
    }

    [TestMethod]
    public async Task RestoreRegistrationFailureSkipsAgainAndReadvancesTheSeries()
    {
        await using var fixture = await RecurringSeriesServiceTests.SeriesFixture.CreateAsync();
        var store = fixture.Database.Store;
        var time = fixture.Now.AddMinutes(4);
        var sleep = PowerSchedule.Create(time, PowerActionType.Sleep, recurrence: RecurrenceRule.CreateDaily(time.TimeOfDay));
        await store.SaveScheduleAsync(sleep);
        var operations = Operations(fixture);
        await operations.SkipAsync(sleep.Id);
        fixture.Registrar.FailRegister = true;

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => operations.RestoreSkippedAsync(sleep.Id));

        Assert.AreEqual(ScheduleStatus.Skipped, (await store.GetScheduleAsync(sleep.Id))?.Status);
        var live = (await store.GetAllSchedulesAsync())
            .Where(item => item.SeriesId == sleep.SeriesId && item.Status is ScheduleStatus.Pending or ScheduleStatus.Disabled)
            .ToArray();
        Assert.HasCount(1, live);
        Assert.AreEqual(time.AddDays(1), live[0].ScheduledLocalDateTime);
    }

    [TestMethod]
    public async Task SkippingAPausedOccurrenceKeepsTheSeriesPaused()
    {
        await using var fixture = await RecurringSeriesServiceTests.SeriesFixture.CreateAsync();
        var store = fixture.Database.Store;
        var time = fixture.Now.AddHours(1);
        var paused = PowerSchedule.Create(time, PowerActionType.Hibernate, recurrence: RecurrenceRule.CreateDaily(time.TimeOfDay)) with
        {
            IsEnabled = false,
            Status = ScheduleStatus.Disabled
        };
        await store.SaveScheduleAsync(paused);

        await Operations(fixture).SkipAsync(paused.Id);

        var next = (await store.GetAllSchedulesAsync()).Single(item => item.Id != paused.Id);
        Assert.AreEqual(ScheduleStatus.Disabled, next.Status);
        Assert.IsFalse(next.IsEnabled);
        Assert.AreEqual(time.AddDays(1), next.ScheduledLocalDateTime);
        Assert.IsEmpty(fixture.Registrar.Registered);
        Assert.IsEmpty(await fixture.Service(registrar: false).HealSeriesAsync());
    }

    [TestMethod]
    public async Task SkippingAnActiveOccurrenceAdvancesToAnActiveRegisteredOccurrence()
    {
        await using var fixture = await RecurringSeriesServiceTests.SeriesFixture.CreateAsync();
        var store = fixture.Database.Store;
        var time = fixture.Now.AddMinutes(4);
        var active = PowerSchedule.Create(time, PowerActionType.Sleep, recurrence: RecurrenceRule.CreateWeekdays(time.TimeOfDay));
        await store.SaveScheduleAsync(active);

        await Operations(fixture).SkipAsync(active.Id);

        var next = (await store.GetAllSchedulesAsync()).Single(item => item.Id != active.Id);
        Assert.AreEqual(ScheduleStatus.Pending, next.Status);
        CollectionAssert.AreEqual(new[] { next.Id }, fixture.Registrar.Registered.ToArray());
        CollectionAssert.AreEqual(new[] { active.Id }, fixture.Registrar.Removed.ToArray());
    }

    [TestMethod]
    public async Task InFlightScheduleCannotBeDeleted()
    {
        await using var fixture = await RecurringSeriesServiceTests.SeriesFixture.CreateAsync();
        var store = fixture.Database.Store;
        var schedule = PowerSchedule.Create(DateTime.Now.AddMinutes(1), PowerActionType.Shutdown);
        await store.SaveScheduleAsync(schedule);
        Assert.IsNotNull(await store.TryBeginPowerTransitionAsync(schedule, "시작"));
        var helper = new RecordingHelper();
        var coordinator = new ScheduleCoordinator(store, helper);

        var error = await Assert.ThrowsExactlyAsync<ScheduleStateConflictException>(() => coordinator.DeleteAsync(schedule.Id));
        await Assert.ThrowsExactlyAsync<ScheduleStateConflictException>(() => store.DeleteScheduleAsync(schedule.Id));
        await Assert.ThrowsExactlyAsync<ScheduleStateConflictException>(() => Operations(fixture).RemoveTasksForDeleteAsync(schedule.Id));

        Assert.AreEqual(SqliteStore.InFlightDeleteMessage, error.Message);
        Assert.IsEmpty(helper.Calls);
        Assert.IsEmpty(fixture.Registrar.Removed);
        Assert.AreEqual(ScheduleStatus.PendingPowerTransition, (await store.GetScheduleAsync(schedule.Id))?.Status);
    }

    [TestMethod]
    public async Task DeletingAnOccurrenceAlsoRemovesOtherLiveOccurrencesOfTheSeries()
    {
        await using var fixture = await RecurringSeriesServiceTests.SeriesFixture.CreateAsync();
        var store = fixture.Database.Store;
        var executed = await RecurringSeriesStoreTests.SaveExecutedOccurrenceAsync(
            store, fixture.Now.AddDays(-1), PowerActionType.Shutdown, ScheduleStatus.Completed);
        var live = await store.TryAdvanceSeriesAsync(executed.Id, fixture.Now);
        Assert.IsNotNull(live);
        var stray = live.CreateNextOccurrence(fixture.Now.AddDays(3), DateTimeOffset.UtcNow);
        await store.SaveScheduleAsync(stray);

        await Operations(fixture).RemoveTasksForDeleteAsync(live.Id);
        var removed = await store.DeleteScheduleAsync(live.Id);

        CollectionAssert.AreEqual(new[] { stray.Id }, removed.ToArray());
        CollectionAssert.AreEquivalent(new[] { live.Id, stray.Id }, fixture.Registrar.Removed.ToArray());
        var remaining = await store.GetAllSchedulesAsync();
        Assert.HasCount(1, remaining);
        Assert.AreEqual(executed.Id, remaining[0].Id);
        Assert.IsNull(remaining[0].Recurrence);
    }

    [TestMethod]
    public async Task PastPausedRecurringRowCanBeReenabledOrDeleted()
    {
        await using var database = await SqliteStoreTests.TestDatabase.CreateAsync();
        var now = new DateTime(2030, 1, 7, 9, 0, 0);
        var helper = new RecordingHelper();
        var coordinator = new ScheduleCoordinator(database.Store, helper, () => now);
        var rule = RecurrenceRule.CreateDaily(new TimeSpan(8, 0, 0));
        var first = PowerSchedule.Create(new DateTime(2030, 1, 5, 8, 0, 0), PowerActionType.Shutdown, recurrence: rule) with
        {
            IsEnabled = false,
            Status = ScheduleStatus.Disabled
        };
        var second = PowerSchedule.Create(new DateTime(2030, 1, 6, 8, 0, 0), PowerActionType.Sleep, recurrence: rule) with
        {
            IsEnabled = false,
            Status = ScheduleStatus.Disabled
        };
        await database.Store.SaveScheduleAsync(first);
        await database.Store.SaveScheduleAsync(second);

        await coordinator.SetEnabledAsync(first.Id, true);
        await coordinator.DeleteAsync(second.Id);

        var reenabled = await database.Store.GetScheduleAsync(first.Id);
        Assert.AreEqual(new DateTime(2030, 1, 8, 8, 0, 0), reenabled?.ScheduledLocalDateTime);
        Assert.AreEqual(ScheduleStatus.Pending, reenabled?.Status);
        Assert.IsNull(await database.Store.GetScheduleAsync(second.Id));
        CollectionAssert.AreEqual(new[] { "remove-schedule", "--schedule", second.Id.ToString("D") }, helper.Calls.Last().ToArray());
        Assert.IsEmpty(await database.Store.GetIncompleteOperationsAsync());
    }

    [TestMethod]
    public async Task ReconcileMarksPastPendingScheduleMissedOnce()
    {
        await using var fixture = await RecurringSeriesServiceTests.SeriesFixture.CreateAsync();
        var store = fixture.Database.Store;
        var wake = PowerSchedule.Create(DateTime.Now.AddHours(-1), PowerActionType.WakeFromSleep);
        await store.SaveScheduleAsync(wake);
        var reconciler = new IntegrityReconciler(
            store,
            fixture.Registrar,
            new AutologonManager(new NoCredentialStore(), new AutologonManagerTests.FakeAutologonSystem(),
                new AutologonManagerTests.MemoryJournal(), fixture.Logger),
            fixture.Logger);

        await reconciler.ReconcileAsync(canRepairSystem: false);
        await reconciler.ReconcileAsync(canRepairSystem: false);

        Assert.AreEqual(ScheduleStatus.Missed, (await store.GetScheduleAsync(wake.Id))?.Status);
        Assert.HasCount(1, (await store.GetHistoryAsync()).Where(item => item.EventType == HistoryEventTypes.ScheduleMissed));
    }

    private static ScheduleSkipOperations Operations(RecurringSeriesServiceTests.SeriesFixture fixture) =>
        new(fixture.Database.Store, fixture.Registrar, fixture.Logger, () => fixture.Now);

    private sealed class NoCredentialStore : IProtectedCredentialStore
    {
        public void Save(string userName, string password) { }
        public StoredCredential? Read() => null;
        public bool Exists() => false;
        public void Delete() { }
    }

    private sealed class RecordingHelper : IPrivilegedHelperClient
    {
        public List<IReadOnlyList<string>> Calls { get; } = [];

        public Task RunAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken = default)
        {
            Calls.Add(arguments);
            return Task.CompletedTask;
        }
    }
}
