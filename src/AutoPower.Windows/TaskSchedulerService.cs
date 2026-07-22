using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Principal;
using AutoPower.Core;

namespace AutoPower.Windows;

public sealed record InstallationLayout(string AppPath, string HelperPath, string AgentPath)
{
    public static InstallationLayout FromBaseDirectory(string baseDirectory) => new(
        Path.Combine(baseDirectory, "AutoPower.App.exe"),
        Path.Combine(baseDirectory, "AutoPower.Helper.exe"),
        Path.Combine(baseDirectory, "AutoPower.Agent.exe"));
}

public interface ISystemScheduleRegistrar
{
    void Register(PowerSchedule schedule);
    void Remove(Guid scheduleId);
    void ConsumePowerTask(Guid scheduleId);
    PowerTaskSnapshot GetPowerTaskSnapshot(Guid scheduleId);
    IReadOnlySet<string> ListOwnedTasks();
    void RegisterStartupAgent(string userName);
    void RemoveAllOwnedTasks();
}

public sealed record PowerTaskSnapshot(
    bool Exists,
    bool Enabled,
    bool Running,
    DateTime? LastRunTime,
    int? LastTaskResult,
    DateTime? NextRunTime)
{
    public static PowerTaskSnapshot Missing { get; } = new(false, false, false, null, null, null);
}

public sealed class TaskSchedulerService : ISystemScheduleRegistrar, IElevatedProgramLauncher
{
    private const int TaskTriggerTime = 1;
    private const int TaskTriggerLogon = 9;
    private const int TaskActionExec = 0;
    private const int TaskCreateOrUpdate = 6;
    private const int TaskLogonInteractiveToken = 3;
    private const int TaskLogonServiceAccount = 5;
    private const int TaskRunLevelLua = 0;
    private const int TaskRunLevelHighest = 1;
    private const int TaskInstancesIgnoreNew = 2;
    internal static bool StartWhenAvailablePolicy => false;
    private readonly InstallationLayout _layout;
    private readonly TechnicalLogger _logger;

    public TaskSchedulerService(InstallationLayout layout, TechnicalLogger logger)
    {
        _layout = layout;
        _logger = logger;
    }

    public void Register(PowerSchedule schedule)
    {
        ArgumentNullException.ThrowIfNull(schedule);
        Remove(schedule.Id);
        if (!schedule.IsEnabled || schedule.Status != ScheduleStatus.Pending)
        {
            return;
        }

        try
        {
            if (PowerSchedulePolicy.IsWakeSchedule(schedule.ActionType))
            {
                var wakeState = PowerSchedulePolicy.WakeStateName(schedule.ActionType);
                RegisterTimeTask(
                    $"wake-{schedule.Id:D}",
                    schedule.ScheduledLocalDateTime,
                    _layout.HelperPath,
                    $"mark-wake --schedule {schedule.Id:D}",
                    runAsSystem: true,
                    wakeToRun: true,
                    $"{wakeState} 상태의 예약 깨우기를 위한 WakeToRun 작업");
                RegisterTimeTask(
                    $"followup-{schedule.Id:D}",
                    schedule.ScheduledLocalDateTime,
                    _layout.AgentPath,
                    $"run-followups --schedule {schedule.Id:D} --source scheduled",
                    runAsSystem: false,
                    wakeToRun: false,
                    $"{wakeState} 사용자 세션이 준비된 뒤 후속 프로그램을 실행하는 작업");
                foreach (var program in schedule.FollowUpPrograms.Where(item => item.RunElevated))
                {
                    RegisterElevatedFollowUpTask(schedule, program);
                }

                if (schedule.FollowUpPrograms.Count > 0 &&
                    schedule.ScheduledLocalDateTime - WarningPolicy.AdvanceNotice > DateTime.Now)
                {
                    RegisterTimeTask(
                        $"warning-{schedule.Id:D}",
                        schedule.ScheduledLocalDateTime - WarningPolicy.AdvanceNotice,
                        _layout.AppPath,
                        $"warning --schedule {schedule.Id:D}",
                        runAsSystem: false,
                        wakeToRun: false,
                        "PC가 이미 실행 중인 경우 후속 프로그램 실행 여부를 확인하는 5분 전 경고");
                }
            }
            else if (PowerSchedulePolicy.IsRemovedSchedule(schedule.ActionType))
            {
                _logger.Warning("task.removed-power-action", $"schedule={schedule.Id:D}; 지원하지 않는 이전 전원 작업을 등록하지 않았습니다.");
            }
            else
            {
                if (schedule.ScheduledLocalDateTime - WarningPolicy.AdvanceNotice > DateTime.Now)
                {
                    RegisterTimeTask(
                        $"warning-{schedule.Id:D}",
                        schedule.ScheduledLocalDateTime - WarningPolicy.AdvanceNotice,
                        _layout.AppPath,
                        $"warning --schedule {schedule.Id:D}",
                        runAsSystem: false,
                        wakeToRun: true,
                        "전원 동작 5분 전 고정 경고");
                }

                RegisterTimeTask(
                    $"power-{schedule.Id:D}",
                    schedule.ScheduledLocalDateTime,
                    _layout.HelperPath,
                    $"execute-power --schedule {schedule.Id:D}",
                    runAsSystem: true,
                    wakeToRun: true,
                    "예약된 전원 동작 실행");
            }

            _logger.Information("task.registered", $"schedule={schedule.Id:D};action={schedule.ActionType}");
        }
        catch
        {
            Remove(schedule.Id);
            throw;
        }
    }

