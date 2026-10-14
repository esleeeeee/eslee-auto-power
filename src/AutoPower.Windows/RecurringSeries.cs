using System.Globalization;
using AutoPower.Core;
using AutoPower.Data;

namespace AutoPower.Windows;

/// <summary>
/// Keeps each recurring series at one live occurrence. The database step is idempotent and race-safe
/// (<see cref="SqliteStore.TryAdvanceSeriesAsync"/>); task registration happens after commit and only when a
/// registrar is supplied, which callers do only in elevated or SYSTEM helper contexts.
/// </summary>
public sealed class RecurringSeriesService
{
    private readonly SqliteStore _store;
    private readonly ISystemScheduleRegistrar? _registrar;
    private readonly TechnicalLogger _logger;
    private readonly Func<DateTime> _localNow;

    public RecurringSeriesService(
        SqliteStore store,
        ISystemScheduleRegistrar? registrar,
        TechnicalLogger logger,
        Func<DateTime>? localNow = null)
    {
        _store = store;
        _registrar = registrar;
        _logger = logger;
        _localNow = localNow ?? (() => DateTime.Now);
    }

    /// <summary>
    /// Best effort: creates and registers the next occurrence after <paramref name="occurrenceId"/> began,
    /// was skipped or was finalized. Failures are logged and recorded as RecurrenceAdvanceFailed history;
    /// this method never throws except for cancellation, so it can run inside the power-transition path.
    /// With <paramref name="createDisabled"/> the next occurrence is created Disabled (no tasks are registered).
    /// </summary>
    public async Task<PowerSchedule?> AdvanceAfterOccurrenceAsync(
        Guid occurrenceId,
        bool createDisabled = false,
        CancellationToken cancellationToken = default)
    {
        PowerSchedule? next;
        try
        {
            next = await _store.TryAdvanceSeriesAsync(
                    occurrenceId, _localNow(), createDisabled: createDisabled, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            _logger.Error("recurrence.advance-failed", error, $"occurrence={occurrenceId:D}");
            await TryRecordFailureAsync(
                occurrenceId,
                null,
                "반복 예약의 다음 회차를 만들지 못했습니다. 다음 로그인 시 다시 시도합니다.",
                error,
                cancellationToken).ConfigureAwait(false);
            return null;
        }

        if (next is null)
        {
            return null;
        }

        _logger.Information("recurrence.advanced",
            $"occurrence={occurrenceId:D};next={next.Id:D};time={next.ScheduledLocalDateTime:O}");
        if (_registrar is null || !next.IsEnabled)
        {
            return next;
        }

        try
        {
            _registrar.Register(next);
        }
        catch (Exception error)
        {
            // The row stays in the database; startup-reconcile re-registers every future enabled Pending row.
            _logger.Error("recurrence.register-failed", error, $"schedule={next.Id:D}");
            await TryRecordFailureAsync(
                next.Id,
                next.ScheduledLocalDateTime,
                "반복 예약의 다음 회차를 만들었지만 Windows 작업 등록에 실패했습니다. 다음 로그인 시 자동으로 다시 등록합니다.",
                error,
                cancellationToken).ConfigureAwait(false);
        }

        return next;
    }

    /// <summary>
    /// Advances every series whose latest occurrence already started or finished and that has no live
    /// (Pending or Disabled) occurrence. Ended series have no rule and paused series keep a Disabled row,
    /// so neither is recreated.
    /// </summary>
    public async Task<IReadOnlyList<PowerSchedule>> HealSeriesAsync(CancellationToken cancellationToken = default)
    {
        var schedules = await _store.GetAllSchedulesAsync(cancellationToken).ConfigureAwait(false);
        var candidates = schedules
            .Where(schedule => schedule.SeriesId is not null)
            .GroupBy(schedule => schedule.SeriesId!.Value)
            .Where(series => !series.Any(schedule => schedule.Status is ScheduleStatus.Pending or ScheduleStatus.Disabled))
            .Select(series => series
                .OrderBy(schedule => schedule.ScheduledLocalDateTime)
                .ThenBy(schedule => schedule.CreatedAtUtc)
                .Last())
            .Where(latest => latest.IsRecurring)
            .ToArray();
        var created = new List<PowerSchedule>();
        foreach (var latest in candidates)
        {
            var next = await AdvanceAfterOccurrenceAsync(latest.Id, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (next is not null)
            {
                created.Add(next);
            }
        }

        return created;
    }

    private async Task TryRecordFailureAsync(
        Guid scheduleId,
        DateTime? plannedLocalTime,
        string message,
        Exception error,
        CancellationToken cancellationToken)
    {
        try
        {
            await _store.AddHistoryAsync(
                scheduleId,
                HistoryEventTypes.RecurrenceAdvanceFailed,
                ResultKind.Warning,
                plannedLocalTime,
                message,
                error.HResult.ToString("X8", CultureInfo.InvariantCulture),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception historyError) when (historyError is not OperationCanceledException)
        {
            _logger.Error("recurrence.failure-history-failed", historyError, $"schedule={scheduleId:D}");
        }
    }
}

/// <summary>Helper-side implementation of the skip-schedule, restore-skipped and remove-schedule commands.</summary>
public sealed class ScheduleSkipOperations
{
    private readonly SqliteStore _store;
    private readonly ISystemScheduleRegistrar _registrar;
    private readonly RecurringSeriesService _series;
    private readonly Func<DateTime> _localNow;

    public ScheduleSkipOperations(
        SqliteStore store,
        ISystemScheduleRegistrar registrar,
        TechnicalLogger logger,
        Func<DateTime>? localNow = null)
    {
        _store = store;
        _registrar = registrar;
        _localNow = localNow ?? (() => DateTime.Now);
        _series = new RecurringSeriesService(store, registrar, logger, _localNow);
    }

    /// <summary>
    /// Skips one occurrence. The database transition is conditional (a started or finished schedule keeps its
    /// state and its tasks). A recurring series advances to its next occurrence; skipping a Disabled (paused)
    /// occurrence creates that next occurrence Disabled too, so the series stays paused.
    /// </summary>
    public async Task SkipAsync(Guid scheduleId, CancellationToken cancellationToken = default)
    {
        var schedule = await _store.GetScheduleAsync(scheduleId, cancellationToken).ConfigureAwait(false)
                       ?? throw new KeyNotFoundException("예약을 찾을 수 없습니다.");
        bool wasPaused;
        if (await _store.TryTransitionScheduleAsync(
                scheduleId, [ScheduleStatus.Pending], false, ScheduleStatus.Skipped,
                cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            wasPaused = false;
        }
        else if (await _store.TryTransitionScheduleAsync(
                     scheduleId, [ScheduleStatus.Disabled], false, ScheduleStatus.Skipped,
                     cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            wasPaused = true;
        }
        else
        {
            throw new InvalidOperationException("이미 실행이 시작됐거나 끝난 예약은 건너뛸 수 없습니다.");
        }

        _registrar.Remove(scheduleId);
        await _store.AddHistoryAsync(scheduleId, HistoryEventTypes.ScheduleSkipped, ResultKind.Skipped, schedule.ScheduledLocalDateTime,
            "사용자가 이번 예약을 건너뛰었습니다.", cancellationToken: cancellationToken).ConfigureAwait(false);
        await _series.AdvanceAfterOccurrenceAsync(scheduleId, wasPaused, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Undoes a skip (see <see cref="SqliteStore.RestoreSkippedAsync"/>), removes the tasks of the occurrence the
    /// skip advanced to and re-registers the restored schedule. If registration fails, the schedule goes back to
    /// Skipped and the series is advanced again, then the error is rethrown.
    /// </summary>
    public async Task<PowerSchedule> RestoreSkippedAsync(Guid scheduleId, CancellationToken cancellationToken = default)
    {
        var result = await _store.RestoreSkippedAsync(scheduleId, _localNow(), cancellationToken).ConfigureAwait(false);
        foreach (var removed in result.RemovedOccurrenceIds)
        {
            _registrar.Remove(removed);
        }

        try
        {
            _registrar.Register(result.Schedule);
        }
        catch
        {
            if (!result.AlreadyRestored &&
                await _store.TryTransitionScheduleAsync(
                    scheduleId, [ScheduleStatus.Pending], false, ScheduleStatus.Skipped,
                    result.Schedule.ScheduledLocalDateTime, cancellationToken: CancellationToken.None).ConfigureAwait(false))
            {
                await _series.AdvanceAfterOccurrenceAsync(scheduleId, cancellationToken: CancellationToken.None).ConfigureAwait(false);
            }

            throw;
        }

        return result.Schedule;
    }

    /// <summary>
    /// Removes the tasks of a schedule that is about to be deleted, and of the other live occurrences of its
    /// series, which the database delete removes as well. An in-flight schedule is refused so that its shutdown
    /// fallback task survives.
    /// </summary>
    public async Task RemoveTasksForDeleteAsync(Guid scheduleId, CancellationToken cancellationToken = default)
    {
        var schedules = await _store.GetAllSchedulesAsync(cancellationToken).ConfigureAwait(false);
        var schedule = schedules.SingleOrDefault(item => item.Id == scheduleId);
        if (schedule?.Status == ScheduleStatus.PendingPowerTransition)
        {
            throw new ScheduleStateConflictException(SqliteStore.InFlightDeleteMessage);
        }

        _registrar.Remove(scheduleId);
        if (schedule?.SeriesId is not Guid seriesId)
        {
            return;
        }

        foreach (var sibling in schedules.Where(item =>
                     item.Id != scheduleId &&
                     item.SeriesId == seriesId &&
                     item.Status is ScheduleStatus.Pending or ScheduleStatus.Disabled))
        {
            _registrar.Remove(sibling.Id);
        }
    }
}

public sealed record ResumeAllResult(int Resumed, int MovedToNextOccurrence, int Conflicts);

/// <summary>Helper-side implementation of the pause-all and resume-all commands.</summary>
public sealed class ScheduleBulkOperations
{
    private readonly SqliteStore _store;
    private readonly ISystemScheduleRegistrar _registrar;
    private readonly Func<DateTime> _localNow;

    public ScheduleBulkOperations(SqliteStore store, ISystemScheduleRegistrar registrar, Func<DateTime>? localNow = null)
    {
        _store = store;
        _registrar = registrar;
        _localNow = localNow ?? (() => DateTime.Now);
    }

    /// <summary>
    /// Disables every future enabled Pending schedule. The database transition is conditional, so a schedule
    /// whose power transition has just begun is left untouched, and its tasks are removed only afterwards.
    /// </summary>
    public async Task<int> PauseAllAsync(CancellationToken cancellationToken = default)
    {
        var now = _localNow();
        var schedules = await _store.GetAllSchedulesAsync(cancellationToken).ConfigureAwait(false);
        var paused = 0;
        foreach (var schedule in schedules.Where(item =>
                     item.IsEnabled && item.Status == ScheduleStatus.Pending && item.ScheduledLocalDateTime > now))
        {
            if (!await _store.TryTransitionScheduleAsync(
                    schedule.Id,
                    [ScheduleStatus.Pending],
                    false,
                    ScheduleStatus.Disabled,
                    schedule.ScheduledLocalDateTime,
                    cancellationToken: cancellationToken).ConfigureAwait(false))
            {
                continue;
            }

            _registrar.Remove(schedule.Id);
            paused++;
        }

        if (paused > 0)
        {
            await _store.AddHistoryAsync(null, HistoryEventTypes.SchedulesPaused, ResultKind.Success, null,
                "모든 미래 예약을 일시 중지했습니다.", cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        return paused;
    }

    /// <summary>
    /// Re-enables Disabled schedules and registers their tasks. A future schedule keeps its time, a past-due
    /// recurring occurrence moves to its next occurrence, and a past-due one-time schedule is left alone.
    /// Schedules that would now conflict with an active schedule stay disabled and are reported in history.
    /// </summary>
    public async Task<ResumeAllResult> ResumeAllAsync(CancellationToken cancellationToken = default)
    {
        var now = _localNow();
        var earliest = now + RecurrencePolicy.MinimumLeadTime;
        var schedules = (await _store.GetAllSchedulesAsync(cancellationToken).ConfigureAwait(false)).ToList();
        int resumed = 0, moved = 0, conflicts = 0;
        foreach (var schedule in schedules
                     .Where(item => item.Status == ScheduleStatus.Disabled &&
                                    !PowerSchedulePolicy.IsRemovedSchedule(item.ActionType))
                     .OrderBy(item => item.ScheduledLocalDateTime)
                     .ToArray())
        {
            DateTime target;
            if (schedule.ScheduledLocalDateTime > earliest)
            {
                target = schedule.ScheduledLocalDateTime;
            }
            else if (schedule.Recurrence is { } rule && RecurrencePolicy.IsUsable(rule))
            {
                target = RecurrencePolicy.NextOccurrence(rule, earliest);
            }
            else
            {
                continue;
            }

            var candidate = schedule with
            {
                ScheduledLocalDateTime = target,
                IsEnabled = true,
                Status = ScheduleStatus.Pending
            };
            if (!ScheduleValidator.Validate(candidate, now, schedules).IsValid)
            {
                conflicts++;
                await _store.AddHistoryAsync(schedule.Id, HistoryEventTypes.ScheduleResumeConflict, ResultKind.Warning, target,
                    "다른 예약과 겹쳐 다시 켜지 못한 예약이 있습니다. 예약 목록에서 확인하세요.",
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (!await _store.TryTransitionScheduleAsync(
                    schedule.Id,
                    [ScheduleStatus.Disabled],
                    true,
                    ScheduleStatus.Pending,
                    schedule.ScheduledLocalDateTime,
                    target,
                    cancellationToken).ConfigureAwait(false))
            {
                continue;
            }

            try
            {
                _registrar.Register(candidate);
            }
            catch
            {
                await _store.TryTransitionScheduleAsync(
                    schedule.Id,
                    [ScheduleStatus.Pending],
                    false,
                    ScheduleStatus.Disabled,
                    target,
                    schedule.ScheduledLocalDateTime,
                    CancellationToken.None).ConfigureAwait(false);
                throw;
            }

            schedules[schedules.FindIndex(item => item.Id == schedule.Id)] = candidate;
            resumed++;
            if (target != schedule.ScheduledLocalDateTime)
            {
                moved++;
                await _store.AddHistoryAsync(schedule.Id, HistoryEventTypes.ScheduleUpdated, ResultKind.Success, target,
                    string.Create(CultureInfo.InvariantCulture,
                        $"지난 반복 예약을 다음 회차로 옮겨 다시 켰습니다. 다음 시각: {target:yyyy.MM.dd HH:mm}"),
                    cancellationToken: cancellationToken).ConfigureAwait(false);
            }
        }

        if (resumed > 0)
        {
            await _store.AddHistoryAsync(null, HistoryEventTypes.SchedulesResumed, ResultKind.Success, null,
                "일시 중지된 예약을 다시 켰습니다.", cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        return new ResumeAllResult(resumed, moved, conflicts);
    }
}
