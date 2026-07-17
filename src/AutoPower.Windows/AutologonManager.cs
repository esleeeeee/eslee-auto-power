using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Win32;

namespace AutoPower.Windows;

public enum AutologonJournalState
{
    Preparing,
    Armed,
    Cleaning,
    Cleaned,
    CleanupFailed,
    RollbackFailed
}

public sealed record RegistrySnapshotValue(bool Exists, string? Value, RegistryValueKind Kind);

public sealed record AutologonRegistrySnapshot(
    RegistrySnapshotValue AutoAdminLogon,
    RegistrySnapshotValue DefaultUserName,
    RegistrySnapshotValue DefaultDomainName,
    RegistrySnapshotValue AutoLogonCount);

public sealed record AutologonJournal(
    Guid OperationId,
    Guid ScheduleId,
    DateTime ScheduledLocalTime,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    AutologonJournalState State,
    AutologonRegistrySnapshot PreviousRegistry,
    string? LastError);

public interface IAutologonJournalStore
{
    AutologonJournal? Read();
    void Write(AutologonJournal journal);
    void Delete();
}

public sealed class JsonAutologonJournalStore : IAutologonJournalStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    private readonly string _path;

    public JsonAutologonJournalStore(string? path = null)
    {
        _path = path ?? AppPaths.AutologonJournalPath;
    }

    public AutologonJournal? Read()
    {
        if (!File.Exists(_path))
        {
            return null;
        }

        return JsonSerializer.Deserialize<AutologonJournal>(File.ReadAllText(_path), Options)
               ?? throw new InvalidDataException("자동 로그인 복구 정보가 손상되었습니다.");
    }

    public void Write(AutologonJournal journal)
    {
        ArgumentNullException.ThrowIfNull(journal);
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temporary = _path + ".tmp";
        var bytes = JsonSerializer.SerializeToUtf8Bytes(journal, Options);
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        {
            stream.Write(bytes);
            stream.Flush(true);
        }

        File.Move(temporary, _path, true);
    }

    public void Delete()
    {
        if (File.Exists(_path))
        {
            File.Delete(_path);
        }
    }
}

public interface IAutologonSystem
{
    bool IsAdministrator { get; }
    bool ExistingAutologonIsEnabled();
    bool PlaintextDefaultPasswordExists();
    bool LsaDefaultPasswordExists();
    AutologonRegistrySnapshot CaptureRegistrySnapshot();
    void ValidateCredential(StoredCredential credential);
    void EnableTemporaryAutologon(StoredCredential credential);
    void DisableTemporaryAutologon(AutologonRegistrySnapshot previousRegistry);
    bool IsRestored(AutologonRegistrySnapshot previousRegistry);
}

public sealed class AutologonManager
{
    private readonly IProtectedCredentialStore _credentialStore;
    private readonly IAutologonSystem _system;
    private readonly IAutologonJournalStore _journalStore;
    private readonly TechnicalLogger _logger;

    public AutologonManager(
        IProtectedCredentialStore credentialStore,
        IAutologonSystem system,
        IAutologonJournalStore journalStore,
        TechnicalLogger logger)
    {
        _credentialStore = credentialStore;
        _system = system;
        _journalStore = journalStore;
        _logger = logger;
    }

    public AutologonJournal? CurrentJournal => _journalStore.Read();

