using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Xml.Linq;
using AutoPower.Core;
using AutoPower.Data;

namespace AutoPower.Windows;

public sealed record PowerTransitionEvidence(
    bool EntryObserved,
    bool CompletionObserved,
    DateTimeOffset? EntryTimeUtc,
    DateTimeOffset? CompletionTimeUtc,
    PowerTaskSnapshot Task,
    string Detail,
    bool EventLogAvailable = true);

public interface IPowerTransitionEvidenceSource
{
    PowerTransitionEvidence Inspect(
        PowerSchedule schedule,
        DateTimeOffset startedAtUtc,
        DateTimeOffset nowUtc);
}

public sealed class WindowsPowerTransitionEvidenceSource : IPowerTransitionEvidenceSource
{
    private readonly ISystemScheduleRegistrar _registrar;
    private readonly TechnicalLogger _logger;

    public WindowsPowerTransitionEvidenceSource(ISystemScheduleRegistrar registrar, TechnicalLogger logger)
    {
        _registrar = registrar;
        _logger = logger;
    }

    public PowerTransitionEvidence Inspect(
        PowerSchedule schedule,
        DateTimeOffset startedAtUtc,
        DateTimeOffset nowUtc)
    {
        PowerTaskSnapshot task;
        try
        {
            task = _registrar.GetPowerTaskSnapshot(schedule.Id);
        }
        catch (Exception error) when (error is UnauthorizedAccessException or COMException)
        {
            task = PowerTaskSnapshot.Missing;
            _logger.Warning("power-transition.task-snapshot-unavailable",
                $"schedule={schedule.Id:D};error={error.GetType().Name}");
        }

        try
        {
            return schedule.ActionType switch
            {
                PowerActionType.Sleep or PowerActionType.Hibernate => InspectSleepState(
                    schedule.ActionType, startedAtUtc, nowUtc, task),
                PowerActionType.Shutdown => InspectShutdown(startedAtUtc, nowUtc, task),
                _ => new PowerTransitionEvidence(false, false, null, null, task,
                    "전원 전환 증거 대상이 아닌 예약입니다.")
            };
        }
        catch (Exception error) when (error is EventLogException or UnauthorizedAccessException)
        {
            _logger.Error("power-transition.evidence-read-failed", error, $"schedule={schedule.Id:D}");
            return new PowerTransitionEvidence(false, false, null, null, PowerTaskSnapshot.Missing,
                $"Windows 전원 이벤트를 읽지 못했습니다: {error.GetType().Name}", false);
        }
    }

    private static PowerTransitionEvidence InspectSleepState(
        PowerActionType action,
        DateTimeOffset startedAtUtc,
        DateTimeOffset nowUtc,
        PowerTaskSnapshot task)
    {
        var events = ReadSystemEvents(startedAtUtc.AddSeconds(-30), nowUtc,
            [187, 42, 1]);
        var request = events.FirstOrDefault(item =>
            item.Id == 187 &&
            string.Equals(item.Provider, "Microsoft-Windows-Kernel-Power", StringComparison.OrdinalIgnoreCase) &&
            item.ValueContains("ApiCallerName", "AutoPower.Helper.exe") &&
            item.TimeUtc >= startedAtUtc.AddSeconds(-30) &&
            item.TimeUtc <= startedAtUtc.Add(PowerTransitionPolicy.InvocationTolerance));
        var entry = events.FirstOrDefault(item =>
            request is not null &&
            item.Id == 42 &&
            string.Equals(item.Provider, "Microsoft-Windows-Kernel-Power", StringComparison.OrdinalIgnoreCase) &&
            MatchesSleepState(action, item.Int32("TargetState")) &&
            item.TimeUtc >= startedAtUtc.AddSeconds(-30) &&
            item.TimeUtc <= startedAtUtc.Add(PowerTransitionPolicy.InvocationTolerance));
        var completion = events.LastOrDefault(item =>
            entry is not null &&
            item.Id == 1 &&
            string.Equals(item.Provider, "Microsoft-Windows-Power-Troubleshooter", StringComparison.OrdinalIgnoreCase) &&
            MatchesSleepState(action, item.Int32("TargetState")) &&
            item.Timestamp("SleepTime") is DateTimeOffset sleepTime &&
            sleepTime >= startedAtUtc.AddSeconds(-30) &&
            sleepTime <= startedAtUtc.Add(PowerTransitionPolicy.InvocationTolerance) &&
            item.Timestamp("WakeTime") is DateTimeOffset wakeTime &&
            wakeTime >= sleepTime);

        var detail = entry is null
            ? "AutoPower.Helper Kernel-Power 187 호출과 일치하는 Kernel-Power 42 전원 진입 이벤트가 없습니다."
            : completion is null
                ? $"AutoPower.Helper 호출 및 Kernel-Power 42 진입 확인({entry.TimeUtc:O}), 복귀 이벤트는 확인되지 않았습니다."
                : $"AutoPower.Helper 호출, Kernel-Power 42 진입({entry.TimeUtc:O}) 및 Power-Troubleshooter 1 복귀({completion.Timestamp("WakeTime"):O}) 확인.";
        return new PowerTransitionEvidence(
            entry is not null,
            completion is not null,
            entry?.TimeUtc,
            completion?.Timestamp("WakeTime"),
            task,
            detail);
    }

