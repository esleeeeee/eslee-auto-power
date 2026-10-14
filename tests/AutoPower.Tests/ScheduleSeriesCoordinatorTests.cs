using AutoPower.Core;
using AutoPower.Windows;

namespace AutoPower.Tests;

[TestClass]
public sealed class ScheduleSeriesCoordinatorTests
{
    private static readonly DateTime Now = new(2030, 1, 7, 22, 56, 20);

    [TestMethod]
    public async Task SnoozeMovesTheScheduleThroughTheRegularSavePath()
    {
        await using var database = await SqliteStoreTests.TestDatabase.CreateAsync();
        var helper = new RecordingHelper();
        var coordinator = new ScheduleCoordinator(database.Store, helper, () => Now);
        var rule = RecurrenceRule.CreateDaily(new TimeSpan(23, 0, 0));
        var schedule = PowerSchedule.Create(new DateTime(2030, 1, 7, 23, 0, 0), PowerActionType.Shutdown, recurrence: rule);
        await database.Store.SaveScheduleAsync(schedule);

        var snoozed = await coordinator.SnoozeAsync(schedule.Id, 10);

        Assert.AreEqual(new DateTime(2030, 1, 7, 23, 10, 0), snoozed);
        var loaded = await database.Store.GetScheduleAsync(schedule.Id);
        Assert.AreEqual(snoozed, loaded?.ScheduledLocalDateTime);
        Assert.AreEqual(rule, loaded?.Recurrence);
        Assert.AreEqual(schedule.SeriesId, loaded?.SeriesId);
        CollectionAssert.AreEqual(new[] { "register-schedule", "--schedule", schedule.Id.ToString("D") }, helper.Calls.Single().ToArray());
        var history = await database.Store.GetHistoryAsync();
        var entry = history.Single();
        Assert.AreEqual(HistoryEventTypes.Snoozed, entry.EventType);
        Assert.AreEqual(ResultKind.Information, entry.Result);
        Assert.AreEqual("예약을 10분 미뤘습니다. 새 시각: 2030.01.07 23:10", entry.UserReadableMessage);
        Assert.IsEmpty(await database.Store.GetIncompleteOperationsAsync());
    }

    [TestMethod]
    public async Task SnoozeRollsBackWhenTheHelperFails()
    {
        await using var database = await SqliteStoreTests.TestDatabase.CreateAsync();
        var coordinator = new ScheduleCoordinator(database.Store, new RecordingHelper { Fail = true }, () => Now);
        var schedule = PowerSchedule.Create(new DateTime(2030, 1, 7, 23, 0, 0), PowerActionType.Sleep);
        await database.Store.SaveScheduleAsync(schedule);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => coordinator.SnoozeAsync(schedule.Id, 30));

