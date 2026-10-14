#if DEBUG
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AutoPower.App.Services;
using AutoPower.Core;
using AutoPower.Windows;

namespace AutoPower.App;

/// <summary>
/// Debug-only screenshot mode (<c>--ui-capture &lt;folder&gt; [light|dark]</c>). It requires an isolated
/// data root (ESLEE_AUTOPOWER_DATA_ROOT), seeds sample data there, renders every window off screen with
/// RenderTargetBitmap and exits. No tray icon, pipe, Task Scheduler task or elevated helper is used.
/// </summary>
internal static class UiCapture
{
    public static bool IsRequested(string[] args) =>
        args.Length >= 2 && string.Equals(args[0], "--ui-capture", StringComparison.Ordinal);

    public static async Task RunAsync(App app, string[] args)
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ESLEE_AUTOPOWER_DATA_ROOT")))
        {
            throw new InvalidOperationException("--ui-capture requires ESLEE_AUTOPOWER_DATA_ROOT so real data is never touched.");
        }

        var output = Path.GetFullPath(args[1]);
        Directory.CreateDirectory(output);
        ThemeService.Apply(args.Length >= 3 && string.Equals(args[2], "dark", StringComparison.OrdinalIgnoreCase)
            ? AppThemePreference.Dark
            : AppThemePreference.Light);
        var suffix = (ThemeService.IsDark ? "dark" : "light") + "-" + AppText.LanguageCode;

        AppPaths.EnsureCreated();
        await AppServices.Store.InitializeAsync();
        await SeedAsync();

        var main = new MainWindow { WindowStartupLocation = WindowStartupLocation.Manual };
        app.MainWindow = main;
        await ShowOffscreenAsync(main);
        await Task.Delay(1500);
        Save(main, Path.Combine(output, $"main-schedules-{suffix}.png"));
        main.SelectPageForCapture(1);
        await Task.Delay(400);
        Save(main, Path.Combine(output, $"main-history-{suffix}.png"));
        main.SelectPageForCapture(2);
        await Task.Delay(400);
        Save(main, Path.Combine(output, $"main-compatibility-{suffix}.png"));
        main.SelectPageForCapture(0);
        ToggleFirstScheduleThroughAutomation(main);
        if (int.TryParse(Environment.GetEnvironmentVariable("ESLEE_AUTOPOWER_CAPTURE_HOLD"), out var holdSeconds) && holdSeconds > 0)
        {
            // Keeps the window open (refreshed like after a save) so external UI Automation can be tried.
            await main.RefreshFromTrayAsync();
            main.Left = 100;
            main.Top = 100;
            await Task.Delay(TimeSpan.FromSeconds(holdSeconds));
        }

        var schedules = await AppServices.Store.GetAllSchedulesAsync();
        var capability = PowerCapabilityDetector.Detect();
        var compatibility = await AppServices.Store.GetCompatibilityAsync();
        var recurring = schedules.First(schedule => schedule.IsRecurring && schedule.Status == ScheduleStatus.Pending);
        var wake = schedules.First(schedule => PowerSchedulePolicy.IsWakeSchedule(schedule.ActionType));

        await CaptureAsync(new ScheduleEditorWindow(recurring, capability, compatibility, schedules), output, $"editor-recurring-{suffix}.png");
        await CaptureAsync(new ScheduleEditorWindow(wake, capability, compatibility, schedules), output, $"editor-wake-{suffix}.png");
        await CaptureAsync(new SettingsWindow(), output, $"settings-{suffix}.png");
        await CaptureAsync(new WarningWindow(recurring), output, $"warning-{suffix}.png", closeWithoutDecision: true);
        await CaptureAsync(new AboutWindow(), output, $"about-{suffix}.png");
        await CaptureAsync(new WelcomeWindow(), output, $"welcome-{suffix}.png");
        await CaptureAsync(new ProgramEditorWindow(), output, $"program-{suffix}.png");
        await CaptureAsync(new ExitConfirmationWindow(), output, $"exit-{suffix}.png");