    private static PowerTransitionEvidence InspectShutdown(
        DateTimeOffset startedAtUtc,
        DateTimeOffset nowUtc,
        PowerTaskSnapshot task)
    {
        var events = ReadSystemEvents(startedAtUtc.AddSeconds(-30), nowUtc,
            [1074, 6006, 12]);
        var request = events.FirstOrDefault(item =>
            item.Id == 1074 &&
            string.Equals(item.Provider, "User32", StringComparison.OrdinalIgnoreCase) &&
            item.TimeUtc >= startedAtUtc.AddSeconds(-30) &&
            item.TimeUtc <= startedAtUtc.Add(PowerTransitionPolicy.InvocationTolerance));
        var completion = request is null
            ? null
            : events.FirstOrDefault(item =>
                item.TimeUtc >= request.TimeUtc &&
                (item.Id == 6006 ||
                 item.Id == 12 && string.Equals(item.Provider, "Microsoft-Windows-Kernel-General", StringComparison.OrdinalIgnoreCase)));
        var detail = request is null
            ? "일치하는 User32 1074 종료 요청 이벤트가 없습니다."
            : completion is null
                ? $"User32 1074 종료 요청({request.TimeUtc:O})은 있으나 종료/다음 부팅 증거가 없습니다."
                : $"User32 1074 종료 요청({request.TimeUtc:O}) 및 종료/다음 부팅 이벤트 {completion.Id}({completion.TimeUtc:O}) 확인.";
        return new PowerTransitionEvidence(
            request is not null,
            completion is not null,
            request?.TimeUtc,
            completion?.TimeUtc,
            task,
            detail);
    }

    private static bool MatchesSleepState(PowerActionType action, int? targetState) => action switch
    {
        PowerActionType.Hibernate => targetState == 5,
        PowerActionType.Sleep => targetState is >= 2 and <= 4,
        _ => false
    };

    private static List<SystemPowerEvent> ReadSystemEvents(
        DateTimeOffset startUtc,
        DateTimeOffset endUtc,
        IReadOnlyCollection<int> eventIds)
    {
        if (endUtc < startUtc)
        {
            return [];
        }

        var ids = string.Join(" or ", eventIds.Select(id => $"EventID={id}"));
        var xpath = string.Create(CultureInfo.InvariantCulture,
            $"*[System[TimeCreated[@SystemTime >= '{startUtc.UtcDateTime:O}' and @SystemTime <= '{endUtc.UtcDateTime:O}'] and ({ids})]]");
        using var reader = new EventLogReader(new EventLogQuery("System", PathType.LogName, xpath)
        {
            ReverseDirection = false
        });
        var result = new List<SystemPowerEvent>();
        for (EventRecord? record = reader.ReadEvent(); record is not null; record = reader.ReadEvent())
        {
            using (record)
            {
                var document = XDocument.Parse(record.ToXml());
                XNamespace ns = "http://schemas.microsoft.com/win/2004/08/events/event";
                var data = document.Descendants(ns + "Data")
                    .Where(item => item.Attribute("Name") is not null)
                    .ToDictionary(
                        item => item.Attribute("Name")!.Value,
                        item => item.Value,
                        StringComparer.OrdinalIgnoreCase);
                result.Add(new SystemPowerEvent(
                    record.Id,
                    record.ProviderName ?? string.Empty,
                    record.TimeCreated is DateTime time
                        ? new DateTimeOffset(DateTime.SpecifyKind(time.ToUniversalTime(), DateTimeKind.Utc))
                        : DateTimeOffset.MinValue,
                    data));
            }
        }

        return result;
    }