    public AutologonJournal Arm(Guid scheduleId, DateTime scheduledLocalTime)
    {
        EnsureAdministrator();
        var active = _journalStore.Read();
        if (active is not null && active.State is not AutologonJournalState.Cleaned)
        {
            throw new InvalidOperationException("정리되지 않은 1회 자동 로그인 상태가 있어 새로 준비할 수 없습니다.");
        }

        if (_system.ExistingAutologonIsEnabled() ||
            _system.PlaintextDefaultPasswordExists() ||
            _system.LsaDefaultPasswordExists())
        {
            throw new InvalidOperationException("기존 Windows 자동 로그인 설정이 감지되어 안전하게 함께 사용할 수 없습니다.");
        }

        var credential = _credentialStore.Read()
                         ?? throw new InvalidOperationException("설정에서 Windows 로그인 자격 증명을 먼저 등록하세요.");
        _system.ValidateCredential(credential);
        var now = DateTimeOffset.UtcNow;
        var journal = new AutologonJournal(
            Guid.NewGuid(),
            scheduleId,
            DateTime.SpecifyKind(scheduledLocalTime, DateTimeKind.Unspecified),
            now,
            now,
            AutologonJournalState.Preparing,
            _system.CaptureRegistrySnapshot(),
            null);
        _journalStore.Write(journal);

        try
        {
            _system.EnableTemporaryAutologon(credential);
            journal = journal with { State = AutologonJournalState.Armed, UpdatedAtUtc = DateTimeOffset.UtcNow };
            _journalStore.Write(journal);
            _logger.Information("autologon.armed", $"schedule={scheduleId:D}");
            return journal;
        }
        catch (Exception error)
        {
            try
            {
                _system.DisableTemporaryAutologon(journal.PreviousRegistry);
                _journalStore.Delete();
            }
            catch (Exception rollbackError)
            {
                journal = journal with
                {
                    State = AutologonJournalState.RollbackFailed,
                    UpdatedAtUtc = DateTimeOffset.UtcNow,
                    LastError = rollbackError.GetType().Name
                };
                _journalStore.Write(journal);
                _logger.Error("autologon.arm.rollback-failed", rollbackError, $"schedule={scheduleId:D}");
            }

            _logger.Error("autologon.arm.failed", error, $"schedule={scheduleId:D}");
            throw;
        }
    }

    public AutologonJournal? Cleanup()
    {
        EnsureAdministrator();
        var journal = _journalStore.Read();
        if (journal is null)
        {
            return null;
        }

        journal = journal with { State = AutologonJournalState.Cleaning, UpdatedAtUtc = DateTimeOffset.UtcNow, LastError = null };
        _journalStore.Write(journal);
        Exception? lastError = null;
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            try
            {
                _system.DisableTemporaryAutologon(journal.PreviousRegistry);
                if (!_system.IsRestored(journal.PreviousRegistry))
                {
                    throw new InvalidOperationException("자동 로그인 설정 제거 후 검증에 실패했습니다.");
                }

                journal = journal with
                {
                    State = AutologonJournalState.Cleaned,
                    UpdatedAtUtc = DateTimeOffset.UtcNow,
                    LastError = null
                };
                _journalStore.Write(journal);
                _logger.Information("autologon.cleaned", $"schedule={journal.ScheduleId:D}");
                return journal;
            }
            catch (Exception error)
            {
                lastError = error;
                _logger.Error("autologon.cleanup.retry", error, $"schedule={journal.ScheduleId:D};attempt={attempt}");
            }
        }

        journal = journal with
        {
            State = AutologonJournalState.CleanupFailed,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
            LastError = lastError?.GetType().Name
        };
        _journalStore.Write(journal);
        throw new InvalidOperationException(
            "1회 자동 로그인 설정을 해제하지 못했습니다. 다음 부팅에서도 자동 로그인이 시도될 수 있습니다.",
            lastError);
    }

    private void EnsureAdministrator()
    {
        if (!_system.IsAdministrator)
        {
            throw new UnauthorizedAccessException("이 작업은 관리자 권한이 필요합니다.");
        }
    }
}

public sealed class WindowsAutologonSystem : IAutologonSystem
{
    private const string WinlogonPath = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon";
    private const string LsaSecretName = "DefaultPassword";
    private const int Logon32LogonInteractive = 2;
    private const int Logon32ProviderDefault = 0;

    public bool IsAdministrator
    {
        get
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
    }

    public bool ExistingAutologonIsEnabled() =>
        string.Equals(ReadString("AutoAdminLogon"), "1", StringComparison.OrdinalIgnoreCase);

