namespace AutoPower.Core;

public sealed class ScheduleValidator
{
    public static ValidationResult Validate(
        PowerSchedule schedule,
        DateTime nowLocal,
        IEnumerable<PowerSchedule> existingSchedules)
    {
        ArgumentNullException.ThrowIfNull(schedule);
        ArgumentNullException.ThrowIfNull(existingSchedules);

        var issues = new List<ValidationIssue>();
        var planned = DateTime.SpecifyKind(schedule.ScheduledLocalDateTime, DateTimeKind.Unspecified);
        var now = DateTime.SpecifyKind(nowLocal, DateTimeKind.Unspecified);
        if (planned <= now)
        {
            issues.Add(new("past", "예약 시각은 현재보다 이후여야 합니다."));
        }

        if (PowerSchedulePolicy.IsRemovedSchedule(schedule.ActionType) && schedule.IsEnabled)
        {
            issues.Add(new(
                "removed-power-action",
                "이전 버전의 앱 기반 완전 종료 자동 부팅 예약은 더 이상 사용할 수 없습니다. 자동 시작, 완전 종료, 최대 절전 또는 절전을 선택하세요."));
        }

        if (!PowerSchedulePolicy.IsWakeSchedule(schedule.ActionType) &&
                 !PowerSchedulePolicy.IsRemovedSchedule(schedule.ActionType) &&
                 (schedule.OneTimeAutoLogonEnabled || schedule.FollowUpPrograms.Count > 0))
        {
            issues.Add(new("wake-options", "1회 자동 로그인과 후속 프로그램은 S3/S4 예약 깨우기에서만 사용할 수 있습니다."));
        }

        foreach (var program in schedule.FollowUpPrograms)
        {
            if (program.DelayAfterDesktopReadyMinutes < 0)
            {
                issues.Add(new("negative-delay", "후속 프로그램 지연 시간은 0분 이상이어야 합니다."));
            }

            if (string.IsNullOrWhiteSpace(program.ExecutablePath) ||
                !Path.IsPathFullyQualified(program.ExecutablePath))
            {
                issues.Add(new("program-path", "후속 프로그램은 올바른 전체 경로를 사용해야 합니다."));
            }
        }

        foreach (var other in existingSchedules.Where(candidate =>
                     candidate.Id != schedule.Id && candidate.IsEnabled &&
                     candidate.Status == ScheduleStatus.Pending &&
                     candidate.ScheduledLocalDateTime == planned))
        {
            if (other.ActionType != schedule.ActionType)
            {
                issues.Add(new("conflicting-action", "같은 시각에 서로 다른 전원 동작을 예약할 수 없습니다."));
                break;
            }

            if (Equivalent(other, schedule))
            {
                issues.Add(new("duplicate", "완전히 동일한 예약이 이미 있습니다."));
                break;
            }
        }

        if (schedule.IsEnabled && schedule.Status == ScheduleStatus.Pending)
        {
            var active = existingSchedules.Where(candidate =>
                candidate.Id != schedule.Id &&
                candidate.IsEnabled &&
                candidate.Status == ScheduleStatus.Pending &&
                candidate.ScheduledLocalDateTime > now);
            var lowPowerEntry = schedule.ActionType is PowerActionType.Sleep or PowerActionType.Hibernate
                ? schedule
                : PowerSchedulePolicy.IsWakeSchedule(schedule.ActionType)
                    ? active
                        .Where(candidate =>
                            candidate.ActionType is PowerActionType.Sleep or PowerActionType.Hibernate &&
                            candidate.ScheduledLocalDateTime < planned)
                        .OrderByDescending(candidate => candidate.ScheduledLocalDateTime)
                        .FirstOrDefault()
                    : null;
            var wakeAfterEntry = lowPowerEntry is null
                ? null
                : lowPowerEntry.Id == schedule.Id
                    ? active
                        .Where(candidate =>
                            PowerSchedulePolicy.IsWakeSchedule(candidate.ActionType) &&
                            candidate.ScheduledLocalDateTime > lowPowerEntry.ScheduledLocalDateTime)
                        .OrderBy(candidate => candidate.ScheduledLocalDateTime)
                        .FirstOrDefault()
                    : schedule;
            if (lowPowerEntry is not null && wakeAfterEntry is not null)
            {
                var expected = lowPowerEntry.ActionType == PowerActionType.Sleep
                    ? PowerActionType.WakeFromSleep
                    : PowerActionType.WakeFromHibernate;
                if (wakeAfterEntry.ActionType != expected)
                {
                    issues.Add(new(
                        "power-state-mismatch",
                        "전원 진입 예약과 깨우기 예약의 상태가 일치하지 않습니다. S3 절전은 S3 깨우기, S4 최대 절전은 S4 깨우기와 연결하세요."));
                }
            }
        }

        return new ValidationResult(issues);
    }

    public static bool NeedsImminentWarning(PowerSchedule schedule, DateTime nowLocal) =>
        schedule.ScheduledLocalDateTime > nowLocal &&
        schedule.ScheduledLocalDateTime - nowLocal < TimeSpan.FromMinutes(2);

    public static PowerSchedule? SelectNextWake(IEnumerable<PowerSchedule> schedules, DateTime nowLocal) =>
        schedules
            .Where(schedule => schedule.IsEnabled &&
                               schedule.Status == ScheduleStatus.Pending &&
                               PowerSchedulePolicy.IsWakeSchedule(schedule.ActionType) &&
                               schedule.ScheduledLocalDateTime > nowLocal)
            .OrderBy(schedule => schedule.ScheduledLocalDateTime)
            .FirstOrDefault();

    private static bool Equivalent(PowerSchedule left, PowerSchedule right)
    {
        if (left.ActionType != right.ActionType ||
            left.OneTimeAutoLogonEnabled != right.OneTimeAutoLogonEnabled ||
            left.FollowUpPrograms.Count != right.FollowUpPrograms.Count)
        {
            return false;
        }

        return left.FollowUpPrograms.OrderBy(program => program.SortOrder)
            .Zip(right.FollowUpPrograms.OrderBy(program => program.SortOrder))
            .All(pair => string.Equals(pair.First.ExecutablePath, pair.Second.ExecutablePath, StringComparison.OrdinalIgnoreCase) &&
                         pair.First.DelayAfterDesktopReadyMinutes == pair.Second.DelayAfterDesktopReadyMinutes &&
                         string.Equals(pair.First.Arguments ?? string.Empty, pair.Second.Arguments ?? string.Empty, StringComparison.Ordinal) &&
                         string.Equals(pair.First.WorkingDirectory ?? string.Empty, pair.Second.WorkingDirectory ?? string.Empty, StringComparison.OrdinalIgnoreCase) &&
                         pair.First.RunElevated == pair.Second.RunElevated);
    }
}