    private sealed record SystemPowerEvent(
        int Id,
        string Provider,
        DateTimeOffset TimeUtc,
        IReadOnlyDictionary<string, string> Data)
    {
        public int? Int32(string name) =>
            Data.TryGetValue(name, out var value) && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : null;

        public DateTimeOffset? Timestamp(string name) =>
            Data.TryGetValue(name, out var value) && DateTimeOffset.TryParse(
                value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed)
                ? parsed
                : null;

        public bool ValueContains(string name, string value) =>
            Data.TryGetValue(name, out var actual) && actual.Contains(value, StringComparison.OrdinalIgnoreCase);
    }
}

public sealed class PowerTransitionReconciler
{
    private const int SchedulerTaskRunning = 0x00041301;
    private const int SchedulerTaskQueued = 0x00041325;
    private const int SchedulerTaskHasNotRun = 0x00041303;
    private readonly SqliteStore _store;
    private readonly ISystemScheduleRegistrar _registrar;
    private readonly IPowerTransitionEvidenceSource _evidence;
    private readonly TechnicalLogger _logger;
    private readonly Func<DateTimeOffset> _utcNow;

    public PowerTransitionReconciler(
        SqliteStore store,
        ISystemScheduleRegistrar registrar,
        IPowerTransitionEvidenceSource evidence,
        TechnicalLogger logger,
        Func<DateTimeOffset>? utcNow = null)
    {
        _store = store;
        _registrar = registrar;
        _evidence = evidence;
        _logger = logger;
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    }