    public void Remove(Guid scheduleId)
    {
        using var session = OpenSession();
        foreach (var prefix in new[] { "wake", "warning", "power", "followup", "shutdown-fallback" })
        {
            TryDeleteTask(session.Folder, $"{prefix}-{scheduleId:D}");
        }

        DeleteTasksWithPrefix(session.Folder, $"elevated-{scheduleId:D}-");
    }

    public void ConsumePowerTask(Guid scheduleId)
    {
        using var session = OpenSession();
        dynamic? task = null;
        try
        {
            task = session.Folder.GetTask($"power-{scheduleId:D}");
            task.Enabled = false;
            _logger.Information("task.power-consumed", $"schedule={scheduleId:D}");
        }
        catch (Exception error) when (IsMissingSchedulerObject(error))
        {
            _logger.Warning("task.power-consume-missing", $"schedule={scheduleId:D}");
        }
        finally
        {
            Release(task);
        }
    }

    public PowerTaskSnapshot GetPowerTaskSnapshot(Guid scheduleId)
    {
        SchedulerSession? session = null;
        dynamic? task = null;
        try
        {
            session = OpenSession(createFolders: false);
            task = session.Folder.GetTask($"power-{scheduleId:D}");
            var lastRun = (DateTime)task.LastRunTime;
            var nextRun = (DateTime)task.NextRunTime;
            return new PowerTaskSnapshot(
                true,
                (bool)task.Enabled,
                (int)task.State == 4,
                lastRun.Year >= 2000 ? lastRun : null,
                (int)task.LastTaskResult,
                nextRun.Year >= 2000 ? nextRun : null);
        }
        catch (Exception error) when (IsMissingSchedulerObject(error))
        {
            return PowerTaskSnapshot.Missing;
        }
        finally
        {
            Release(task);
            session?.Dispose();
        }
    }

    public void RegisterShutdownFallback(Guid scheduleId, DateTime localTime)
    {
        using (var session = OpenSession())
        {
            TryDeleteTask(session.Folder, ShutdownFallbackTaskName(scheduleId));
        }

        RegisterTimeTask(
            ShutdownFallbackTaskName(scheduleId),
            localTime,
            _layout.HelperPath,
            $"force-shutdown --schedule {scheduleId:D}",
            runAsSystem: true,
            wakeToRun: false,
            "정상 종료가 30초 안에 완료되지 않은 경우 실행하는 강제 종료 fallback");
        _logger.Information("task.shutdown-fallback-registered", $"schedule={scheduleId:D};time={localTime:O}");
    }

    internal static string ShutdownFallbackTaskName(Guid scheduleId) =>
        $"shutdown-fallback-{scheduleId:D}";

    public void RunElevatedFollowUp(Guid scheduleId, Guid programId)
    {
        var name = ElevatedFollowUpTaskName(scheduleId, programId);
        using var session = OpenSession();
        dynamic? task = null;
        dynamic? running = null;
        try
        {
            task = session.Folder.GetTask(name);
            running = task.Run(null);
            _logger.Information("followup.elevated-task-started", $"schedule={scheduleId:D};program={programId:D}");
        }
        catch (Exception error) when (IsMissingSchedulerObject(error))
        {
            throw new InvalidOperationException(
                "관리자 권한 후속 프로그램 작업을 찾을 수 없습니다. 예약을 다시 저장해 권한 작업을 등록하세요.",
                error);
        }
        finally
        {
            Release(running);
            Release(task);
        }
    }

    internal static string ElevatedFollowUpTaskName(Guid scheduleId, Guid programId) =>
        $"elevated-{scheduleId:D}-{programId:D}";

    public IReadOnlySet<string> ListOwnedTasks()
    {
        using var session = OpenSession();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        dynamic tasks = session.Folder.GetTasks(0);
        try
        {
            var count = (int)tasks.Count;
            for (var index = 1; index <= count; index++)
            {
                dynamic task = tasks.Item(index);
                try
                {
                    names.Add((string)task.Name);
                }
                finally
                {
                    Release(task);
                }
            }
        }
        finally
        {
            Release(tasks);
        }

        return names;
    }

