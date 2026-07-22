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
    private readonly PowerSchedule? _nextWake;
    private readonly DispatcherTimer _timer;
    private int _seconds = 30;
    private bool _decisionMade;

    public WarningWindow(PowerSchedule schedule, PowerSchedule? nextWake = null)
    {
        InitializeComponent();
        _schedule = schedule;
        _nextWake = nextWake;
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
        else if (schedule.ActionType == PowerActionType.Shutdown && nextWake is not null)
        {
            var stateName = AppText.T(PowerSchedulePolicy.WakeStateName(nextWake.ActionType));
            Heading.Text = AppText.T("완전 종료 전에 확인하세요");
            Description.Text = AppText.IsEnglish
                ? $"The next scheduled wake is {nextWake.ScheduledLocalDateTime:MMM dd HH:mm}. A full shutdown prevents this app from turning the PC back on. Choose {stateName} now to keep that wake schedule, or continue with the full shutdown."
                : $"다음 자동 시작 예약은 {nextWake.ScheduledLocalDateTime:MM월 dd일 HH:mm}입니다. PC를 완전히 종료하면 앱이 예약 시각에 다시 켤 수 없습니다. 자동 시작을 유지하려면 지금 {stateName} 상태로 전환하거나, 경고를 확인한 뒤 완전 종료를 계속하세요.";
            NowButton.Content = AppText.IsEnglish
                ? $"Keep wake · Enter {stateName}"
                : $"자동 시작 유지 · 지금 {stateName}";
        }
        else
        {
            Heading.Text = AppText.IsEnglish
                ? $"{AppText.T(ActionText(schedule.ActionType))} in 5 minutes"
                : $"5분 후 {ActionText(schedule.ActionType)} 예정";
            Description.Text = AppText.T(schedule.ActionType == PowerActionType.Shutdown
                ? "예약 시각에는 정상 종료를 먼저 시도하고, 30초 안에 끝나지 않으면 강제 종료합니다. 완전히 종료된 뒤에는 앱의 자동 시작 예약으로 PC를 다시 켤 수 없습니다."
                : "예약 시각에는 저장된 절전 또는 최대 절전 동작을 실행합니다.");
            NowButton.Content = AppText.T(schedule.ActionType switch
            {
                PowerActionType.Shutdown => "지금 완전 종료",
                PowerActionType.Hibernate => "지금 최대 절전",
                _ => "지금 절전"
            });
        }

        UpdateKeepButton();
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += Timer_Tick;
        _timer.Start();
        Closing += WarningWindow_Closing;
    }

    private async void Now_Click(object sender, RoutedEventArgs e)
    {
        var skippedShutdownForWake = false;
        _decisionMade = true;
        _timer.Stop();
        try
        {
            if (_schedule.ActionType == PowerActionType.Shutdown && _nextWake is not null)
            {
                await AppServices.Helper.RunAsync(["skip-schedule", "--schedule", _schedule.Id.ToString("D")]);
                skippedShutdownForWake = true;
                await AppServices.Helper.RunAsync(["prepare-wake", "--schedule", _nextWake.Id.ToString("D")]);
                Close();
            }
            else if (PowerSchedulePolicy.IsWakeSchedule(_schedule.ActionType))
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
            if (skippedShutdownForWake)
            {
                try
                {
                    await AppServices.Coordinator.SetEnabledAsync(_schedule.Id, true);
                }
                catch (Exception rollbackError)
                {
                    AppServices.Logger.Error("shutdown-guard.rollback-failed", rollbackError);
                }
            }

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
        UpdateKeepButton();
        if (_seconds <= 0)
        {
            _timer.Stop();
            await KeepOriginalAsync();
            Close();
        }
    }

    private void UpdateKeepButton()
    {
        KeepButton.Content = _schedule.ActionType == PowerActionType.Shutdown && _nextWake is not null
            ? AppText.IsEnglish ? $"Full shutdown anyway ({_seconds})" : $"그래도 완전 종료 ({_seconds})"
            : AppText.IsEnglish ? $"Keep original schedule ({_seconds})" : $"원래대로 진행 ({_seconds})";
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
        PowerActionType.PowerOn => "지원하지 않는 이전 전원 동작",
        PowerActionType.Shutdown => "완전 종료",
        PowerActionType.WakeFromSleep => "S3 절전 깨우기",
        PowerActionType.WakeFromHibernate => "S4 최대 절전 깨우기",
        PowerActionType.Hibernate => "최대 절전",
        PowerActionType.Sleep => "절전",
        _ => action.ToString()
    };
}
