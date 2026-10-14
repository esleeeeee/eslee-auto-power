using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using AutoPower.App.Services;
using AutoPower.Core;
using AutoPower.Data;
using AutoPower.Windows;
using MessageBox = AutoPower.App.LocalizedMessageBox;

namespace AutoPower.App;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001", Justification = "WPF Application owns and disposes the tray icon in ExitApplication.")]
public partial class App : System.Windows.Application
{
    private TrayService? _tray;
    private TrayHostLink? _trayHostLink;
    private MainWindow? _mainWindow;
    private Mutex? _singleInstanceMutex;
    private bool _ownsSingleInstanceMutex;
    private EventWaitHandle? _activationEvent;
    private RegisteredWaitHandle? _activationWait;

    protected override async void OnStartup(StartupEventArgs e)
    {
        // Hardware-rendered WPF windows paint as blank white rectangles when the desktop moves to a
        // monitorless output (remote session with the monitor off). Software rendering avoids that.
        System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
        WpfLocalizer.Enable();
        base.OnStartup(e);
        ThemeService.Install(this, AppThemePreference.System);
        try
        {
            await AppServices.InitializeAsync();
            try
            {
                ThemeService.Apply(AppThemeSetting.Parse(await AppServices.Store.GetSettingAsync(AppThemeSetting.Key)));
            }
            catch
            {
                // An unreadable theme preference must not prevent startup.
            }

            var language = AppLanguagePreference.System;
            try
            {
                language = AppLanguageSetting.Parse(await AppServices.Store.GetSettingAsync(AppLanguageSetting.Key));
            }
            catch
            {
                // An unreadable language preference must not prevent startup.
            }

            AppText.SetLanguage(language);
            AppText.ApplyCulture();
#if DEBUG
            if (e.Args.Length == 1 && string.Equals(e.Args[0], "--ui-preview-program", StringComparison.Ordinal))
            {
                var preview = new ProgramEditorWindow();
                MainWindow = preview;
                preview.Show();
                return;
            }

            if (e.Args.Length == 1 && string.Equals(e.Args[0], "--ui-preview-schedule", StringComparison.Ordinal))
            {
                var preview = new ScheduleEditorWindow(
                    null,
                    PowerCapabilityDetector.Detect(),
                    await AppServices.Store.GetCompatibilityAsync(),
                    await AppServices.Store.GetAllSchedulesAsync());
                MainWindow = preview;
                preview.Show();
                return;
            }

            if (e.Args.Length == 1 && string.Equals(e.Args[0], "--ui-preview-about", StringComparison.Ordinal))
            {
                var preview = new AboutWindow();
                MainWindow = preview;
                preview.Show();
                return;
            }

            if (e.Args.Length == 1 && string.Equals(e.Args[0], "--ui-preview-main", StringComparison.Ordinal))
            {
                await AppServices.Store.SetSettingAsync("welcome-shown", "1");
                await AppServices.Store.SetSettingAsync("removed-power-features-cleanup-v3", "1");
            }
#endif
            if (TryReadScheduleCommand(e.Args, "warning", out var warningId))
            {
                var schedule = await AppServices.Store.GetScheduleAsync(warningId);
                if (schedule is not null && !PowerSchedulePolicy.IsRemovedSchedule(schedule.ActionType))
                {
                    var nextWake = schedule.ActionType == PowerActionType.Shutdown
                        ? ScheduleValidator.SelectNextWake(
                            (await AppServices.Store.GetAllSchedulesAsync()).Where(item => item.Id != schedule.Id),
                            schedule.ScheduledLocalDateTime)
                        : null;
                    var warning = new WarningWindow(schedule, nextWake);
                    MainWindow = warning;
                    warning.Show();
                    return;
                }

                Shutdown(2);
                return;
            }

            if (!TryBecomeSingleInstance())
            {
                Shutdown(0);
                return;
            }

            _mainWindow = new MainWindow();
            MainWindow = _mainWindow;
            _tray = new TrayService(_mainWindow);
            _mainWindow.AttachTray(_tray);
            _trayHostLink = new TrayHostLink(
                TrayHostLink.BuildDefaultPipeName(),
                Environment.ProcessId,
                visible => Dispatcher.InvokeAsync(() => _tray?.SetTrayIconVisible(visible)).Task,
                () => Dispatcher.InvokeAsync(() => _mainWindow?.ShowFromTray()).Task,
                () => Dispatcher.InvokeAsync(
                    () => _tray?.BuildHostedMenuItems() ?? []).Task,
                actionId => Dispatcher.InvokeAsync(
                    () => _tray?.TryStartMenuAction(actionId) ?? false).Task,
                AppServices.Logger.Information,
                (eventName, error) => AppServices.Logger.Error(eventName, error));
            _trayHostLink.Start();
            AppServices.Updates.Start();
            _mainWindow.Show();

            if (e.Args.Length == 1 && string.Equals(e.Args[0], "tray", StringComparison.OrdinalIgnoreCase))
            {
                _mainWindow.Hide();
            }

        }
        catch (Exception error)
        {
            try
            {
                AppServices.Logger.Error("app.startup-failed", error);
            }
            catch
            {
                // The fallback dialog is still shown if even the log directory is unavailable.
            }

            MessageBox.Show(error.Message, "eslee Auto Power 시작 오류", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    public void ExitApplication()
    {
        ReleaseSingleInstance();
        AppServices.Updates.Dispose();
        _trayHostLink?.Dispose();
        _trayHostLink = null;
        _tray?.Dispose();
        if (_mainWindow is not null)
        {
            _mainWindow.AllowClose = true;
            _mainWindow.Close();
        }

        Shutdown();
    }

    /// <summary>
    /// Starts a new instance with the same command line (for example after a language change)
    /// and exits this one. Environment overrides such as the data root are inherited.
    /// </summary>
    public void RestartApplication()
    {
        var executable = Environment.ProcessPath;
        if (string.IsNullOrEmpty(executable))
        {
            return;
        }

        ReleaseSingleInstance();
        var startInfo = new System.Diagnostics.ProcessStartInfo(executable) { UseShellExecute = false };
        foreach (var argument in Environment.GetCommandLineArgs().Skip(1))
        {
            startInfo.ArgumentList.Add(argument);
        }

        System.Diagnostics.Process.Start(startInfo)?.Dispose();
        ExitApplication();
    }

    /// <summary>
    /// One main instance per data root. A second launch asks the running instance to show
    /// its window and exits. Warning windows started by scheduled tasks are not affected.
    /// </summary>
    private bool TryBecomeSingleInstance()
    {
        var suffix = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            AppPaths.SharedStateDirectory.ToUpperInvariant())))[..16];
        _singleInstanceMutex = new Mutex(initiallyOwned: true, $@"Local\eslee-auto-power-app-{suffix}", out _ownsSingleInstanceMutex);
        if (!_ownsSingleInstanceMutex)
        {
            try
            {
                using var handle = EventWaitHandle.OpenExisting($@"Local\eslee-auto-power-activate-{suffix}");
                handle.Set();
            }
            catch (Exception error)
            {
                AppServices.Logger.Error("app.single-instance-activate-failed", error);
            }

            _singleInstanceMutex.Dispose();
            _singleInstanceMutex = null;
            return false;
        }

        _activationEvent = new EventWaitHandle(false, EventResetMode.AutoReset, $@"Local\eslee-auto-power-activate-{suffix}");
        _activationWait = ThreadPool.RegisterWaitForSingleObject(
            _activationEvent,
            (_, _) => Dispatcher.BeginInvoke(() => _mainWindow?.ShowFromTray()),
            null,
            Timeout.Infinite,
            executeOnlyOnce: false);
        return true;
    }

