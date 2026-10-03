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
            (eventName, error) => AppServices.Logger.Error(eventName, error),
            getSchedulesAsync: AppServices.Store.GetAllSchedulesAsync);
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

    /// <summary>
    /// Hosted 모드 전환용 아이콘 표시 제어입니다. 아이콘이 숨겨진 동안 풍선 알림은
    /// 표시되지 않습니다. UI 스레드에서 호출해야 합니다.
    /// </summary>
    public void SetTrayIconVisible(bool visible) => _icon.Visible = visible;

    /// <summary>
    /// Tray Folder가 렌더링할 현재 트레이 메뉴 스냅숏입니다. UI 스레드에서 호출해야 합니다.
    /// </summary>
    internal IReadOnlyList<TrayHostMenuItem> BuildHostedMenuItems() =>
        TrayHostedMenuBuilder.Build(_quickShutdown.IsBusy, _window.NextScheduleSummary);

    /// <summary>
    /// Tray Folder 메뉴에서 클릭된 항목을 실행합니다. 자체 트레이 메뉴의 클릭 핸들러와
    /// 같은 동작을 백그라운드로 시작하고, 알려진 항목인지 여부만 즉시 돌려줍니다.
    /// 대화 상자를 띄우는 항목이 파이프 응답을 막지 않도록 실행은 UI 큐에 넘깁니다.
    /// UI 스레드에서 호출해야 합니다.
    /// </summary>
    internal bool TryStartMenuAction(string actionId)
    {
        switch (actionId)
        {
            case TrayMenuActionIds.QuickShutdownOneHour:
                StartMenuWork("tray-host.menu.quick-shutdown-1h", () => _quickShutdown.ExecuteAsync(1));
                return true;
            case TrayMenuActionIds.QuickShutdownTwoHours:
                StartMenuWork("tray-host.menu.quick-shutdown-2h", () => _quickShutdown.ExecuteAsync(2));
                return true;
            case TrayMenuActionIds.OpenApp:
                PostMenuAction("tray-host.menu.open-app", _window.ShowFromTray);
                return true;
            case TrayMenuActionIds.NewSchedule:
                PostMenuAction("tray-host.menu.new-schedule", _window.ShowNewSchedule);
                return true;
            case TrayMenuActionIds.PauseAll:
                StartMenuWork("tray-host.menu.pause-all", _window.PauseAllAsync);
                return true;
            case TrayMenuActionIds.ExitApp:
                PostMenuAction("tray-host.menu.exit-app", _window.RequestExit);
                return true;
            default:
                return false;
        }
    }

    public void ShowBalloon(
        string title,
        string message,
        Forms.ToolTipIcon icon = Forms.ToolTipIcon.Info,
        int timeoutMilliseconds = 4000)
    {
        if (!_icon.Visible)
        {
            // Hosted 모드에서는 아이콘이 없어 풍선을 표시할 수 없습니다. 성공 확인은
            // 조용히 생략해도 되지만, 실패는 사용자가 반드시 알아야 하므로(예: 빠른
            // 완전 종료 예약 실패) 오류만 대화 상자로 대체합니다.
            if (icon == Forms.ToolTipIcon.Error)
            {
                LocalizedMessageBox.Show(
                    message,
                    title,
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Warning);
            }

            return;
        }

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

    private static void StartMenuWork(string eventName, Func<Task> work) =>
        _ = RunMenuWorkAsync(eventName, work);

    private static async Task RunMenuWorkAsync(string eventName, Func<Task> work)
    {
        try
        {
            await InvokeAsync(work);
        }
        catch (Exception error)
        {
            AppServices.Logger.Error(eventName, error);
        }
    }

    private static void PostMenuAction(string eventName, Action action) =>
        _ = System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
        {
            try
            {
                action();
            }
            catch (Exception error)
            {
                AppServices.Logger.Error(eventName, error);
            }
        });

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
    int? QuickShutdownHours = null,
    string? ActionId = null);

/// <summary>Tray Folder 파이프 메뉴에서 쓰는 항목 id입니다. 프로토콜의 일부이므로 바꾸면 안 됩니다.</summary>
internal static class TrayMenuActionIds
{
    public const string QuickShutdownOneHour = "quick-shutdown-1h";
    public const string QuickShutdownTwoHours = "quick-shutdown-2h";
    public const string NextSchedule = "next-schedule";
    public const string OpenApp = "open-app";
    public const string NewSchedule = "new-schedule";
    public const string PauseAll = "pause-all";
    public const string ExitApp = "exit-app";
}

internal static class TrayMenuLayout
{
    public static IReadOnlyList<TrayMenuEntry> Entries { get; } = Array.AsReadOnly<TrayMenuEntry>(
    [
        new(TrayMenuCommand.QuickShutdown, "1시간 후 완전 종료", 1, TrayMenuActionIds.QuickShutdownOneHour),
        new(TrayMenuCommand.QuickShutdown, "2시간 후 완전 종료", 2, TrayMenuActionIds.QuickShutdownTwoHours),
        new(TrayMenuCommand.Separator),
        new(TrayMenuCommand.NextSchedule, "다음 예약: 확인 중", null, TrayMenuActionIds.NextSchedule),
        new(TrayMenuCommand.Separator),
        new(TrayMenuCommand.OpenApp, "앱 열기", null, TrayMenuActionIds.OpenApp),
        new(TrayMenuCommand.NewSchedule, "새 예약", null, TrayMenuActionIds.NewSchedule),
        new(TrayMenuCommand.PauseAll, "모든 예약 일시 중지", null, TrayMenuActionIds.PauseAll),
        new(TrayMenuCommand.Separator),
        new(TrayMenuCommand.ExitApp, "앱 종료", null, TrayMenuActionIds.ExitApp)
    ]);
}

/// <summary>
/// 기존 트레이 메뉴 레이아웃(TrayMenuLayout)을 Tray Folder 파이프 메뉴 항목으로
/// 변환합니다. 자체 NotifyIcon 메뉴와 같은 데이터를 쓰므로 두 메뉴가 어긋나지 않습니다.
/// </summary>
internal static class TrayHostedMenuBuilder
{
    public static IReadOnlyList<TrayHostMenuItem> Build(bool quickShutdownBusy, string nextScheduleSummary)
    {
        var items = new List<TrayHostMenuItem>(TrayMenuLayout.Entries.Count);
        foreach (var entry in TrayMenuLayout.Entries)
        {
            switch (entry.Command)
            {
                case TrayMenuCommand.Separator:
                    items.Add(TrayHostMenuItem.Separator);
                    break;
                case TrayMenuCommand.NextSchedule:
                    items.Add(TrayHostMenuItem.Action(
                        entry.ActionId!,
                        AppText.T($"다음 예약: {nextScheduleSummary}"),
                        enabled: false));
                    break;
                case TrayMenuCommand.QuickShutdown:
                    items.Add(TrayHostMenuItem.Action(
                        entry.ActionId!,
                        AppText.T(entry.KoreanText!),
                        enabled: !quickShutdownBusy));
                    break;
                default:
                    items.Add(TrayHostMenuItem.Action(entry.ActionId!, AppText.T(entry.KoreanText!)));
                    break;
            }
        }

        return items;
    }
}
