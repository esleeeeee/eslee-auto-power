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
                Status INTEGER NOT NULL,
                Name TEXT NULL,
                RecurrenceKind INTEGER NOT NULL DEFAULT 0,
                RecurrenceDays INTEGER NOT NULL DEFAULT 0,
                RecurrenceTimeTicks INTEGER NULL,
                SeriesId TEXT NULL
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

        // Column migrations run in one write transaction so that the app and the helper can initialize the
        // same database concurrently without racing on ALTER TABLE.
        await using var transaction = connection.BeginTransaction(deferred: false);
        await EnsureColumnAsync(connection, transaction, "FollowUpPrograms", "RunElevated",
            "INTEGER NOT NULL DEFAULT 0", cancellationToken).ConfigureAwait(false);
        await EnsureColumnAsync(connection, transaction, "Schedules", "Name",
            "TEXT NULL", cancellationToken).ConfigureAwait(false);
        await EnsureColumnAsync(connection, transaction, "Schedules", "RecurrenceKind",
            "INTEGER NOT NULL DEFAULT 0", cancellationToken).ConfigureAwait(false);
        await EnsureColumnAsync(connection, transaction, "Schedules", "RecurrenceDays",
            "INTEGER NOT NULL DEFAULT 0", cancellationToken).ConfigureAwait(false);
        await EnsureColumnAsync(connection, transaction, "Schedules", "RecurrenceTimeTicks",
            "INTEGER NULL", cancellationToken).ConfigureAwait(false);
        await EnsureColumnAsync(connection, transaction, "Schedules", "SeriesId",
            "TEXT NULL", cancellationToken).ConfigureAwait(false);
        await using (var schema = connection.CreateCommand())
        {
            schema.Transaction = transaction;
            schema.CommandText = """
                CREATE INDEX IF NOT EXISTS IX_Schedules_SeriesId
                    ON Schedules(SeriesId, ScheduledLocalDateTime);
                UPDATE SchemaInfo SET Version=3;
                """;
            await schema.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Inserts or updates a schedule. An existing row that is in flight (PendingPowerTransition) is never
    /// overwritten, and a finished row is never made Pending again (nor Disabled when it belongs to a recurring
    /// series, which would break the one-live-occurrence rule); such a write throws
    /// <see cref="ScheduleStateConflictException"/> and changes nothing.
    /// </summary>
    public async Task SaveScheduleAsync(PowerSchedule schedule, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(schedule);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: false);
        try
        {
            Guid? previousSeriesId = null;
            await using (var previous = connection.CreateCommand())
            {
                previous.Transaction = transaction;
                previous.CommandText = "SELECT SeriesId FROM Schedules WHERE Id=$id;";
                previous.Parameters.AddWithValue("$id", schedule.Id.ToString("D"));
                if (await previous.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is string value)
                {
                    previousSeriesId = Guid.Parse(value);
                }
            }

            int changed;
            await using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = """
                    INSERT INTO Schedules(
                        Id, ScheduledLocalDateTime, ActionType, IsEnabled,
                        OneTimeAutoLogonEnabled, CreatedAtUtc, UpdatedAtUtc, Status,
                        Name, RecurrenceKind, RecurrenceDays, RecurrenceTimeTicks, SeriesId)
                    VALUES($id, $time, $action, $enabled, $autologon, $created, $updated, $status,
                        $name, $recurrenceKind, $recurrenceDays, $recurrenceTime, $seriesId)
                    ON CONFLICT(Id) DO UPDATE SET
                        ScheduledLocalDateTime=excluded.ScheduledLocalDateTime,
                        ActionType=excluded.ActionType,
                        IsEnabled=excluded.IsEnabled,
                        OneTimeAutoLogonEnabled=excluded.OneTimeAutoLogonEnabled,
                        UpdatedAtUtc=excluded.UpdatedAtUtc,
                        Status=excluded.Status,
                        Name=excluded.Name,
                        RecurrenceKind=excluded.RecurrenceKind,
                        RecurrenceDays=excluded.RecurrenceDays,
                        RecurrenceTimeTicks=excluded.RecurrenceTimeTicks,
                        SeriesId=excluded.SeriesId
                    WHERE Schedules.Status <> $inFlight
                      AND NOT (Schedules.Status IN ($completed, $failed, $skipped, $missed, $resultUnknown)
                               AND (excluded.Status = $pending
                                    OR (excluded.Status = $disabled AND Schedules.SeriesId IS NOT NULL)));
                    SELECT changes();
                    """;
                AddScheduleParameters(command, schedule);
                AddStatusGuardParameters(command);
                changed = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
            }

            if (changed != 1)
            {
                throw new ScheduleStateConflictException("이미 실행이 시작됐거나 끝난 예약은 변경할 수 없습니다.");
            }

            await ReplaceFollowUpProgramsAsync(connection, transaction, schedule, cancellationToken).ConfigureAwait(false);
            if (previousSeriesId is Guid oldSeries && oldSeries != schedule.SeriesId)
            {
                await EndSeriesAsync(connection, transaction, oldSeries, schedule.Id, cancellationToken).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Creates the next occurrence of the series that <paramref name="occurrenceId"/> belongs to, unless the
    /// series already has a live (Pending or Disabled) row or a later occurrence. The check and the insert run
    /// in one immediate write transaction, so concurrent callers (execute-power, wake handling,
    /// startup-reconcile) create at most one row. Returns the new row, or null when nothing was created.
    /// </summary>
    public async Task<PowerSchedule?> TryAdvanceSeriesAsync(
        Guid occurrenceId,
        DateTime nowLocal,
        DateTimeOffset? nowUtc = null,
        CancellationToken cancellationToken = default)
    {
        var created = nowUtc ?? DateTimeOffset.UtcNow;
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: false);
        try
        {
            var source = (await ReadSchedulesAsync(
                    connection, transaction, "WHERE s.Id=$id", ("$id", occurrenceId.ToString("D")), cancellationToken)
                .ConfigureAwait(false)).SingleOrDefault();
            if (source?.Recurrence is not { } rule ||
                source.SeriesId is not Guid seriesId ||
                !RecurrencePolicy.IsUsable(rule) ||
                source.Status is ScheduleStatus.Pending or ScheduleStatus.Disabled)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return null;
            }

            var series = await ReadSchedulesAsync(
                connection, transaction, "WHERE s.SeriesId=$series", ("$series", seriesId.ToString("D")), cancellationToken)
                .ConfigureAwait(false);
            if (series.Any(item => item.Id != source.Id &&
                                   (item.Status is ScheduleStatus.Pending or ScheduleStatus.Disabled ||
                                    item.ScheduledLocalDateTime > source.ScheduledLocalDateTime)))
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return null;
            }

            var next = source.CreateNextOccurrence(
                RecurrencePolicy.NextOccurrenceAfter(rule, source.ScheduledLocalDateTime, nowLocal),
                created);
            await using (var insert = connection.CreateCommand())
            {
                insert.Transaction = transaction;
                insert.CommandText = """
                    INSERT INTO Schedules(
                        Id, ScheduledLocalDateTime, ActionType, IsEnabled,
                        OneTimeAutoLogonEnabled, CreatedAtUtc, UpdatedAtUtc, Status,
                        Name, RecurrenceKind, RecurrenceDays, RecurrenceTimeTicks, SeriesId)
                    VALUES($id, $time, $action, $enabled, $autologon, $created, $updated, $status,
                        $name, $recurrenceKind, $recurrenceDays, $recurrenceTime, $seriesId);
                    """;
                AddScheduleParameters(insert, next);
                await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await ReplaceFollowUpProgramsAsync(connection, transaction, next, cancellationToken).ConfigureAwait(false);
            await using (var history = connection.CreateCommand())
            {
                history.Transaction = transaction;
                history.CommandText = """
                    INSERT INTO ExecutionHistory(
                        ScheduleId, EventType, Result, PlannedLocalTime,
                        ActualTimeUtc, UserReadableMessage, ErrorCode)
                    VALUES($scheduleId, $eventType, $result, $planned, $actual, $message, NULL);
                    """;
                history.Parameters.AddWithValue("$scheduleId", next.Id.ToString("D"));
                history.Parameters.AddWithValue("$eventType", HistoryEventTypes.RecurrenceAdvanced);
                history.Parameters.AddWithValue("$result", (int)ResultKind.Information);
                history.Parameters.AddWithValue("$planned", Local(next.ScheduledLocalDateTime));
                history.Parameters.AddWithValue("$actual", Utc(created));
                history.Parameters.AddWithValue("$message", string.Create(CultureInfo.InvariantCulture,
                    $"반복 예약의 다음 회차를 만들었습니다. 다음 시각: {next.ScheduledLocalDateTime:yyyy.MM.dd HH:mm}"));
                await history.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return next;
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Conditionally changes the enabled flag, status and optionally the time of one schedule. Returns false
    /// without changing anything when the row is not in one of <paramref name="expectedStatuses"/> (or no longer
    /// has <paramref name="expectedScheduledLocal"/>), which protects in-flight power transitions.
    /// </summary>
    public async Task<bool> TryTransitionScheduleAsync(
        Guid id,
        IReadOnlyCollection<ScheduleStatus> expectedStatuses,
        bool isEnabled,
        ScheduleStatus status,
        DateTime? expectedScheduledLocal = null,
        DateTime? newScheduledLocal = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expectedStatuses);
        if (expectedStatuses.Count == 0)
        {
            throw new ArgumentException("At least one expected status is required.", nameof(expectedStatuses));
        }

        var statusParameters = expectedStatuses.Select((_, index) => $"$expected{index}").ToArray();
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            UPDATE Schedules
            SET IsEnabled=$enabled, Status=$status, UpdatedAtUtc=$updated
                {(newScheduledLocal is null ? string.Empty : ", ScheduledLocalDateTime=$newTime")}
            WHERE Id=$id
              AND Status IN ({string.Join(",", statusParameters)})
              {(expectedScheduledLocal is null ? string.Empty : "AND ScheduledLocalDateTime=$expectedTime")};
            SELECT changes();
            """;
        command.Parameters.AddWithValue("$enabled", isEnabled ? 1 : 0);
        command.Parameters.AddWithValue("$status", (int)status);
        command.Parameters.AddWithValue("$updated", Utc(DateTimeOffset.UtcNow));
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        for (var index = 0; index < expectedStatuses.Count; index++)
        {
            command.Parameters.AddWithValue(statusParameters[index], (int)expectedStatuses.ElementAt(index));
        }

        if (newScheduledLocal is DateTime newTime)
        {
            command.Parameters.AddWithValue("$newTime", Local(newTime));
        }

        if (expectedScheduledLocal is DateTime expectedTime)
        {
            command.Parameters.AddWithValue("$expectedTime", Local(expectedTime));
        }

        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture) == 1;
    }

    public async Task<PowerSchedule?> GetScheduleAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var schedules = await ReadSchedulesAsync(connection, null, "WHERE s.Id=$id", ("$id", id.ToString("D")), cancellationToken).ConfigureAwait(false);
        return schedules.SingleOrDefault();
    }

    public async Task<IReadOnlyList<PowerSchedule>> GetAllSchedulesAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await ReadSchedulesAsync(connection, null, string.Empty, null, cancellationToken).ConfigureAwait(false);
    }

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

    /// <summary>
    /// Deletes a schedule. Deleting an occurrence of a recurring series ends that series in the same
    /// transaction so that reconciliation never recreates it from an older occurrence.
    /// </summary>
    public async Task DeleteScheduleAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: false);
        try
        {
            Guid? seriesId = null;
            await using (var series = connection.CreateCommand())
            {
                series.Transaction = transaction;
                series.CommandText = "SELECT SeriesId FROM Schedules WHERE Id=$id;";
                series.Parameters.AddWithValue("$id", id.ToString("D"));
                if (await series.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is string value)
                {
                    seriesId = Guid.Parse(value);
                }
            }

            await using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = "DELETE FROM Schedules WHERE Id=$id;";
                command.Parameters.AddWithValue("$id", id.ToString("D"));
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            if (seriesId is Guid endedSeries)
            {
                await EndSeriesAsync(connection, transaction, endedSeries, id, cancellationToken).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw;
        }
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
                    WHERE Id=$id AND ActionType=$action AND IsEnabled=1 AND Status=$pendingStatus
                      AND ScheduledLocalDateTime=$time;
                    SELECT changes();
                    """;
                update.Parameters.AddWithValue("$newStatus", (int)ScheduleStatus.PendingPowerTransition);
                update.Parameters.AddWithValue("$updated", Utc(started));
                update.Parameters.AddWithValue("$id", schedule.Id.ToString("D"));
                update.Parameters.AddWithValue("$action", (int)schedule.ActionType);
                update.Parameters.AddWithValue("$time", Local(schedule.ScheduledLocalDateTime));
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

    private static async Task<IReadOnlyList<PowerSchedule>> ReadSchedulesAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string whereClause,
        (string Name, string Value)? parameter,
        CancellationToken cancellationToken)
    {
        var rows = new List<(PowerSchedule Schedule, List<FollowUpProgram> Programs)>();
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = $"""
                SELECT s.Id, s.ScheduledLocalDateTime, s.ActionType, s.IsEnabled,
                       s.OneTimeAutoLogonEnabled, s.CreatedAtUtc, s.UpdatedAtUtc, s.Status,
                       s.Name, s.RecurrenceKind, s.RecurrenceDays, s.RecurrenceTimeTicks, s.SeriesId
                FROM Schedules s
                {whereClause}
                ORDER BY s.ScheduledLocalDateTime, s.Id;
                """;
            if (parameter is var (name, value))
            {
                command.Parameters.AddWithValue(name, value);
            }

            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var scheduled = ParseLocal(reader.GetString(1));
                var recurrenceKind = (RecurrenceKind)reader.GetInt32(9);
                rows.Add((new PowerSchedule(
                    Guid.Parse(reader.GetString(0)),
                    scheduled,
                    (PowerActionType)reader.GetInt32(2),
                    reader.GetInt32(3) != 0,
                    reader.GetInt32(4) != 0,
                    ParseUtc(reader.GetString(5)),
                    ParseUtc(reader.GetString(6)),
                    (ScheduleStatus)reader.GetInt32(7),
                    [])
                {
                    Name = reader.IsDBNull(8) ? null : reader.GetString(8),
                    Recurrence = recurrenceKind == RecurrenceKind.None
                        ? null
                        : new RecurrenceRule(
                            recurrenceKind,
                            (RecurrenceDays)reader.GetInt32(10),
                            reader.IsDBNull(11) ? scheduled.TimeOfDay : TimeSpan.FromTicks(reader.GetInt64(11))),
                    SeriesId = reader.IsDBNull(12) ? null : Guid.Parse(reader.GetString(12))
                }, []));
            }
        }

        foreach (var row in rows)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
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

    private static void AddScheduleParameters(SqliteCommand command, PowerSchedule schedule)
    {
        command.Parameters.AddWithValue("$id", schedule.Id.ToString("D"));
        command.Parameters.AddWithValue("$time", Local(schedule.ScheduledLocalDateTime));
        command.Parameters.AddWithValue("$action", (int)schedule.ActionType);
        command.Parameters.AddWithValue("$enabled", schedule.IsEnabled ? 1 : 0);
        command.Parameters.AddWithValue("$autologon", schedule.OneTimeAutoLogonEnabled ? 1 : 0);
        command.Parameters.AddWithValue("$created", Utc(schedule.CreatedAtUtc));
        command.Parameters.AddWithValue("$updated", Utc(schedule.UpdatedAtUtc));
        command.Parameters.AddWithValue("$status", (int)schedule.Status);
        command.Parameters.AddWithValue("$name", (object?)schedule.Name ?? DBNull.Value);
        command.Parameters.AddWithValue("$recurrenceKind", (int)(schedule.Recurrence?.Kind ?? RecurrenceKind.None));
        command.Parameters.AddWithValue("$recurrenceDays", (int)(schedule.Recurrence?.Days ?? RecurrenceDays.None));
        command.Parameters.AddWithValue("$recurrenceTime", schedule.Recurrence is null ? DBNull.Value : schedule.Recurrence.TimeOfDay.Ticks);
        command.Parameters.AddWithValue("$seriesId", schedule.SeriesId is null ? DBNull.Value : schedule.SeriesId.Value.ToString("D"));
    }

    private static void AddStatusGuardParameters(SqliteCommand command)
    {
        command.Parameters.AddWithValue("$inFlight", (int)ScheduleStatus.PendingPowerTransition);
        command.Parameters.AddWithValue("$completed", (int)ScheduleStatus.Completed);
        command.Parameters.AddWithValue("$failed", (int)ScheduleStatus.Failed);
        command.Parameters.AddWithValue("$skipped", (int)ScheduleStatus.Skipped);
        command.Parameters.AddWithValue("$missed", (int)ScheduleStatus.Missed);
        command.Parameters.AddWithValue("$resultUnknown", (int)ScheduleStatus.ResultUnknown);
        command.Parameters.AddWithValue("$pending", (int)ScheduleStatus.Pending);
        command.Parameters.AddWithValue("$disabled", (int)ScheduleStatus.Disabled);
    }

    private static async Task ReplaceFollowUpProgramsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        PowerSchedule schedule,
        CancellationToken cancellationToken)
    {
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
    }

    // Ending a series clears the rule on its remaining (past) occurrences; the SeriesId is kept for traceability.
    private static async Task EndSeriesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid seriesId,
        Guid exceptScheduleId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE Schedules
            SET RecurrenceKind=0, RecurrenceDays=0, RecurrenceTimeTicks=NULL
            WHERE SeriesId=$series AND Id<>$id;
            """;
        command.Parameters.AddWithValue("$series", seriesId.ToString("D"));
        command.Parameters.AddWithValue("$id", exceptScheduleId.ToString("D"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
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
        SqliteTransaction transaction,
        string table,
        string column,
        string definition,
        CancellationToken cancellationToken)
    {
        var exists = false;
        await using (var info = connection.CreateCommand())
        {
            info.Transaction = transaction;
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
            alter.Transaction = transaction;
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