    private void ReleaseSingleInstance()
    {
        _activationWait?.Unregister(null);
        _activationWait = null;
        _activationEvent?.Dispose();
        _activationEvent = null;
        if (_singleInstanceMutex is not null)
        {
            if (_ownsSingleInstanceMutex)
            {
                _singleInstanceMutex.ReleaseMutex();
                _ownsSingleInstanceMutex = false;
            }

            _singleInstanceMutex.Dispose();
            _singleInstanceMutex = null;
        }
    }

    private static bool TryReadScheduleCommand(string[] args, string command, out Guid scheduleId)
    {
        scheduleId = Guid.Empty;
        return args.Length == 3 &&
               string.Equals(args[0], command, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(args[1], "--schedule", StringComparison.OrdinalIgnoreCase) &&
               Guid.TryParse(args[2], out scheduleId);
    }
}

internal static class AppServices
{
    public static TechnicalLogger Logger { get; } = new();
    public static SqliteStore Store { get; } = new(AppPaths.DatabasePath);
    public static InstallationLayout Layout { get; } = InstallationLayout.FromBaseDirectory(AppContext.BaseDirectory);
    public static ElevatedHelperClient Helper { get; } = new(Layout.HelperPath);
    public static CredentialManager Credentials { get; } = new();
    public static ScheduleCoordinator Coordinator { get; } = new(Store, Helper);
    public static UpdateCheckService Updates { get; } = new(
        UpdateCheckPolicy.ParseCurrentVersion(
            typeof(App).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
            typeof(App).Assembly.GetName().Version),
        Logger.Information);

    public static async Task InitializeAsync()
    {
        AppPaths.EnsureCreated();
        await Store.InitializeAsync();
        await ReconcilePowerTransitionsAsync();
        if (!string.Equals(Environment.GetEnvironmentVariable("ESLEE_AUTOPOWER_NO_STARTUP"), "1", StringComparison.Ordinal) &&
            await Store.GetSettingAsync("startup-initialized") is null)
        {
            StartupManager.SetEnabled(true, Layout.AppPath);
            await Store.SetSettingAsync("startup-initialized", "1");
        }

        var snapshot = PowerCapabilityDetector.Detect();
        foreach (var result in snapshot.Results)
        {
            var existing = (await Store.GetCompatibilityAsync()).SingleOrDefault(item => item.Capability == result.Capability);
            if (existing?.Status is not (CapabilityStatus.Confirmed or CapabilityStatus.UnsupportedOrFailed))
            {
                await Store.SaveCompatibilityAsync(result);
            }
        }
    }

    public static async Task ReconcilePowerTransitionsAsync()
    {
        var registrar = new TaskSchedulerService(Layout, Logger);
        var reconciler = new PowerTransitionReconciler(
            Store,
            registrar,
            new WindowsPowerTransitionEvidenceSource(registrar, Logger),
            Logger);
        await reconciler.ReconcileAsync(canRepairSystem: false);
    }
}
