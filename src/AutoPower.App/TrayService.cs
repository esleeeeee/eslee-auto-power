using System.Drawing;
using AutoPower.Core;
using Forms = System.Windows.Forms;

namespace AutoPower.App;

public sealed class TrayService : IDisposable
{
    private readonly MainWindow _window;
    private readonly Forms.NotifyIcon _icon;
    private readonly Forms.ToolStripMenuItem _nextItem;
    private readonly Icon? _applicationIcon;

    public TrayService(MainWindow window)
    {
        _window = window;
        var menu = new Forms.ContextMenuStrip();
        _nextItem = new Forms.ToolStripMenuItem(AppText.T("다음 예약: 확인 중")) { Enabled = false };
        menu.Items.Add(_nextItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(AppText.T("앱 열기"), null, (_, _) => Invoke(window.ShowFromTray));
        menu.Items.Add(AppText.T("새 예약"), null, (_, _) => Invoke(window.ShowNewSchedule));
        menu.Items.Add(AppText.T("모든 예약 일시 중지"), null, async (_, _) => await window.PauseAllAsync());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(AppText.T("앱 종료"), null, (_, _) => Invoke(window.RequestExit));
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
        _icon.Visible = false;
        _icon.Dispose();
        _applicationIcon?.Dispose();
    }

    private static void Invoke(Action action) => System.Windows.Application.Current.Dispatcher.Invoke(action);
}
