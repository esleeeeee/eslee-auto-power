using AutoPower.Core;

namespace AutoPower.App;

internal enum TrayQuickShutdownOutcome
{
    Created,
    ValidationFailed,
    Failed,
    IgnoredWhileBusy
}

internal sealed class TrayQuickShutdownController
{
    private readonly Func<PowerSchedule, CancellationToken, Task<ValidationResult>> _saveScheduleAsync;
    private readonly Func<Task> _refreshAsync;
    private readonly Action<string, string, bool> _showNotification;
    private readonly Action<string, string> _logInformation;
    private readonly Action<string, string> _logWarning;
    private readonly Action<string, Exception> _logError;
    private readonly Func<DateTime> _localNow;
    private readonly Func<CancellationToken, Task<IReadOnlyList<PowerSchedule>>> _getSchedulesAsync;
    private int _busy;

    public TrayQuickShutdownController(
        Func<PowerSchedule, CancellationToken, Task<ValidationResult>> saveScheduleAsync,
        Func<Task> refreshAsync,
        Action<string, string, bool> showNotification,
        Action<string, string> logInformation,
        Action<string, string> logWarning,
        Action<string, Exception> logError,
        Func<DateTime>? localNow = null,
        Func<CancellationToken, Task<IReadOnlyList<PowerSchedule>>>? getSchedulesAsync = null)
    {
        _saveScheduleAsync = saveScheduleAsync ?? throw new ArgumentNullException(nameof(saveScheduleAsync));
        _refreshAsync = refreshAsync ?? throw new ArgumentNullException(nameof(refreshAsync));
        _showNotification = showNotification ?? throw new ArgumentNullException(nameof(showNotification));
        _logInformation = logInformation ?? throw new ArgumentNullException(nameof(logInformation));
        _logWarning = logWarning ?? throw new ArgumentNullException(nameof(logWarning));
        _logError = logError ?? throw new ArgumentNullException(nameof(logError));
        _localNow = localNow ?? (() => DateTime.Now);
        _getSchedulesAsync = getSchedulesAsync ?? (_ => Task.FromResult<IReadOnlyList<PowerSchedule>>([]));
    }

    public bool IsBusy => Volatile.Read(ref _busy) != 0;

    public event Action<bool>? BusyChanged;

    public Task<TrayQuickShutdownOutcome> ExecuteAsync(
        int hours,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(PowerActionType.Shutdown, hours * 60, cancellationToken);

    /// <summary>Creates a full shutdown, hibernate or sleep schedule <paramref name="minutes"/> from now.</summary>
    public async Task<TrayQuickShutdownOutcome> ExecuteAsync(
        PowerActionType action,
        int minutes,
        CancellationToken cancellationToken = default)
    {
        if (!QuickSchedulePolicy.IsSupportedAction(action))
        {
            throw new ArgumentOutOfRangeException(nameof(action));
        }

        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
        {
            return TrayQuickShutdownOutcome.IgnoredWhileBusy;
        }

        BusyChanged?.Invoke(true);
        try
        {
            DateTime scheduledLocalTime;
            PowerSchedule schedule;
            var consequence = string.Empty;
            try
            {
                scheduledLocalTime = QuickSchedulePolicy.At(_localNow(), minutes);
                schedule = PowerSchedule.Create(scheduledLocalTime, action);
                if (action == PowerActionType.Shutdown)
                {
                    var nextWake = ScheduleValidator.SelectNextWake(await _getSchedulesAsync(cancellationToken), scheduledLocalTime);
                    if (nextWake is not null) consequence = Environment.NewLine + ShutdownConsequence.Describe(nextWake);
                }

                var validation = await _saveScheduleAsync(schedule, cancellationToken);
                if (!validation.IsValid)
                {
                    var technicalDetail = string.Join(
                        " | ",
                        validation.Issues.Select(issue => $"{issue.Code}: {issue.Message}"));
                    _logWarning("tray.quick-shutdown.validation-failed", technicalDetail);
                    _showNotification(
                        FailedTitle(action),
                        string.Join(Environment.NewLine, validation.Issues.Select(issue => AppText.T(issue.Message))),
                        true);
                    return TrayQuickShutdownOutcome.ValidationFailed;
                }
            }
            catch (Exception error)
            {
                _logError("tray.quick-shutdown.failed", error);
                _showNotification(
                    FailedTitle(action),
                    action == PowerActionType.Shutdown
                        ? AppText.T("완전 종료 예약을 만들지 못했습니다. 진단 로그에서 자세한 내용을 확인할 수 있습니다.")
                        : AppText.F("{0} 예약을 만들지 못했습니다. 진단 로그에서 자세한 내용을 확인할 수 있습니다.", ActionName(action)),
                    true);
                return TrayQuickShutdownOutcome.Failed;
            }

            // The schedule and its Task Scheduler entry are durably committed at this point.
            // A refresh failure must never be reported as a failed schedule and must not
            // trigger a second save, so the user sees exactly one accurate notification.
            _logInformation(
                "tray.quick-shutdown.created",
                $"schedule={schedule.Id:D}; minutes={minutes}; localTime={scheduledLocalTime:O}; action={schedule.ActionType}");
            try
            {
                await _refreshAsync();
            }
            catch (Exception refreshError)
            {
                _logError("tray.quick-shutdown.refresh-failed", refreshError);
                _showNotification(
                    CreatedTitle(action),
                    (action == PowerActionType.Shutdown
                        ? AppText.F("완전 종료가 {0:t}으로 예약되었습니다. 화면을 새로고치지 못했지만 예약은 정상적으로 저장되었습니다.", scheduledLocalTime)
                        : AppText.F("{0}이 {1:t}으로 예약되었습니다. 화면을 새로고치지 못했지만 예약은 정상적으로 저장되었습니다.", ActionName(action), scheduledLocalTime)) + consequence,
                    false);
                return TrayQuickShutdownOutcome.Created;
            }

            _showNotification(
                CreatedTitle(action),
                (action == PowerActionType.Shutdown
                    ? AppText.F("완전 종료가 {0:t}으로 예약되었습니다.", scheduledLocalTime)
                    : AppText.F("{0}이 {1:t}으로 예약되었습니다.", ActionName(action), scheduledLocalTime)) + consequence,
                false);
            return TrayQuickShutdownOutcome.Created;
        }
        finally
        {
            Volatile.Write(ref _busy, 0);
            BusyChanged?.Invoke(false);
        }
    }

    private static string ActionName(PowerActionType action) =>
        AppText.T(action == PowerActionType.Hibernate ? "최대 절전" : "절전");

    private static string CreatedTitle(PowerActionType action) => action == PowerActionType.Shutdown
        ? AppText.T("완전 종료 예약 생성 완료")
        : AppText.F("{0} 예약 생성 완료", ActionName(action));

    private static string FailedTitle(PowerActionType action) => action == PowerActionType.Shutdown
        ? AppText.T("완전 종료 예약 생성 실패")
        : AppText.F("{0} 예약 생성 실패", ActionName(action));
}
