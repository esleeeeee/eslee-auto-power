using System.Globalization;
using AutoPower.Core;
using AutoPower.Data;

namespace AutoPower.Windows;

public sealed class ShutdownPowerTransitionWorkflow
{
    private readonly SqliteStore _store;
    private readonly IShutdownFallbackRegistrar _registrar;
    private readonly IShutdownController _shutdown;
    private readonly TechnicalLogger _logger;
    private readonly Func<DateTime> _localNow;

    public ShutdownPowerTransitionWorkflow(
        SqliteStore store,
        IShutdownFallbackRegistrar registrar,
        IShutdownController shutdown,
        TechnicalLogger logger,
        Func<DateTime>? localNow = null)
    {
        _store = store;
        _registrar = registrar;
        _shutdown = shutdown;
        _logger = logger;
        _localNow = localNow ?? (() => DateTime.Now);
    }

    public async Task<ShutdownExecutionResult?> StartPrimaryAsync(
        PowerSchedule schedule,
        PendingOperation operation,
        CancellationToken cancellationToken = default)
    {
        EnsureShutdownOperation(schedule, operation);
        var fallbackAt = _localNow() + WarningPolicy.ShutdownGracePeriod;
        try
        {
            _registrar.RegisterShutdownFallback(schedule.Id, fallbackAt);
        }
        catch (Exception error)
        {
            await _store.TryFinalizePowerTransitionAsync(
                schedule.Id,
                ScheduleStatus.Failed,
                "PowerTransitionFailed",
                ResultKind.Failure,
                schedule.ScheduledLocalDateTime,
                "강제 종료 fallback 작업을 등록하지 못해 완전 종료 절차를 중단했습니다.",
                [ScheduleStatus.PendingPowerTransition],
                "FALLBACK_REGISTRATION_FAILED",
                cancellationToken: cancellationToken).ConfigureAwait(false);
            _logger.Error(
                "shutdown.fallback-registration-failed",
                error,
                $"schedule={schedule.Id:D};action={schedule.ActionType};task={TaskSchedulerService.ShutdownFallbackTaskName(schedule.Id)};time={fallbackAt:O}");
            throw;
        }

        await RecordStageAsync(
            schedule,
            operation,
            PendingOperationState.Pending,
            "FallbackPending",
            "ShutdownFallbackPending",
            ResultKind.Information,
            $"30초 뒤 강제 종료 fallback 작업을 준비했습니다. ({fallbackAt:O})",
            null,
            cancellationToken).ConfigureAwait(false);
        _logger.Information(
            "shutdown.fallback-pending",
            $"schedule={schedule.Id:D};action={schedule.ActionType};task={TaskSchedulerService.ShutdownFallbackTaskName(schedule.Id)};time={fallbackAt:O};operationBefore=Pending;operationAfter=Pending");

        ShutdownExecutionResult result;
        try
        {
            result = await _shutdown.StartGracefulShutdownAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            await RecordStageAsync(
                schedule,
                operation,
                PendingOperationState.Compensating,
                "PrimaryRejected;FallbackPending;source=ManagedException",
                "ShutdownPrimaryRejected",
                ResultKind.Warning,
                "정상 종료 호출 중 예기치 않은 오류가 발생해 강제 종료 fallback을 유지합니다.",
                "MANAGED_EXCEPTION",
                cancellationToken).ConfigureAwait(false);
            _logger.Error(
                "shutdown.primary-unexpected",
                error,
                $"schedule={schedule.Id:D};action={schedule.ActionType};operationBefore=Pending;operationAfter=Compensating;fallback=Pending");
            return null;
        }

        if (result.Accepted)
        {
            await RecordStageAsync(
                schedule,
                operation,
                PendingOperationState.Pending,
                $"PrimaryAccepted;FallbackPending;{NativeSummary(result)}",
                "ShutdownPrimaryAccepted",
                ResultKind.Information,
                "Windows가 30초 유예 후 완전 종료 요청을 접수했습니다.",
                NativeCode(result),
                cancellationToken).ConfigureAwait(false);
            _logger.Information(
                "shutdown.primary-accepted",
                $"schedule={schedule.Id:D};action={schedule.ActionType};operationBefore=Pending;operationAfter=Pending;fallback=Pending;{WindowsShutdownController.FormatDiagnostic(result)}");
        }
        else
        {
            await RecordStageAsync(
                schedule,
                operation,
                PendingOperationState.Compensating,
                $"PrimaryRejected;FallbackPending;{NativeSummary(result)}",
                "ShutdownPrimaryRejected",
                ResultKind.Warning,
                $"Windows가 정상 종료 요청을 거부했습니다. 강제 종료 fallback을 유지합니다. ({result.SymbolicError})",
                NativeCode(result),
                cancellationToken).ConfigureAwait(false);
            _logger.Warning(
                "shutdown.primary-rejected",
                $"schedule={schedule.Id:D};action={schedule.ActionType};operationBefore=Pending;operationAfter=Compensating;fallback=Pending;{WindowsShutdownController.FormatDiagnostic(result)}");
        }

        return result;
    }

