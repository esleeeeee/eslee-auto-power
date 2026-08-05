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
    private int _busy;

    public TrayQuickShutdownController(
        Func<PowerSchedule, CancellationToken, Task<ValidationResult>> saveScheduleAsync,
        Func<Task> refreshAsync,
        Action<string, string, bool> showNotification,
        Action<string, string> logInformation,
        Action<string, string> logWarning,
        Action<string, Exception> logError,
        Func<DateTime>? localNow = null)
    {
        _saveScheduleAsync = saveScheduleAsync ?? throw new ArgumentNullException(nameof(saveScheduleAsync));
        _refreshAsync = refreshAsync ?? throw new ArgumentNullException(nameof(refreshAsync));
        _showNotification = showNotification ?? throw new ArgumentNullException(nameof(showNotification));
        _logInformation = logInformation ?? throw new ArgumentNullException(nameof(logInformation));
        _logWarning = logWarning ?? throw new ArgumentNullException(nameof(logWarning));
        _logError = logError ?? throw new ArgumentNullException(nameof(logError));
        _localNow = localNow ?? (() => DateTime.Now);
    }

    public bool IsBusy => Volatile.Read(ref _busy) != 0;

    public event Action<bool>? BusyChanged;

    public async Task<TrayQuickShutdownOutcome> ExecuteAsync(
        int hours,
        CancellationToken cancellationToken = default)
    {
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
        {
            return TrayQuickShutdownOutcome.IgnoredWhileBusy;
        }

        BusyChanged?.Invoke(true);
        try
        {
            DateTime scheduledLocalTime;
            PowerSchedule schedule;
            try
            {
                scheduledLocalTime = ScheduleTimePolicy.QuickPowerTransition(_localNow(), hours);
                schedule = PowerSchedule.Create(scheduledLocalTime, PowerActionType.Shutdown);
                var validation = await _saveScheduleAsync(schedule, cancellationToken);
                if (!validation.IsValid)
                {
                    var technicalDetail = string.Join(
                        " | ",
                        validation.Issues.Select(issue => $"{issue.Code}: {issue.Message}"));
                    _logWarning("tray.quick-shutdown.validation-failed", technicalDetail);
                    _showNotification(
                        AppText.T("완전 종료 예약 생성 실패"),
                        string.Join(Environment.NewLine, validation.Issues.Select(issue => AppText.T(issue.Message))),
                        true);
                    return TrayQuickShutdownOutcome.ValidationFailed;
                }
            }
            catch (Exception error)
            {
                _logError("tray.quick-shutdown.failed", error);
                _showNotification(
                    AppText.T("완전 종료 예약 생성 실패"),
                    AppText.T("완전 종료 예약을 만들지 못했습니다. 진단 로그에서 자세한 내용을 확인할 수 있습니다."),
                    true);
                return TrayQuickShutdownOutcome.Failed;
            }

            // The schedule and its Task Scheduler entry are durably committed at this point.
            // A refresh failure must never be reported as a failed schedule and must not
            // trigger a second save, so the user sees exactly one accurate notification.
            _logInformation(
                "tray.quick-shutdown.created",
                $"schedule={schedule.Id:D}; hours={hours}; localTime={scheduledLocalTime:O}; action={schedule.ActionType}");
            try
            {
                await _refreshAsync();
            }
            catch (Exception refreshError)
            {
                _logError("tray.quick-shutdown.refresh-failed", refreshError);
                _showNotification(
                    AppText.T("완전 종료 예약 생성 완료"),
                    AppText.F("완전 종료가 {0:t}으로 예약되었습니다. 화면을 새로고치지 못했지만 예약은 정상적으로 저장되었습니다.", scheduledLocalTime),
                    false);
                return TrayQuickShutdownOutcome.Created;
            }

            _showNotification(
                AppText.T("완전 종료 예약 생성 완료"),
                AppText.F("완전 종료가 {0:t}으로 예약되었습니다.", scheduledLocalTime),
                false);
            return TrayQuickShutdownOutcome.Created;
        }
        finally
        {
            Volatile.Write(ref _busy, 0);
            BusyChanged?.Invoke(false);
        }
    }
}
