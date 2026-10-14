using AutoPower.Core;
using AutoPower.Data;
using Microsoft.Data.Sqlite;

namespace AutoPower.Tests;

[TestClass]
public sealed class RecurringSeriesStoreTests
{
    [TestMethod]
    public async Task NameRecurrenceAndSeriesRoundTripThroughSaveLoadAndUpdate()
    {
        await using var database = await SqliteStoreTests.TestDatabase.CreateAsync();
        var time = DateTime.Now.AddHours(2);
        var rule = RecurrenceRule.CreateWeekly(RecurrenceDays.Monday | RecurrenceDays.Friday, time.TimeOfDay);
        var schedule = PowerSchedule.Create(time, PowerActionType.Hibernate, name: "주간 절전", recurrence: rule);

        await database.Store.SaveScheduleAsync(schedule);
        var loaded = await database.Store.GetScheduleAsync(schedule.Id);

        Assert.IsNotNull(loaded);
        Assert.AreEqual("주간 절전", loaded.Name);
        Assert.AreEqual(rule, loaded.Recurrence);
        Assert.AreEqual(schedule.SeriesId, loaded.SeriesId);

        await database.Store.SaveScheduleAsync(loaded with { Name = "변경", Recurrence = RecurrenceRule.CreateDaily(time.TimeOfDay) });
        var updated = await database.Store.GetScheduleAsync(schedule.Id);

        Assert.AreEqual("변경", updated?.Name);
        Assert.AreEqual(RecurrenceKind.Daily, updated?.Recurrence?.Kind);
        Assert.AreEqual(schedule.SeriesId, updated?.SeriesId);
    }