    public async Task ReconcileAsync(bool canRepairSystem, CancellationToken cancellationToken = default)
    {
        var nowUtc = _utcNow();
        var nowLocal = nowUtc.ToLocalTime().DateTime;
        var schedules = await _store.GetAllSchedulesAsync(cancellationToken).ConfigureAwait(false);
        var history = await _store.GetHistoryAsync(cancellationToken).ConfigureAwait(false);
        var operations = (await _store.GetIncompleteOperationsAsync(cancellationToken).ConfigureAwait(false))
            .Where(item => item.Type == PendingOperationType.PowerTransition && item.ScheduleId is not null)
            .GroupBy(item => item.ScheduleId!.Value)
            .ToDictionary(group => group.Key, group => group.OrderByDescending(item => item.CreatedAtUtc).First());

        foreach (var schedule in schedules.Where(item =>
                     PowerSchedulePolicy.IsPowerTransition(item.ActionType) &&
                     item.ScheduledLocalDateTime < nowLocal - PowerTransitionPolicy.InvocationTolerance))
        {
            var scheduleHistory = history.Where(item => item.ScheduleId == schedule.Id).ToArray();
            if (HasTerminalHistory(scheduleHistory))
            {
                continue;
            }

            operations.TryGetValue(schedule.Id, out var operation);
            var legacyStarted = scheduleHistory
                .Where(item => item.EventType is "ExecutionStarted" or "PowerAction")
                .OrderByDescending(item => item.ActualTimeUtc)
                .FirstOrDefault()?.ActualTimeUtc;
            var hasExecutionEvidence = operation is not null || legacyStarted is not null;
            if (schedule.Status is not (ScheduleStatus.PendingPowerTransition or ScheduleStatus.Pending or ScheduleStatus.Completed) &&
                !hasExecutionEvidence)
            {
                continue;
            }

            var startedAt = operation?.CreatedAtUtc ?? legacyStarted ?? ToUtc(schedule.ScheduledLocalDateTime);
            var evidence = _evidence.Inspect(schedule, startedAt, nowUtc);
            if (canRepairSystem)
            {
                _registrar.ConsumePowerTask(schedule.Id);
            }

            var resolution = Resolve(schedule, evidence, hasExecutionEvidence, startedAt, nowUtc);
            if (resolution is null)
            {
                continue;
            }

            var expected = new[] { schedule.Status };
            var changed = await _store.TryFinalizePowerTransitionAsync(
                schedule.Id,
                resolution.Status,
                resolution.EventType,
                resolution.Result,
                schedule.ScheduledLocalDateTime,
                resolution.Message + $" ({evidence.Detail})",
                expected,
                actualTimeUtc: evidence.CompletionTimeUtc ?? nowUtc,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            if (!changed)
            {
                continue;
            }

            if (canRepairSystem)
            {
                _registrar.Remove(schedule.Id);
            }

            _logger.Information("power-transition.reconciled",
                $"schedule={schedule.Id:D};status={resolution.Status};entry={evidence.EntryObserved};completion={evidence.CompletionObserved}");
        }
    }

    private static PowerTransitionResolution? Resolve(
        PowerSchedule schedule,
        PowerTransitionEvidence evidence,
        bool hasExecutionEvidence,
        DateTimeOffset startedAtUtc,
        DateTimeOffset nowUtc)
    {
        if (evidence.CompletionObserved)
        {
            return new PowerTransitionResolution(
                ScheduleStatus.Completed,
                "PowerTransitionRecovered",
                ResultKind.Success,
                $"{ActionName(schedule.ActionType)} 전원 전환과 복귀 증거를 확인해 완료 처리했습니다.");
        }

        if (IsTaskFailure(evidence.Task))
        {
            return new PowerTransitionResolution(
                ScheduleStatus.Failed,
                "PowerTransitionFailed",
                ResultKind.Failure,
                $"{ActionName(schedule.ActionType)} 작업이 오류 결과로 종료되어 실패 처리했습니다.");
        }

        if (!evidence.EventLogAvailable && !evidence.Task.Exists)
        {
            return null;
        }

        if (nowUtc < startedAtUtc + PowerTransitionPolicy.EvidenceGracePeriod || evidence.Task.Running)
        {
            return null;
        }

        var taskRan = evidence.Task.LastRunTime is DateTime lastRun &&
                      lastRun >= schedule.ScheduledLocalDateTime - PowerTransitionPolicy.InvocationTolerance;
        if (evidence.EntryObserved || hasExecutionEvidence || taskRan)
        {
            return new PowerTransitionResolution(
                ScheduleStatus.ResultUnknown,
                "PowerTransitionResultUnknown",
                ResultKind.Warning,
                $"{ActionName(schedule.ActionType)} 실행은 시작됐지만 완료 여부를 입증할 증거가 부족합니다.");
        }

        return new PowerTransitionResolution(
            ScheduleStatus.Missed,
            "ScheduleMissed",
            ResultKind.Warning,
            "미실행 — 예약 시각에 전원 작업이 실행된 증거가 없습니다.");
    }

    private static bool IsTaskFailure(PowerTaskSnapshot task)
    {
        if (!task.LastTaskResult.HasValue || task.LastTaskResult == 0)
        {
            return false;
        }

        return task.LastTaskResult is not (SchedulerTaskRunning or SchedulerTaskQueued or SchedulerTaskHasNotRun);
    }

    private static bool HasTerminalHistory(IEnumerable<ExecutionHistory> history) =>
        history.Any(item => item.EventType is
            "PowerTransitionCompleted" or
            "PowerTransitionFailed" or
            "PowerTransitionResultUnknown" or
            "PowerTransitionRecovered");

    private static DateTimeOffset ToUtc(DateTime local)
    {
        var unspecified = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        return new DateTimeOffset(unspecified, TimeZoneInfo.Local.GetUtcOffset(unspecified)).ToUniversalTime();
    }

    private static string ActionName(PowerActionType action) => action switch
    {
        PowerActionType.Hibernate => "최대 절전",
        PowerActionType.Sleep => "절전",
        PowerActionType.Shutdown => "완전 종료",
        _ => "전원"
    };

    private sealed record PowerTransitionResolution(
        ScheduleStatus Status,
        string EventType,
        ResultKind Result,
        string Message);
}