        Assert.AreEqual(schedule.ScheduledLocalDateTime, (await database.Store.GetScheduleAsync(schedule.Id))?.ScheduledLocalDateTime);
        Assert.IsFalse((await database.Store.GetHistoryAsync()).Any(item => item.EventType == HistoryEventTypes.Snoozed));
    }

    [TestMethod]
    public async Task SnoozeRejectsUnsupportedMinutesWakeDisabledAndInFlightSchedules()
    {
        await using var database = await SqliteStoreTests.TestDatabase.CreateAsync();
        var helper = new RecordingHelper();
        var coordinator = new ScheduleCoordinator(database.Store, helper, () => Now);
        var shutdown = PowerSchedule.Create(new DateTime(2030, 1, 7, 23, 0, 0), PowerActionType.Shutdown);
        var wake = PowerSchedule.Create(new DateTime(2030, 1, 7, 23, 30, 0), PowerActionType.WakeFromSleep);
        var disabled = PowerSchedule.Create(new DateTime(2030, 1, 8, 1, 0, 0), PowerActionType.Hibernate) with
        {
            IsEnabled = false,
            Status = ScheduleStatus.Disabled
        };
        var inFlight = PowerSchedule.Create(new DateTime(2030, 1, 8, 2, 0, 0), PowerActionType.Sleep) with
        {
            IsEnabled = false,
            Status = ScheduleStatus.PendingPowerTransition
        };
        foreach (var schedule in new[] { shutdown, wake, disabled, inFlight })
        {
            await database.Store.SaveScheduleAsync(schedule);
        }

        await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(() => coordinator.SnoozeAsync(shutdown.Id, 15));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => coordinator.SnoozeAsync(wake.Id, 10));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => coordinator.SnoozeAsync(disabled.Id, 10));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => coordinator.SnoozeAsync(inFlight.Id, 10));
        await Assert.ThrowsExactlyAsync<KeyNotFoundException>(() => coordinator.SnoozeAsync(Guid.NewGuid(), 10));
        Assert.IsEmpty(helper.Calls);
    }

    [TestMethod]
    public async Task ReenablingAPastRecurringOccurrenceMovesItToTheNextOccurrence()
    {
        await using var database = await SqliteStoreTests.TestDatabase.CreateAsync();
        var coordinator = new ScheduleCoordinator(database.Store, new RecordingHelper(), () => Now);
        var rule = RecurrenceRule.CreateWeekdays(new TimeSpan(22, 57, 0));
        var paused = PowerSchedule.Create(new DateTime(2030, 1, 4, 22, 57, 0), PowerActionType.Sleep, recurrence: rule) with
        {
            IsEnabled = false,
            Status = ScheduleStatus.Disabled
        };
        await database.Store.SaveScheduleAsync(paused);

        await coordinator.SetEnabledAsync(paused.Id, true);

        var loaded = await database.Store.GetScheduleAsync(paused.Id);
        Assert.AreEqual(DayOfWeek.Monday, Now.DayOfWeek);
        Assert.AreEqual(new DateTime(2030, 1, 8, 22, 57, 0), loaded?.ScheduledLocalDateTime);
        Assert.AreEqual(ScheduleStatus.Pending, loaded?.Status);
        Assert.IsTrue(loaded?.IsEnabled);
    }

    [TestMethod]
    public async Task ReenablingAPastOneTimeScheduleIsStillRejected()
    {
        await using var database = await SqliteStoreTests.TestDatabase.CreateAsync();
        var coordinator = new ScheduleCoordinator(database.Store, new RecordingHelper(), () => Now);
        var paused = PowerSchedule.Create(Now.AddMinutes(-5), PowerActionType.Sleep) with
        {
            IsEnabled = false,
            Status = ScheduleStatus.Disabled
        };
        await database.Store.SaveScheduleAsync(paused);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => coordinator.SetEnabledAsync(paused.Id, true));
    }

    [TestMethod]
    public async Task SaveAssignsSeriesToRecurringAndRemovingRecurrenceEndsTheSeries()
    {
        await using var database = await SqliteStoreTests.TestDatabase.CreateAsync();
        var coordinator = new ScheduleCoordinator(database.Store, new RecordingHelper(), () => Now);
        var executed = await RecurringSeriesStoreTests.SaveExecutedOccurrenceAsync(
            database.Store, Now.AddDays(-1), PowerActionType.Shutdown, ScheduleStatus.Completed);
        var live = await database.Store.TryAdvanceSeriesAsync(executed.Id, Now);
        Assert.IsNotNull(live);

        var result = await coordinator.SaveAsync(live with { Recurrence = null });
        var recurringWithoutSeries = new PowerSchedule(Guid.NewGuid(), Now.AddHours(3), PowerActionType.Sleep, true, false,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, ScheduleStatus.Pending, [])
        {
            Recurrence = RecurrenceRule.CreateDaily(Now.AddHours(3).TimeOfDay)
        };
        await coordinator.SaveAsync(recurringWithoutSeries);

        Assert.IsTrue(result.IsValid);
        Assert.IsNull((await database.Store.GetScheduleAsync(live.Id))?.SeriesId);
        Assert.IsNull((await database.Store.GetScheduleAsync(executed.Id))?.Recurrence);
        Assert.IsNotNull((await database.Store.GetScheduleAsync(recurringWithoutSeries.Id))?.SeriesId);
    }

    [TestMethod]
    public async Task ResumeAllReenablesFutureMovesPastRecurringAndLeavesPastOneTime()
    {
        await using var database = await SqliteStoreTests.TestDatabase.CreateAsync();
        var registrar = new RecurringSeriesServiceTests.RecordingRegistrar();
        var store = database.Store;
        var future = Disabled(PowerSchedule.Create(Now.AddHours(1), PowerActionType.Sleep));
        var pastRecurring = Disabled(PowerSchedule.Create(Now.AddDays(-2), PowerActionType.Shutdown,
            recurrence: RecurrenceRule.CreateDaily(new TimeSpan(23, 30, 0))));
        var pastOneTime = Disabled(PowerSchedule.Create(Now.AddHours(-1), PowerActionType.Hibernate));
        var active = PowerSchedule.Create(Now.AddHours(2), PowerActionType.Hibernate);
        foreach (var schedule in new[] { future, pastRecurring, pastOneTime, active })
        {
            await store.SaveScheduleAsync(schedule);
        }

        var result = await new ScheduleBulkOperations(store, registrar, () => Now).ResumeAllAsync();

        Assert.AreEqual(new ResumeAllResult(2, 1, 0), result);
        var loadedFuture = await store.GetScheduleAsync(future.Id);
        Assert.AreEqual(ScheduleStatus.Pending, loadedFuture?.Status);
        Assert.AreEqual(future.ScheduledLocalDateTime, loadedFuture?.ScheduledLocalDateTime);
        var loadedRecurring = await store.GetScheduleAsync(pastRecurring.Id);
        Assert.AreEqual(ScheduleStatus.Pending, loadedRecurring?.Status);
        Assert.AreEqual(new DateTime(2030, 1, 7, 23, 30, 0), loadedRecurring?.ScheduledLocalDateTime);
        Assert.AreEqual(ScheduleStatus.Disabled, (await store.GetScheduleAsync(pastOneTime.Id))?.Status);
        CollectionAssert.AreEquivalent(new[] { future.Id, pastRecurring.Id }, registrar.Registered.ToArray());
        Assert.IsTrue((await store.GetHistoryAsync()).Any(item => item.EventType == HistoryEventTypes.SchedulesResumed));
    }

    [TestMethod]
    public async Task ResumeAllKeepsConflictingScheduleDisabledAndRevertsOnRegistrationFailure()
    {
        await using var database = await SqliteStoreTests.TestDatabase.CreateAsync();
        var registrar = new RecurringSeriesServiceTests.RecordingRegistrar();
        var store = database.Store;
        var conflicting = Disabled(PowerSchedule.Create(Now.AddHours(1), PowerActionType.Sleep));
        await store.SaveScheduleAsync(conflicting);
        await store.SaveScheduleAsync(PowerSchedule.Create(Now.AddHours(1), PowerActionType.Shutdown));

        var result = await new ScheduleBulkOperations(store, registrar, () => Now).ResumeAllAsync();

        Assert.AreEqual(new ResumeAllResult(0, 0, 1), result);
        Assert.AreEqual(ScheduleStatus.Disabled, (await store.GetScheduleAsync(conflicting.Id))?.Status);
        Assert.IsTrue((await store.GetHistoryAsync()).Any(item => item.EventType == HistoryEventTypes.ScheduleResumeConflict));

        var failing = Disabled(PowerSchedule.Create(Now.AddHours(5), PowerActionType.Hibernate));
        await store.SaveScheduleAsync(failing);
        registrar.FailRegister = true;
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            new ScheduleBulkOperations(store, registrar, () => Now).ResumeAllAsync());
        var reverted = await store.GetScheduleAsync(failing.Id);
        Assert.AreEqual(ScheduleStatus.Disabled, reverted?.Status);
        Assert.IsFalse(reverted?.IsEnabled);
    }

    [TestMethod]
    public async Task PauseAllDisablesOnlyFutureActiveSchedulesAndSkipsInFlight()
    {
        await using var database = await SqliteStoreTests.TestDatabase.CreateAsync();
        var registrar = new RecurringSeriesServiceTests.RecordingRegistrar();
        var store = database.Store;
        var active = PowerSchedule.Create(Now.AddHours(1), PowerActionType.Sleep);
        var inFlight = PowerSchedule.Create(Now.AddHours(2), PowerActionType.Shutdown) with
        {
            IsEnabled = false,
            Status = ScheduleStatus.PendingPowerTransition
        };
        await store.SaveScheduleAsync(active);
        await store.SaveScheduleAsync(inFlight);

        var paused = await new ScheduleBulkOperations(store, registrar, () => Now).PauseAllAsync();

        Assert.AreEqual(1, paused);
        Assert.AreEqual(ScheduleStatus.Disabled, (await store.GetScheduleAsync(active.Id))?.Status);
        Assert.AreEqual(ScheduleStatus.PendingPowerTransition, (await store.GetScheduleAsync(inFlight.Id))?.Status);
        CollectionAssert.AreEqual(new[] { active.Id }, registrar.Removed.ToArray());
        Assert.IsTrue((await store.GetHistoryAsync()).Any(item => item.EventType == HistoryEventTypes.SchedulesPaused));
    }

    private static PowerSchedule Disabled(PowerSchedule schedule) =>
        schedule with { IsEnabled = false, Status = ScheduleStatus.Disabled };

    private sealed class RecordingHelper : IPrivilegedHelperClient
    {
        public bool Fail { get; init; }
        public List<IReadOnlyList<string>> Calls { get; } = [];

        public Task RunAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken = default)
        {
            Calls.Add(arguments);
            return Fail ? Task.FromException(new InvalidOperationException("injected helper failure")) : Task.CompletedTask;
        }
    }
}