    public void RegisterStartupAgent(string userName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userName);
        RegisterLogonTask(
            "startup-reconcile",
            userName,
            _layout.HelperPath,
            "reconcile",
            runAsSystem: true,
            "로그인 시 DB, 앱 전용 작업 및 Wake 상태 복구");
        RegisterLogonTask(
            "desktop-agent",
            userName,
            _layout.AgentPath,
            "startup",
            runAsSystem: false,
            "사용자 바탕화면 준비 확인, 복구, 후속 프로그램 실행");
    }

    public void RemoveAllOwnedTasks()
    {
        using var session = OpenSession();
        foreach (var task in ListOwnedTasks())
        {
            TryDeleteTask(session.Folder, task);
        }
    }

    public static string CurrentUserName()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return identity.Name;
    }

    private void RegisterTimeTask(
        string name,
        DateTime localTime,
        string executable,
        string arguments,
        bool runAsSystem,
        bool wakeToRun,
        string description)
    {
        ValidateExecutable(executable);
        using var session = OpenSession();
        dynamic definition = session.Service.NewTask(0);
        try
        {
            ConfigureDefinition(definition, name, wakeToRun, description, deleteAfterExpiration: true);
            dynamic trigger = definition.Triggers.Create(TaskTriggerTime);
            try
            {
                var boundaries = CreateTimeTriggerBoundaries(localTime);
                trigger.StartBoundary = boundaries.StartBoundary;
                trigger.EndBoundary = boundaries.EndBoundary;
                trigger.Enabled = true;
            }
            finally
            {
                Release(trigger);
            }

            AddExecAction(definition, executable, arguments);
            RegisterDefinition(session.Folder, name, definition, runAsSystem, null);
        }
        finally
        {
            Release(definition);
        }
    }

    private void RegisterLogonTask(
        string name,
        string triggerUser,
        string executable,
        string arguments,
        bool runAsSystem,
        string description)
    {
        ValidateExecutable(executable);
        using var session = OpenSession();
        dynamic definition = session.Service.NewTask(0);
        try
        {
            ConfigureDefinition(definition, name, false, description, deleteAfterExpiration: false);
            dynamic trigger = definition.Triggers.Create(TaskTriggerLogon);
            try
            {
                trigger.UserId = triggerUser;
                trigger.Enabled = true;
            }
            finally
            {
                Release(trigger);
            }

            AddExecAction(definition, executable, arguments);
            RegisterDefinition(session.Folder, name, definition, runAsSystem, triggerUser);
        }
        finally
        {
            Release(definition);
        }
    }

    private void RegisterElevatedFollowUpTask(PowerSchedule schedule, FollowUpProgram program)
    {
        ValidateExecutable(program.ExecutablePath);
        var name = ElevatedFollowUpTaskName(schedule.Id, program.Id);
        using var session = OpenSession();
        dynamic definition = session.Service.NewTask(0);
        try
        {
            ConfigureDefinition(
                definition,
                name,
                false,
                $"{Path.GetFileName(program.ExecutablePath)} 관리자 권한 후속 실행",
                false);
            definition.Settings.ExecutionTimeLimit = "PT0S";
            AddExecAction(
                definition,
                program.ExecutablePath,
                program.Arguments ?? string.Empty,
                program.WorkingDirectory);
            RegisterDefinition(
                session.Folder,
                name,
                definition,
                false,
                CurrentUserName(),
                true);
        }
        finally
        {
            Release(definition);
        }
    }

    private static void ConfigureDefinition(
        dynamic definition,
        string name,
        bool wakeToRun,
        string description,
        bool deleteAfterExpiration)
    {
        definition.RegistrationInfo.Description = description;
        definition.RegistrationInfo.Author = "eslee Auto Power";
        definition.RegistrationInfo.Source = "eslee.AutoPower";
        definition.RegistrationInfo.URI = $"\\eslee\\AutoPower\\{name}";
        definition.Settings.Enabled = true;
        definition.Settings.AllowDemandStart = true;
        definition.Settings.StartWhenAvailable = StartWhenAvailablePolicy;
        definition.Settings.DisallowStartIfOnBatteries = false;
        definition.Settings.StopIfGoingOnBatteries = false;
        definition.Settings.WakeToRun = wakeToRun;
        definition.Settings.MultipleInstances = TaskInstancesIgnoreNew;
        definition.Settings.ExecutionTimeLimit = "PT10M";
        if (deleteAfterExpiration)
        {
            definition.Settings.DeleteExpiredTaskAfter = "P30D";
        }
    }

    internal static (string StartBoundary, string EndBoundary) CreateTimeTriggerBoundaries(DateTime localTime)
    {
        var start = DateTime.SpecifyKind(localTime, DateTimeKind.Unspecified);
        var end = start.Add(PowerTransitionPolicy.InvocationTolerance);
        return (FormatBoundary(start), FormatBoundary(end));
    }

    private static string FormatBoundary(DateTime value) =>
        value.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);

    private static void AddExecAction(
        dynamic definition,
        string executable,
        string arguments,
        string? workingDirectory = null)
    {
        dynamic action = definition.Actions.Create(TaskActionExec);
        try
        {
            action.Path = executable;
            action.Arguments = arguments;
            action.WorkingDirectory = string.IsNullOrWhiteSpace(workingDirectory)
                ? Path.GetDirectoryName(executable) ?? string.Empty
                : workingDirectory;
        }
        finally
        {
            Release(action);
        }
    }

    private static void RegisterDefinition(
        dynamic folder,
        string name,
        dynamic definition,
        bool runAsSystem,
        string? interactiveUser,
        bool runHighest = false)
    {
        if (runAsSystem)
        {
            definition.Principal.UserId = "SYSTEM";
            definition.Principal.LogonType = TaskLogonServiceAccount;
            definition.Principal.RunLevel = TaskRunLevelHighest;
            dynamic task = folder.RegisterTaskDefinition(name, definition, TaskCreateOrUpdate, "SYSTEM", null, TaskLogonServiceAccount, null);
            Release(task);
        }
        else
        {
            var user = interactiveUser ?? CurrentUserName();
            definition.Principal.UserId = user;
            definition.Principal.LogonType = TaskLogonInteractiveToken;
            definition.Principal.RunLevel = runHighest ? TaskRunLevelHighest : TaskRunLevelLua;
            dynamic task = folder.RegisterTaskDefinition(name, definition, TaskCreateOrUpdate, user, null, TaskLogonInteractiveToken, null);
            Release(task);
        }
    }

    private static void ValidateExecutable(string executable)
    {
        if (!Path.IsPathFullyQualified(executable) || !string.Equals(Path.GetExtension(executable), ".exe", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("작업 실행 파일은 검증된 전체 EXE 경로여야 합니다.");
        }
    }

    private static SchedulerSession OpenSession(bool createFolders = true)
    {
        var serviceType = Type.GetTypeFromProgID("Schedule.Service", throwOnError: true)
                          ?? throw new PlatformNotSupportedException("Windows Task Scheduler 2.0을 사용할 수 없습니다.");
        dynamic service = Activator.CreateInstance(serviceType)
                          ?? throw new InvalidOperationException("Windows Task Scheduler에 연결할 수 없습니다.");
        service.Connect();
        dynamic root = service.GetFolder("\\");
        dynamic eslee;
        try
        {
            eslee = root.GetFolder("eslee");
        }
        catch (Exception error) when (IsMissingSchedulerObject(error))
        {
            if (!createFolders)
            {
                throw;
            }

            eslee = root.CreateFolder("eslee");
        }

        dynamic folder;
        try
        {
            folder = eslee.GetFolder("AutoPower");
        }
        catch (Exception error) when (IsMissingSchedulerObject(error))
        {
            if (!createFolders)
            {
                throw;
            }

            folder = eslee.CreateFolder("AutoPower");
        }

        Release(eslee);
        Release(root);
        return new SchedulerSession(service, folder);
    }

    private static void TryDeleteTask(dynamic folder, string name)
    {
        try
        {
            folder.DeleteTask(name, 0);
        }
        catch (Exception error) when (IsMissingSchedulerObject(error))
        {
            // Deletion is idempotent; only a missing task is ignored.
        }
    }

    private static void DeleteTasksWithPrefix(dynamic folder, string prefix)
    {
        var names = new List<string>();
        dynamic tasks = folder.GetTasks(0);
        try
        {
            var count = (int)tasks.Count;
            for (var index = 1; index <= count; index++)
            {
                dynamic task = tasks.Item(index);
                try
                {
                    var name = (string)task.Name;
                    if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    {
                        names.Add(name);
                    }
                }
                finally
                {
                    Release(task);
                }
            }
        }
        finally
        {
            Release(tasks);
        }

        foreach (var name in names)
        {
            TryDeleteTask(folder, name);
        }
    }

    internal static bool IsMissingSchedulerObject(Exception error) =>
        (uint)error.HResult is 0x80070002 or 0x8004130F;

    private static void Release(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
        {
            Marshal.FinalReleaseComObject(value);
        }
    }

    private sealed class SchedulerSession : IDisposable
    {
        public SchedulerSession(object service, object folder)
        {
            Service = service;
            Folder = folder;
        }

        public dynamic Service { get; }
        public dynamic Folder { get; }

        public void Dispose()
        {
            Release(Folder);
            Release(Service);
        }
    }
}