    [TestMethod]
    public async Task VersionOneDatabaseUpgradesInPlaceWithNonRecurringDefaults()
    {
        var directory = Path.Combine(Path.GetTempPath(), "AutoPower.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "legacy.db");
        var scheduleId = Guid.NewGuid();
        try
        {
            await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path }.ToString()))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = """
                    CREATE TABLE SchemaInfo(Version INTEGER NOT NULL);
                    INSERT INTO SchemaInfo VALUES(1);
                    CREATE TABLE Schedules(
                        Id TEXT PRIMARY KEY, ScheduledLocalDateTime TEXT NOT NULL, ActionType INTEGER NOT NULL,
                        IsEnabled INTEGER NOT NULL, OneTimeAutoLogonEnabled INTEGER NOT NULL,
                        CreatedAtUtc TEXT NOT NULL, UpdatedAtUtc TEXT NOT NULL, Status INTEGER NOT NULL);
                    CREATE TABLE FollowUpPrograms(
                        Id TEXT PRIMARY KEY, ScheduleId TEXT NOT NULL REFERENCES Schedules(Id) ON DELETE CASCADE,
                        ExecutablePath TEXT NOT NULL, DelayAfterDesktopReadyMinutes INTEGER NOT NULL,
                        Arguments TEXT NULL, WorkingDirectory TEXT NULL, SortOrder INTEGER NOT NULL);
                    CREATE TABLE ExecutionHistory(
                        Id INTEGER PRIMARY KEY AUTOINCREMENT, ScheduleId TEXT NULL, EventType TEXT NOT NULL,
                        Result INTEGER NOT NULL, PlannedLocalTime TEXT NULL, ActualTimeUtc TEXT NOT NULL,
                        UserReadableMessage TEXT NOT NULL, ErrorCode TEXT NULL);
                    INSERT INTO Schedules VALUES($scheduleId, '2030-01-01T12:00:00.0000000', 1, 1, 0,
                        '2026-07-17T00:00:00.0000000+00:00', '2026-07-17T00:00:00.0000000+00:00', 0);
                    INSERT INTO ExecutionHistory(ScheduleId, EventType, Result, ActualTimeUtc, UserReadableMessage)
                        VALUES($scheduleId, 'ScheduleCreated', 1, '2026-07-17T00:00:00.0000000+00:00', 'created');
                    """;
                command.Parameters.AddWithValue("$scheduleId", scheduleId.ToString("D"));
                await command.ExecuteNonQueryAsync();
            }

            var store = new SqliteStore(path);
            await store.InitializeAsync();
            await store.InitializeAsync();
            var loaded = await store.GetScheduleAsync(scheduleId);

            Assert.IsNotNull(loaded);
            Assert.AreEqual(new DateTime(2030, 1, 1, 12, 0, 0), loaded.ScheduledLocalDateTime);
            Assert.AreEqual(PowerActionType.Shutdown, loaded.ActionType);
            Assert.IsNull(loaded.Name);
            Assert.IsNull(loaded.Recurrence);
            Assert.IsNull(loaded.SeriesId);
            Assert.HasCount(1, await store.GetHistoryAsync());

            await store.SaveScheduleAsync(loaded with { Name = "이전 예약", Recurrence = RecurrenceRule.CreateDaily(new TimeSpan(12, 0, 0)), SeriesId = Guid.NewGuid() });
            var updated = await store.GetScheduleAsync(scheduleId);
            Assert.AreEqual("이전 예약", updated?.Name);
            Assert.AreEqual(RecurrenceKind.Daily, updated?.Recurrence?.Kind);

            await using var check = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path }.ToString());
            await check.OpenAsync();
            await using var version = check.CreateCommand();
            version.CommandText = "SELECT Version FROM SchemaInfo;";
            Assert.AreEqual(3L, await version.ExecuteScalarAsync());
        }
        finally
        {
            SqliteStoreTests.TestDatabase.ReleasePools(path);
            Directory.Delete(directory, true);
        }
    }

    [TestMethod]
    public async Task AdvanceCopiesTheOccurrenceAndIsIdempotent()
    {
        await using var database = await SqliteStoreTests.TestDatabase.CreateAsync();
        var now = DateTime.Now;
        var occurrence = await SaveExecutedOccurrenceAsync(database.Store, now.AddMinutes(-1), PowerActionType.WakeFromSleep);

        var next = await database.Store.TryAdvanceSeriesAsync(occurrence.Id, now);
        var again = await database.Store.TryAdvanceSeriesAsync(occurrence.Id, now);

        Assert.IsNotNull(next);
        Assert.IsNull(again);
        var loaded = await database.Store.GetScheduleAsync(next.Id);
        Assert.IsNotNull(loaded);
        Assert.AreEqual(ScheduleStatus.Pending, loaded.Status);
        Assert.IsTrue(loaded.IsEnabled);
        Assert.AreEqual(occurrence.SeriesId, loaded.SeriesId);
        Assert.AreEqual(occurrence.Name, loaded.Name);
        Assert.AreEqual(occurrence.Recurrence, loaded.Recurrence);
        Assert.AreEqual(occurrence.OneTimeAutoLogonEnabled, loaded.OneTimeAutoLogonEnabled);
        Assert.AreEqual(occurrence.ScheduledLocalDateTime.AddDays(1), loaded.ScheduledLocalDateTime);
        Assert.HasCount(1, loaded.FollowUpPrograms);
        Assert.AreNotEqual(occurrence.FollowUpPrograms[0].Id, loaded.FollowUpPrograms[0].Id);
        Assert.AreEqual(occurrence.FollowUpPrograms[0].ExecutablePath, loaded.FollowUpPrograms[0].ExecutablePath);
        Assert.HasCount(1, (await database.Store.GetHistoryAsync()).Where(item => item.EventType == HistoryEventTypes.RecurrenceAdvanced));
    }

    [TestMethod]
    public async Task ConcurrentAdvancesCreateExactlyOneFutureOccurrence()
    {
        await using var database = await SqliteStoreTests.TestDatabase.CreateAsync();
        var now = DateTime.Now;
        var occurrence = await SaveExecutedOccurrenceAsync(database.Store, now.AddMinutes(-1), PowerActionType.Shutdown);
        var otherProcess = new SqliteStore(database.DatabasePath);

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(index => Task.Run(() =>
            (index % 2 == 0 ? database.Store : otherProcess).TryAdvanceSeriesAsync(occurrence.Id, now))));

        Assert.HasCount(1, results.Where(result => result is not null));
        var future = (await database.Store.GetAllSchedulesAsync())
            .Where(item => item.SeriesId == occurrence.SeriesId && item.Status == ScheduleStatus.Pending)
            .ToArray();
        Assert.HasCount(1, future);
    }

    [TestMethod]
    public async Task LiveOccurrenceIsNeverAdvancedAndLaterOccurrenceBlocksOlderSource()
    {
        await using var database = await SqliteStoreTests.TestDatabase.CreateAsync();
        var now = DateTime.Now;
        var live = PowerSchedule.Create(now.AddHours(1), PowerActionType.Sleep,
            recurrence: RecurrenceRule.CreateDaily(now.AddHours(1).TimeOfDay));
        await database.Store.SaveScheduleAsync(live);
        var older = live.CreateNextOccurrence(now.AddDays(-1), DateTimeOffset.UtcNow) with { IsEnabled = false, Status = ScheduleStatus.Completed };
        await database.Store.SaveScheduleAsync(older);

        Assert.IsNull(await database.Store.TryAdvanceSeriesAsync(live.Id, now));
        Assert.IsNull(await database.Store.TryAdvanceSeriesAsync(older.Id, now));
        Assert.HasCount(2, await database.Store.GetAllSchedulesAsync());
    }

    [TestMethod]
    public async Task DeletingTheLiveOccurrenceEndsTheSeries()
    {
        await using var database = await SqliteStoreTests.TestDatabase.CreateAsync();
        var now = DateTime.Now;
        var executed = await SaveExecutedOccurrenceAsync(database.Store, now.AddMinutes(-1), PowerActionType.Shutdown);
        var live = await database.Store.TryAdvanceSeriesAsync(executed.Id, now);
        Assert.IsNotNull(live);

        await database.Store.DeleteScheduleAsync(live.Id);

        Assert.IsNull((await database.Store.GetScheduleAsync(executed.Id))?.Recurrence);
        Assert.IsNull(await database.Store.TryAdvanceSeriesAsync(executed.Id, now));
    }

    [TestMethod]
    public async Task SavingAnInFlightOrFinishedScheduleBackToLiveIsRejected()
    {
        await using var database = await SqliteStoreTests.TestDatabase.CreateAsync();
        var schedule = PowerSchedule.Create(DateTime.Now.AddMinutes(1), PowerActionType.Hibernate);
        await database.Store.SaveScheduleAsync(schedule);
        Assert.IsNotNull(await database.Store.TryBeginPowerTransitionAsync(schedule, "시작"));

        await Assert.ThrowsExactlyAsync<ScheduleStateConflictException>(() =>
            database.Store.SaveScheduleAsync(schedule with { ScheduledLocalDateTime = schedule.ScheduledLocalDateTime.AddMinutes(10) }));

        var loaded = await database.Store.GetScheduleAsync(schedule.Id);
        Assert.AreEqual(ScheduleStatus.PendingPowerTransition, loaded?.Status);
        Assert.AreEqual(schedule.ScheduledLocalDateTime, loaded?.ScheduledLocalDateTime);
    }

    [TestMethod]
    public async Task FinishedRowCanOnlyBeDisabledWhenItIsNotPartOfASeries()
    {
        await using var database = await SqliteStoreTests.TestDatabase.CreateAsync();
        var oneTime = PowerSchedule.Create(DateTime.Now.AddHours(-1), PowerActionType.PowerOn) with { Status = ScheduleStatus.Completed };
        await database.Store.SaveScheduleAsync(oneTime);
        var occurrence = await SaveExecutedOccurrenceAsync(database.Store, DateTime.Now.AddHours(-1), PowerActionType.Sleep, ScheduleStatus.Completed);

        await Assert.ThrowsExactlyAsync<ScheduleStateConflictException>(() =>
            database.Store.SaveScheduleAsync(oneTime with { Status = ScheduleStatus.Pending }));
        await database.Store.SaveScheduleAsync(oneTime with { IsEnabled = false, Status = ScheduleStatus.Disabled });
        await Assert.ThrowsExactlyAsync<ScheduleStateConflictException>(() =>
            database.Store.SaveScheduleAsync(occurrence with { Status = ScheduleStatus.Disabled }));

        Assert.AreEqual(ScheduleStatus.Disabled, (await database.Store.GetScheduleAsync(oneTime.Id))?.Status);
        Assert.AreEqual(ScheduleStatus.Completed, (await database.Store.GetScheduleAsync(occurrence.Id))?.Status);
    }

    [TestMethod]
    public async Task BeginRequiresTheTimeThatWasValidated()
    {
        await using var database = await SqliteStoreTests.TestDatabase.CreateAsync();
        var schedule = PowerSchedule.Create(DateTime.Now.AddMinutes(1), PowerActionType.Sleep);
        await database.Store.SaveScheduleAsync(schedule);
        await database.Store.SaveScheduleAsync(schedule with { ScheduledLocalDateTime = schedule.ScheduledLocalDateTime.AddMinutes(10) });

        Assert.IsNull(await database.Store.TryBeginPowerTransitionAsync(schedule, "이전 시각으로 시작"));
        Assert.AreEqual(ScheduleStatus.Pending, (await database.Store.GetScheduleAsync(schedule.Id))?.Status);
    }

    [TestMethod]
    public async Task ConditionalTransitionLeavesUnexpectedStatusesAlone()
    {
        await using var database = await SqliteStoreTests.TestDatabase.CreateAsync();
        var schedule = PowerSchedule.Create(DateTime.Now.AddMinutes(1), PowerActionType.Shutdown);
        await database.Store.SaveScheduleAsync(schedule);
        Assert.IsNotNull(await database.Store.TryBeginPowerTransitionAsync(schedule, "시작"));

        Assert.IsFalse(await database.Store.TryTransitionScheduleAsync(
            schedule.Id, [ScheduleStatus.Pending, ScheduleStatus.Disabled], false, ScheduleStatus.Skipped));
        Assert.AreEqual(ScheduleStatus.PendingPowerTransition, (await database.Store.GetScheduleAsync(schedule.Id))?.Status);
    }

    internal static async Task<PowerSchedule> SaveExecutedOccurrenceAsync(
        SqliteStore store,
        DateTime scheduled,
        PowerActionType action,
        ScheduleStatus status = ScheduleStatus.PendingPowerTransition)
    {
        var programs = PowerSchedulePolicy.IsWakeSchedule(action)
            ? new[] { new FollowUpProgram(Guid.NewGuid(), Guid.Empty, @"C:\Apps\OBS.exe", 0, null, null, false, 0) }
            : [];
        var schedule = PowerSchedule.Create(
            scheduled,
            action,
            followUpPrograms: programs,
            name: "반복",
            recurrence: RecurrenceRule.CreateDaily(scheduled.TimeOfDay)) with
        {
            IsEnabled = false,
            Status = status
        };
        await store.SaveScheduleAsync(schedule);
        return schedule;
    }
}
