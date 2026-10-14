using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using AutoPower.App.Controls;
using AutoPower.App.Services;
using AutoPower.Core;
using AutoPower.Windows;

namespace AutoPower.App;

/// <summary>
/// Windows 11 style settings: every change applies immediately, there is no save button.
/// </summary>
public partial class SettingsWindow : ThemedWindow
{
    private bool _loading = true;

    public SettingsWindow()
    {
        InitializeComponent();

        ThemeChoice.ItemsSource = new[]
        {
            new ChoiceOption<AppThemePreference>(AppThemePreference.System, AppText.T("Windows 설정 따르기")),
            new ChoiceOption<AppThemePreference>(AppThemePreference.Light, AppText.T("밝게")),
            new ChoiceOption<AppThemePreference>(AppThemePreference.Dark, AppText.T("어둡게"))
        };
        LanguageChoice.ItemsSource = new[]
        {
            new ChoiceOption<AppLanguagePreference>(AppLanguagePreference.System, AppText.T("Windows 설정 따르기")),
            new ChoiceOption<AppLanguagePreference>(AppLanguagePreference.Korean, "한국어"),
            new ChoiceOption<AppLanguagePreference>(AppLanguagePreference.English, "English")
        };

        VersionText.Text = AppText.F("버전 {0}", GetDisplayVersion());
        Loaded += SettingsWindow_Loaded;
    }

