using System.Drawing;
using AutoPower.Core;
using Forms = System.Windows.Forms;

namespace AutoPower.App;

public sealed class TrayService : IDisposable
{
    private readonly MainWindow _window;
    private readonly Forms.NotifyIcon _icon;
    private readonly TrayQuickShutdownController _quickShutdown;
    private readonly Icon? _applicationIcon;
    private Forms.ToolStripMenuItem _oneHourShutdownItem = null!;
    private Forms.ToolStripMenuItem _twoHourShutdownItem = null!;
    private Forms.ToolStripMenuItem _nextItem = null!;

    public TrayService(MainWindow window)
    {
        _window = window;
        _quickShutdown = new TrayQuickShutdownController(
            AppServices.Coordinator.SaveAsync,
            window.RefreshFromTrayAsync,
            (title, message, isError) => ShowBalloon(
                title,
                message,
                isError ? Forms.ToolTipIcon.Error : Forms.ToolTipIcon.Info),
            AppServices.Logger.Information,
            AppServices.Logger.Warning,
            (eventName, error) => AppServices.Logger.Error(eventName, error));
        _quickShutdown.BusyChanged += QuickShutdown_BusyChanged;

        var menu = CreateMenu(window);
        menu.Opening += (_, _) => _nextItem.Text = AppText.T($"다음 예약: {window.NextScheduleSummary}");

        _applicationIcon = Icon.ExtractAssociatedIcon(AppServices.Layout.AppPath);
        _icon = new Forms.NotifyIcon
        {
            Icon = _applicationIcon ?? SystemIcons.Application,
            Text = "eslee Auto Power",
            ContextMenuStrip = menu,
            Visible = true
        };
        _icon.DoubleClick += (_, _) => Invoke(window.ShowFromTray);
    }

    private Forms.ContextMenuStrip CreateMenu(MainWindow window)
    {
        var menu = new Forms.ContextMenuStrip();
        foreach (var entry in TrayMenuLayout.Entries)
        {
            if (entry.Command == TrayMenuCommand.Separator)
            {
                menu.Items.Add(new Forms.ToolStripSeparator());
                continue;
            }

            var item = new Forms.ToolStripMenuItem(AppText.T(entry.KoreanText!));
            menu.Items.Add(item);
            switch (entry.Command)
            {
                case TrayMenuCommand.QuickShutdown:
                    var hours = entry.QuickShutdownHours
                                ?? throw new InvalidOperationException("빠른 완전 종료 메뉴에 시간 값이 없습니다.");
                    item.Click += async (_, _) => await InvokeAsync(() => _quickShutdown.ExecuteAsync(hours));
                    if (hours == 1)
                    {
                        _oneHourShutdownItem = item;
                    }
                    else
                    {
                        _twoHourShutdownItem = item;
                    }

                    break;
                case TrayMenuCommand.NextSchedule:
                    item.Enabled = false;
                    _nextItem = item;
                    break;
                case TrayMenuCommand.OpenApp:
                    item.Click += (_, _) => Invoke(window.ShowFromTray);
                    break;
                case TrayMenuCommand.NewSchedule:
                    item.Click += (_, _) => Invoke(window.ShowNewSchedule);
                    break;
                case TrayMenuCommand.PauseAll:
                    item.Click += async (_, _) => await InvokeAsync(window.PauseAllAsync);
                    break;
                case TrayMenuCommand.ExitApp:
                    item.Click += (_, _) => Invoke(window.RequestExit);
                    break;
                default:
                    throw new InvalidOperationException($"지원하지 않는 트레이 메뉴 명령입니다: {entry.Command}");
            }
        }

        return menu;
    }

    public void ShowBalloon(
        string title,
        string message,
        Forms.ToolTipIcon icon = Forms.ToolTipIcon.Info,
        int timeoutMilliseconds = 4000)
    {
        _icon.BalloonTipTitle = AppText.T(title);
        _icon.BalloonTipText = AppText.T(message);
        _icon.BalloonTipIcon = icon;
        _icon.ShowBalloonTip(timeoutMilliseconds);
    }

    public void Dispose()
    {
        _quickShutdown.BusyChanged -= QuickShutdown_BusyChanged;
        _icon.Visible = false;
        _icon.Dispose();
        _applicationIcon?.Dispose();
    }

    private void QuickShutdown_BusyChanged(bool isBusy)
    {
        void UpdateItems()
        {
            _oneHourShutdownItem.Enabled = !isBusy;
            _twoHourShutdownItem.Enabled = !isBusy;
        }

        var dispatcher = System.Windows.Application.Current.Dispatcher;
        if (dispatcher.CheckAccess())
        {
            UpdateItems();
        }
        else
        {
            _ = dispatcher.BeginInvoke(UpdateItems);
        }
    }

    private static void Invoke(Action action) => System.Windows.Application.Current.Dispatcher.Invoke(action);

    private static Task InvokeAsync(Func<Task> action)
    {
        var dispatcher = System.Windows.Application.Current.Dispatcher;
        return dispatcher.CheckAccess()
            ? action()
            : dispatcher.InvokeAsync(action).Task.Unwrap();
    }
}

internal enum TrayMenuCommand
{
    QuickShutdown,
    Separator,
    NextSchedule,
    OpenApp,
    NewSchedule,
    PauseAll,
    ExitApp
}

internal sealed record TrayMenuEntry(
    TrayMenuCommand Command,
    string? KoreanText = null,
    int? QuickShutdownHours = null);

internal static class TrayMenuLayout
{
    public static IReadOnlyList<TrayMenuEntry> Entries { get; } = Array.AsReadOnly<TrayMenuEntry>(
    [
        new(TrayMenuCommand.QuickShutdown, "1시간 후 완전 종료", 1),
        new(TrayMenuCommand.QuickShutdown, "2시간 후 완전 종료", 2),
        new(TrayMenuCommand.Separator),
        new(TrayMenuCommand.NextSchedule, "다음 예약: 확인 중"),
        new(TrayMenuCommand.Separator),
        new(TrayMenuCommand.OpenApp, "앱 열기"),
        new(TrayMenuCommand.NewSchedule, "새 예약"),
        new(TrayMenuCommand.PauseAll, "모든 예약 일시 중지"),
        new(TrayMenuCommand.Separator),
        new(TrayMenuCommand.ExitApp, "앱 종료")
    ]);
}
