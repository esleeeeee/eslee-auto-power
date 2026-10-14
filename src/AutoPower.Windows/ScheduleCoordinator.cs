using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using AutoPower.Core;
using AutoPower.Data;

namespace AutoPower.Windows;

public interface IPrivilegedHelperClient
{
    Task RunAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken = default);
}

public sealed class ElevatedHelperClient : IPrivilegedHelperClient
{
    private readonly string _helperPath;

    public ElevatedHelperClient(string helperPath)
    {
        _helperPath = Path.GetFullPath(helperPath);
    }

    public async Task RunAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (arguments.Count == 0)
        {
            throw new ArgumentException("관리자 권한 작업 명령이 필요합니다.", nameof(arguments));
        }

        if (!File.Exists(_helperPath))
        {
            throw new FileNotFoundException("관리자 권한 도우미를 찾을 수 없습니다.", _helperPath);
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = _helperPath,
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        var startedAt = DateTimeOffset.Now;
        try
        {
            using var process = Process.Start(startInfo)
                                ?? throw new InvalidOperationException("관리자 권한 도우미를 시작하지 못했습니다.");
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            if (process.ExitCode != 0)
            {
                var reason = TryReadRecentHelperError(AppPaths.LogDirectory, startedAt, arguments[0]);
                var detail = reason is null ? string.Empty : $"\n\n원인: {reason}";
                throw new InvalidOperationException(
                    $"관리자 권한 작업이 실패했습니다. 종료 코드: {process.ExitCode}{detail}\n\n설정의 '진단 로그 폴더 열기'에서 자세한 내용을 확인할 수 있습니다.");
            }
        }
        catch (Win32Exception error) when (error.NativeErrorCode == 1223)
        {
            throw new OperationCanceledException("사용자가 관리자 권한 요청을 취소했습니다.", error, cancellationToken);
        }
    }

