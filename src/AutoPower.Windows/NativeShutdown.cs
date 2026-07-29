using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Principal;
using AutoPower.Core;
using Microsoft.Win32.SafeHandles;

namespace AutoPower.Windows;

public enum ShutdownStage
{
    Primary,
    Fallback
}

public enum ShutdownDisposition
{
    Accepted,
    AlreadyInProgress,
    ScheduledPending,
    Rejected
}

public sealed record ShutdownRequest(
    ShutdownStage Stage,
    uint GraceSeconds,
    uint Flags,
    uint Reason);

public sealed record ShutdownPrivilegeResult(
    bool? HasPrivilege,
    bool? WasEnabled,
    bool Enabled,
    string Operation,
    uint NativeErrorCode,
    string SymbolicError,
    string SystemMessage);

public sealed record ShutdownNativeAttempt(
    uint Flags,
    uint ReturnCode,
    string SymbolicError,
    string SystemMessage);

public sealed record ShutdownExecutionContext(
    int ProcessId,
    int SessionId,
    bool IsSystem,
    bool IsElevated);

public sealed record ShutdownExecutionResult(
    ShutdownRequest Request,
    ShutdownDisposition Disposition,
    ShutdownPrivilegeResult Privilege,
    ShutdownExecutionContext Context,
    IReadOnlyList<ShutdownNativeAttempt> Attempts,
    DateTimeOffset StartedAt,
    DateTimeOffset EndedAt)
{
    public bool Accepted => Disposition is not ShutdownDisposition.Rejected;
    public uint NativeErrorCode => Attempts.Count == 0
        ? Privilege.NativeErrorCode
        : Attempts[^1].ReturnCode;
    public string SymbolicError => Attempts.Count == 0
        ? Privilege.SymbolicError
        : Attempts[^1].SymbolicError;
    public string SystemMessage => Attempts.Count == 0
        ? Privilege.SystemMessage
        : Attempts[^1].SystemMessage;
    public double ElapsedMilliseconds => Math.Max(0, (EndedAt - StartedAt).TotalMilliseconds);
}

public sealed class ShutdownRequestRejectedException : Exception
{
    public ShutdownRequestRejectedException(ShutdownExecutionResult result)
        : base($"InitiateShutdownW가 종료 요청을 거부했습니다: {result.NativeErrorCode} ({result.SymbolicError}) {result.SystemMessage}")
    {
        Result = result;
    }

    public ShutdownExecutionResult Result { get; }
    public uint NativeErrorCode => Result.NativeErrorCode;
}

internal interface IWindowsShutdownNativeApi
{
    ShutdownPrivilegeResult EnableShutdownPrivilege();
    uint InitiateShutdown(uint graceSeconds, uint flags, uint reason);
    string? FormatMessage(uint errorCode);
    ShutdownExecutionContext CaptureExecutionContext();
}

internal static class ShutdownNativeConstants
{
    internal const uint ErrorSuccess = 0;
    internal const uint ErrorInvalidFunction = 1;
    internal const uint ErrorAccessDenied = 5;
    internal const uint ErrorNotReady = 21;
    internal const uint ErrorInvalidParameter = 87;
    internal const uint ErrorShutdownInProgress = 1115;
    internal const uint ErrorShutdownIsScheduled = 1190;
    internal const uint ErrorShutdownUsersLoggedOn = 1191;
    internal const uint ErrorNotAllAssigned = 1300;

    internal const uint ShutdownForceOthers = 0x00000001;
    internal const uint ShutdownForceSelf = 0x00000002;
    internal const uint ShutdownPowerOff = 0x00000008;
    internal const uint ShutdownGraceOverride = 0x00000020;
    internal const uint ShutdownHybrid = 0x00000200;

    internal const uint ReasonMajorApplication = 0x00040000;
    internal const uint ReasonMinorMaintenance = 0x00000001;
    internal const uint ReasonFlagPlanned = 0x80000000;
    internal const uint PlannedApplicationMaintenance =
        ReasonMajorApplication | ReasonMinorMaintenance | ReasonFlagPlanned;

    internal const uint RequiredShutdownFlags =
        ShutdownPowerOff | ShutdownForceSelf | ShutdownForceOthers;
}

