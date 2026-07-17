using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using AutoPower.Core;
using AutoPower.Windows;

namespace AutoPower.App;

public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();

        VersionValue.Text = AppText.T($"버전 {GetDisplayVersion()}");
        OperatingSystemValue.Text = RuntimeInformation.OSDescription.Trim();
        RuntimeValue.Text = $"{RuntimeInformation.ProcessArchitecture} · .NET {Environment.Version}";
        DataDirectoryValue.Text = AppPaths.SharedStateDirectory;
        LogDirectoryValue.Text = AppPaths.LogDirectory;
    }

    private static string GetDisplayVersion()
    {
        var assembly = typeof(AboutWindow).Assembly;
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational))
        {
            return informational.Split('+', 2)[0];
        }

        return assembly.GetName().Version?.ToString(3) ?? AppText.T("알 수 없음");
    }

    private void OpenLogs_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(AppPaths.LogDirectory);
        Process.Start(new ProcessStartInfo("explorer.exe")
        {
            UseShellExecute = false,
            ArgumentList = { AppPaths.LogDirectory }
        })?.Dispose();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
