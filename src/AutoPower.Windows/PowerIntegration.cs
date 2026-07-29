using System.ComponentModel;
using System.Management;
using System.Runtime.InteropServices;
using System.Text.Json;
using AutoPower.Core;

namespace AutoPower.Windows;

public sealed record PowerCapabilitySnapshot(
    string WindowsVersion,
    string Architecture,
    string ComputerManufacturer,
    string ComputerModel,
    string BaseBoardManufacturer,
    string BaseBoardModel,
    string BiosVersion,
    bool S3Available,
    bool S4Available,
    bool HibernateEnabled,
    IReadOnlyList<CompatibilityResult> Results);

public static class PowerCapabilityDetector
{
    public static PowerCapabilitySnapshot Detect()
    {
        ValidateNativePowerCapabilitiesLayout();
        if (!GetPwrCapabilities(out var native))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows 전원 기능을 읽지 못했습니다.");
        }

        var computer = ReadWmi("Win32_ComputerSystem", "Manufacturer", "Model");
        var board = ReadWmi("Win32_BaseBoard", "Manufacturer", "Product");
        var bios = ReadWmi("Win32_BIOS", "SMBIOSBIOSVersion");
        var now = DateTimeOffset.UtcNow;
        var results = new List<CompatibilityResult>
        {
            new(
                CompatibilityCapabilities.S3Wake,
                native.SystemS3 != 0 ? CapabilityStatus.NeedsPhysicalTest : CapabilityStatus.UnsupportedOrFailed,
                now,
                native.SystemS3 != 0
                    ? "이 PC는 S3 절전을 지원합니다. Task Scheduler WakeToRun 실제 테스트가 필요합니다."
                    : "이 PC의 펌웨어가 S3 절전을 제공하지 않습니다."),
            new(
                CompatibilityCapabilities.S4Wake,
                native.SystemS4 != 0 && native.HiberFilePresent != 0
                    ? CapabilityStatus.NeedsPhysicalTest
                    : native.SystemS4 != 0 ? CapabilityStatus.Unknown : CapabilityStatus.UnsupportedOrFailed,
                now,
                native.SystemS4 == 0
                    ? "이 PC의 펌웨어가 최대 절전을 제공하지 않습니다."
                    : native.HiberFilePresent == 0
                        ? "펌웨어는 S4를 제공하지만 Windows 최대 절전이 현재 비활성화되어 있습니다."
                        : "최대 절전이 활성화되어 있습니다. WakeToRun 실제 테스트가 필요합니다."),
        };