    public bool PlaintextDefaultPasswordExists() => ValueExists("DefaultPassword");

    public bool LsaDefaultPasswordExists() => LsaSecretStore.Exists(LsaSecretName);

    public AutologonRegistrySnapshot CaptureRegistrySnapshot() => new(
        Capture("AutoAdminLogon"),
        Capture("DefaultUserName"),
        Capture("DefaultDomainName"),
        Capture("AutoLogonCount"));

    public void ValidateCredential(StoredCredential credential)
    {
        var (domain, user) = WindowsCredentialIdentity.Split(credential.UserName);
        if (!LogonUser(user, domain, credential.Password, Logon32LogonInteractive, Logon32ProviderDefault, out var token))
        {
            var error = Marshal.GetLastWin32Error();
            var message = WindowsCredentialIdentity.IsMicrosoftAccount(credential.UserName)
                ? "Microsoft 계정 로그인 검증에 실패했습니다. Windows Hello PIN이 아닌 실제 Microsoft 계정 암호를 입력하세요. 암호 없는 계정에서는 이 기능을 사용할 수 없습니다."
                : "Windows 로그인 자격 증명이 올바르지 않거나 이 계정에서 자동 로그인을 사용할 수 없습니다.";
            throw new Win32Exception(error, message);
        }

        CloseHandle(token);
    }

    public void EnableTemporaryAutologon(StoredCredential credential)
    {
        var (domain, user) = WindowsCredentialIdentity.Split(credential.UserName);
        LsaSecretStore.Store(LsaSecretName, credential.Password);
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(WinlogonPath, true)
                            ?? throw new InvalidOperationException("Windows 로그인 설정을 열 수 없습니다.");
            key.SetValue("DefaultUserName", user, RegistryValueKind.String);
            key.SetValue("DefaultDomainName", domain ?? string.Empty, RegistryValueKind.String);
            key.DeleteValue("AutoLogonCount", false);
            key.SetValue("AutoAdminLogon", "1", RegistryValueKind.String);
            key.Flush();
        }
        catch
        {
            LsaSecretStore.Delete(LsaSecretName);
            throw;
        }
    }

    public void DisableTemporaryAutologon(AutologonRegistrySnapshot previousRegistry)
    {
        using var key = Registry.LocalMachine.OpenSubKey(WinlogonPath, true)
                        ?? throw new InvalidOperationException("Windows 로그인 설정을 열 수 없습니다.");
        key.SetValue("AutoAdminLogon", "0", RegistryValueKind.String);
        key.DeleteValue("AutoLogonCount", false);
        key.Flush();
        LsaSecretStore.Delete(LsaSecretName);

        Restore(key, "DefaultUserName", previousRegistry.DefaultUserName);
        Restore(key, "DefaultDomainName", previousRegistry.DefaultDomainName);
        Restore(key, "AutoLogonCount", previousRegistry.AutoLogonCount);
        Restore(key, "AutoAdminLogon", previousRegistry.AutoAdminLogon);
        key.Flush();
    }

    public bool IsRestored(AutologonRegistrySnapshot previousRegistry) =>
        !LsaSecretStore.Exists(LsaSecretName) &&
        Matches("AutoAdminLogon", previousRegistry.AutoAdminLogon) &&
        Matches("DefaultUserName", previousRegistry.DefaultUserName) &&
        Matches("DefaultDomainName", previousRegistry.DefaultDomainName) &&
        Matches("AutoLogonCount", previousRegistry.AutoLogonCount);

    private static RegistrySnapshotValue Capture(string name)
    {
        using var key = Registry.LocalMachine.OpenSubKey(WinlogonPath, false)
                        ?? throw new InvalidOperationException("Windows 로그인 설정을 열 수 없습니다.");
        if (!key.GetValueNames().Contains(name, StringComparer.OrdinalIgnoreCase))
        {
            return new RegistrySnapshotValue(false, null, RegistryValueKind.None);
        }

        return new RegistrySnapshotValue(true, Convert.ToString(key.GetValue(name), System.Globalization.CultureInfo.InvariantCulture), key.GetValueKind(name));
    }

    private static string? ReadString(string name)
    {
        using var key = Registry.LocalMachine.OpenSubKey(WinlogonPath, false);
        return key?.GetValue(name) as string;
    }

    private static bool ValueExists(string name)
    {
        using var key = Registry.LocalMachine.OpenSubKey(WinlogonPath, false);
        return key?.GetValueNames().Contains(name, StringComparer.OrdinalIgnoreCase) == true;
    }

    private static void Restore(RegistryKey key, string name, RegistrySnapshotValue value)
    {
        if (!value.Exists)
        {
            key.DeleteValue(name, false);
            return;
        }

        if (value.Value is null || value.Value.Length > 512)
        {
            throw new InvalidDataException("자동 로그인 복구 정보의 레지스트리 값이 올바르지 않습니다.");
        }

        var safeKind = value.Kind is RegistryValueKind.String or RegistryValueKind.ExpandString
            ? value.Kind
            : RegistryValueKind.String;
        key.SetValue(name, value.Value, safeKind);
    }

    private static bool Matches(string name, RegistrySnapshotValue expected)
    {
        using var key = Registry.LocalMachine.OpenSubKey(WinlogonPath, false);
        if (key is null)
        {
            return false;
        }

        var exists = key.GetValueNames().Contains(name, StringComparer.OrdinalIgnoreCase);
        return expected.Exists == exists &&
               (!exists || string.Equals(Convert.ToString(key.GetValue(name), System.Globalization.CultureInfo.InvariantCulture), expected.Value, StringComparison.Ordinal));
    }

    [DllImport("advapi32.dll", EntryPoint = "LogonUserW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LogonUser(
        string userName,
        string? domain,
        string password,
        int logonType,
        int logonProvider,
        out IntPtr token);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}

