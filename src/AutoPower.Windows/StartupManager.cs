using Microsoft.Win32;

namespace AutoPower.Windows;

public static class StartupManager
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "eslee Auto Power";

    public static bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, false);
            return key?.GetValue(ValueName) is string;
        }
    }

    public static void SetEnabled(bool enabled, string applicationPath)
    {
        if (!Path.IsPathFullyQualified(applicationPath) || !string.Equals(Path.GetExtension(applicationPath), ".exe", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("시작 프로그램 경로가 올바르지 않습니다.");
        }

        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, true);
        if (enabled)
        {
            key.SetValue(ValueName, $"\"{applicationPath}\" tray", RegistryValueKind.String);
        }
        else
        {
            key.DeleteValue(ValueName, false);
        }
    }

    public static void Remove() => SetEnabled(false, Path.Combine(AppContext.BaseDirectory, "AutoPower.App.exe"));
}