internal static class ShutdownErrorCatalog
{
    internal static string SymbolicName(uint errorCode) => errorCode switch
    {
        ShutdownNativeConstants.ErrorSuccess => "ERROR_SUCCESS",
        ShutdownNativeConstants.ErrorAccessDenied => "ERROR_ACCESS_DENIED",
        ShutdownNativeConstants.ErrorInvalidFunction => "ERROR_INVALID_FUNCTION",
        ShutdownNativeConstants.ErrorInvalidParameter => "ERROR_INVALID_PARAMETER",
        ShutdownNativeConstants.ErrorShutdownInProgress => "ERROR_SHUTDOWN_IN_PROGRESS",
        ShutdownNativeConstants.ErrorShutdownIsScheduled => "ERROR_SHUTDOWN_IS_SCHEDULED",
        ShutdownNativeConstants.ErrorShutdownUsersLoggedOn => "ERROR_SHUTDOWN_USERS_LOGGED_ON",
        ShutdownNativeConstants.ErrorNotReady => "ERROR_NOT_READY",
        ShutdownNativeConstants.ErrorNotAllAssigned => "ERROR_NOT_ALL_ASSIGNED",
        _ => $"WIN32_ERROR_{errorCode.ToString(CultureInfo.InvariantCulture)}"
    };

    internal static string FlagNames(uint flags)
    {
        var names = new List<string>();
        AddFlag(names, flags, ShutdownNativeConstants.ShutdownPowerOff, "SHUTDOWN_POWEROFF");
        AddFlag(names, flags, ShutdownNativeConstants.ShutdownForceSelf, "SHUTDOWN_FORCE_SELF");
        AddFlag(names, flags, ShutdownNativeConstants.ShutdownForceOthers, "SHUTDOWN_FORCE_OTHERS");
        AddFlag(names, flags, ShutdownNativeConstants.ShutdownGraceOverride, "SHUTDOWN_GRACE_OVERRIDE");
        AddFlag(names, flags, ShutdownNativeConstants.ShutdownHybrid, "SHUTDOWN_HYBRID");
        return names.Count == 0 ? "NONE" : string.Join('|', names);
    }

    private static void AddFlag(List<string> names, uint flags, uint value, string name)
    {
        if ((flags & value) == value)
        {
            names.Add(name);
        }
    }
}

internal static class ShutdownPrivilegeEvaluator
{
    internal static ShutdownPrivilegeResult ApiFailure(
        string operation,
        uint errorCode,
        Func<uint, string?> formatMessage) =>
        new(
            null,
            null,
            false,
            operation,
            errorCode,
            ShutdownErrorCatalog.SymbolicName(errorCode),
            SafeMessage(errorCode, formatMessage));

    internal static ShutdownPrivilegeResult Adjustment(
        bool adjustmentReturned,
        uint lastError,
        bool wasEnabled,
        Func<uint, string?> formatMessage)
    {
        var enabled = adjustmentReturned && lastError == ShutdownNativeConstants.ErrorSuccess;
        bool? held = lastError switch
        {
            ShutdownNativeConstants.ErrorSuccess when adjustmentReturned => true,
            ShutdownNativeConstants.ErrorNotAllAssigned => false,
            _ => null
        };
        var effectiveError = enabled ? ShutdownNativeConstants.ErrorSuccess : lastError;
        return new ShutdownPrivilegeResult(
            held,
            held is true ? wasEnabled : null,
            enabled,
            "AdjustTokenPrivileges",
            effectiveError,
            ShutdownErrorCatalog.SymbolicName(effectiveError),
            SafeMessage(effectiveError, formatMessage));
    }

    private static string SafeMessage(uint errorCode, Func<uint, string?> formatMessage)
    {
        var message = formatMessage(errorCode);
        return string.IsNullOrWhiteSpace(message)
            ? $"Win32 error {errorCode.ToString(CultureInfo.InvariantCulture)}"
            : message.Trim();
    }
}

internal sealed class WindowsShutdownNativeApi : IWindowsShutdownNativeApi
{
    private const uint TokenQuery = 0x0008;
    private const uint TokenAdjustPrivileges = 0x0020;
    private const uint SePrivilegeEnabled = 0x00000002;
    private const uint FormatMessageFromSystem = 0x00001000;
    private const uint FormatMessageIgnoreInserts = 0x00000200;
    private const string SeShutdownName = "SeShutdownPrivilege";