        return new PowerCapabilitySnapshot(
            Environment.OSVersion.VersionString,
            RuntimeInformation.OSArchitecture.ToString(),
            computer.GetValueOrDefault("Manufacturer") ?? "확인 불가",
            computer.GetValueOrDefault("Model") ?? "확인 불가",
            board.GetValueOrDefault("Manufacturer") ?? "확인 불가",
            board.GetValueOrDefault("Product") ?? "확인 불가",
            bios.GetValueOrDefault("SMBIOSBIOSVersion") ?? "확인 불가",
            native.SystemS3 != 0,
            native.SystemS4 != 0,
            native.HiberFilePresent != 0,
            results);
    }

    private static Dictionary<string, string?> ReadWmi(string className, params string[] properties)
    {
        var result = properties.ToDictionary(property => property, _ => (string?)null, StringComparer.OrdinalIgnoreCase);
        try
        {
            using var searcher = new ManagementObjectSearcher($"SELECT {string.Join(',', properties)} FROM {className}");
            using var objects = searcher.Get();
            foreach (ManagementObject item in objects)
            {
                using (item)
                {
                    foreach (var property in properties)
                    {
                        result[property] = Convert.ToString(item[property], System.Globalization.CultureInfo.InvariantCulture);
                    }

                    break;
                }
            }
        }
        catch (ManagementException)
        {
            // A restricted WMI provider is represented as unknown instead of a false capability claim.
        }

        return result;
    }

    internal static int NativePowerCapabilitiesSize => Marshal.SizeOf<SystemPowerCapabilities>();

    internal static int NativePowerCapabilitiesOffset(string fieldName) =>
        checked((int)Marshal.OffsetOf<SystemPowerCapabilities>(fieldName));

    internal static void ValidateNativePowerCapabilitiesLayout()
    {
        var valid = NativePowerCapabilitiesSize == 84 &&
                    NativePowerCapabilitiesOffset(nameof(SystemPowerCapabilities.ProcessorThrottleScale)) == 16 &&
                    NativePowerCapabilitiesOffset(nameof(SystemPowerCapabilities.ProcessorMaxThrottle)) == 21 &&
                    NativePowerCapabilitiesOffset(nameof(SystemPowerCapabilities.Hiberboot)) == 23 &&
                    NativePowerCapabilitiesOffset(nameof(SystemPowerCapabilities.WakeAlarmPresent)) == 24 &&
                    NativePowerCapabilitiesOffset(nameof(SystemPowerCapabilities.HiberFileType)) == 35 &&
                    NativePowerCapabilitiesOffset(nameof(SystemPowerCapabilities.SystemBatteriesPresent)) == 37 &&
                    NativePowerCapabilitiesOffset(nameof(SystemPowerCapabilities.BatteryScale0)) == 40 &&
                    NativePowerCapabilitiesOffset(nameof(SystemPowerCapabilities.AcOnLineWake)) == 64 &&
                    NativePowerCapabilitiesOffset(nameof(SystemPowerCapabilities.RtcWake)) == 72 &&
                    NativePowerCapabilitiesOffset(nameof(SystemPowerCapabilities.DefaultLowLatencyWake)) == 80;
        if (!valid)
        {
            throw new InvalidOperationException(
                $"SYSTEM_POWER_CAPABILITIES 네이티브 레이아웃이 Windows SDK 정의와 일치하지 않습니다. 감지 크기: {NativePowerCapabilitiesSize}바이트");
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerCapabilities
    {
        public byte PowerButtonPresent;
        public byte SleepButtonPresent;
        public byte LidPresent;
        public byte SystemS1;
        public byte SystemS2;
        public byte SystemS3;
        public byte SystemS4;
        public byte SystemS5;
        public byte HiberFilePresent;
        public byte FullWake;
        public byte VideoDimPresent;
        public byte ApmPresent;
        public byte UpsPresent;
        public byte ThermalControl;
        public byte ProcessorThrottle;
        public byte ProcessorMinThrottle;
        public byte ProcessorThrottleScale;
        public byte Spare2_0;
        public byte Spare2_1;
        public byte Spare2_2;
        public byte Spare2_3;
        public byte ProcessorMaxThrottle;
        public byte FastSystemS4;
        public byte Hiberboot;
        public byte WakeAlarmPresent;
        public byte AoAc;
        public byte DiskSpinDown;
        public byte Spare3_0;
        public byte Spare3_1;
        public byte Spare3_2;
        public byte Spare3_3;
        public byte Spare3_4;
        public byte Spare3_5;
        public byte Spare3_6;
        public byte Spare3_7;
        public byte HiberFileType;
        public byte AoAcConnectivitySupported;
        public byte SystemBatteriesPresent;
        public byte BatteriesAreShortTerm;
        public BatteryReportingScale BatteryScale0;
        public BatteryReportingScale BatteryScale1;
        public BatteryReportingScale BatteryScale2;
        public int AcOnLineWake;
        public int SoftLidWake;
        public int RtcWake;
        public int MinDeviceWakeState;
        public int DefaultLowLatencyWake;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BatteryReportingScale
    {
        public uint Granularity;
        public uint Capacity;
    }

    [DllImport("powrprof.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool GetPwrCapabilities(out SystemPowerCapabilities systemPowerCapabilities);
}

public interface IPowerActionExecutor
{
    Task ExecuteAsync(PowerActionType action, CancellationToken cancellationToken = default);
}

public interface IShutdownController
{
    Task<ShutdownExecutionResult> StartGracefulShutdownAsync(CancellationToken cancellationToken = default);
    Task<ShutdownExecutionResult> ForceShutdownAsync(CancellationToken cancellationToken = default);
}

public sealed class PowerActionExecutor : IPowerActionExecutor
{
    private readonly IShutdownController _shutdown;

    public PowerActionExecutor(IShutdownController? shutdown = null)
    {
        _shutdown = shutdown ?? new WindowsShutdownController();
    }

    public async Task ExecuteAsync(PowerActionType action, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        switch (action)
        {
            case PowerActionType.Hibernate:
                if (!SetSuspendState(true, false, false))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "최대 절전 모드로 전환하지 못했습니다.");
                }

                break;
            case PowerActionType.Sleep:
                if (!SetSuspendState(false, false, false))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "절전 모드로 전환하지 못했습니다.");
                }

                break;
            case PowerActionType.Shutdown:
                var result = await _shutdown.StartGracefulShutdownAsync(cancellationToken).ConfigureAwait(false);
                if (!result.Accepted)
                {
                    throw new ShutdownRequestRejectedException(result);
                }
                break;
            case PowerActionType.PowerOn:
                throw new InvalidOperationException("앱 기반 완전 종료 자동 부팅은 현재 제품에서 제공하지 않습니다.");
            case PowerActionType.WakeFromSleep:
            case PowerActionType.WakeFromHibernate:
                throw new InvalidOperationException("예약 깨우기는 직접 실행하는 전원 동작이 아닙니다. 준비 흐름에서 S3/S4 상태로 전환해야 합니다.");
            default:
                throw new ArgumentOutOfRangeException(nameof(action));
        }
    }

    [DllImport("powrprof.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetSuspendState(bool hibernate, bool forceCritical, bool disableWakeEvent);

}

public enum SleepWakeTestTarget
{
    S3 = 3,
    S4 = 4
}

public static class SleepWakeTestPolicy
{
    public static TimeSpan WakeDelay { get; } = TimeSpan.FromMinutes(2);

    public static DateTime CreateWakeTime(DateTime nowLocal)
    {
        var target = nowLocal + WakeDelay;
        return new DateTime(
            target.Year,
            target.Month,
            target.Day,
            target.Hour,
            target.Minute,
            target.Second,
            DateTimeKind.Unspecified);
    }

    public static PowerSchedule CreateSchedule(SleepWakeTestTarget target, DateTime wakeLocalTime) =>
        PowerSchedule.Create(
            wakeLocalTime,
            target switch
            {
                SleepWakeTestTarget.S3 => PowerActionType.WakeFromSleep,
                SleepWakeTestTarget.S4 => PowerActionType.WakeFromHibernate,
                _ => throw new ArgumentOutOfRangeException(nameof(target))
            },
            oneTimeAutoLogonEnabled: true);
}

public enum SleepWakeTestStage
{
    Prepared,
    TransitionRequested,
    CandidateResumeObserved,
    Confirmed,
    Failed
}

public sealed record SleepWakeTestState(
    Guid Id,
    Guid ScheduleId,
    SleepWakeTestTarget Target,
    DateTime RequestedWakeLocalTime,
    DateTimeOffset PreparedAtUtc,
    SleepWakeTestStage Stage,
    string Detail);

public sealed class SleepWakeTestStateStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _path;

    public SleepWakeTestStateStore(string? path = null) =>
        _path = path ?? AppPaths.SleepWakeTestStatePath;

    public SleepWakeTestState Prepare(Guid scheduleId, SleepWakeTestTarget target, DateTime wakeLocalTime)
    {
        var existing = Read();
        if (existing?.Stage is SleepWakeTestStage.Prepared or
            SleepWakeTestStage.TransitionRequested or
            SleepWakeTestStage.CandidateResumeObserved)
        {
            throw new InvalidOperationException("이미 진행 중인 S3 또는 S4 Wake 테스트가 있습니다.");
        }

        var state = new SleepWakeTestState(
            Guid.NewGuid(),
            scheduleId,
            target,
            DateTime.SpecifyKind(wakeLocalTime, DateTimeKind.Unspecified),
            DateTimeOffset.UtcNow,
            SleepWakeTestStage.Prepared,
            $"{TargetName(target)} Wake 예약을 준비했습니다.");
        Write(state);
        return state;
    }

    public SleepWakeTestState MarkTransitionRequested()
    {
        var state = Read() ?? throw new InvalidOperationException("진행 중인 S3 또는 S4 Wake 테스트가 없습니다.");
        if (state.Stage != SleepWakeTestStage.Prepared)
        {
            throw new InvalidOperationException("준비된 Wake 테스트만 전원 상태 전환을 시작할 수 있습니다.");
        }

        state = state with
        {
            Stage = SleepWakeTestStage.TransitionRequested,
            Detail = $"{TargetName(state.Target)} 상태로 전환했습니다. 예약 시각의 자동 깨우기를 기다리는 중입니다."
        };
        Write(state);
        return state;
    }

    public SleepWakeTestState? EvaluateResume(DateTimeOffset resumedAt)
    {
        var state = Read();
        if (state?.Stage != SleepWakeTestStage.TransitionRequested)
        {
            return state;
        }

        var resumedLocal = DateTime.SpecifyKind(resumedAt.LocalDateTime, DateTimeKind.Unspecified);
        var earliestCandidate = state.RequestedWakeLocalTime - TimeSpan.FromMinutes(1);
        var latestCandidate = state.RequestedWakeLocalTime + TimeSpan.FromMinutes(5);
        if (resumedLocal >= earliestCandidate && resumedLocal <= latestCandidate)
        {
            state = state with
            {
                Stage = SleepWakeTestStage.CandidateResumeObserved,
                Detail = $"예약 시각 근처에 {TargetName(state.Target)} 재개가 감지되었습니다. 자동 깨우기였는지 사용자 확인이 필요합니다."
            };
        }
        else if (resumedLocal < earliestCandidate)
        {
            state = state with
            {
                Stage = SleepWakeTestStage.Failed,
                Detail = $"예약 시각 전에 {TargetName(state.Target)} 상태가 해제되어 자동 깨우기를 검증하지 못했습니다."
            };
        }
        else
        {
            state = state with
            {
                Stage = SleepWakeTestStage.Failed,
                Detail = $"예약 시각 후 5분 이내에 {TargetName(state.Target)} 자동 깨우기가 확인되지 않았습니다."
            };
        }

        Write(state);
        return state;
    }

    public SleepWakeTestState ConfirmAutomaticResume()
    {
        var state = Read() ?? throw new InvalidOperationException("진행 중인 S3 또는 S4 Wake 테스트가 없습니다.");
        if (state.Stage != SleepWakeTestStage.CandidateResumeObserved)
        {
            throw new InvalidOperationException("예약 시각 근처의 재개가 먼저 감지되어야 합니다.");
        }

        state = state with
        {
            Stage = SleepWakeTestStage.Confirmed,
            Detail = $"{TargetName(state.Target)} 상태에서 예약 시각 자동 깨우기가 확인되었습니다."
        };
        Write(state);
        return state;
    }

    public SleepWakeTestState MarkFailed(string detail)
    {
        var state = Read() ?? throw new InvalidOperationException("진행 중인 S3 또는 S4 Wake 테스트가 없습니다.");
        state = state with { Stage = SleepWakeTestStage.Failed, Detail = detail };
        Write(state);
        return state;
    }

    public SleepWakeTestState? Read()
    {
        if (!File.Exists(_path))
        {
            return null;
        }

        return JsonSerializer.Deserialize<SleepWakeTestState>(File.ReadAllText(_path));
    }

    public void Clear()
    {
        if (File.Exists(_path))
        {
            File.Delete(_path);
        }
    }

    public static string TargetName(SleepWakeTestTarget target) => target switch
    {
        SleepWakeTestTarget.S3 => "S3 절전",
        SleepWakeTestTarget.S4 => "S4 최대 절전",
        _ => throw new ArgumentOutOfRangeException(nameof(target))
    };

    private void Write(SleepWakeTestState state)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path, JsonSerializer.Serialize(state, JsonOptions));
    }
}