internal static class LsaSecretStore
{
    private const int PolicyGetPrivateInformation = 0x00000004;
    private const int PolicyCreateSecret = 0x00000020;

    public static bool Exists(string keyName)
    {
        var policy = OpenPolicy(PolicyGetPrivateInformation);
        try
        {
            using var key = new LsaString(keyName);
            var status = LsaRetrievePrivateData(policy, ref key.Value, out var dataPointer);
            if (status == 0)
            {
                if (dataPointer != IntPtr.Zero)
                {
                    _ = LsaFreeMemory(dataPointer);
                }

                return true;
            }

            var error = checked((int)LsaNtStatusToWinError(status));
            if (error is 2 or 1168)
            {
                return false;
            }

            throw new Win32Exception(error, "LSA 보호 저장소를 확인하지 못했습니다.");
        }
        finally
        {
            _ = LsaClose(policy);
        }
    }

    public static string? Retrieve(string keyName)
    {
        var policy = OpenPolicy(PolicyGetPrivateInformation);
        try
        {
            using var key = new LsaString(keyName);
            var status = LsaRetrievePrivateData(policy, ref key.Value, out var dataPointer);
            if (status != 0)
            {
                var error = checked((int)LsaNtStatusToWinError(status));
                if (error is 2 or 1168)
                {
                    return null;
                }

                throw new Win32Exception(error, "LSA 보호 저장소를 읽지 못했습니다.");
            }

            if (dataPointer == IntPtr.Zero)
            {
                return null;
            }

            try
            {
                var value = Marshal.PtrToStructure<LsaUnicodeString>(dataPointer);
                return value.Buffer == IntPtr.Zero ? null : Marshal.PtrToStringUni(value.Buffer, value.Length / sizeof(char));
            }
            finally
            {
                _ = LsaFreeMemory(dataPointer);
            }
        }
        finally
        {
            _ = LsaClose(policy);
        }
    }

