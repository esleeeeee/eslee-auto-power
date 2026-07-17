using System.Globalization;
using AutoPower.Core;
using AutoPower.Data;
using AutoPower.Windows;

return await AgentProgram.RunAsync(args).ConfigureAwait(false);

internal static class AgentProgram
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
        try
        {
            return args[0].ToLowerInvariant() switch
            {
                "startup" when args.Length == 1 => await StartupAsync(store, logger).ConfigureAwait(false),
                "run-followups" => await RunRequestedAsync(args, store, logger).ConfigureAwait(false),
                _ => 64
            };
        }
        catch (Exception error)
        {
            logger.Error("agent.failed", error, $"command={args[0]}");
            return 1;
        }
    }

    private static async Task<int> StartupAsync(SqliteStore store, TechnicalLogger logger)
    {
        var desktopReady = await DesktopReadyDetector.WaitAsync(TimeSpan.FromMinutes(2)).ConfigureAwait(false);
        var schedules = await store.GetAllSchedulesAsync().ConfigureAwait(false);
        var candidate = schedules
            .Where(schedule => PowerSchedulePolicy.IsWakeSchedule(schedule.ActionType) &&
                               schedule.ScheduledLocalDateTime <= DateTime.Now &&
                               schedule.ScheduledLocalDateTime >= DateTime.Now - TimeSpan.FromMinutes(2))
            .OrderByDescending(schedule => schedule.ScheduledLocalDateTime)
            .FirstOrDefault();
        if (candidate is not null)
        {
            await RunFollowUpsAsync(candidate, desktopReady, store, logger).ConfigureAwait(false);
        }

        return 0;
    }

    private static async Task<int> RunRequestedAsync(string[] args, SqliteStore store, TechnicalLogger logger)
    {
        if (args.Length != 5 ||
            !string.Equals(args[1], "--schedule", StringComparison.OrdinalIgnoreCase) ||
            !Guid.TryParse(args[2], out var id) ||
            !string.Equals(args[3], "--source", StringComparison.OrdinalIgnoreCase) ||
            args[4] is not ("scheduled" or "now"))
        {
            return 64;
        }

        var schedule = await store.GetScheduleAsync(id).ConfigureAwait(false);
        if (schedule is null || !PowerSchedulePolicy.IsWakeSchedule(schedule.ActionType))
        {
            return 2;
        }

        if (string.Equals(await store.GetSettingAsync($"wake-decision:{id:D}").ConfigureAwait(false), "skip", StringComparison.Ordinal))
        {
            await store.AddHistoryAsync(id, "FollowUpPrograms", ResultKind.Skipped, schedule.ScheduledLocalDateTime,
                "이번 S3/S4 깨우기 예약의 후속 프로그램을 건너뛰었습니다.").ConfigureAwait(false);
            return 0;
        }

        if (args[4] == "scheduled" &&
            (DateTime.Now - schedule.ScheduledLocalDateTime).Duration() > TimeSpan.FromMinutes(2))
        {
            await store.AddHistoryAsync(id, "FollowUpPrograms", ResultKind.Warning, schedule.ScheduledLocalDateTime,
                "미실행 — 예약 시각에 실행 가능한 상태가 아니었음").ConfigureAwait(false);
            return 0;
        }

        var ready = await DesktopReadyDetector.WaitForResumeReadyAsync(TimeSpan.FromSeconds(90)).ConfigureAwait(false);
        await RunFollowUpsAsync(schedule, ready, store, logger).ConfigureAwait(false);
        return 0;
    }

    private static async Task RunFollowUpsAsync(
        PowerSchedule schedule,
        DateTimeOffset actualDesktopReady,
        SqliteStore store,
        TechnicalLogger logger)
    {
        var startedKey = $"followup-schedule-started:{schedule.Id:D}";
        if (string.Equals(await store.GetSettingAsync(startedKey).ConfigureAwait(false), "1", StringComparison.Ordinal))
        {
            return;
        }

        await store.SetSettingAsync(startedKey, "1").ConfigureAwait(false);
        var virtualValue = await store.GetSettingAsync($"virtual-t0:{schedule.Id:D}").ConfigureAwait(false);
        var t0 = DateTimeOffset.TryParse(virtualValue, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var virtualT0)
            ? virtualT0
            : actualDesktopReady;
        await store.AddHistoryAsync(schedule.Id, "ResumeReady", ResultKind.Success, schedule.ScheduledLocalDateTime,
            $"{PowerSchedulePolicy.WakeStateName(schedule.ActionType)} 복귀 후 사용자 세션 준비 시점을 기준으로 후속 프로그램 {schedule.FollowUpPrograms.Count}개를 처리합니다.").ConfigureAwait(false);
        var layout = InstallationLayout.FromBaseDirectory(AppContext.BaseDirectory);
        var taskScheduler = new TaskSchedulerService(layout, logger);
        var runner = new FollowUpProgramRunner(store, new ProgramProcessService(taskScheduler), logger);
        await runner.RunAsync(schedule, t0).ConfigureAwait(false);
    }

}