    public ShutdownPrivilegeResult EnableShutdownPrivilege()
    {
        using var process = Process.GetCurrentProcess();
        if (!OpenProcessToken(
                process.Handle,
                TokenQuery | TokenAdjustPrivileges,
                out var token))
        {
            var error = unchecked((uint)Marshal.GetLastPInvokeError());
            return ShutdownPrivilegeEvaluator.ApiFailure("OpenProcessToken", error, FormatMessage);
        }

        using (token)
        {
            if (!LookupPrivilegeValueW(null, SeShutdownName, out var luid))
            {
                var error = unchecked((uint)Marshal.GetLastPInvokeError());
                return ShutdownPrivilegeEvaluator.ApiFailure("LookupPrivilegeValueW", error, FormatMessage);
            }

            var requested = new TokenPrivileges
            {
                PrivilegeCount = 1,
                Privileges = new LuidAndAttributes
                {
                    Luid = luid,
                    Attributes = SePrivilegeEnabled
                }
            };
            Marshal.SetLastPInvokeError(0);
            var adjusted = AdjustTokenPrivileges(
                token,
                false,
                ref requested,
                Marshal.SizeOf<TokenPrivileges>(),
                out var previous,
                out _);
            var lastError = unchecked((uint)Marshal.GetLastPInvokeError());
            var wasEnabled = previous.PrivilegeCount > 0 &&
                             (previous.Privileges.Attributes & SePrivilegeEnabled) != 0;
            return ShutdownPrivilegeEvaluator.Adjustment(adjusted, lastError, wasEnabled, FormatMessage);
        }
    }

    public uint InitiateShutdown(uint graceSeconds, uint flags, uint reason) =>
        InitiateShutdownW(
            null,
            "eslee Auto Power 예약 완전 종료",
            graceSeconds,
            flags,
            reason);

    public unsafe string? FormatMessage(uint errorCode)
    {
        const int capacity = 512;
        var buffer = stackalloc char[capacity];
        var length = FormatMessageW(
            FormatMessageFromSystem | FormatMessageIgnoreInserts,
            IntPtr.Zero,
            errorCode,
            0,
            buffer,
            capacity,
            IntPtr.Zero);
        return length == 0 ? null : new string(buffer, 0, checked((int)length));
    }