    private async void SettingsWindow_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            ThemeChoice.SelectedIndex = (int)ThemeService.Preference;
            var language = AppLanguageSetting.Parse(await AppServices.Store.GetSettingAsync(AppLanguageSetting.Key));
            LanguageChoice.SelectedIndex = (int)language;
            StartupOption.IsChecked = StartupManager.IsEnabled;
            RefreshCredentialUi();
        }
        catch (Exception error)
        {
            AppServices.Logger.Error("settings.load-failed", error);
        }
        finally
        {
            _loading = false;
        }
    }

    private async void ThemeChoice_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_loading || ThemeChoice.SelectedItem is not ChoiceOption<AppThemePreference> option)
        {
            return;
        }

        ThemeService.Apply(option.Value);
        try
        {
            await AppServices.Store.SetSettingAsync(AppThemeSetting.Key, AppThemeSetting.ToSettingValue(option.Value));
        }
        catch (Exception error)
        {
            AppServices.Logger.Error("settings.theme-save-failed", error);
        }
    }

    private async void LanguageChoice_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_loading || LanguageChoice.SelectedItem is not ChoiceOption<AppLanguagePreference> option)
        {
            return;
        }

        try
        {
            await AppServices.Store.SetSettingAsync(AppLanguageSetting.Key, AppLanguageSetting.ToSettingValue(option.Value));
        }
        catch (Exception error)
        {
            AppDialog.ShowError(this, error);
            return;
        }

        if (AppDialog.Confirm(
                this,
                "언어",
                "새 언어는 앱을 다시 시작하면 적용됩니다. 지금 다시 시작할까요? 등록된 예약은 그대로 유지됩니다.",
                primaryText: "다시 시작",
                secondaryText: "나중에"))
        {
            ((App)System.Windows.Application.Current).RestartApplication();
        }
    }

    // Saved from Checked/Unchecked rather than Click so that a change made through
    // UI Automation (screen readers) is stored as well.
    private void StartupOption_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        var enable = StartupOption.IsChecked == true;
        try
        {
            StartupManager.SetEnabled(enable, AppServices.Layout.AppPath);
        }
        catch (Exception error)
        {
            AppDialog.ShowError(this, error);
            _loading = true;
            StartupOption.IsChecked = StartupManager.IsEnabled;
            _loading = false;
        }
    }

    private async void SaveCredential_Click(object sender, RoutedEventArgs e)
    {
        var userNameInput = CredentialUserNameInput.Text.Trim();
        var password = CredentialPasswordInput.Password;
        if (string.IsNullOrWhiteSpace(userNameInput) || string.IsNullOrWhiteSpace(password))
        {
            AppDialog.Warning(this, "Windows 사용자 이름과 계정 암호를 모두 입력하세요. PIN은 사용할 수 없습니다.", "1회 자동 로그인");
            return;
        }

        SetCredentialButtonsEnabled(false);
        var previous = AppServices.Credentials.Read();
        string? transferPath = null;
        try
        {
            var userName = WindowsCredentialIdentity.Normalize(userNameInput);
            AppServices.Credentials.Save(userName, password);
            transferPath = CredentialTransferFile.Create(userName, password);
            await AppServices.Helper.RunAsync(["sync-credential", "--file", transferPath]);
            transferPath = null;
            AppDialog.Info(this, "Windows 계정과 암호를 실제 로그온 API로 검증하고 보호 저장소에 등록했습니다.", "로그인 검증 성공");
        }
        catch (Exception error)
        {
            try
            {
                if (previous is null)
                {
                    AppServices.Credentials.Delete();
                }
                else
                {
                    AppServices.Credentials.Save(previous.UserName, previous.Password);
                }
            }
            catch (Exception rollbackError)
            {
                AppServices.Logger.Error("credential.user-copy-rollback-failed", rollbackError);
            }

            AppDialog.ShowError(this, error);
        }
        finally
        {
            CredentialTransferFile.DeleteIfPresent(transferPath);
            CredentialPasswordInput.Clear();
            RefreshCredentialUi();
            SetCredentialButtonsEnabled(true);
            await RefreshMainWindowAsync();
        }
    }

    private async void TestCredential_Click(object sender, RoutedEventArgs e)
    {
        SetCredentialButtonsEnabled(false);
        try
        {
            if (!AppServices.Credentials.Exists())
            {
                throw new InvalidOperationException("먼저 Windows 로그인 자격 증명을 저장하세요.");
            }

            await AppServices.Helper.RunAsync(["test-credential"]);
            AppDialog.Info(this, "저장된 Windows 계정과 암호로 실제 로그온 자격 증명 검증에 성공했습니다.", "로그인 테스트 성공");
        }
        catch (Exception error)
        {
            AppDialog.ShowError(this, error);
        }
        finally
        {
            CredentialPasswordInput.Clear();
            RefreshCredentialUi();
            SetCredentialButtonsEnabled(true);
            await RefreshMainWindowAsync();
        }
    }

    private async void RemoveCredential_Click(object sender, RoutedEventArgs e)
    {
        if (!AppDialog.Confirm(this, "로그인 정보 제거", "앱이 보호 저장소에 등록한 Windows 로그인 정보를 제거하시겠습니까?", danger: true, primaryText: "제거", secondaryText: "취소"))
        {
            return;
        }

        SetCredentialButtonsEnabled(false);
        try
        {
            await AppServices.Helper.RunAsync(["delete-credential"]);
            AppServices.Credentials.Delete();
        }
        catch (Exception error)
        {
            AppDialog.ShowError(this, error);
        }
        finally
        {
            CredentialPasswordInput.Clear();
            RefreshCredentialUi();
            SetCredentialButtonsEnabled(true);
            await RefreshMainWindowAsync();
        }
    }

    private void RefreshCredentialUi()
    {
        if (AppServices.CaptureMode)
        {
            // Screenshots use an example account, never the real Windows credential.
            CredentialStatusText.Text = AppText.F("등록됨: {0}", @"EXAMPLE-PC\user");
            CredentialUserNameInput.Text = @"EXAMPLE-PC\user";
            return;
        }

        try
        {
            var credential = AppServices.Credentials.Read();
            if (credential is null)
            {
                CredentialStatusText.Text = AppText.T("등록된 Windows 로그인 정보가 없습니다.");
                if (string.IsNullOrWhiteSpace(CredentialUserNameInput.Text))
                {
                    CredentialUserNameInput.Text = TaskSchedulerService.CurrentUserName();
                }
            }
            else
            {
                CredentialStatusText.Text = AppText.F("등록됨: {0}", credential.UserName);
                CredentialUserNameInput.Text = credential.UserName;
            }
        }
        catch (Exception error)
        {
            CredentialStatusText.Text = AppText.T("등록 상태를 확인하지 못했습니다.");
            AppServices.Logger.Error("credential.status-read-failed", error);
        }
    }

    private void SetCredentialButtonsEnabled(bool enabled)
    {
        SaveCredentialButton.IsEnabled = enabled;
        TestCredentialButton.IsEnabled = enabled;
        RemoveCredentialButton.IsEnabled = enabled;
    }

    private static async Task RefreshMainWindowAsync()
    {
        if (System.Windows.Application.Current?.MainWindow is MainWindow main)
        {
            await main.RefreshFromTrayAsync();
        }
    }

    private void OpenLogs_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(AppPaths.LogDirectory);
        Process.Start(new ProcessStartInfo("explorer.exe") { UseShellExecute = false, ArgumentList = { AppPaths.LogDirectory } })?.Dispose();
    }

    private void About_Click(object sender, RoutedEventArgs e) => new AboutWindow { Owner = this }.ShowDialog();

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    internal static string GetDisplayVersion()
    {
        var assembly = typeof(SettingsWindow).Assembly;
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational))
        {
            return informational.Split('+', 2)[0];
        }

        return assembly.GetName().Version?.ToString(3) ?? AppText.T("알 수 없음");
    }
}