        main.AllowClose = true;
        main.Close();
        app.Shutdown(0);
    }

    /// <summary>Exercises the list toggle the way a screen reader does (UI Automation Toggle pattern).</summary>
    private static void ToggleFirstScheduleThroughAutomation(MainWindow main)
    {
        var toggle = FindDescendant<System.Windows.Controls.CheckBox>(main);
        if (toggle is null)
        {
            AppServices.Logger.Warning("capture.toggle-missing", "no schedule toggle found");
            return;
        }

        var peer = new System.Windows.Automation.Peers.CheckBoxAutomationPeer(toggle);
        ((System.Windows.Automation.Provider.IToggleProvider)peer).Toggle();
        AppServices.Logger.Information("capture.toggle-invoked", $"isChecked={toggle.IsChecked}");
    }

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match)
            {
                return match;
            }

            if (FindDescendant<T>(child) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    private static async Task CaptureAsync(Window window, string output, string name, bool closeWithoutDecision = false)
    {
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        if (window is ScheduleEditorWindow or SettingsWindow)
        {
            // Tall enough to show the whole scrollable form in one image.
            window.Height = 1700;
        }

        await ShowOffscreenAsync(window);
        await Task.Delay(700);
        Save(window, Path.Combine(output, name));
        if (FindScrollContent(window) is { } form)
        {
            SaveElement(form, window.Background, Path.Combine(output, Path.GetFileNameWithoutExtension(name) + "-full.png"));
        }

        if (closeWithoutDecision && window is WarningWindow warning)
        {
            warning.CloseForCapture();
        }
        else
        {
            window.Close();
        }
    }

    private static Task ShowOffscreenAsync(Window window)
    {
        window.ShowActivated = false;
        window.ShowInTaskbar = false;
        window.Left = -20000;
        window.Top = -20000;
        var loaded = new TaskCompletionSource();
        window.Loaded += (_, _) => loaded.TrySetResult();
        window.Show();
        return loaded.Task;
    }

    private static FrameworkElement? FindScrollContent(DependencyObject root)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is System.Windows.Controls.ScrollViewer { Content: FrameworkElement content })
            {
                return content;
            }

            if (FindScrollContent(child) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    private static void Save(Window window, string path)
    {
        window.UpdateLayout();
        SaveElement((FrameworkElement)window.Content, window.Background, path);
    }

    private static void SaveElement(FrameworkElement content, System.Windows.Media.Brush background, string path)
    {
        var dpi = VisualTreeHelper.GetDpi(content);
        var width = Math.Max(1, (int)Math.Ceiling(content.ActualWidth * dpi.DpiScaleX));
        var height = Math.Max(1, (int)Math.Ceiling(content.ActualHeight * dpi.DpiScaleY));
        var bitmap = new RenderTargetBitmap(width, height, dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        var backdrop = new DrawingVisual();
        using (var context = backdrop.RenderOpen())
        {
            context.DrawRectangle(background, null, new Rect(0, 0, content.ActualWidth, content.ActualHeight));
        }

        bitmap.Render(backdrop);
        bitmap.Render(content);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static async Task SeedAsync()
    {
        var store = AppServices.Store;
        await store.SetSettingAsync("welcome-shown", "1");
        await store.SetSettingAsync("removed-power-features-cleanup-v3", "1");
        await store.SetSettingAsync(MainViewModel.AttentionAcknowledgedKey, DateTimeOffset.UtcNow.AddDays(-7).ToString("O", System.Globalization.CultureInfo.InvariantCulture));
        if ((await store.GetAllSchedulesAsync()).Count > 0)
        {
            return;
        }

        var today = DateTime.Today;
        var nightly = PowerSchedule.Create(
            today.AddHours(23).AddMinutes(30),
            PowerActionType.Shutdown,
            name: AppText.IsEnglish ? "Shut down after work" : "퇴근 후 종료",
            recurrence: RecurrenceRule.CreateWeekdays(new TimeSpan(23, 30, 0)));
        if (nightly.ScheduledLocalDateTime <= DateTime.Now.AddMinutes(5))
        {
            nightly = nightly with { ScheduledLocalDateTime = RecurrencePolicy.FirstOccurrence(nightly.Recurrence!, DateTime.Now.AddMinutes(10), DateTime.Now) };
        }

        var wakeId = Guid.NewGuid();
        var wake = new PowerSchedule(
            wakeId,
            today.AddDays(1).AddHours(7),
            PowerActionType.WakeFromHibernate,
            true,
            true,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            ScheduleStatus.Pending,
            [
                new FollowUpProgram(Guid.NewGuid(), wakeId, @"C:\Program Files\Example\backup.exe", 0, null, null, false, 0),
                new FollowUpProgram(Guid.NewGuid(), wakeId, @"C:\Program Files\Example\sync.exe", 5, null, null, true, 1)
            ])
        {
            Name = AppText.IsEnglish ? "Morning backup" : "아침 백업"
        };
        var sleep = PowerSchedule.Create(today.AddDays(2).AddHours(1), PowerActionType.Sleep) with { IsEnabled = false, Status = ScheduleStatus.Disabled };
        var hibernate = PowerSchedule.Create(today.AddDays(3).AddHours(13).AddMinutes(15), PowerActionType.Hibernate,
            recurrence: RecurrenceRule.CreateWeekly(RecurrenceDays.Saturday | RecurrenceDays.Sunday, new TimeSpan(13, 15, 0)));
        hibernate = hibernate with { ScheduledLocalDateTime = RecurrencePolicy.FirstOccurrence(hibernate.Recurrence!, hibernate.ScheduledLocalDateTime, DateTime.Now) };

        foreach (var schedule in new[] { nightly, wake, sleep, hibernate })
        {
            await store.SaveScheduleAsync(schedule);
        }

        await store.AddHistoryAsync(nightly.Id, "ScheduleCreated", ResultKind.Information, nightly.ScheduledLocalDateTime, "예약을 만들었습니다.");
        await store.AddHistoryAsync(nightly.Id, "PowerTransitionCompleted", ResultKind.Success, nightly.ScheduledLocalDateTime.AddDays(-1), "완전 종료가 완료되었습니다.");
        await store.AddHistoryAsync(wake.Id, "WakeResumed", ResultKind.Success, wake.ScheduledLocalDateTime.AddDays(-1), "예약 시각에 최대 절전에서 복귀했습니다.");
        await store.AddHistoryAsync(wake.Id, "FollowUpProgram", ResultKind.Failure, wake.ScheduledLocalDateTime.AddDays(-1), "후속 프로그램을 시작하지 못했습니다.");
        await store.AddHistoryAsync(null, "ScheduleMissed", ResultKind.Warning, today.AddDays(-2).AddHours(22), "PC가 꺼져 있어 예약을 놓쳤습니다.");
    }
}
#endif