    public async Task<ShutdownExecutionResult?> ExecuteFallbackAsync(
        PowerSchedule schedule,
        CancellationToken cancellationToken = default)
    {
        var operation = (await _store.GetIncompleteOperationsAsync(cancellationToken).ConfigureAwait(false))
            .Where(item =>
                item.Type == PendingOperationType.PowerTransition &&
                item.ScheduleId == schedule.Id &&
                item.State is PendingOperationState.Pending or PendingOperationState.Compensating)
            .OrderByDescending(item => item.CreatedAtUtc)
            .FirstOrDefault()
            ?? throw new InvalidOperationException("완전 종료 fallback에 필요한 pending operation을 찾을 수 없습니다.");

        await RecordStageAsync(
            schedule,
            operation,
            PendingOperationState.Compensating,
            "FallbackInvoked",
            "ShutdownFallbackInvoked",
            ResultKind.Warning,
            "30초 유예 후 강제 종료 fallback을 실행했습니다.",
            null,
            cancellationToken).ConfigureAwait(false);
        _logger.Warning(
            "shutdown.fallback-invoked",
            $"schedule={schedule.Id:D};action={schedule.ActionType};task={TaskSchedulerService.ShutdownFallbackTaskName(schedule.Id)};operationBefore={operation.State};operationAfter=Compensating;fallback=True");

        ShutdownExecutionResult result;
        try
        {
            result = await _shutdown.ForceShutdownAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            await FinalizeFallbackFailureAsync(
                schedule,
                "MANAGED_EXCEPTION",
                "강제 종료 fallback 호출 중 예기치 않은 오류가 발생했습니다.",
                cancellationToken).ConfigureAwait(false);
            _registrar.Remove(schedule.Id);
            _logger.Error(
                "shutdown.fallback-unexpected",
                error,
                $"schedule={schedule.Id:D};action={schedule.ActionType};operationBefore=Compensating;operationAfter=Failed;fallback=True");
            throw;
        }

        if (result.Accepted)
        {
            await RecordStageAsync(
                schedule,
                operation,
                PendingOperationState.Compensating,
                $"FallbackAccepted;{NativeSummary(result)}",
                "ShutdownFallbackAccepted",
                ResultKind.Warning,
                $"강제 종료 fallback 요청이 접수됐습니다. ({result.Disposition})",
                NativeCode(result),
                cancellationToken).ConfigureAwait(false);
            _logger.Warning(
                "shutdown.fallback-accepted",
                $"schedule={schedule.Id:D};action={schedule.ActionType};operationBefore=Compensating;operationAfter=Compensating;fallback=True;{WindowsShutdownController.FormatDiagnostic(result)}");
            return result;
        }

        await FinalizeFallbackFailureAsync(
            schedule,
            NativeCode(result),
            $"강제 종료 fallback 요청도 거부되어 최종 실패했습니다. ({result.SymbolicError})",
            cancellationToken).ConfigureAwait(false);
        _registrar.Remove(schedule.Id);
        _logger.Warning(
            "shutdown.fallback-rejected",
            $"schedule={schedule.Id:D};action={schedule.ActionType};operationBefore=Compensating;operationAfter=Failed;fallback=True;{WindowsShutdownController.FormatDiagnostic(result)}");
        return result;
    }

    private async Task RecordStageAsync(
        PowerSchedule schedule,
        PendingOperation operation,
        PendingOperationState state,
        string detail,
        string eventType,
        ResultKind result,
        string message,
        string? errorCode,
        CancellationToken cancellationToken)
    {
        var changed = await _store.RecordPowerTransitionStageAsync(
            operation.Id,
            schedule.Id,
            state,
            detail,
            eventType,
            result,
            schedule.ScheduledLocalDateTime,
            message,
            errorCode,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!changed)
        {
            throw new InvalidOperationException($"전원 전환 단계 {eventType}를 pending journal에 기록하지 못했습니다.");
        }
    }

    private async Task FinalizeFallbackFailureAsync(
        PowerSchedule schedule,
        string errorCode,
        string message,
        CancellationToken cancellationToken)
    {
        await _store.TryFinalizePowerTransitionAsync(
            schedule.Id,
            ScheduleStatus.Failed,
            "PowerTransitionFailed",
            ResultKind.Failure,
            schedule.ScheduledLocalDateTime,
            message,
            [ScheduleStatus.PendingPowerTransition],
            errorCode,
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private static void EnsureShutdownOperation(PowerSchedule schedule, PendingOperation operation)
    {
        if (schedule.ActionType != PowerActionType.Shutdown ||
            operation.Type != PendingOperationType.PowerTransition ||
            operation.ScheduleId != schedule.Id)
        {
            throw new InvalidOperationException("완전 종료 예약과 pending operation이 일치하지 않습니다.");
        }
    }

    private static string NativeCode(ShutdownExecutionResult result) =>
        result.NativeErrorCode.ToString(CultureInfo.InvariantCulture);

    private static string NativeSummary(ShutdownExecutionResult result) =>
        $"disposition={result.Disposition};native={result.NativeErrorCode};symbolic={result.SymbolicError};fallback={result.Request.Stage == ShutdownStage.Fallback}";
}
