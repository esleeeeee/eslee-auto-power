using System.Globalization;
using AutoPower.Core;
using Microsoft.Data.Sqlite;

namespace AutoPower.Data;

public sealed class SqliteStore
{
    private static int _providerInitialized;
    private readonly string _connectionString;

    public SqliteStore(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        InitializeProvider();
        var fullPath = Path.GetFullPath(databasePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = fullPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true,
            DefaultTimeout = 10
        }.ToString();
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            PRAGMA foreign_keys=ON;
            PRAGMA synchronous=FULL;
            PRAGMA busy_timeout=10000;

            CREATE TABLE IF NOT EXISTS SchemaInfo(
                Version INTEGER NOT NULL
            );
            INSERT INTO SchemaInfo(Version)
            SELECT 1 WHERE NOT EXISTS(SELECT 1 FROM SchemaInfo);

            CREATE TABLE IF NOT EXISTS Schedules(
                Id TEXT PRIMARY KEY,
                ScheduledLocalDateTime TEXT NOT NULL,
                ActionType INTEGER NOT NULL,
                IsEnabled INTEGER NOT NULL,
                OneTimeAutoLogonEnabled INTEGER NOT NULL,
                CreatedAtUtc TEXT NOT NULL,
                UpdatedAtUtc TEXT NOT NULL,
                Status INTEGER NOT NULL
            );

            CREATE TABLE IF NOT EXISTS FollowUpPrograms(
                Id TEXT PRIMARY KEY,
                ScheduleId TEXT NOT NULL REFERENCES Schedules(Id) ON DELETE CASCADE,
                ExecutablePath TEXT NOT NULL,
                DelayAfterDesktopReadyMinutes INTEGER NOT NULL CHECK(DelayAfterDesktopReadyMinutes >= 0),
                Arguments TEXT NULL,
                WorkingDirectory TEXT NULL,
                RunElevated INTEGER NOT NULL DEFAULT 0,
                SortOrder INTEGER NOT NULL
            );
            CREATE INDEX IF NOT EXISTS IX_FollowUpPrograms_ScheduleId
                ON FollowUpPrograms(ScheduleId, SortOrder);

            CREATE TABLE IF NOT EXISTS ExecutionHistory(
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                ScheduleId TEXT NULL,
                EventType TEXT NOT NULL,
                Result INTEGER NOT NULL,
                PlannedLocalTime TEXT NULL,
                ActualTimeUtc TEXT NOT NULL,
                UserReadableMessage TEXT NOT NULL,
                ErrorCode TEXT NULL
            );
            CREATE INDEX IF NOT EXISTS IX_ExecutionHistory_ActualTimeUtc
                ON ExecutionHistory(ActualTimeUtc DESC);

            CREATE TABLE IF NOT EXISTS CompatibilityResults(
                Capability TEXT PRIMARY KEY,
                Status INTEGER NOT NULL,
                TestedAtUtc TEXT NOT NULL,
                Detail TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS PendingOperations(
                Id TEXT PRIMARY KEY,
                Type INTEGER NOT NULL,
                ScheduleId TEXT NULL,
                State INTEGER NOT NULL,
                CreatedAtUtc TEXT NOT NULL,
                UpdatedAtUtc TEXT NOT NULL,
                Detail TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS AppSettings(
                Key TEXT PRIMARY KEY,
                Value TEXT NOT NULL
            );
            """;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await EnsureColumnAsync(connection, "FollowUpPrograms", "RunElevated",
            "INTEGER NOT NULL DEFAULT 0", cancellationToken).ConfigureAwait(false);
        await using var schema = connection.CreateCommand();
        schema.CommandText = "UPDATE SchemaInfo SET Version=2;";
        await schema.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task SaveScheduleAsync(PowerSchedule schedule, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(schedule);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = """
                    INSERT INTO Schedules(
                        Id, ScheduledLocalDateTime, ActionType, IsEnabled,
                        OneTimeAutoLogonEnabled, CreatedAtUtc, UpdatedAtUtc, Status)
                    VALUES($id, $time, $action, $enabled, $autologon, $created, $updated, $status)
                    ON CONFLICT(Id) DO UPDATE SET
                        ScheduledLocalDateTime=excluded.ScheduledLocalDateTime,
                        ActionType=excluded.ActionType,
                        IsEnabled=excluded.IsEnabled,
                        OneTimeAutoLogonEnabled=excluded.OneTimeAutoLogonEnabled,
                        UpdatedAtUtc=excluded.UpdatedAtUtc,
                        Status=excluded.Status;
                    """;
                command.Parameters.AddWithValue("$id", schedule.Id.ToString("D"));
                command.Parameters.AddWithValue("$time", Local(schedule.ScheduledLocalDateTime));
                command.Parameters.AddWithValue("$action", (int)schedule.ActionType);
                command.Parameters.AddWithValue("$enabled", schedule.IsEnabled ? 1 : 0);
                command.Parameters.AddWithValue("$autologon", schedule.OneTimeAutoLogonEnabled ? 1 : 0);
                command.Parameters.AddWithValue("$created", Utc(schedule.CreatedAtUtc));
                command.Parameters.AddWithValue("$updated", Utc(schedule.UpdatedAtUtc));
                command.Parameters.AddWithValue("$status", (int)schedule.Status);
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await using (var delete = connection.CreateCommand())
            {
                delete.Transaction = transaction;
                delete.CommandText = "DELETE FROM FollowUpPrograms WHERE ScheduleId=$scheduleId;";
                delete.Parameters.AddWithValue("$scheduleId", schedule.Id.ToString("D"));
                await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            foreach (var program in schedule.FollowUpPrograms)
            {
                await using var insert = connection.CreateCommand();
                insert.Transaction = transaction;
                insert.CommandText = """
                    INSERT INTO FollowUpPrograms(
                        Id, ScheduleId, ExecutablePath, DelayAfterDesktopReadyMinutes,
                        Arguments, WorkingDirectory, RunElevated, SortOrder)
                    VALUES($id, $scheduleId, $path, $delay, $arguments, $workingDirectory, $runElevated, $sortOrder);
                    """;
                insert.Parameters.AddWithValue("$id", program.Id.ToString("D"));
                insert.Parameters.AddWithValue("$scheduleId", schedule.Id.ToString("D"));
                insert.Parameters.AddWithValue("$path", program.ExecutablePath);
                insert.Parameters.AddWithValue("$delay", program.DelayAfterDesktopReadyMinutes);
                insert.Parameters.AddWithValue("$arguments", (object?)program.Arguments ?? DBNull.Value);
                insert.Parameters.AddWithValue("$workingDirectory", (object?)program.WorkingDirectory ?? DBNull.Value);
                insert.Parameters.AddWithValue("$runElevated", program.RunElevated ? 1 : 0);
                insert.Parameters.AddWithValue("$sortOrder", program.SortOrder);
                await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    public async Task<PowerSchedule?> GetScheduleAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var schedules = await ReadSchedulesAsync("WHERE s.Id=$id", id.ToString("D"), cancellationToken).ConfigureAwait(false);
        return schedules.SingleOrDefault();
    }

    public Task<IReadOnlyList<PowerSchedule>> GetAllSchedulesAsync(CancellationToken cancellationToken = default) =>
        ReadSchedulesAsync(string.Empty, null, cancellationToken);

    public async Task<IReadOnlyList<PowerSchedule>> GetFutureSchedulesAsync(
        DateTime nowLocal,
        CancellationToken cancellationToken = default)
    {
        var all = await GetAllSchedulesAsync(cancellationToken).ConfigureAwait(false);
        return all.Where(schedule => schedule.ScheduledLocalDateTime > nowLocal &&
                                     schedule.Status is ScheduleStatus.Pending or ScheduleStatus.Disabled)
            .OrderBy(schedule => schedule.ScheduledLocalDateTime)
            .ToArray();
    }

    public async Task DeleteScheduleAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM Schedules WHERE Id=$id;";
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task UpdateScheduleStateAsync(
        Guid id,
        bool isEnabled,
        ScheduleStatus status,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE Schedules
            SET IsEnabled=$enabled, Status=$status, UpdatedAtUtc=$updated
            WHERE Id=$id;
            """;
        command.Parameters.AddWithValue("$enabled", isEnabled ? 1 : 0);
        command.Parameters.AddWithValue("$status", (int)status);
        command.Parameters.AddWithValue("$updated", Utc(DateTimeOffset.UtcNow));
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<PendingOperation?> TryBeginPowerTransitionAsync(
        PowerSchedule schedule,
        string message,
        DateTimeOffset? startedAtUtc = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(schedule);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        var started = startedAtUtc ?? DateTimeOffset.UtcNow;
        var operation = new PendingOperation(
            Guid.NewGuid(),
            PendingOperationType.PowerTransition,
            schedule.Id,
            PendingOperationState.Pending,
            started,
            started,
            $"{schedule.ActionType} 전원 전환 결과 확인 대기");

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using (var pending = connection.CreateCommand())
            {
                pending.Transaction = transaction;
                pending.CommandText = """
                    INSERT INTO PendingOperations(Id, Type, ScheduleId, State, CreatedAtUtc, UpdatedAtUtc, Detail)
                    VALUES($id, $type, $scheduleId, $state, $created, $updated, $detail);
                    """;
                pending.Parameters.AddWithValue("$id", operation.Id.ToString("D"));
                pending.Parameters.AddWithValue("$type", (int)operation.Type);
                pending.Parameters.AddWithValue("$scheduleId", schedule.Id.ToString("D"));
                pending.Parameters.AddWithValue("$state", (int)operation.State);
                pending.Parameters.AddWithValue("$created", Utc(operation.CreatedAtUtc));
                pending.Parameters.AddWithValue("$updated", Utc(operation.UpdatedAtUtc));
                pending.Parameters.AddWithValue("$detail", operation.Detail);
                await pending.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await using (var history = connection.CreateCommand())
            {
                history.Transaction = transaction;
                history.CommandText = """
                    INSERT INTO ExecutionHistory(
                        ScheduleId, EventType, Result, PlannedLocalTime,
                        ActualTimeUtc, UserReadableMessage, ErrorCode)
                    VALUES($scheduleId, 'ExecutionStarted', $result, $planned, $actual, $message, NULL);
                    """;
                history.Parameters.AddWithValue("$scheduleId", schedule.Id.ToString("D"));
                history.Parameters.AddWithValue("$result", (int)ResultKind.Information);
                history.Parameters.AddWithValue("$planned", Local(schedule.ScheduledLocalDateTime));
                history.Parameters.AddWithValue("$actual", Utc(started));
                history.Parameters.AddWithValue("$message", message);
                await history.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            int changed;
            await using (var update = connection.CreateCommand())
            {
                update.Transaction = transaction;
                update.CommandText = """
                    UPDATE Schedules
                    SET IsEnabled=0, Status=$newStatus, UpdatedAtUtc=$updated
                    WHERE Id=$id AND ActionType=$action AND IsEnabled=1 AND Status=$pendingStatus;
                    SELECT changes();
                    """;
                update.Parameters.AddWithValue("$newStatus", (int)ScheduleStatus.PendingPowerTransition);
                update.Parameters.AddWithValue("$updated", Utc(started));
                update.Parameters.AddWithValue("$id", schedule.Id.ToString("D"));
                update.Parameters.AddWithValue("$action", (int)schedule.ActionType);
                update.Parameters.AddWithValue("$pendingStatus", (int)ScheduleStatus.Pending);
                changed = Convert.ToInt32(await update.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
            }

            if (changed != 1)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return null;
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return operation;
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    public async Task<bool> TryFinalizePowerTransitionAsync(
        Guid scheduleId,
        ScheduleStatus finalStatus,
        string eventType,
        ResultKind result,
        DateTime? plannedLocalTime,
        string message,
        IReadOnlyCollection<ScheduleStatus> expectedStatuses,
        string? errorCode = null,
        DateTimeOffset? actualTimeUtc = null,
        CancellationToken cancellationToken = default)
    {
        if (finalStatus is not (ScheduleStatus.Completed or ScheduleStatus.Failed or ScheduleStatus.Missed or ScheduleStatus.ResultUnknown))
        {
            throw new ArgumentOutOfRangeException(nameof(finalStatus));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(eventType);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        if (expectedStatuses.Count == 0)
        {
            throw new ArgumentException("At least one expected status is required.", nameof(expectedStatuses));
        }

        var actual = actualTimeUtc ?? DateTimeOffset.UtcNow;
        var statusParameters = expectedStatuses.Select((_, index) => $"$expected{index}").ToArray();
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            int changed;
            await using (var update = connection.CreateCommand())
            {
                update.Transaction = transaction;
                update.CommandText = $"""
                    UPDATE Schedules
                    SET IsEnabled=0, Status=$finalStatus, UpdatedAtUtc=$updated
                    WHERE Id=$id
                      AND Status IN ({string.Join(",", statusParameters)})
                      AND NOT EXISTS(
                          SELECT 1 FROM ExecutionHistory
                          WHERE ScheduleId=$id AND EventType IN (
                              'PowerTransitionCompleted', 'PowerTransitionFailed',
                              'PowerTransitionResultUnknown', 'PowerTransitionRecovered'));
                    SELECT changes();
                    """;
                update.Parameters.AddWithValue("$finalStatus", (int)finalStatus);
                update.Parameters.AddWithValue("$updated", Utc(actual));
                update.Parameters.AddWithValue("$id", scheduleId.ToString("D"));
                for (var index = 0; index < expectedStatuses.Count; index++)
                {
                    update.Parameters.AddWithValue(statusParameters[index], (int)expectedStatuses.ElementAt(index));
                }

                changed = Convert.ToInt32(await update.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
            }

            if (changed != 1)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return false;
            }

            await using (var pending = connection.CreateCommand())
            {
                pending.Transaction = transaction;
                pending.CommandText = """
                    UPDATE PendingOperations
                    SET State=$terminalState, UpdatedAtUtc=$updated, Detail=$detail
                    WHERE ScheduleId=$scheduleId AND Type=$type;
                    """;
                var terminalState = finalStatus == ScheduleStatus.Failed
                    ? PendingOperationState.Failed
                    : PendingOperationState.Completed;
                pending.Parameters.AddWithValue("$terminalState", (int)terminalState);
                pending.Parameters.AddWithValue("$updated", Utc(actual));
                pending.Parameters.AddWithValue("$detail", $"전원 전환 결과 확정: {finalStatus}");
                pending.Parameters.AddWithValue("$scheduleId", scheduleId.ToString("D"));
                pending.Parameters.AddWithValue("$type", (int)PendingOperationType.PowerTransition);
                await pending.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await using (var history = connection.CreateCommand())
            {
                history.Transaction = transaction;
                history.CommandText = """
                    INSERT INTO ExecutionHistory(
                        ScheduleId, EventType, Result, PlannedLocalTime,
                        ActualTimeUtc, UserReadableMessage, ErrorCode)
                    VALUES($scheduleId, $eventType, $result, $planned, $actual, $message, $errorCode);
                    """;
                history.Parameters.AddWithValue("$scheduleId", scheduleId.ToString("D"));
                history.Parameters.AddWithValue("$eventType", eventType);
                history.Parameters.AddWithValue("$result", (int)result);
                history.Parameters.AddWithValue("$planned", plannedLocalTime is null ? DBNull.Value : Local(plannedLocalTime.Value));
                history.Parameters.AddWithValue("$actual", Utc(actual));
                history.Parameters.AddWithValue("$message", message);
                history.Parameters.AddWithValue("$errorCode", (object?)errorCode ?? DBNull.Value);
                await history.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    public async Task<bool> RecordPowerTransitionStageAsync(
        Guid operationId,
        Guid scheduleId,
        PendingOperationState operationState,
        string detail,
        string eventType,
        ResultKind result,
        DateTime? plannedLocalTime,
        string message,
        string? errorCode = null,
        DateTimeOffset? actualTimeUtc = null,
        CancellationToken cancellationToken = default)
    {
        if (operationState is not (PendingOperationState.Pending or PendingOperationState.Compensating))
        {
            throw new ArgumentOutOfRangeException(nameof(operationState));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(detail);
        ArgumentException.ThrowIfNullOrWhiteSpace(eventType);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        var actual = actualTimeUtc ?? DateTimeOffset.UtcNow;

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            int changed;
            await using (var pending = connection.CreateCommand())
            {
                pending.Transaction = transaction;
                pending.CommandText = """
                    UPDATE PendingOperations
                    SET State=$state, UpdatedAtUtc=$updated, Detail=$detail
                    WHERE Id=$operationId AND ScheduleId=$scheduleId AND Type=$type
                      AND State IN ($pending, $compensating);
                    SELECT changes();
                    """;
                pending.Parameters.AddWithValue("$state", (int)operationState);
                pending.Parameters.AddWithValue("$updated", Utc(actual));
                pending.Parameters.AddWithValue("$detail", detail);
                pending.Parameters.AddWithValue("$operationId", operationId.ToString("D"));
                pending.Parameters.AddWithValue("$scheduleId", scheduleId.ToString("D"));
                pending.Parameters.AddWithValue("$type", (int)PendingOperationType.PowerTransition);
                pending.Parameters.AddWithValue("$pending", (int)PendingOperationState.Pending);
                pending.Parameters.AddWithValue("$compensating", (int)PendingOperationState.Compensating);
                changed = Convert.ToInt32(
                    await pending.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
                    CultureInfo.InvariantCulture);
            }

            if (changed != 1)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return false;
            }

            await using (var history = connection.CreateCommand())
            {
                history.Transaction = transaction;
                history.CommandText = """
                    INSERT INTO ExecutionHistory(
                        ScheduleId, EventType, Result, PlannedLocalTime,
                        ActualTimeUtc, UserReadableMessage, ErrorCode)
                    VALUES($scheduleId, $eventType, $result, $planned, $actual, $message, $errorCode);
                    """;
                history.Parameters.AddWithValue("$scheduleId", scheduleId.ToString("D"));
                history.Parameters.AddWithValue("$eventType", eventType);
                history.Parameters.AddWithValue("$result", (int)result);
                history.Parameters.AddWithValue("$planned", plannedLocalTime is null ? DBNull.Value : Local(plannedLocalTime.Value));
                history.Parameters.AddWithValue("$actual", Utc(actual));
                history.Parameters.AddWithValue("$message", message);
                history.Parameters.AddWithValue("$errorCode", (object?)errorCode ?? DBNull.Value);
                await history.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    public async Task<long> AddHistoryAsync(
        Guid? scheduleId,
        string eventType,
        ResultKind result,
        DateTime? plannedLocalTime,
        string userReadableMessage,
        string? errorCode = null,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO ExecutionHistory(
                ScheduleId, EventType, Result, PlannedLocalTime,
                ActualTimeUtc, UserReadableMessage, ErrorCode)
            VALUES($scheduleId, $eventType, $result, $planned, $actual, $message, $errorCode);
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$scheduleId", scheduleId is null ? DBNull.Value : scheduleId.Value.ToString("D"));
        command.Parameters.AddWithValue("$eventType", eventType);
        command.Parameters.AddWithValue("$result", (int)result);
        command.Parameters.AddWithValue("$planned", plannedLocalTime is null ? DBNull.Value : Local(plannedLocalTime.Value));
        command.Parameters.AddWithValue("$actual", Utc(DateTimeOffset.UtcNow));
        command.Parameters.AddWithValue("$message", userReadableMessage);
        command.Parameters.AddWithValue("$errorCode", (object?)errorCode ?? DBNull.Value);
        return (long)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) ?? 0L);
    }

    public async Task<IReadOnlyList<ExecutionHistory>> GetHistoryAsync(CancellationToken cancellationToken = default)
    {
        var result = new List<ExecutionHistory>();
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, ScheduleId, EventType, Result, PlannedLocalTime,
                   ActualTimeUtc, UserReadableMessage, ErrorCode
            FROM ExecutionHistory
            ORDER BY ActualTimeUtc DESC, Id DESC;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(new ExecutionHistory(
                reader.GetInt64(0),
                reader.IsDBNull(1) ? null : Guid.Parse(reader.GetString(1)),
                reader.GetString(2),
                (ResultKind)reader.GetInt32(3),
                reader.IsDBNull(4) ? null : ParseLocal(reader.GetString(4)),
                ParseUtc(reader.GetString(5)),
                reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetString(7)));
        }

        return result;
    }

    public async Task ClearHistoryAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM ExecutionHistory;";
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task SaveCompatibilityAsync(CompatibilityResult result, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO CompatibilityResults(Capability, Status, TestedAtUtc, Detail)
            VALUES($capability, $status, $tested, $detail)
            ON CONFLICT(Capability) DO UPDATE SET
                Status=excluded.Status, TestedAtUtc=excluded.TestedAtUtc, Detail=excluded.Detail;
            """;
        command.Parameters.AddWithValue("$capability", result.Capability);
        command.Parameters.AddWithValue("$status", (int)result.Status);
        command.Parameters.AddWithValue("$tested", Utc(result.TestedAtUtc));
        command.Parameters.AddWithValue("$detail", result.Detail);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<CompatibilityResult>> GetCompatibilityAsync(CancellationToken cancellationToken = default)
    {
        var results = new List<CompatibilityResult>();
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Capability, Status, TestedAtUtc, Detail FROM CompatibilityResults ORDER BY Capability;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(new CompatibilityResult(
                reader.GetString(0),
                (CapabilityStatus)reader.GetInt32(1),
                ParseUtc(reader.GetString(2)),
                reader.GetString(3)));
        }

        return results;
    }

    public async Task DeleteCompatibilityAsync(string capability, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(capability);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM CompatibilityResults WHERE Capability=$capability;";
        command.Parameters.AddWithValue("$capability", capability);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task SavePendingOperationAsync(PendingOperation operation, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO PendingOperations(Id, Type, ScheduleId, State, CreatedAtUtc, UpdatedAtUtc, Detail)
            VALUES($id, $type, $scheduleId, $state, $created, $updated, $detail)
            ON CONFLICT(Id) DO UPDATE SET
                State=excluded.State, UpdatedAtUtc=excluded.UpdatedAtUtc, Detail=excluded.Detail;
            """;
        command.Parameters.AddWithValue("$id", operation.Id.ToString("D"));
        command.Parameters.AddWithValue("$type", (int)operation.Type);
        command.Parameters.AddWithValue("$scheduleId", operation.ScheduleId is null ? DBNull.Value : operation.ScheduleId.Value.ToString("D"));
        command.Parameters.AddWithValue("$state", (int)operation.State);
        command.Parameters.AddWithValue("$created", Utc(operation.CreatedAtUtc));
        command.Parameters.AddWithValue("$updated", Utc(operation.UpdatedAtUtc));
        command.Parameters.AddWithValue("$detail", operation.Detail);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<PendingOperation>> GetIncompleteOperationsAsync(CancellationToken cancellationToken = default)
    {
        var operations = new List<PendingOperation>();
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, Type, ScheduleId, State, CreatedAtUtc, UpdatedAtUtc, Detail
            FROM PendingOperations
            WHERE State <> $completed
            ORDER BY CreatedAtUtc;
            """;
        command.Parameters.AddWithValue("$completed", (int)PendingOperationState.Completed);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            operations.Add(new PendingOperation(
                Guid.Parse(reader.GetString(0)),
                (PendingOperationType)reader.GetInt32(1),
                reader.IsDBNull(2) ? null : Guid.Parse(reader.GetString(2)),
                (PendingOperationState)reader.GetInt32(3),
                ParseUtc(reader.GetString(4)),
                ParseUtc(reader.GetString(5)),
                reader.GetString(6)));
        }

        return operations;
    }

    public async Task SetSettingAsync(string key, string value, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO AppSettings(Key, Value) VALUES($key, $value)
            ON CONFLICT(Key) DO UPDATE SET Value=excluded.Value;
            """;
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$value", value);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<string?> GetSettingAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Value FROM AppSettings WHERE Key=$key;";
        command.Parameters.AddWithValue("$key", key);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
    }

    public async Task DeleteAllAppDataAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        foreach (var table in new[]
                 {
                     "FollowUpPrograms", "Schedules", "ExecutionHistory", "CompatibilityResults",
                     "PendingOperations", "AppSettings"
                 })
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $"DELETE FROM {table};";
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<PowerSchedule>> ReadSchedulesAsync(
        string whereClause,
        string? id,
        CancellationToken cancellationToken)
    {
        var rows = new List<(PowerSchedule Schedule, List<FollowUpProgram> Programs)>();
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = $"""
                SELECT s.Id, s.ScheduledLocalDateTime, s.ActionType, s.IsEnabled,
                       s.OneTimeAutoLogonEnabled, s.CreatedAtUtc, s.UpdatedAtUtc, s.Status
                FROM Schedules s
                {whereClause}
                ORDER BY s.ScheduledLocalDateTime, s.Id;
                """;
            if (id is not null)
            {
                command.Parameters.AddWithValue("$id", id);
            }

            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                rows.Add((new PowerSchedule(
                    Guid.Parse(reader.GetString(0)),
                    ParseLocal(reader.GetString(1)),
                    (PowerActionType)reader.GetInt32(2),
                    reader.GetInt32(3) != 0,
                    reader.GetInt32(4) != 0,
                    ParseUtc(reader.GetString(5)),
                    ParseUtc(reader.GetString(6)),
                    (ScheduleStatus)reader.GetInt32(7),
                    []), []));
            }
        }

        foreach (var row in rows)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT Id, ExecutablePath, DelayAfterDesktopReadyMinutes,
                       Arguments, WorkingDirectory, RunElevated, SortOrder
                FROM FollowUpPrograms
                WHERE ScheduleId=$scheduleId
                ORDER BY SortOrder, Id;
                """;
            command.Parameters.AddWithValue("$scheduleId", row.Schedule.Id.ToString("D"));
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                row.Programs.Add(new FollowUpProgram(
                    Guid.Parse(reader.GetString(0)),
                    row.Schedule.Id,
                    reader.GetString(1),
                    reader.GetInt32(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4),
                    reader.GetInt32(5) != 0,
                    reader.GetInt32(6)));
            }
        }

        return rows.Select(row => row.Schedule with { FollowUpPrograms = row.Programs }).ToArray();
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=10000;";
        await pragma.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }

    private static async Task EnsureColumnAsync(
        SqliteConnection connection,
        string table,
        string column,
        string definition,
        CancellationToken cancellationToken)
    {
        var exists = false;
        await using (var info = connection.CreateCommand())
        {
            info.CommandText = $"PRAGMA table_info({table});";
            await using var reader = await info.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
                {
                    exists = true;
                    break;
                }
            }
        }

        if (!exists)
        {
            await using var alter = connection.CreateCommand();
            alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {definition};";
            await alter.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static void InitializeProvider()
    {
        if (Interlocked.Exchange(ref _providerInitialized, 1) == 0)
        {
            SQLitePCL.raw.SetProvider(new SQLitePCL.SQLite3Provider_winsqlite3());
            SQLitePCL.raw.FreezeProvider(true);
        }
    }

    private static string Local(DateTime value) =>
        DateTime.SpecifyKind(value, DateTimeKind.Unspecified).ToString("O", CultureInfo.InvariantCulture);

    private static DateTime ParseLocal(string value) =>
        DateTime.SpecifyKind(DateTime.ParseExact(value, "O", CultureInfo.InvariantCulture, DateTimeStyles.None), DateTimeKind.Unspecified);

    private static string Utc(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseUtc(string value) =>
        DateTimeOffset.ParseExact(value, "O", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal).ToUniversalTime();
}