    public static void Store(string keyName, string secret)
    {
        var policy = OpenPolicy(PolicyCreateSecret);
        try
        {
            using var key = new LsaString(keyName);
            using var value = new LsaString(secret);
            var pointer = Marshal.AllocHGlobal(Marshal.SizeOf<LsaUnicodeString>());
            try
            {
                Marshal.StructureToPtr(value.Value, pointer, false);
                ThrowIfFailed(LsaStorePrivateData(policy, ref key.Value, pointer), "LSA 보호 저장소에 자동 로그인 자격 증명을 저장하지 못했습니다.");
            }
            finally
            {
                Marshal.FreeHGlobal(pointer);
            }
        }
        finally
        {
            _ = LsaClose(policy);
        }
    }

    public static void Delete(string keyName)
    {
        var policy = OpenPolicy(PolicyCreateSecret);
        try
        {
            using var key = new LsaString(keyName);
            var status = LsaStorePrivateData(policy, ref key.Value, IntPtr.Zero);
            if (status != 0)
            {
                var error = checked((int)LsaNtStatusToWinError(status));
                if (error is not (2 or 1168))
                {
                    throw new Win32Exception(error, "LSA 자동 로그인 자격 증명을 제거하지 못했습니다.");
                }
            }
        }
        finally
        {
            _ = LsaClose(policy);
        }
    }

    private static IntPtr OpenPolicy(int access)
    {
        var attributes = new LsaObjectAttributes { Length = Marshal.SizeOf<LsaObjectAttributes>() };
        ThrowIfFailed(LsaOpenPolicy(IntPtr.Zero, ref attributes, access, out var policy), "로컬 보안 정책을 열 수 없습니다.");
        return policy;
    }

    private static void ThrowIfFailed(uint status, string message)
    {
        if (status != 0)
        {
            throw new Win32Exception(checked((int)LsaNtStatusToWinError(status)), message);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct LsaUnicodeString
    {
        public ushort Length;
        public ushort MaximumLength;
        public IntPtr Buffer;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LsaObjectAttributes
    {
        public int Length;
        public IntPtr RootDirectory;
        public IntPtr ObjectName;
        public uint Attributes;
        public IntPtr SecurityDescriptor;
        public IntPtr SecurityQualityOfService;
    }

    private sealed class LsaString : IDisposable
    {
        public LsaString(string value)
        {
            Buffer = Marshal.StringToHGlobalUni(value);
            Value = new LsaUnicodeString
            {
                Buffer = Buffer,
                Length = checked((ushort)(value.Length * sizeof(char))),
                MaximumLength = checked((ushort)((value.Length + 1) * sizeof(char)))
            };
        }

        public IntPtr Buffer { get; }
        public LsaUnicodeString Value;

        public void Dispose()
        {
            if (Value.Length > 0)
            {
                unsafe
                {
                    new Span<byte>(Buffer.ToPointer(), Value.MaximumLength).Clear();
                }
            }

            Marshal.FreeHGlobal(Buffer);
        }
    }

    [DllImport("advapi32.dll", SetLastError = false)]
    private static extern uint LsaOpenPolicy(IntPtr systemName, ref LsaObjectAttributes objectAttributes, int desiredAccess, out IntPtr policyHandle);

    [DllImport("advapi32.dll", SetLastError = false)]
    private static extern uint LsaStorePrivateData(IntPtr policyHandle, ref LsaUnicodeString keyName, IntPtr privateData);

    [DllImport("advapi32.dll", SetLastError = false)]
    private static extern uint LsaRetrievePrivateData(IntPtr policyHandle, ref LsaUnicodeString keyName, out IntPtr privateData);

    [DllImport("advapi32.dll", SetLastError = false)]
    private static extern uint LsaNtStatusToWinError(uint status);

    [DllImport("advapi32.dll", SetLastError = false)]
    private static extern uint LsaFreeMemory(IntPtr buffer);

    [DllImport("advapi32.dll", SetLastError = false)]
    private static extern uint LsaClose(IntPtr policyHandle);
}
