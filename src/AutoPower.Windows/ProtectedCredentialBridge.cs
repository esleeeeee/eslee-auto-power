using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AutoPower.Windows;

public sealed class LsaAppCredentialStore : IProtectedCredentialStore
{
    private const string SecretName = "eslee.AutoPower.Credential.v1";
    private static readonly JsonSerializerOptions JsonOptions = new();

    public void Save(string userName, string password)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userName);
        ArgumentException.ThrowIfNullOrWhiteSpace(password);
        LsaSecretStore.Store(SecretName, JsonSerializer.Serialize(new StoredCredential(userName, password), JsonOptions));
    }

    public StoredCredential? Read()
    {
        var value = LsaSecretStore.Retrieve(SecretName);
        if (value is null)
        {
            return null;
        }

        return JsonSerializer.Deserialize<StoredCredential>(value, JsonOptions)
               ?? throw new InvalidDataException("앱 자동 로그인 자격 증명이 손상되었습니다.");
    }

    public bool Exists() => LsaSecretStore.Exists(SecretName);
    public void Delete() => LsaSecretStore.Delete(SecretName);
}
public static class CredentialTransferFile
{
    private static readonly byte[] Entropy = SHA256.HashData(Encoding.UTF8.GetBytes("eslee Auto Power credential transfer v1"));
    private static readonly JsonSerializerOptions JsonOptions = new();

    public static string Create(string userName, string password)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userName);
        ArgumentException.ThrowIfNullOrWhiteSpace(password);
        Directory.CreateDirectory(AppPaths.LocalDataDirectory);
        var path = Path.Combine(AppPaths.LocalDataDirectory, $"credential-transfer-{Guid.NewGuid():N}.bin");
        var plain = JsonSerializer.SerializeToUtf8Bytes(new StoredCredential(userName, password), JsonOptions);
        try
        {
            var encrypted = ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser);
            try
            {
                File.WriteAllBytes(path, encrypted);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(encrypted);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }

        return path;
    }

    public static StoredCredential ReadAndDelete(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var expectedDirectory = Path.GetFullPath(AppPaths.LocalDataDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(expectedDirectory, StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(fullPath).StartsWith("credential-transfer-", StringComparison.Ordinal) ||
            !string.Equals(Path.GetExtension(fullPath), ".bin", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("자격 증명 전달 파일 경로가 허용된 앱 폴더 밖에 있습니다.");
        }

        try
        {
            var info = new FileInfo(fullPath);
            if (!info.Exists || info.Length is <= 0 or > 65536 || info.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                throw new InvalidDataException("자격 증명 전달 파일이 올바르지 않습니다.");
            }

            var encrypted = File.ReadAllBytes(fullPath);
            try
            {
                var plain = ProtectedData.Unprotect(encrypted, Entropy, DataProtectionScope.CurrentUser);
                try
                {
                    return JsonSerializer.Deserialize<StoredCredential>(plain, JsonOptions)
                           ?? throw new InvalidDataException("자격 증명 전달 데이터를 읽을 수 없습니다.");
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(plain);
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(encrypted);
            }
        }
        finally
        {
            if (File.Exists(fullPath))
            {
                File.Delete(fullPath);
            }
        }
    }

    public static void DeleteIfPresent(string? path)
    {
        if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
        {
            File.Delete(path);
        }
    }
}
