#if DEBUG
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using AutoPower.App.Services;
using AutoPower.Core;
using AutoPower.Windows;
using Microsoft.Data.Sqlite;

namespace AutoPower.App;

/// <summary>
/// Debug-only screenshot mode (<c>--ui-capture &lt;folder&gt; [light|dark] [ko|en]</c>). It requires an isolated
/// data root (ESLEE_AUTOPOWER_DATA_ROOT), seeds sample data there, captures every window (title bar included)
/// off screen with PrintWindow and exits. No tray icon, pipe, Task Scheduler task or elevated helper is used.
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
        await Task.Delay(500);
        Save(main, Path.Combine(output, $"main-history-{suffix}.png"));
        main.SelectPageForCapture(2);
        await Task.Delay(500);
        Save(main, Path.Combine(output, $"main-compatibility-{suffix}.png"));
        main.SelectPageForCapture(0);
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
        var recurring = schedules.First(schedule => schedule.IsRecurring && schedule.ActionType == PowerActionType.Shutdown);
        var wake = schedules.First(schedule => PowerSchedulePolicy.IsWakeSchedule(schedule.ActionType));

        await CaptureAsync(new ScheduleEditorWindow(recurring, capability, compatibility, schedules), output, $"editor-recurring-{suffix}.png", scrollToEnd: true);
        await CaptureAsync(new ScheduleEditorWindow(wake, capability, compatibility, schedules), output, $"editor-wake-{suffix}.png", scrollToEnd: true);
        await CaptureAsync(new SettingsWindow(), output, $"settings-{suffix}.png", scrollToEnd: true);
        await CaptureAsync(new WarningWindow(recurring), output, $"warning-{suffix}.png", closeWithoutDecision: true);
        await CaptureAsync(new AboutWindow(), output, $"about-{suffix}.png");
        await CaptureAsync(new WelcomeWindow(), output, $"welcome-{suffix}.png");
        await CaptureAsync(new ProgramEditorWindow(), output, $"program-{suffix}.png");
        await CaptureAsync(new ExitConfirmationWindow(), output, $"exit-{suffix}.png");

        main.AllowClose = true;
        main.Close();
        app.Shutdown(0);
    }

    private static async Task CaptureAsync(Window window, string output, string name, bool closeWithoutDecision = false, bool scrollToEnd = false)
    {
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        await ShowOffscreenAsync(window);
        await Task.Delay(800);
        Save(window, Path.Combine(output, name));
        if (scrollToEnd && FindDescendant<System.Windows.Controls.ScrollViewer>(window) is { } scroller)
        {
            scroller.ScrollToEnd();
            await Task.Delay(500);
            Save(window, Path.Combine(output, Path.GetFileNameWithoutExtension(name) + "-bottom.png"));
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

    /// <summary>Captures the whole window, title bar included, cropped to the visible frame.</summary>
    private static void Save(Window window, string path)
    {
        window.UpdateLayout();
        var handle = new WindowInteropHelper(window).Handle;
        GetWindowRect(handle, out var bounds);
        if (DwmGetWindowAttribute(handle, DwmExtendedFrameBounds, out var frame, Marshal.SizeOf<Rect32>()) != 0)
        {
            frame = bounds;
        }

        using var bitmap = new System.Drawing.Bitmap(bounds.Right - bounds.Left, bounds.Bottom - bounds.Top, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var graphics = System.Drawing.Graphics.FromImage(bitmap))
        {
            var hdc = graphics.GetHdc();
            PrintWindow(handle, hdc, PrintRenderFullContent);
            graphics.ReleaseHdc(hdc);
        }

        var crop = new System.Drawing.Rectangle(frame.Left - bounds.Left, frame.Top - bounds.Top, frame.Right - frame.Left, frame.Bottom - frame.Top);
        using var cropped = bitmap.Clone(crop, bitmap.PixelFormat);
        cropped.Save(path, ImageFormat.Png);
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

        var english = AppText.IsEnglish;
        var now = DateTime.Now;
        var nightly = PowerSchedule.Create(
            now.Date.AddHours(23).AddMinutes(30),
            PowerActionType.Shutdown,
            name: english ? "Shut down after work" : "퇴근 후 종료",
            recurrence: RecurrenceRule.CreateWeekdays(new TimeSpan(23, 30, 0)));
        nightly = nightly with { ScheduledLocalDateTime = RecurrencePolicy.FirstOccurrence(nightly.Recurrence!, nightly.ScheduledLocalDateTime, now) };

        var wakeId = Guid.NewGuid();
        var wake = new PowerSchedule(
            wakeId,
            now.Date.AddDays(1).AddHours(7),
            PowerActionType.WakeFromHibernate,
            true,
            true,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            ScheduleStatus.Pending,
            [
                new FollowUpProgram(Guid.NewGuid(), wakeId, @"C:\Program Files\Example\Backup.exe", 0, null, null, false, 0),
                new FollowUpProgram(Guid.NewGuid(), wakeId, @"C:\Program Files\Example\Sync.exe", 5, null, null, true, 1)
            ])
        {
            Name = english ? "Morning backup" : "아침 백업"
        };
        var weekend = PowerSchedule.Create(now.Date.AddDays(1).AddHours(13).AddMinutes(30), PowerActionType.Hibernate,
            name: english ? "Weekend lunch break" : "주말 점심 최대 절전",
            recurrence: RecurrenceRule.CreateWeekly(RecurrenceDays.Saturday | RecurrenceDays.Sunday, new TimeSpan(13, 30, 0)));
        weekend = weekend with { ScheduledLocalDateTime = RecurrencePolicy.FirstOccurrence(weekend.Recurrence!, weekend.ScheduledLocalDateTime, now) };
        var paused = PowerSchedule.Create(now.Date.AddDays(3).AddHours(1), PowerActionType.Sleep) with
        {
            IsEnabled = false,
            Status = ScheduleStatus.Disabled
        };

        foreach (var schedule in new[] { nightly, wake, weekend, paused })
        {
            await store.SaveScheduleAsync(schedule);
        }

        await store.SaveCompatibilityAsync(new CompatibilityResult(CompatibilityCapabilities.S3Wake, CapabilityStatus.NeedsPhysicalTest, DateTimeOffset.UtcNow,
            "이 PC는 S3 절전을 지원합니다. Task Scheduler WakeToRun 실제 테스트가 필요합니다."));
        await store.SaveCompatibilityAsync(new CompatibilityResult(CompatibilityCapabilities.S4Wake, CapabilityStatus.Confirmed, DateTimeOffset.UtcNow,
            "실제 테스트 성공이 확인된 경로입니다."));

        var yesterday = now.Date.AddDays(-1);
        var history = new (Guid? Schedule, string Type, ResultKind Result, DateTime At, string Message)[]
        {
            (nightly.Id, "ScheduleCreated", ResultKind.Success, now.AddDays(-3).AddHours(-2), "예약을 만들었습니다."),
            (wake.Id, "ScheduleCreated", ResultKind.Success, now.AddDays(-3).AddHours(-1), "예약을 만들었습니다."),
            (nightly.Id, "Snoozed", ResultKind.Information, yesterday.AddHours(23).AddMinutes(26),
                $"예약을 30분 미뤘습니다. 새 시각: {yesterday.AddDays(1):yyyy.MM.dd} 00:00"),
            (nightly.Id, "PowerTransitionRecovered", ResultKind.Success, yesterday.AddDays(1).AddMinutes(1),
                "완전 종료 전원 전환과 복귀 증거를 확인해 완료 처리했습니다."),
            (nightly.Id, "RecurrenceAdvanced", ResultKind.Information, yesterday.AddDays(1),
                $"반복 예약의 다음 회차를 만들었습니다. 다음 시각: {nightly.ScheduledLocalDateTime:yyyy.MM.dd HH:mm}"),
            (wake.Id, "WakeResumed", ResultKind.Success, now.Date.AddHours(7), "S4 최대 절전 상태에서 Windows 사용자 세션으로 복귀했습니다."),
            (wake.Id, "ScheduleSkipped", ResultKind.Skipped, now.AddDays(-2), "사용자가 이번 예약을 건너뛰었습니다.")
        };
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = AppPaths.DatabasePath }.ToString());
        await connection.OpenAsync();
        foreach (var item in history.OrderBy(item => item.At))
        {
            var id = await store.AddHistoryAsync(item.Schedule, item.Type, item.Result, item.At, item.Message);
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE ExecutionHistory SET ActualTimeUtc = $at WHERE Id = $id;";
            command.Parameters.AddWithValue("$at", new DateTimeOffset(item.At).ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("$id", id);
            await command.ExecuteNonQueryAsync();
        }
    }

    private const int DwmExtendedFrameBounds = 9;
    private const uint PrintRenderFullContent = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect32
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr handle, out Rect32 rect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PrintWindow(IntPtr handle, IntPtr hdc, uint flags);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr handle, int attribute, out Rect32 value, int size);
}
#endif
