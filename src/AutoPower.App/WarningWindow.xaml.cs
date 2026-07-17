using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Threading;
using AutoPower.Core;
using MessageBox = AutoPower.App.LocalizedMessageBox;

namespace AutoPower.App;

public partial class WarningWindow : Window
{
    private readonly PowerSchedule _schedule;
    private readonly DispatcherTimer _timer;
    private int _seconds = 30;
    private bool _decisionMade;

    public WarningWindow(PowerSchedule schedule)
    {
        InitializeComponent();
        _schedule = schedule;
        ScheduleText.Text = $"{schedule.ScheduledLocalDateTime:yyyy.MM.dd HH:mm} · {AppText.T(ActionText(schedule.ActionType))}";
        if (PowerSchedulePolicy.IsWakeSchedule(schedule.ActionType))
        {
            Heading.Text = AppText.T("PC가 이미 켜져 있습니다");
            Description.Text = AppText.IsEnglish
                ? $"Choose how to run the {schedule.FollowUpPrograms.Count} follow-up programs linked to this {AppText.T(PowerSchedulePolicy.WakeStateName(schedule.ActionType))} wake schedule."
                : $"이 {PowerSchedulePolicy.WakeStateName(schedule.ActionType)} 깨우기 예약에 연결된 후속 프로그램 {schedule.FollowUpPrograms.Count}개를 실행할 방법을 선택하세요.";
            NowButton.Content = AppText.T("지금 시작");
            DataLossWarning.Visibility = Visibility.Collapsed;
        }
        else
        {
            Heading.Text = AppText.IsEnglish
                ? $"{AppText.T(ActionText(schedule.ActionType))} in 5 minutes"
                : $"5분 후 {ActionText(schedule.ActionType)} 예정";
            Description.Text = AppText.T("예약 시각에는 저장된 절전 또는 최대 절전 동작을 실행합니다.");
            NowButton.Content = AppText.T(schedule.ActionType switch
            {
                PowerActionType.Hibernate => "지금 최대 절전",
                _ => "지금 절전"
            });
        }

        KeepButton.Content = AppText.T("원래대로 진행 (30)");
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += Timer_Tick;
        _timer.Start();
        Closing += WarningWindow_Closing;
    }

    private async void Now_Click(object sender, RoutedEventArgs e)
    {
        _decisionMade = true;
        _timer.Stop();
        try
        {
            if (PowerSchedulePolicy.IsWakeSchedule(_schedule.ActionType))
            {
                var now = DateTimeOffset.Now;
                await AppServices.Store.SetSettingAsync($"wake-decision:{_schedule.Id:D}", "now");
                await AppServices.Store.SetSettingAsync($"virtual-t0:{_schedule.Id:D}", now.ToString("O", CultureInfo.InvariantCulture));
                StartAgent("now");
                Close();
            }
            else
            {
                await AppServices.Helper.RunAsync(["execute-power-now", "--schedule", _schedule.Id.ToString("D")]);
            }
        }
        catch (Exception error)
        {
            MessageBox.Show(error.Message, "eslee Auto Power", MessageBoxButton.OK, MessageBoxImage.Error);
            _decisionMade = false;
            _timer.Start();
        }
    }

    private async void Keep_Click(object sender, RoutedEventArgs e)
    {
        await KeepOriginalAsync();
        Close();
    }

    private async void Skip_Click(object sender, RoutedEventArgs e)
    {
        _decisionMade = true;
        _timer.Stop();
        try
        {
            if (PowerSchedulePolicy.IsWakeSchedule(_schedule.ActionType))
            {
                await AppServices.Store.SetSettingAsync($"wake-decision:{_schedule.Id:D}", "skip");
            }

            await AppServices.Helper.RunAsync(["skip-schedule", "--schedule", _schedule.Id.ToString("D")]);
            Close();
        }
        catch (Exception error)
        {
            MessageBox.Show(error.Message, "eslee Auto Power", MessageBoxButton.OK, MessageBoxImage.Error);
            _decisionMade = false;
            _timer.Start();
        }
    }

    private async void Timer_Tick(object? sender, EventArgs e)
    {
        _seconds--;
        KeepButton.Content = AppText.IsEnglish ? $"Keep original schedule ({_seconds})" : $"원래대로 진행 ({_seconds})";
        if (_seconds <= 0)
        {
            _timer.Stop();
            await KeepOriginalAsync();
            Close();
        }
    }

    private void WarningWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _timer.Stop();
        if (!_decisionMade)
        {
            KeepOriginalAsync().GetAwaiter().GetResult();
        }

        Dispatcher.BeginInvoke(() => System.Windows.Application.Current.Shutdown());
    }

    private async Task KeepOriginalAsync()
    {
        if (_decisionMade)
        {
            return;
        }

        _decisionMade = true;
        _timer.Stop();
        if (PowerSchedulePolicy.IsWakeSchedule(_schedule.ActionType))
        {
            var original = new DateTimeOffset(_schedule.ScheduledLocalDateTime, TimeZoneInfo.Local.GetUtcOffset(_schedule.ScheduledLocalDateTime));
            await AppServices.Store.SetSettingAsync($"wake-decision:{_schedule.Id:D}", "original");
            await AppServices.Store.SetSettingAsync($"virtual-t0:{_schedule.Id:D}", original.ToString("O", CultureInfo.InvariantCulture));
        }
    }

    private void StartAgent(string source)
    {
        var startInfo = new ProcessStartInfo(AppServices.Layout.AgentPath) { UseShellExecute = false };
        startInfo.ArgumentList.Add("run-followups");
        startInfo.ArgumentList.Add("--schedule");
        startInfo.ArgumentList.Add(_schedule.Id.ToString("D"));
        startInfo.ArgumentList.Add("--source");
        startInfo.ArgumentList.Add(source);
        Process.Start(startInfo)?.Dispose();
    }

    private static string ActionText(PowerActionType action) => action switch
    {
        PowerActionType.PowerOn or PowerActionType.Shutdown => "지원하지 않는 이전 전원 동작",
        PowerActionType.WakeFromSleep => "S3 절전 깨우기",
        PowerActionType.WakeFromHibernate => "S4 최대 절전 깨우기",
        PowerActionType.Hibernate => "최대 절전",
        PowerActionType.Sleep => "절전",
        _ => action.ToString()
    };
}