    internal static string? TryReadRecentHelperError(
        string logDirectory,
        DateTimeOffset startedAt,
        string command)
    {
        try
        {
            if (!Directory.Exists(logDirectory))
            {
                return null;
            }

            foreach (var file in Directory.EnumerateFiles(logDirectory, "autopower-*.log")
                         .OrderByDescending(File.GetLastWriteTimeUtc))
            {
                var lines = File.ReadAllLines(file, Encoding.UTF8);
                for (var index = lines.Length - 1; index >= 0; index--)
                {
                    var parts = lines[index].Split('\t', 5);
                    if (parts.Length != 5 ||
                        !DateTimeOffset.TryParseExact(parts[0], "O", CultureInfo.InvariantCulture,
                            DateTimeStyles.RoundtripKind, out var timestamp) ||
                        timestamp < startedAt.AddSeconds(-2) ||
                        !string.Equals(parts[1], "ERROR", StringComparison.Ordinal) ||
                        !string.Equals(parts[2], "helper.command-failed", StringComparison.Ordinal) ||
                        !string.Equals(parts[3], $"command={command}", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var message = parts[4];
                    var typeSeparator = message.IndexOf(": ", StringComparison.Ordinal);
                    if (typeSeparator >= 0)
                    {
                        message = message[(typeSeparator + 2)..];
                    }

                    var stackStart = message.IndexOf("     at ", StringComparison.Ordinal);
                    if (stackStart >= 0)
                    {
                        message = message[..stackStart];
                    }

                    message = message.Trim();
                    return message.Length > 400 ? message[..400] + "…" : message;
                }
            }
        }
        catch (IOException)
        {
            // The elevated process can still be releasing the log file; the exit code remains useful.
        }
        catch (UnauthorizedAccessException)
        {
            // Log access must not replace the original helper failure.
        }

        return null;
    }
}

public sealed class ScheduleCoordinator
{
    private readonly SqliteStore _store;
    private readonly IPrivilegedHelperClient _helper;
    private readonly Func<DateTime> _localNow;

    public ScheduleCoordinator(SqliteStore store, IPrivilegedHelperClient helper, Func<DateTime>? localNow = null)
    {
        _store = store;
        _helper = helper;
        _localNow = localNow ?? (() => DateTime.Now);
    }

    public Task<ValidationResult> SaveAsync(PowerSchedule schedule, CancellationToken cancellationToken = default) =>
        SaveCoreAsync(schedule, null, cancellationToken);

    /// <summary>
    /// Postpones an enabled Pending shutdown, hibernate or sleep schedule by 10, 30 or 60 minutes from the later
    /// of its scheduled time and the current minute (rounded up). Uses the regular save path, so the helper
    /// re-registers the schedule's tasks and a failure rolls the database back. A recurring series keeps its
    /// anchor time. Returns the new scheduled time.
    /// </summary>
    public async Task<DateTime> SnoozeAsync(Guid scheduleId, int minutes, CancellationToken cancellationToken = default)
    {
        if (!ScheduleTimePolicy.IsSupportedSnooze(minutes))
        {
            throw new ArgumentOutOfRangeException(nameof(minutes), minutes, "예약은 10분, 30분, 60분 중 하나로만 미룰 수 있습니다.");
        }

        var schedule = await _store.GetScheduleAsync(scheduleId, cancellationToken).ConfigureAwait(false)
                       ?? throw new KeyNotFoundException("예약을 찾을 수 없습니다.");
        if (!PowerSchedulePolicy.IsPowerTransition(schedule.ActionType) ||
            !schedule.IsEnabled ||
            schedule.Status != ScheduleStatus.Pending)
        {
            throw new InvalidOperationException("활성 상태의 완전 종료, 최대 절전, 절전 예약만 미룰 수 있습니다.");
        }

        var snoozed = ScheduleTimePolicy.Snooze(schedule.ScheduledLocalDateTime, _localNow(), minutes);
        var message = string.Create(CultureInfo.InvariantCulture,
            $"예약을 {minutes}분 미뤘습니다. 새 시각: {snoozed:yyyy.MM.dd HH:mm}");
        var validation = await SaveCoreAsync(
            schedule with { ScheduledLocalDateTime = snoozed },
            (HistoryEventTypes.Snoozed, ResultKind.Information, message),
            cancellationToken).ConfigureAwait(false);
        if (!validation.IsValid)
        {
            throw new InvalidOperationException(string.Join(Environment.NewLine, validation.Issues.Select(issue => issue.Message)));
        }

        return snoozed;
    }

    /// <summary>
    /// Undoes a skip through the helper command <c>restore-skipped --schedule {id}</c>: the Skipped schedule
    /// becomes enabled Pending again (only while it is at least one minute in the future), its tasks are
    /// re-registered and a series occurrence created by the skip is removed. Throws when the schedule changed
    /// meanwhile or is too close; calling it again after success is harmless.
    /// </summary>
    public Task RestoreSkippedAsync(Guid scheduleId, CancellationToken cancellationToken = default) =>
        _helper.RunAsync(["restore-skipped", "--schedule", scheduleId.ToString("D")], cancellationToken);

    /// <summary>Asks the helper to disable every future schedule (helper command <c>pause-all</c>).</summary>
    public Task PauseAllAsync(CancellationToken cancellationToken = default) =>
        _helper.RunAsync(["pause-all"], cancellationToken);

    /// <summary>
    /// Asks the helper to re-enable paused schedules (helper command <c>resume-all</c>). Past-due recurring
    /// occurrences move to their next occurrence; past-due one-time schedules stay disabled.
    /// </summary>
    public Task ResumeAllAsync(CancellationToken cancellationToken = default) =>
        _helper.RunAsync(["resume-all"], cancellationToken);

    private async Task<ValidationResult> SaveCoreAsync(
        PowerSchedule schedule,
        (string EventType, ResultKind Result, string Message)? history,
        CancellationToken cancellationToken)
    {
        schedule = schedule.Recurrence is null
            ? schedule with { SeriesId = null }
            : schedule.SeriesId is null ? schedule with { SeriesId = Guid.NewGuid() } : schedule;
        var all = await _store.GetAllSchedulesAsync(cancellationToken).ConfigureAwait(false);
        var validation = ScheduleValidator.Validate(schedule, _localNow(), all);
        if (!validation.IsValid)
        {
            return validation;
        }

        var previous = all.SingleOrDefault(candidate => candidate.Id == schedule.Id);
        var operation = NewOperation(
            previous is null ? PendingOperationType.RegisterSchedule : PendingOperationType.UpdateSchedule,
            schedule.Id,
            "DB 저장 및 Windows 작업 등록 진행 중");
        await _store.SavePendingOperationAsync(operation, cancellationToken).ConfigureAwait(false);
        try
        {
            await _store.SaveScheduleAsync(schedule with { UpdatedAtUtc = DateTimeOffset.UtcNow }, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await _store.SavePendingOperationAsync(operation with
            {
                State = PendingOperationState.Failed,
                UpdatedAtUtc = DateTimeOffset.UtcNow,
                Detail = "DB 저장 실패"
            }, CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        try
        {
            await _helper.RunAsync(["register-schedule", "--schedule", schedule.Id.ToString("D")], cancellationToken).ConfigureAwait(false);
            await _store.SavePendingOperationAsync(operation with
            {
                State = PendingOperationState.Completed,
                UpdatedAtUtc = DateTimeOffset.UtcNow,
                Detail = "DB와 Windows 작업 등록 완료"
            }, cancellationToken).ConfigureAwait(false);
            var (eventType, result, message) = history ?? (
                previous is null ? HistoryEventTypes.ScheduleCreated : HistoryEventTypes.ScheduleUpdated,
                ResultKind.Success,
                previous is null ? "예약을 만들었습니다." : "예약을 수정했습니다.");
            await _store.AddHistoryAsync(
                schedule.Id,
                eventType,
                result,
                schedule.ScheduledLocalDateTime,
                message,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            return ValidationResult.Success;
        }
        catch
        {
            if (previous is null)
            {
                await _store.DeleteScheduleAsync(schedule.Id, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                try
                {
                    await _store.SaveScheduleAsync(previous, cancellationToken).ConfigureAwait(false);
                }
                catch (ScheduleStateConflictException)
                {
                    // The power transition began meanwhile; its durable state must not be overwritten.
                }
            }

            await _store.SavePendingOperationAsync(operation with
            {
                State = PendingOperationState.Failed,
                UpdatedAtUtc = DateTimeOffset.UtcNow,
                Detail = "Windows 작업 등록 실패로 DB 변경 롤백"
            }, cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    public async Task DeleteAsync(Guid scheduleId, CancellationToken cancellationToken = default)
    {
        var schedule = await _store.GetScheduleAsync(scheduleId, cancellationToken).ConfigureAwait(false)
                       ?? throw new KeyNotFoundException("삭제할 예약을 찾을 수 없습니다.");
        if (schedule.Status == ScheduleStatus.PendingPowerTransition)
        {
            // Removing the tasks of an in-flight run would also remove its shutdown fallback.
            throw new ScheduleStateConflictException(SqliteStore.InFlightDeleteMessage);
        }

        var operation = NewOperation(PendingOperationType.RemoveSchedule, scheduleId, "Windows 작업과 DB 예약 삭제 진행 중");
        await _store.SavePendingOperationAsync(operation, cancellationToken).ConfigureAwait(false);
        await _helper.RunAsync(["remove-schedule", "--schedule", scheduleId.ToString("D")], cancellationToken).ConfigureAwait(false);
        try
        {
            await _store.DeleteScheduleAsync(scheduleId, cancellationToken).ConfigureAwait(false);
            await _store.SavePendingOperationAsync(operation with
            {
                State = PendingOperationState.Completed,
                UpdatedAtUtc = DateTimeOffset.UtcNow,
                Detail = "Windows 작업과 DB 예약 삭제 완료"
            }, cancellationToken).ConfigureAwait(false);
            await _store.AddHistoryAsync(scheduleId, "ScheduleDeleted", ResultKind.Success, schedule.ScheduledLocalDateTime, "예약을 삭제했습니다.", cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await _store.SavePendingOperationAsync(operation with
            {
                State = PendingOperationState.Compensating,
                UpdatedAtUtc = DateTimeOffset.UtcNow,
                Detail = "DB 삭제 실패; 다음 복구에서 Windows 작업 재등록 필요"
            }, cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    public async Task SetEnabledAsync(Guid scheduleId, bool enabled, CancellationToken cancellationToken = default)
    {
        var schedule = await _store.GetScheduleAsync(scheduleId, cancellationToken).ConfigureAwait(false)
                       ?? throw new KeyNotFoundException("예약을 찾을 수 없습니다.");
        var scheduled = schedule.ScheduledLocalDateTime;
        var now = _localNow();
        if (enabled && scheduled <= now + RecurrencePolicy.MinimumLeadTime)
        {
            // A paused recurring occurrence that is past or too close resumes at its next occurrence.
            if (schedule.Recurrence is { } rule && RecurrencePolicy.IsUsable(rule))
            {
                scheduled = RecurrencePolicy.NextOccurrence(rule, now + RecurrencePolicy.MinimumLeadTime);
            }
            else if (scheduled <= now)
            {
                throw new InvalidOperationException("지난 예약은 다시 켤 수 없습니다.");
            }
        }

        var changed = schedule with
        {
            ScheduledLocalDateTime = scheduled,
            IsEnabled = enabled,
            Status = enabled ? ScheduleStatus.Pending : ScheduleStatus.Disabled,
            UpdatedAtUtc = DateTimeOffset.UtcNow
        };
        var validation = await SaveAsync(changed, cancellationToken).ConfigureAwait(false);
        if (!validation.IsValid)
        {
            throw new InvalidOperationException(string.Join(Environment.NewLine, validation.Issues.Select(issue => issue.Message)));
        }
    }

    private static PendingOperation NewOperation(PendingOperationType type, Guid scheduleId, string detail)
    {
        var now = DateTimeOffset.UtcNow;
        return new PendingOperation(Guid.NewGuid(), type, scheduleId, PendingOperationState.Pending, now, now, detail);
    }
}

public sealed class IntegrityReconciler
{
    private readonly SqliteStore _store;
    private readonly ISystemScheduleRegistrar _registrar;
    private readonly AutologonManager _autologon;
    private readonly TechnicalLogger _logger;

    public IntegrityReconciler(SqliteStore store, ISystemScheduleRegistrar registrar, AutologonManager autologon, TechnicalLogger logger)
    {
        _store = store;
        _registrar = registrar;
        _autologon = autologon;
        _logger = logger;
    }

    public async Task ReconcileAsync(bool canRepairSystem, CancellationToken cancellationToken = default)
    {
        var journal = _autologon.CurrentJournal;
        if (journal is not null && journal.State is not AutologonJournalState.Cleaned)
        {
            if (!canRepairSystem)
            {
                await _store.AddHistoryAsync(journal.ScheduleId, "RemovedSecurityCleanup", ResultKind.Warning, journal.ScheduledLocalTime,
                    "이전 버전의 보안 설정 정리가 필요합니다.", cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            else
            {
                _autologon.Cleanup();
            }
        }

        var powerTransitions = new PowerTransitionReconciler(
            _store,
            _registrar,
            new WindowsPowerTransitionEvidenceSource(_registrar, _logger),
            _logger);
        await powerTransitions.ReconcileAsync(canRepairSystem, cancellationToken).ConfigureAwait(false);

        var all = await _store.GetAllSchedulesAsync(cancellationToken).ConfigureAwait(false);
        foreach (var missed in all.Where(schedule =>
                     schedule.Status == ScheduleStatus.Pending && schedule.ScheduledLocalDateTime < DateTime.Now - TimeSpan.FromMinutes(2)))
        {
            // Conditional: the row may have gone in flight (or been edited) since it was read above.
            if (!await _store.TryTransitionScheduleAsync(
                    missed.Id,
                    [ScheduleStatus.Pending],
                    false,
                    ScheduleStatus.Missed,
                    missed.ScheduledLocalDateTime,
                    cancellationToken: cancellationToken).ConfigureAwait(false))
            {
                continue;
            }

            await _store.AddHistoryAsync(missed.Id, "ScheduleMissed", ResultKind.Warning, missed.ScheduledLocalDateTime,
                "미실행 — 예약 시각에 실행 가능한 상태가 아니었음", cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        if (!canRepairSystem)
        {
            return;
        }

        // Only the SYSTEM/elevated reconcile advances recurring series. New rows are registered by the loop below,
        // which re-registers every future enabled Pending schedule and so also heals rows whose registration failed.
        await new RecurringSeriesService(_store, null, _logger).HealSeriesAsync(cancellationToken).ConfigureAwait(false);
        all = await _store.GetAllSchedulesAsync(cancellationToken).ConfigureAwait(false);
        var future = all.Where(schedule =>
            schedule.IsEnabled &&
            schedule.Status == ScheduleStatus.Pending &&
            schedule.ScheduledLocalDateTime > DateTime.Now &&
            !PowerSchedulePolicy.IsRemovedSchedule(schedule.ActionType)).ToArray();
        var expectedIds = future.Select(schedule => schedule.Id).ToHashSet();
        foreach (var schedule in future)
        {
            _registrar.Register(schedule);
        }

        foreach (var taskName in _registrar.ListOwnedTasks())
        {
            if (TryGetScheduleIdFromOwnedTaskName(taskName, out var id) && !expectedIds.Contains(id))
            {
                _registrar.Remove(id);
                _logger.Information("task.orphan-removed", $"task={taskName}");
            }
        }
    }

    internal static bool TryGetScheduleIdFromOwnedTaskName(string taskName, out Guid scheduleId)
    {
        foreach (var prefix in new[] { "wake-", "warning-", "power-", "followup-", "shutdown-fallback-" })
        {
            if (taskName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                Guid.TryParseExact(taskName[prefix.Length..], "D", out scheduleId))
            {
                return true;
            }
        }

        const string elevatedPrefix = "elevated-";
        if (taskName.StartsWith(elevatedPrefix, StringComparison.OrdinalIgnoreCase) &&
            taskName.Length > elevatedPrefix.Length + 36 &&
            taskName[elevatedPrefix.Length + 36] == '-' &&
            Guid.TryParseExact(taskName.Substring(elevatedPrefix.Length, 36), "D", out scheduleId))
        {
            return true;
        }

        scheduleId = Guid.Empty;
        return false;
    }
}
