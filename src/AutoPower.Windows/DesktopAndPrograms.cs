using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using AutoPower.Core;
using AutoPower.Data;

namespace AutoPower.Windows;

public static class DesktopReadyDetector
{
    private static readonly TimeSpan ProbeInterval = TimeSpan.FromSeconds(2);

    public static async Task<DateTimeOffset> WaitAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsInteractiveDesktopReady())
            {
                return DateTimeOffset.Now;
            }

            await Task.Delay(ProbeInterval, cancellationToken).ConfigureAwait(false);
        }

        throw new TimeoutException("Windows 사용자 바탕화면이 제한 시간 안에 준비되지 않았습니다.");
    }

    public static async Task<DateTimeOffset> WaitForResumeReadyAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsUserSessionReady())
            {
                return DateTimeOffset.Now;
            }

            await Task.Delay(ProbeInterval, cancellationToken).ConfigureAwait(false);
        }

        throw new TimeoutException("절전 복귀 후 Windows 사용자 세션이 제한 시간 안에 준비되지 않았습니다.");
    }

    public static bool IsInteractiveDesktopReady()
    {
        if (!IsDefaultInputDesktop())
        {
            return false;
        }

        return IsUserSessionReady();
    }

    public static bool IsUserSessionReady()
    {

        var shellWindow = GetShellWindow();
        if (shellWindow == IntPtr.Zero)
        {
            return false;
        }

        _ = GetWindowThreadProcessId(shellWindow, out var shellProcessId);
        if (shellProcessId == 0)
        {
            return false;
        }

        try
        {
            using var shell = Process.GetProcessById(checked((int)shellProcessId));
            var currentSession = Process.GetCurrentProcess().SessionId;
            return currentSession != 0 &&
                   shell.SessionId == currentSession &&
                   WTSGetActiveConsoleSessionId() == checked((uint)currentSession);
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static bool IsDefaultInputDesktop()
    {
        const uint desktopReadObjects = 0x0001;
        var desktop = OpenInputDesktop(0, false, desktopReadObjects);
        if (desktop == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            _ = GetUserObjectInformation(desktop, 2, IntPtr.Zero, 0, out var needed);
            if (needed == 0)
            {
                return false;
            }

            var buffer = Marshal.AllocHGlobal(checked((int)needed));
            try
            {
                if (!GetUserObjectInformation(desktop, 2, buffer, needed, out _))
                {
                    return false;
                }

                return string.Equals(Marshal.PtrToStringUni(buffer), "Default", StringComparison.OrdinalIgnoreCase);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        finally
        {
            CloseDesktop(desktop);
        }
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetShellWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("kernel32.dll")]
    private static extern uint WTSGetActiveConsoleSessionId();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr OpenInputDesktop(uint flags, bool inherit, uint desiredAccess);

    [DllImport("user32.dll", EntryPoint = "GetUserObjectInformationW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetUserObjectInformation(IntPtr handle, int index, IntPtr information, uint length, out uint needed);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseDesktop(IntPtr desktop);
}

public sealed record ProgramLaunchResult(FollowUpProgram Program, ResultKind Result, string Message, string? ErrorCode);

public interface IProgramProcessService
{
    bool IsRunning(string executablePath);
    void Start(FollowUpProgram program);
}

public interface IElevatedProgramLauncher
{
    void RunElevatedFollowUp(Guid scheduleId, Guid programId);
}

public sealed class ProgramProcessService : IProgramProcessService
{
    private readonly IElevatedProgramLauncher? _elevatedLauncher;

    public ProgramProcessService(IElevatedProgramLauncher? elevatedLauncher = null)
    {
        _elevatedLauncher = elevatedLauncher;
    }

    public bool IsRunning(string executablePath)
    {
        var expected = Path.GetFullPath(executablePath);
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    var actual = process.MainModule?.FileName;
                    if (actual is not null && string.Equals(Path.GetFullPath(actual), expected, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
                catch (Win32Exception)
                {
                    // Protected or elevated processes cannot always expose MainModule to a normal user.
                }
                catch (InvalidOperationException)
                {
                    // A process may exit while it is being inspected.
                }
            }
        }

        return false;
    }

    public void Start(FollowUpProgram program)
    {
        if (!Path.IsPathFullyQualified(program.ExecutablePath) || !File.Exists(program.ExecutablePath))
        {
            throw new FileNotFoundException("후속 프로그램 파일을 찾을 수 없습니다.", program.ExecutablePath);
        }

        if (program.RunElevated)
        {
            if (_elevatedLauncher is null)
            {
                throw new InvalidOperationException("관리자 권한 후속 프로그램 실행기가 준비되지 않았습니다.");
            }

            _elevatedLauncher.RunElevatedFollowUp(program.ScheduleId, program.Id);
            return;
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = program.ExecutablePath,
            Arguments = program.Arguments ?? string.Empty,
            WorkingDirectory = string.IsNullOrWhiteSpace(program.WorkingDirectory)
                ? Path.GetDirectoryName(program.ExecutablePath) ?? Environment.CurrentDirectory
                : program.WorkingDirectory,
            UseShellExecute = false
        };
        Process.Start(startInfo)?.Dispose();
    }
}

public sealed class FollowUpProgramRunner
{
    private readonly SqliteStore _store;
    private readonly IProgramProcessService _processes;
    private readonly TechnicalLogger _logger;

    public FollowUpProgramRunner(SqliteStore store, IProgramProcessService processes, TechnicalLogger logger)
    {
        _store = store;
        _processes = processes;
        _logger = logger;
    }

    public async Task<IReadOnlyList<ProgramLaunchResult>> RunAsync(
        PowerSchedule schedule,
        DateTimeOffset desktopReadyTime,
        CancellationToken cancellationToken = default)
    {
        if (!PowerSchedulePolicy.IsWakeSchedule(schedule.ActionType))
        {
            throw new InvalidOperationException("후속 프로그램은 S3/S4 예약 깨우기에서만 실행할 수 있습니다.");
        }

        var jobs = schedule.FollowUpPrograms
            .OrderBy(program => program.DelayAfterDesktopReadyMinutes)
            .ThenBy(program => program.SortOrder)
            .GroupBy(program => program.DelayAfterDesktopReadyMinutes)
            .Select(group => RunGroupAsync(schedule, group.ToArray(), desktopReadyTime, cancellationToken))
            .ToArray();
        var groups = await Task.WhenAll(jobs).ConfigureAwait(false);
        return groups.SelectMany(group => group).ToArray();
    }

    private async Task<IReadOnlyList<ProgramLaunchResult>> RunGroupAsync(
        PowerSchedule schedule,
        FollowUpProgram[] programs,
        DateTimeOffset desktopReadyTime,
        CancellationToken cancellationToken)
    {
        var target = desktopReadyTime + TimeSpan.FromMinutes(programs[0].DelayAfterDesktopReadyMinutes);
        var delay = target - DateTimeOffset.Now;
        if (delay > TimeSpan.Zero)
        {
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }

        var launches = programs.Select(program => RunOneAsync(schedule, program, cancellationToken));
        return await Task.WhenAll(launches).ConfigureAwait(false);
    }

    private async Task<ProgramLaunchResult> RunOneAsync(
        PowerSchedule schedule,
        FollowUpProgram program,
        CancellationToken cancellationToken)
    {
        var attemptKey = $"followup-attempted:{schedule.Id:D}:{program.Id:D}";
        if (string.Equals(await _store.GetSettingAsync(attemptKey, cancellationToken).ConfigureAwait(false), "1", StringComparison.Ordinal))
        {
            return new ProgramLaunchResult(program, ResultKind.Skipped, "이 후속 프로그램은 이미 처리되었습니다.", null);
        }

        await _store.SetSettingAsync(attemptKey, "1", cancellationToken).ConfigureAwait(false);
        try
        {
            if (_processes.IsRunning(program.ExecutablePath))
            {
                const string message = "프로그램이 이미 실행 중이어서 다시 실행하지 않았습니다.";
                await _store.AddHistoryAsync(schedule.Id, "FollowUpProgram", ResultKind.Skipped, schedule.ScheduledLocalDateTime, message, cancellationToken: cancellationToken).ConfigureAwait(false);
                return new ProgramLaunchResult(program, ResultKind.Skipped, message, null);
            }

            _processes.Start(program);
            var success = program.RunElevated
                ? "후속 프로그램을 관리자 권한으로 실행했습니다."
                : "후속 프로그램을 실행했습니다.";
            await _store.AddHistoryAsync(schedule.Id, "FollowUpProgram", ResultKind.Success, schedule.ScheduledLocalDateTime, success, cancellationToken: cancellationToken).ConfigureAwait(false);
            _logger.Information("followup.started", $"schedule={schedule.Id:D};program={program.Id:D}");
            return new ProgramLaunchResult(program, ResultKind.Success, success, null);
        }
        catch (Exception error) when (error is Win32Exception or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            const string message = "후속 프로그램을 실행하지 못했습니다. 다른 프로그램은 계속 처리합니다.";
            var code = error is Win32Exception win32 ? win32.NativeErrorCode.ToString(System.Globalization.CultureInfo.InvariantCulture) : error.GetType().Name;
            await _store.AddHistoryAsync(schedule.Id, "FollowUpProgram", ResultKind.Failure, schedule.ScheduledLocalDateTime, message, code, cancellationToken).ConfigureAwait(false);
            _logger.Error("followup.failed", error, $"schedule={schedule.Id:D};program={program.Id:D}");
            return new ProgramLaunchResult(program, ResultKind.Failure, message, code);
        }
    }
}
