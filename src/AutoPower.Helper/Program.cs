using AutoPower.Core;
using AutoPower.Data;
using AutoPower.Windows;

return await HelperProgram.RunAsync(args).ConfigureAwait(false);

internal static class HelperProgram
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length == 0)
        {
            return 64;
        }

        AppPaths.EnsureCreated();
        var logger = new TechnicalLogger();
        var store = new SqliteStore(AppPaths.DatabasePath);
        await store.InitializeAsync().ConfigureAwait(false);
        var layout = InstallationLayout.FromBaseDirectory(AppContext.BaseDirectory);
        var registrar = new TaskSchedulerService(layout, logger);
        var userCredentials = new CredentialManager();
        var credentials = new LsaAppCredentialStore();
        var journalStore = new JsonAutologonJournalStore();
        var autologonSystem = new WindowsAutologonSystem();
        var autologon = new AutologonManager(credentials, autologonSystem, journalStore, logger);
        var resumeSignIn = new ResumeSignInManager(
            new WindowsResumeSignInSettings(),
            new JsonResumeSignInJournalStore(),
            logger);

        try
        {
            var command = args[0].ToLowerInvariant();
            switch (command)
            {
                case "register-schedule":
                {
                    var id = RequiredScheduleId(args);
                    var schedule = await RequiredScheduleAsync(store, id).ConfigureAwait(false);
                    registrar.Register(schedule);
                    registrar.RegisterStartupAgent(TaskSchedulerService.CurrentUserName());
                    CleanupRemovedSecurityState(autologon);
                    await RestoreResumeSignInAsync(resumeSignIn, store, null,
                        "다른 작업을 등록하기 전에 남아 있던 S3/S4 1회 자동 로그인 설정을 복원했습니다.").ConfigureAwait(false);
                    return 0;
                }
                case "remove-schedule":
                {
                    var id = RequiredScheduleId(args);
                    registrar.Remove(id);
                    var journal = autologon.CurrentJournal;
                    if (journal is not null && journal.ScheduleId == id && journal.State is not AutologonJournalState.Cleaned)
                    {
                        autologon.Cleanup();
                    }

                    await RestoreResumeSignInAsync(resumeSignIn, store, id,
                        "예약 제거와 함께 S3/S4 1회 자동 로그인 설정을 복원했습니다.").ConfigureAwait(false);

                    return 0;
                }
                case "cleanup-autologon":
                {
                    var result = autologon.Cleanup();
                    if (result is not null)
                    {
                        await store.AddHistoryAsync(result.ScheduleId, "RemovedSecurityCleanup", ResultKind.Success, result.ScheduledLocalTime,
                            "이전 버전의 보안 설정을 제거하고 검증했습니다.").ConfigureAwait(false);
                    }

                    return 0;
                }
                case "sync-credential":
                {
                    var transferPath = RequiredCredentialTransferPath(args);
                    var transferred = CredentialTransferFile.ReadAndDelete(transferPath);
                    var incoming = transferred with { UserName = WindowsCredentialIdentity.Normalize(transferred.UserName) };
                    var previous = credentials.Read();
                    autologonSystem.ValidateCredential(incoming);
                    try
                    {
                        credentials.Save(incoming.UserName, incoming.Password);
                        var verified = credentials.Read();
                        if (verified is null ||
                            !string.Equals(verified.UserName, incoming.UserName, StringComparison.Ordinal) ||
                            !string.Equals(verified.Password, incoming.Password, StringComparison.Ordinal))
                        {
                            throw new InvalidOperationException("보호 저장소에 저장한 Windows 로그인 자격 증명을 다시 확인하지 못했습니다.");
                        }
                    }
                    catch
                    {
                        if (previous is null)
                        {
                            credentials.Delete();
                        }
                        else
                        {
                            credentials.Save(previous.UserName, previous.Password);
                        }

                        throw;
                    }

                    await store.AddHistoryAsync(null, "CredentialSaved", ResultKind.Success, null,
                        "Windows 로그인 자격 증명을 검증하고 보호 저장소에 등록했습니다.").ConfigureAwait(false);
                    return 0;
                }
                case "test-credential" when args.Length == 1:
                {
                    var saved = credentials.Read()
                                ?? throw new InvalidOperationException("설정에서 Windows 로그인 자격 증명을 먼저 등록하세요.");
                    var normalized = saved with { UserName = WindowsCredentialIdentity.Normalize(saved.UserName) };
                    autologonSystem.ValidateCredential(normalized);
                    if (!string.Equals(saved.UserName, normalized.UserName, StringComparison.Ordinal))
                    {
                        credentials.Save(normalized.UserName, normalized.Password);
                    }
                    await store.AddHistoryAsync(null, "CredentialTest", ResultKind.Success, null,
                        "저장된 Windows 계정과 암호로 실제 로그온 자격 증명 검증에 성공했습니다.").ConfigureAwait(false);
                    return 0;
                }
                case "delete-credential" when args.Length == 1:
                {
                    var active = autologon.CurrentJournal;
                    if (active is not null && active.State is not AutologonJournalState.Cleaned)
                    {
                        autologon.Cleanup();
                    }

                    credentials.Delete();
                    userCredentials.Delete();
                    await store.AddHistoryAsync(null, "CredentialRemoved", ResultKind.Success, null,
                        "앱이 저장한 Windows 로그인 자격 증명을 제거했습니다.").ConfigureAwait(false);
                    return 0;
                }
                case "mark-wake":
                {
                    var id = RequiredScheduleId(args);
                    var schedule = await RequiredScheduleAsync(store, id).ConfigureAwait(false);
                    ValidateWakeInvocation(schedule);
                    await RestoreResumeSignInAfterDelayAsync(resumeSignIn, store, id,
                        "예약 시각에 PC가 깨어나 데스크톱 복귀 유예 후 원래 로그인 요구 설정을 복원했습니다.").ConfigureAwait(false);
                    await store.UpdateScheduleStateAsync(id, false, ScheduleStatus.Completed).ConfigureAwait(false);
                    await store.AddHistoryAsync(id, "WakeTask", ResultKind.Information, schedule.ScheduledLocalDateTime,
                        $"{PowerSchedulePolicy.WakeStateName(schedule.ActionType)} 예약 시각에 WakeToRun 작업이 실행되었습니다.").ConfigureAwait(false);
                    return 0;
                }
                case "prepare-wake":
                {
                    var id = RequiredScheduleId(args);
                    var schedule = await RequiredScheduleAsync(store, id).ConfigureAwait(false);
                    if (!PowerSchedulePolicy.IsWakeSchedule(schedule.ActionType) ||
                        !schedule.IsEnabled ||
                        schedule.Status != ScheduleStatus.Pending ||
                        schedule.ScheduledLocalDateTime < DateTime.Now + TimeSpan.FromMinutes(1))
                    {
                        throw new InvalidOperationException("1분 이상 남은 활성 S3/S4 깨우기 예약만 준비할 수 있습니다.");
                    }

                    var snapshot = PowerCapabilityDetector.Detect();
                    if (schedule.ActionType == PowerActionType.WakeFromSleep && !snapshot.S3Available)
                    {
                        throw new InvalidOperationException("현재 시스템은 S3 절전을 지원하지 않습니다.");
                    }

                    if (schedule.ActionType == PowerActionType.WakeFromHibernate && (!snapshot.S4Available || !snapshot.HibernateEnabled))
                    {
                        throw new InvalidOperationException("Windows 최대 절전 기능이 활성화된 S4 지원 시스템에서만 이 예약을 준비할 수 있습니다.");
                    }

                    await RestoreResumeSignInAsync(resumeSignIn, store, null,
                        "새 예약을 준비하기 전에 남아 있던 S3/S4 1회 자동 로그인 설정을 복원했습니다.").ConfigureAwait(false);
                    registrar.Register(schedule);
                    var stateName = PowerSchedulePolicy.WakeStateName(schedule.ActionType);
                    await store.AddHistoryAsync(id, "WakePrepared", ResultKind.Success, schedule.ScheduledLocalDateTime,
                        $"WakeToRun 등록을 확인하고 {stateName} 상태로 전환을 시작했습니다.").ConfigureAwait(false);
                    var autoLogonArmed = false;
                    if (schedule.OneTimeAutoLogonEnabled)
                    {
                        var saved = credentials.Read()
                                    ?? throw new InvalidOperationException("설정에서 Windows 로그인 자격 증명을 먼저 등록하고 테스트하세요.");
                        autologonSystem.ValidateCredential(saved);
                        resumeSignIn.Arm(schedule.Id, schedule.ScheduledLocalDateTime);
                        autoLogonArmed = true;
                        await store.AddHistoryAsync(id, "ResumeSignInArmed", ResultKind.Success,
                            schedule.ScheduledLocalDateTime,
                            "S3/S4 복귀 1회에 한해 잠금 화면을 건너뛰도록 준비했습니다. 원래 설정은 복귀 즉시 복원됩니다.").ConfigureAwait(false);
                    }

                    try
                    {
                        await new PowerActionExecutor().ExecuteAsync(
                            PowerSchedulePolicy.RequiredLowPowerState(schedule.ActionType)).ConfigureAwait(false);
                    }
                    finally
                    {
                        if (autoLogonArmed)
                        {
                            await RestoreResumeSignInAfterDelayAsync(resumeSignIn, store, id,
                                "S3/S4 상태에서 복귀하여 데스크톱 복귀 유예 후 원래 로그인 요구 설정을 복원했습니다.").ConfigureAwait(false);
                        }
                    }

                    await store.AddHistoryAsync(id, "WakeResumed", ResultKind.Information, schedule.ScheduledLocalDateTime,
                        $"{stateName} 상태에서 Windows 사용자 세션으로 복귀했습니다.").ConfigureAwait(false);
                    return 0;
                }
                case "enter-low-power":
                {
                    var state = RequiredLowPowerState(args);
                    var snapshot = PowerCapabilityDetector.Detect();
                    if (state == PowerActionType.Sleep && !snapshot.S3Available)
                    {
                        throw new InvalidOperationException("현재 시스템은 S3 절전을 지원하지 않습니다.");
                    }

                    if (state == PowerActionType.Hibernate && (!snapshot.S4Available || !snapshot.HibernateEnabled))
                    {
                        throw new InvalidOperationException("Windows 최대 절전 기능이 활성화된 S4 지원 시스템에서만 최대 절전을 사용할 수 있습니다.");
                    }

                    var stateName = state == PowerActionType.Sleep ? "S3 절전" : "S4 최대 절전";
                    await store.AddHistoryAsync(null, "ManualLowPower", ResultKind.Information, null,
                        $"사용자 선택으로 {stateName} 상태 전환을 시작했습니다.").ConfigureAwait(false);
                    await new PowerActionExecutor().ExecuteAsync(state).ConfigureAwait(false);
                    await store.AddHistoryAsync(null, "ManualLowPowerResume", ResultKind.Information, null,
                        $"{stateName} 상태에서 Windows 사용자 세션으로 복귀했습니다.").ConfigureAwait(false);
                    return 0;
                }
                case "restore-resume-signin" when args.Length == 1:
                {
                    await RestoreResumeSignInAsync(resumeSignIn, store, null,
                        "복구 요청으로 원래 S3/S4 로그인 요구 설정을 복원했습니다.").ConfigureAwait(false);
                    return 0;
                }
                case "execute-power":
                {
                    var id = RequiredScheduleId(args);
                    var schedule = await RequiredScheduleAsync(store, id).ConfigureAwait(false);
                    if (PowerSchedulePolicy.IsRemovedSchedule(schedule.ActionType) || PowerSchedulePolicy.IsWakeSchedule(schedule.ActionType))
                    {
                        throw new InvalidOperationException("예약 깨우기는 execute-power로 실행할 수 없습니다.");
                    }

                    ValidateScheduledInvocation(schedule, schedule.ActionType);
                    if (schedule.ActionType == PowerActionType.Shutdown)
                    {
                        await RecordShutdownWakeConflictAsync(store, schedule).ConfigureAwait(false);
                        registrar.RegisterShutdownFallback(id, DateTime.Now + WarningPolicy.ShutdownGracePeriod);
                    }
                    else
                    {
                        await EnsureNextWakeAsync(store, registrar, id).ConfigureAwait(false);
                    }
                    await store.AddHistoryAsync(id, "PowerAction", ResultKind.Information, schedule.ScheduledLocalDateTime,
                        $"{KoreanAction(schedule.ActionType)} 동작을 시작했습니다.").ConfigureAwait(false);
                    await store.UpdateScheduleStateAsync(id, false, ScheduleStatus.Completed).ConfigureAwait(false);
                    try
                    {
                        await new PowerActionExecutor().ExecuteAsync(schedule.ActionType).ConfigureAwait(false);
                    }
                    catch
                    {
                        if (schedule.ActionType == PowerActionType.Shutdown)
                        {
                            registrar.Remove(id);
                        }

                        await store.UpdateScheduleStateAsync(id, false, ScheduleStatus.Failed).ConfigureAwait(false);
                        throw;
                    }

                    return 0;
                }
                case "execute-power-now":
                {
                    var id = RequiredScheduleId(args);
                    var schedule = await RequiredScheduleAsync(store, id).ConfigureAwait(false);
                    if (PowerSchedulePolicy.IsRemovedSchedule(schedule.ActionType) || PowerSchedulePolicy.IsWakeSchedule(schedule.ActionType) ||
                        !schedule.IsEnabled || schedule.Status != ScheduleStatus.Pending ||
                        schedule.ScheduledLocalDateTime < DateTime.Now || schedule.ScheduledLocalDateTime > DateTime.Now + TimeSpan.FromMinutes(6))
                    {
                        throw new InvalidOperationException("5분 전 경고가 열린 활성 전원 예약만 지금 실행할 수 있습니다.");
                    }

                    registrar.Remove(id);
                    if (schedule.ActionType == PowerActionType.Shutdown)
                    {
                        await RecordShutdownWakeConflictAsync(store, schedule).ConfigureAwait(false);
                        registrar.RegisterShutdownFallback(id, DateTime.Now + WarningPolicy.ShutdownGracePeriod);
                    }
                    else
                    {
                        await EnsureNextWakeAsync(store, registrar, id).ConfigureAwait(false);
                    }
                    await store.UpdateScheduleStateAsync(id, false, ScheduleStatus.Completed).ConfigureAwait(false);
                    await store.AddHistoryAsync(id, "PowerAction", ResultKind.Information, schedule.ScheduledLocalDateTime,
                        $"사용자 선택으로 {KoreanAction(schedule.ActionType)} 동작을 지금 시작했습니다.").ConfigureAwait(false);
                    try
                    {
                        await new PowerActionExecutor().ExecuteAsync(schedule.ActionType).ConfigureAwait(false);
                    }
                    catch
                    {
                        if (schedule.ActionType == PowerActionType.Shutdown)
                        {
                            registrar.Remove(id);
                        }

                        await store.UpdateScheduleStateAsync(id, false, ScheduleStatus.Failed).ConfigureAwait(false);
                        throw;
                    }

                    return 0;
                }
                case "force-shutdown":
                {
                    var id = RequiredScheduleId(args);
                    var schedule = await RequiredScheduleAsync(store, id).ConfigureAwait(false);
                    if (!WarningPolicy.CanRunShutdownFallback(schedule, DateTime.Now))
                    {
                        throw new InvalidOperationException("완료 처리된 직전 완전 종료 예약의 30초 fallback만 실행할 수 있습니다.");
                    }
                    await store.AddHistoryAsync(id, "ShutdownForcedFallback", ResultKind.Warning,
                        schedule.ScheduledLocalDateTime,
                        "정상 종료가 30초 안에 완료되지 않아 강제 종료 fallback을 실행했습니다.").ConfigureAwait(false);
                    await new WindowsShutdownController().ForceShutdownAsync().ConfigureAwait(false);
                    return 0;
                }
                case "skip-schedule":
                {
                    var id = RequiredScheduleId(args);
                    var schedule = await RequiredScheduleAsync(store, id).ConfigureAwait(false);
                    registrar.Remove(id);
                    await store.UpdateScheduleStateAsync(id, false, ScheduleStatus.Skipped).ConfigureAwait(false);
                    await store.AddHistoryAsync(id, "ScheduleSkipped", ResultKind.Skipped, schedule.ScheduledLocalDateTime,
                        "사용자가 이번 예약을 건너뛰었습니다.").ConfigureAwait(false);
                    return 0;
                }
                case "run-sleep-wake-test":
                {
                    var (target, id) = RequiredSleepWakeTestArguments(args);
                    var stateStore = new SleepWakeTestStateStore();
                    var state = stateStore.Read()
                                ?? throw new InvalidOperationException("준비된 S3 또는 S4 Wake 테스트가 없습니다.");
                    var schedule = await RequiredScheduleAsync(store, id).ConfigureAwait(false);
                    var snapshot = PowerCapabilityDetector.Detect();
                    var supported = target switch
                    {
                        SleepWakeTestTarget.S3 => snapshot.S3Available,
                        SleepWakeTestTarget.S4 => snapshot.S4Available && snapshot.HibernateEnabled,
                        _ => false
                    };
                    if (!supported)
                    {
                        throw new InvalidOperationException(target == SleepWakeTestTarget.S4
                            ? "Windows 최대 절전 기능이 현재 비활성화되어 있어 S4 테스트를 시작할 수 없습니다."
                            : "현재 시스템은 S3 절전을 지원하지 않아 테스트를 시작할 수 없습니다.");
                    }

                    if (state.Stage != SleepWakeTestStage.Prepared ||
                        state.Target != target ||
                        state.ScheduleId != id ||
                        schedule.ActionType != (target == SleepWakeTestTarget.S3
                            ? PowerActionType.WakeFromSleep
                            : PowerActionType.WakeFromHibernate) ||
                        !schedule.IsEnabled ||
                        schedule.Status != ScheduleStatus.Pending ||
                        schedule.ScheduledLocalDateTime != state.RequestedWakeLocalTime ||
                        !schedule.OneTimeAutoLogonEnabled ||
                        schedule.ScheduledLocalDateTime < DateTime.Now + TimeSpan.FromMinutes(1) ||
                        schedule.ScheduledLocalDateTime > DateTime.Now + TimeSpan.FromMinutes(15))
                    {
                        throw new InvalidOperationException("S3/S4 테스트 Wake 상태가 안전 조건과 일치하지 않습니다.");
                    }

                    var eventType = target == SleepWakeTestTarget.S3 ? "S3WakeTest" : "S4WakeTest";
                    await RestoreResumeSignInAsync(resumeSignIn, store, null,
                        "새 S3/S4 실기 테스트를 시작하기 전에 남아 있던 로그인 요구 설정을 복원했습니다.").ConfigureAwait(false);
                    var saved = credentials.Read()
                                ?? throw new InvalidOperationException("설정에서 Windows 로그인 자격 증명을 먼저 등록하고 테스트하세요.");
                    autologonSystem.ValidateCredential(saved);
                    resumeSignIn.Arm(schedule.Id, schedule.ScheduledLocalDateTime);
                    try
                    {
                        await store.AddHistoryAsync(schedule.Id, "ResumeSignInArmed", ResultKind.Success,
                            schedule.ScheduledLocalDateTime,
                            "S3/S4 실기 테스트 복귀 1회에 한해 잠금 화면을 건너뛰도록 준비했습니다.").ConfigureAwait(false);
                        registrar.Register(schedule);
                        stateStore.MarkTransitionRequested();
                        await store.AddHistoryAsync(schedule.Id, eventType, ResultKind.Information, schedule.ScheduledLocalDateTime,
                            $"{SleepWakeTestStateStore.TargetName(target)} 자동 깨우기와 1회 자동 로그인 실기 테스트를 시작했습니다.").ConfigureAwait(false);
                        await new PowerActionExecutor().ExecuteAsync(
                            target == SleepWakeTestTarget.S3 ? PowerActionType.Sleep : PowerActionType.Hibernate).ConfigureAwait(false);
                        await store.AddHistoryAsync(schedule.Id, eventType, ResultKind.Information, schedule.ScheduledLocalDateTime,
                            $"{SleepWakeTestStateStore.TargetName(target)} 상태에서 Windows가 다시 실행 상태로 돌아왔습니다. 결과 확인을 기다립니다.").ConfigureAwait(false);
                    }
                    catch (Exception error)
                    {
                        stateStore.MarkFailed($"{SleepWakeTestStateStore.TargetName(target)} 전환에 실패했습니다: {error.Message}");
                        await store.AddHistoryAsync(schedule.Id, eventType, ResultKind.Failure, schedule.ScheduledLocalDateTime,
                            $"{SleepWakeTestStateStore.TargetName(target)} 전환 또는 재개 처리에 실패했습니다.", error.HResult.ToString("X8", System.Globalization.CultureInfo.InvariantCulture)).ConfigureAwait(false);
                        throw;
                    }
                    finally
                    {
                        await RestoreResumeSignInAfterDelayAsync(resumeSignIn, store, id,
                            "S3/S4 실기 테스트에서 복귀하여 데스크톱 복귀 유예 후 원래 로그인 요구 설정을 복원했습니다.").ConfigureAwait(false);
                    }

                    return 0;
                }
                case "reconcile":
                {
                    await RestoreResumeSignInAsync(resumeSignIn, store, null,
                        "로그인 시 무결성 검사에서 남아 있던 S3/S4 1회 자동 로그인 설정을 복원했습니다.").ConfigureAwait(false);
                    var reconciler = new IntegrityReconciler(store, registrar, autologon, logger);
                    await reconciler.ReconcileAsync(canRepairSystem: true).ConfigureAwait(false);
                    CleanupRemovedSecurityState(autologon);
                    return 0;
                }
                case "cleanup-removed-features" when args.Length == 1:
                {
                    var schedules = await store.GetAllSchedulesAsync().ConfigureAwait(false);
                    foreach (var schedule in schedules.Where(item => PowerSchedulePolicy.IsRemovedSchedule(item.ActionType)))
                    {
                        registrar.Remove(schedule.Id);
                        if (schedule.IsEnabled || schedule.Status == ScheduleStatus.Pending)
                        {
                            await store.UpdateScheduleStateAsync(schedule.Id, false, ScheduleStatus.Disabled).ConfigureAwait(false);
                        }
                    }

                    var active = autologon.CurrentJournal;
                    if (active is not null && active.State is not AutologonJournalState.Cleaned)
                    {
                        autologon.Cleanup();
                    }

                    credentials.Delete();
                    userCredentials.Delete();
                    await RestoreResumeSignInAsync(resumeSignIn, store, null,
                        "이전 기능 정리 중 남아 있던 S3/S4 로그인 요구 설정을 복원했습니다.").ConfigureAwait(false);
                    DeleteLegacyPowerTestState();
                    return 0;
                }
                case "pause-all" when args.Length == 1:
                {
                    var schedules = await store.GetAllSchedulesAsync().ConfigureAwait(false);
                    foreach (var schedule in schedules.Where(item => item.IsEnabled && item.Status == ScheduleStatus.Pending && item.ScheduledLocalDateTime > DateTime.Now))
                    {
                        registrar.Remove(schedule.Id);
                        await store.UpdateScheduleStateAsync(schedule.Id, false, ScheduleStatus.Disabled).ConfigureAwait(false);
                    }

                    var active = autologon.CurrentJournal;
                    if (active is not null && active.State is not AutologonJournalState.Cleaned)
                    {
                        autologon.Cleanup();
                    }

                    await RestoreResumeSignInAsync(resumeSignIn, store, null,
                        "전체 예약 중지와 함께 S3/S4 1회 자동 로그인 설정을 복원했습니다.").ConfigureAwait(false);

                    return 0;
                }
                case "cleanup-app":
                {
                    var journal = autologon.CurrentJournal;
                    if (journal is not null && journal.State is not AutologonJournalState.Cleaned)
                    {
                        autologon.Cleanup();
                    }

                    await RestoreResumeSignInAsync(resumeSignIn, store, null,
                        "앱 제거 전에 S3/S4 1회 자동 로그인 설정을 복원했습니다.").ConfigureAwait(false);

                    registrar.RemoveAllOwnedTasks();
                    credentials.Delete();
                    userCredentials.Delete();
                    StartupManager.Remove();
                    DeleteLegacyPowerTestState();
                    new SleepWakeTestStateStore().Clear();
                    new JsonResumeSignInJournalStore().Delete();
                    await store.DeleteAllAppDataAsync().ConfigureAwait(false);
                    if (Directory.Exists(AppPaths.LocalDataDirectory))
                    {
                        Directory.Delete(AppPaths.LocalDataDirectory, true);
                    }
                    return 0;
                }
                default:
                    return 64;
            }
        }
        catch (Exception error)
        {
            logger.Error("helper.command-failed", error, $"command={args[0]}");
            Console.Error.WriteLine(error.Message);
            return error is OperationCanceledException ? 1223 : 1;
        }
    }

    private static void CleanupRemovedSecurityState(AutologonManager autologon)
    {
        var journal = autologon.CurrentJournal;
        if (journal is not null && journal.State is not AutologonJournalState.Cleaned)
        {
            autologon.Cleanup();
        }
    }

    private static async Task RestoreResumeSignInAsync(
        ResumeSignInManager manager,
        SqliteStore store,
        Guid? expectedScheduleId,
        string message)
    {
        var restored = manager.Restore(expectedScheduleId);
        if (restored is null)
        {
            manager.DeleteCompletedJournal();
            return;
        }

        await store.AddHistoryAsync(
            restored.ScheduleId,
            "ResumeSignInRestored",
            ResultKind.Success,
            restored.ScheduledLocalTime,
            message).ConfigureAwait(false);
        manager.DeleteCompletedJournal();
    }

    private static async Task RestoreResumeSignInAfterDelayAsync(
        ResumeSignInManager manager,
        SqliteStore store,
        Guid expectedScheduleId,
        string message)
    {
        var journal = manager.CurrentJournal;
        if (journal is null ||
            journal.State == ResumeSignInJournalState.Restored ||
            journal.ScheduleId != expectedScheduleId)
        {
            return;
        }

        await Task.Delay(ResumeSignInPolicy.RestoreDelayAfterResume).ConfigureAwait(false);
        await RestoreResumeSignInAsync(manager, store, expectedScheduleId, message).ConfigureAwait(false);
    }

    private static async Task EnsureNextWakeAsync(SqliteStore store, TaskSchedulerService registrar, Guid currentScheduleId)
    {
        var schedules = await store.GetAllSchedulesAsync().ConfigureAwait(false);
        var next = ScheduleValidator.SelectNextWake(schedules.Where(schedule => schedule.Id != currentScheduleId), DateTime.Now);
        if (next is not null)
        {
            registrar.Register(next);
        }
    }

    private static async Task RecordShutdownWakeConflictAsync(SqliteStore store, PowerSchedule shutdown)
    {
        var schedules = await store.GetAllSchedulesAsync().ConfigureAwait(false);
        var nextWake = ScheduleValidator.SelectNextWake(
            schedules.Where(schedule => schedule.Id != shutdown.Id),
            shutdown.ScheduledLocalDateTime);
        if (nextWake is null)
        {
            return;
        }

        await store.AddHistoryAsync(
            shutdown.Id,
            "ShutdownWakeWarning",
            ResultKind.Warning,
            shutdown.ScheduledLocalDateTime,
            $"완전 종료 후 {nextWake.ScheduledLocalDateTime:yyyy.MM.dd HH:mm} 자동 시작 예약은 PC를 다시 켤 수 없습니다. 사용자가 완전 종료를 선택해 예약대로 진행합니다.").ConfigureAwait(false);
    }

    private static Guid RequiredScheduleId(string[] args)
    {
        if (args.Length != 3 || !string.Equals(args[1], "--schedule", StringComparison.OrdinalIgnoreCase) || !Guid.TryParse(args[2], out var id))
        {
            throw new InvalidDataException("--schedule 뒤에 올바른 예약 ID 하나가 필요합니다.");
        }

        return id;
    }

    private static (SleepWakeTestTarget Target, Guid ScheduleId) RequiredSleepWakeTestArguments(string[] args)
    {
        if (args.Length != 5 ||
            !string.Equals(args[1], "--state", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(args[3], "--schedule", StringComparison.OrdinalIgnoreCase) ||
            !Guid.TryParse(args[4], out var id))
        {
            throw new InvalidDataException("--state s3|s4와 --schedule 예약 ID가 필요합니다.");
        }

        var target = args[2].ToLowerInvariant() switch
        {
            "s3" => SleepWakeTestTarget.S3,
            "s4" => SleepWakeTestTarget.S4,
            _ => throw new InvalidDataException("Wake 테스트 전원 상태는 s3 또는 s4여야 합니다.")
        };
        return (target, id);
    }

    private static string RequiredCredentialTransferPath(string[] args)
    {
        if (args.Length != 3 ||
            !string.Equals(args[1], "--file", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(args[2]))
        {
            throw new InvalidDataException("--file 뒤에 앱이 만든 자격 증명 전달 파일 경로가 필요합니다.");
        }

        return args[2];
    }

    private static PowerActionType RequiredLowPowerState(string[] args)
    {
        if (args.Length != 3 || !string.Equals(args[1], "--state", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("--state s3|s4가 필요합니다.");
        }

        return args[2].ToLowerInvariant() switch
        {
            "s3" => PowerActionType.Sleep,
            "s4" => PowerActionType.Hibernate,
            _ => throw new InvalidDataException("전원 상태는 s3 또는 s4여야 합니다.")
        };
    }

    private static async Task<PowerSchedule> RequiredScheduleAsync(SqliteStore store, Guid id) =>
        await store.GetScheduleAsync(id).ConfigureAwait(false)
        ?? throw new KeyNotFoundException("예약을 찾을 수 없습니다.");

    private static void ValidateScheduledInvocation(PowerSchedule schedule, PowerActionType expectedAction)
    {
        if (!schedule.IsEnabled || schedule.Status != ScheduleStatus.Pending || schedule.ActionType != expectedAction)
        {
            throw new InvalidOperationException("이 예약은 현재 실행 가능한 상태가 아닙니다.");
        }

        if ((DateTime.Now - schedule.ScheduledLocalDateTime).Duration() > TimeSpan.FromMinutes(2))
        {
            throw new InvalidOperationException("예약 시각에서 2분 이상 벗어나 안전을 위해 전원 동작을 실행하지 않았습니다.");
        }
    }

    private static void ValidateWakeInvocation(PowerSchedule schedule)
    {
        if (!PowerSchedulePolicy.IsWakeSchedule(schedule.ActionType) ||
            !schedule.IsEnabled ||
            schedule.Status != ScheduleStatus.Pending)
        {
            throw new InvalidOperationException("이 S3/S4 깨우기 예약은 현재 실행 가능한 상태가 아닙니다.");
        }

        if ((DateTime.Now - schedule.ScheduledLocalDateTime).Duration() > TimeSpan.FromMinutes(2))
        {
            throw new InvalidOperationException("예약 시각에서 2분 이상 벗어나 Wake 완료로 처리하지 않았습니다.");
        }
    }

    private static string KoreanAction(PowerActionType action) => action switch
    {
        PowerActionType.Shutdown => "완전 종료",
        PowerActionType.Hibernate => "최대 절전",
        PowerActionType.Sleep => "절전",
        PowerActionType.WakeFromSleep => "S3 절전 깨우기",
        PowerActionType.WakeFromHibernate => "S4 최대 절전 깨우기",
        _ => "지원하지 않는 전원 동작"
    };

    private static void DeleteLegacyPowerTestState()
    {
        if (File.Exists(AppPaths.RemovedPowerTestStatePath))
        {
            File.Delete(AppPaths.RemovedPowerTestStatePath);
        }
    }
}
