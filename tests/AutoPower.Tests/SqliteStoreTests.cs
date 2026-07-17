using AutoPower.Core;
using AutoPower.Data;
using Microsoft.Data.Sqlite;

namespace AutoPower.Tests;

[TestClass]
public sealed class SqliteStoreTests
{
    [TestMethod]
    public async Task ScheduleAndFollowUpsRoundTripInRealDatabase()
    {
        await using var database = await TestDatabase.CreateAsync();
        var id = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var schedule = new PowerSchedule(id, DateTime.Now.AddHours(1), PowerActionType.WakeFromHibernate, true, false, now, now,
            ScheduleStatus.Pending, [new FollowUpProgram(Guid.NewGuid(), id, @"C:\Apps\OBS.exe", 0, "--startrecording", @"C:\Apps", true, 0)]);
        await database.Store.SaveScheduleAsync(schedule);

        var loaded = await database.Store.GetScheduleAsync(id);

        Assert.IsNotNull(loaded);
        Assert.AreEqual(schedule.ScheduledLocalDateTime, loaded.ScheduledLocalDateTime);
        Assert.HasCount(1, loaded.FollowUpPrograms);
        Assert.AreEqual(0, loaded.FollowUpPrograms[0].DelayAfterDesktopReadyMinutes);
        Assert.IsTrue(loaded.FollowUpPrograms[0].RunElevated);
    }

    [TestMethod]
    public async Task VersionOneDatabaseAddsElevatedFlagWithoutChangingExistingPrograms()
    {
        var directory = Path.Combine(Path.GetTempPath(), "AutoPower.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "legacy.db");
        var scheduleId = Guid.NewGuid();
        var programId = Guid.NewGuid();
        var store = new SqliteStore(path);
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
                    INSERT INTO Schedules VALUES($scheduleId, $time, $action, 1, 0, $utc, $utc, $status);
                    INSERT INTO FollowUpPrograms VALUES($programId, $scheduleId, $path, 1, NULL, NULL, 0);
                    """;
                command.Parameters.AddWithValue("$scheduleId", scheduleId.ToString("D"));
                command.Parameters.AddWithValue("$programId", programId.ToString("D"));
                command.Parameters.AddWithValue("$time", "2030-01-01T12:00:00.0000000");
                command.Parameters.AddWithValue("$utc", "2026-07-17T00:00:00.0000000+00:00");
                command.Parameters.AddWithValue("$action", (int)PowerActionType.WakeFromSleep);
                command.Parameters.AddWithValue("$status", (int)ScheduleStatus.Pending);
                command.Parameters.AddWithValue("$path", @"C:\Apps\legacy.exe");
                await command.ExecuteNonQueryAsync();
            }

            await store.InitializeAsync();
            var loaded = await store.GetScheduleAsync(scheduleId);

            Assert.IsNotNull(loaded);
            Assert.HasCount(1, loaded.FollowUpPrograms);
            Assert.IsFalse(loaded.FollowUpPrograms[0].RunElevated);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, true);
        }
    }

    [TestMethod]
    public async Task DeletingScheduleKeepsHistory()
    {
        await using var database = await TestDatabase.CreateAsync();
        var schedule = PowerSchedule.Create(DateTime.Now.AddHours(1), PowerActionType.Sleep);
        await database.Store.SaveScheduleAsync(schedule);
        await database.Store.AddHistoryAsync(schedule.Id, "Created", ResultKind.Success, schedule.ScheduledLocalDateTime, "created");
        await database.Store.DeleteScheduleAsync(schedule.Id);
        Assert.IsNull(await database.Store.GetScheduleAsync(schedule.Id));
        Assert.HasCount(1, await database.Store.GetHistoryAsync());
    }

    [TestMethod]
    public async Task PendingOperationIsDurableForRecovery()
    {
        await using var database = await TestDatabase.CreateAsync();
        var now = DateTimeOffset.UtcNow;
        var operation = new PendingOperation(Guid.NewGuid(), PendingOperationType.RegisterSchedule, Guid.NewGuid(),
            PendingOperationState.Pending, now, now, "test");
        await database.Store.SavePendingOperationAsync(operation);
        Assert.AreEqual(operation.Id, (await database.Store.GetIncompleteOperationsAsync()).Single().Id);
    }

    internal sealed class TestDatabase : IAsyncDisposable
    {
        private readonly string _directory;
        private TestDatabase(string directory, SqliteStore store) { _directory = directory; Store = store; }
        public SqliteStore Store { get; }
        public static async Task<TestDatabase> CreateAsync()
        {
            var directory = Path.Combine(Path.GetTempPath(), "AutoPower.Tests", Guid.NewGuid().ToString("N"));
            var store = new SqliteStore(Path.Combine(directory, "test.db"));
            await store.InitializeAsync();
            return new TestDatabase(directory, store);
        }
        public ValueTask DisposeAsync()
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
            return ValueTask.CompletedTask;
        }
    }
}