    public ShutdownExecutionContext CaptureExecutionContext()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        using var process = Process.GetCurrentProcess();
        return new ShutdownExecutionContext(
            Environment.ProcessId,
            process.SessionId,
            identity.IsSystem,
            principal.IsInRole(WindowsBuiltInRole.Administrator));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Luid
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LuidAndAttributes
    {
        public Luid Luid;
        public uint Attributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenPrivileges
    {
        public uint PrivilegeCount;
        public LuidAndAttributes Privileges;
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(
        IntPtr processHandle,
        uint desiredAccess,
        out SafeAccessTokenHandle tokenHandle);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LookupPrivilegeValueW(
        string? systemName,
        string name,
        out Luid luid);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AdjustTokenPrivileges(
        SafeAccessTokenHandle tokenHandle,
        [MarshalAs(UnmanagedType.Bool)] bool disableAllPrivileges,
        ref TokenPrivileges newState,
        int bufferLength,
        out TokenPrivileges previousState,
        out int returnLength);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    private static extern uint InitiateShutdownW(
        string? machineName,
        string? message,
        uint gracePeriod,
        uint shutdownFlags,
        uint reason);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern unsafe uint FormatMessageW(
        uint flags,
        IntPtr source,
        uint messageId,
        uint languageId,
        char* buffer,
        int size,
        IntPtr arguments);
}

public sealed class WindowsShutdownController : IShutdownController
{
    private readonly IWindowsShutdownNativeApi _native;
    private readonly TechnicalLogger _logger;
    private readonly Func<DateTimeOffset> _clock;

    public WindowsShutdownController(
        TechnicalLogger? logger = null,
        Func<DateTimeOffset>? clock = null)
        : this(new WindowsShutdownNativeApi(), logger ?? new TechnicalLogger(), clock)
    {
    }

    internal WindowsShutdownController(
        IWindowsShutdownNativeApi native,
        TechnicalLogger logger,
        Func<DateTimeOffset>? clock = null)
    {
        _native = native;
        _logger = logger;
        _clock = clock ?? (() => DateTimeOffset.Now);
    }

    public Task<ShutdownExecutionResult> StartGracefulShutdownAsync(
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(
            new ShutdownRequest(
                ShutdownStage.Primary,
                checked((uint)WarningPolicy.ShutdownGracePeriod.TotalSeconds),
                ShutdownNativeConstants.RequiredShutdownFlags,
                ShutdownNativeConstants.PlannedApplicationMaintenance),
            cancellationToken);

    public Task<ShutdownExecutionResult> ForceShutdownAsync(
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(
            new ShutdownRequest(
                ShutdownStage.Fallback,
                0,
                ShutdownNativeConstants.RequiredShutdownFlags,
                ShutdownNativeConstants.PlannedApplicationMaintenance),
            cancellationToken);

    internal static string FormatDiagnostic(ShutdownExecutionResult result)
    {
        var attempts = result.Attempts.Count == 0
            ? "none"
            : string.Join(",", result.Attempts.Select((attempt, index) =>
                $"{index + 1}:{attempt.ReturnCode}/{attempt.SymbolicError}/0x{attempt.Flags:X8}"));
        return string.Join(";",
            $"stage={result.Request.Stage}",
            $"pid={result.Context.ProcessId}",
            $"session={result.Context.SessionId}",
            $"system={result.Context.IsSystem}",
            $"elevated={result.Context.IsElevated}",
            $"privilegeHeld={NullableBool(result.Privilege.HasPrivilege)}",
            $"privilegeWasEnabled={NullableBool(result.Privilege.WasEnabled)}",
            $"privilegeEnabled={result.Privilege.Enabled}",
            $"privilegeOperation={result.Privilege.Operation}",
            "api=InitiateShutdownW",
            $"graceSeconds={result.Request.GraceSeconds}",
            $"flags=0x{result.Request.Flags:X8}",
            $"flagNames={ShutdownErrorCatalog.FlagNames(result.Request.Flags)}",
            $"reason=0x{result.Request.Reason:X8}",
            $"return={result.NativeErrorCode}",
            $"symbolic={result.SymbolicError}",
            $"message={Sanitize(result.SystemMessage)}",
            $"started={result.StartedAt:O}",
            $"ended={result.EndedAt:O}",
            $"elapsedMs={result.ElapsedMilliseconds.ToString("F3", CultureInfo.InvariantCulture)}",
            $"disposition={result.Disposition}",
            $"attempts={attempts}");
    }

    private Task<ShutdownExecutionResult> ExecuteAsync(
        ShutdownRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var started = _clock();
        var context = _native.CaptureExecutionContext();
        var privilege = _native.EnableShutdownPrivilege();
        var attempts = new List<ShutdownNativeAttempt>();
        var disposition = ShutdownDisposition.Rejected;

        if (privilege.Enabled)
        {
            var first = Invoke(request.Flags);
            attempts.Add(first);
            disposition = Classify(request.Stage, first.ReturnCode);

            if (request.Stage == ShutdownStage.Fallback &&
                first.ReturnCode == ShutdownNativeConstants.ErrorShutdownIsScheduled)
            {
                var overrideFlags = request.Flags | ShutdownNativeConstants.ShutdownGraceOverride;
                var retry = Invoke(overrideFlags);
                attempts.Add(retry);
                disposition = Classify(request.Stage, retry.ReturnCode);
            }
        }

        var result = new ShutdownExecutionResult(
            request,
            disposition,
            privilege,
            context,
            attempts,
            started,
            _clock());
        var detail = FormatDiagnostic(result);
        if (result.Accepted)
        {
            _logger.Information("shutdown.native-accepted", detail);
        }
        else
        {
            _logger.Warning("shutdown.native-rejected", detail);
        }

        return Task.FromResult(result);

        ShutdownNativeAttempt Invoke(uint flags)
        {
            var code = _native.InitiateShutdown(request.GraceSeconds, flags, request.Reason);
            var message = _native.FormatMessage(code);
            return new ShutdownNativeAttempt(
                flags,
                code,
                ShutdownErrorCatalog.SymbolicName(code),
                string.IsNullOrWhiteSpace(message)
                    ? $"Win32 error {code.ToString(CultureInfo.InvariantCulture)}"
                    : message.Trim());
        }
    }

    private static ShutdownDisposition Classify(ShutdownStage stage, uint returnCode)
    {
        if (returnCode == ShutdownNativeConstants.ErrorSuccess)
        {
            return ShutdownDisposition.Accepted;
        }

        if (stage == ShutdownStage.Fallback)
        {
            if (returnCode == ShutdownNativeConstants.ErrorShutdownInProgress)
            {
                return ShutdownDisposition.AlreadyInProgress;
            }

            if (returnCode == ShutdownNativeConstants.ErrorShutdownIsScheduled)
            {
                return ShutdownDisposition.ScheduledPending;
            }
        }

        return ShutdownDisposition.Rejected;
    }

    private static string NullableBool(bool? value) => value?.ToString() ?? "Unknown";
    private static string Sanitize(string value) => value.Replace('\r', ' ').Replace('\n', ' ');
}
