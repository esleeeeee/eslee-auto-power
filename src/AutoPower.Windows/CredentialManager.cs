using System.ComponentModel;
using System.Runtime.InteropServices;

namespace AutoPower.Windows;

public sealed record StoredCredential(string UserName, string Password);

public static class WindowsCredentialIdentity
{
    private const string MicrosoftAccountDomain = "MicrosoftAccount";

    public static string Normalize(string identity)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(identity);
        var trimmed = identity.Trim();
        var separator = trimmed.IndexOf('\\', StringComparison.Ordinal);
        if (separator >= 0)
        {
            if (separator == 0 || separator == trimmed.Length - 1)
            {
                throw new ArgumentException("Windows 사용자 이름은 '도메인\\사용자' 형식이어야 합니다.", nameof(identity));
            }

            return trimmed;
        }

        return trimmed.Contains('@', StringComparison.Ordinal)
            ? $@"{MicrosoftAccountDomain}\{trimmed}"
            : $@"{Environment.MachineName}\{trimmed}";
    }

    public static (string Domain, string User) Split(string identity)
    {
        var normalized = Normalize(identity);
        var separator = normalized.IndexOf('\\', StringComparison.Ordinal);
        return (normalized[..separator], normalized[(separator + 1)..]);
    }

    public static bool IsMicrosoftAccount(string identity) =>
        Normalize(identity).StartsWith($@"{MicrosoftAccountDomain}\", StringComparison.OrdinalIgnoreCase);
}

public interface IProtectedCredentialStore
{
    void Save(string userName, string password);
    StoredCredential? Read();
    bool Exists();
    void Delete();
}

public sealed class CredentialManager : IProtectedCredentialStore
{
    public const string TargetName = "eslee.AutoPower/OneTimeAutoLogon";
    private const int CredentialTypeGeneric = 1;
    private const int CredentialPersistLocalMachine = 2;
    private const int ErrorNotFound = 1168;

    public void Save(string userName, string password)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userName);
        ArgumentException.ThrowIfNullOrWhiteSpace(password);
        var blob = Marshal.StringToCoTaskMemUni(password);
        try
        {
            var credential = new NativeCredential
            {
                Type = CredentialTypeGeneric,
                TargetName = TargetName,
                CredentialBlobSize = checked((uint)(password.Length * sizeof(char))),
                CredentialBlob = blob,
                Persist = CredentialPersistLocalMachine,
                UserName = userName
            };
            if (!CredWrite(ref credential, 0))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows 자격 증명 저장에 실패했습니다.");
            }
        }
        finally
        {
            Marshal.ZeroFreeCoTaskMemUnicode(blob);
        }
    }

    public StoredCredential? Read()
    {
        if (!CredRead(TargetName, CredentialTypeGeneric, 0, out var pointer))
        {
            var error = Marshal.GetLastWin32Error();
            if (error == ErrorNotFound)
            {
                return null;
            }

            throw new Win32Exception(error, "Windows 자격 증명 읽기에 실패했습니다.");
        }

        try
        {
            var credential = Marshal.PtrToStructure<NativeCredential>(pointer);
            var password = credential.CredentialBlob == IntPtr.Zero
                ? string.Empty
                : Marshal.PtrToStringUni(credential.CredentialBlob, checked((int)credential.CredentialBlobSize / sizeof(char))) ?? string.Empty;
            return new StoredCredential(credential.UserName ?? string.Empty, password);
        }
        finally
        {
            CredFree(pointer);
        }
    }

    public bool Exists() => Read() is not null;

    public void Delete()
    {
        if (!CredDelete(TargetName, CredentialTypeGeneric, 0))
        {
            var error = Marshal.GetLastWin32Error();
            if (error != ErrorNotFound)
            {
                throw new Win32Exception(error, "Windows 자격 증명 제거에 실패했습니다.");
            }
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeCredential
    {
        public uint Flags;
        public int Type;
        public string? TargetName;
        public string? Comment;
        public long LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public int Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias;
        public string? UserName;
    }

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredWrite(ref NativeCredential credential, uint flags);

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredRead(string target, int type, int reservedFlag, out IntPtr credentialPtr);

    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredDelete(string target, int type, int flags);

    [DllImport("advapi32.dll", SetLastError = false)]
    private static extern void CredFree(IntPtr credential);
}
