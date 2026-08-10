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

        ShowUpdateStatus(AppServices.Updates.Latest);
        AppServices.Updates.Updated += Updates_Changed;
        Closed += (_, _) => AppServices.Updates.Updated -= Updates_Changed;
    }

    private void Updates_Changed(UpdateCheckResult result) =>
        Dispatcher.BeginInvoke(() => ShowUpdateStatus(result));

    private void ShowUpdateStatus(UpdateCheckResult? result)
    {
        UpdateStatusValue.Text = result switch
        {
            null => AppText.T("업데이트를 아직 확인하지 않았습니다."),
            { Status: UpdateCheckStatus.UpdateAvailable, LatestVersion: { } latest } =>
                AppText.F("새 버전 v{0}을 사용할 수 있습니다.", latest.ToString(3)),
            { Status: UpdateCheckStatus.UpToDate } => AppText.T("최신 버전을 사용하고 있습니다."),
            _ => AppText.T("업데이트 확인에 실패했습니다. 네트워크 연결을 확인하세요.")
        };
        UpdateStatusValue.Foreground = result?.Status == UpdateCheckStatus.UpdateAvailable
            ? (System.Windows.Media.Brush)FindResource("AccentBrush")
            : (System.Windows.Media.Brush)FindResource("MutedBrush");
    }

    private async void CheckUpdate_Click(object sender, RoutedEventArgs e)
    {
        CheckUpdateButton.IsEnabled = false;
        UpdateStatusValue.Text = AppText.T("업데이트 확인 중…");
        try
        {
            var result = await AppServices.Updates.CheckNowAsync();
            ShowUpdateStatus(result);
        }
        finally
        {
            CheckUpdateButton.IsEnabled = true;
        }
    }

    private void OpenReleases_Click(object sender, RoutedEventArgs e)
    {
        var url = AppServices.Updates.Latest?.ReleaseUrl ?? UpdateCheckPolicy.ReleasesPageUrl;
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose();
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
