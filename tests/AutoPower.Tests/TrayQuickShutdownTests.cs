using AutoPower.App;
using AutoPower.Core;
using AutoPower.Windows;

namespace AutoPower.Tests;

[TestClass]
public sealed class TrayQuickShutdownTests
{
    private static readonly bool[] ExpectedBusyStates = [true, false];
    private static readonly bool[] ExpectedBusyStatesForTwoRuns = [true, false, true, false];
    private static readonly TrayMenuCommand[] ExpectedTrayMenuCommands =
    [
        TrayMenuCommand.QuickShutdown,
        TrayMenuCommand.QuickShutdown,
        TrayMenuCommand.Separator,
        TrayMenuCommand.NextSchedule,
        TrayMenuCommand.Separator,
        TrayMenuCommand.OpenApp,
        TrayMenuCommand.NewSchedule,
        TrayMenuCommand.PauseAll,
        TrayMenuCommand.Separator,
        TrayMenuCommand.ExitApp
    ];

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task LaterWakeConsequenceIsShownEvenWhenRefreshFails(bool refreshFails)
    {
        var now = new DateTime(2030, 4, 5, 12, 0, 30);
        var wake = PowerSchedule.Create(now.AddHours(3), PowerActionType.WakeFromSleep);
        var notifications = new List<string>();
        var saves = 0;
        var controller = new TrayQuickShutdownController(
            (_, _) => { saves++; return Task.FromResult(ValidationResult.Success); },
            () => refreshFails ? Task.FromException(new InvalidOperationException("refresh")) : Task.CompletedTask,
            (_, message, _) => notifications.Add(message), (_, _) => { }, (_, _) => { }, (_, _) => { },
            () => now, _ => Task.FromResult<IReadOnlyList<PowerSchedule>>([wake]));
        Assert.AreEqual(TrayQuickShutdownOutcome.Created, await controller.ExecuteAsync(1));
        Assert.AreEqual(1, saves);
        Assert.HasCount(1, notifications);
        StringAssert.Contains(notifications[0], AppText.IsEnglish ? "scheduled wake" : "자동 시작 예약");
    }

    [TestMethod]
    [DataRow(1)]
    [DataRow(2)]
    public async Task TrayQuickMenuCreatesShutdownThroughExistingCoordinator(int hours)
    {
        await using var database = await SqliteStoreTests.TestDatabase.CreateAsync();
        var helper = new FakeHelper();
        var coordinator = new ScheduleCoordinator(database.Store, helper);
        var clicked = CurrentMinuteWithSubMinutePrecision();
        var notifications = new List<Notification>();
        var refreshCount = 0;
        var controller = CreateController(
            coordinator.SaveAsync,
            clicked,
            notifications,
            () =>
            {
                refreshCount++;
                return Task.CompletedTask;
            });

        var outcome = await controller.ExecuteAsync(hours);

        Assert.AreEqual(TrayQuickShutdownOutcome.Created, outcome);
        var schedule = (await database.Store.GetAllSchedulesAsync()).Single();
        Assert.AreEqual(PowerActionType.Shutdown, schedule.ActionType);
        Assert.AreEqual(ScheduleTimePolicy.QuickPowerTransition(clicked, hours), schedule.ScheduledLocalDateTime);
        CollectionAssert.AreEqual(
            new[] { "register-schedule", "--schedule", schedule.Id.ToString("D") },
            helper.LastArguments?.ToArray());
        Assert.AreEqual("ScheduleCreated", (await database.Store.GetHistoryAsync()).Single().EventType);
        Assert.AreEqual(1, refreshCount);
        AssertSuccessNotification(notifications.Single(), schedule.ScheduledLocalDateTime);
    }

    [TestMethod]
    [DataRow(1, 2026, 8, 5, 16, 26)]
    [DataRow(2, 2026, 8, 5, 17, 26)]
    public async Task TrayQuickMenuAddsExactHoursAndNormalizesToMinute(
        int hours,
        int year,
        int month,
        int day,
        int hour,
        int minute)
    {
        var clicked = new DateTime(2026, 8, 5, 15, 26, 48, 927, DateTimeKind.Local);
        PowerSchedule? saved = null;
        var controller = CreateController(
            (schedule, _) =>
            {
                saved = schedule;
                return Task.FromResult(ValidationResult.Success);
            },
            clicked);

        await controller.ExecuteAsync(hours);

        Assert.IsNotNull(saved);
        Assert.AreEqual(PowerActionType.Shutdown, saved.ActionType);
        Assert.AreEqual(
            new DateTime(year, month, day, hour, minute, 0, DateTimeKind.Unspecified),
            saved.ScheduledLocalDateTime);
        Assert.AreEqual(
            ScheduleTimePolicy.QuickPowerTransition(clicked, hours),
            saved.ScheduledLocalDateTime,
            "트레이와 새 예약 화면은 같은 v1.0.3 시간 정책을 사용해야 합니다.");
    }

    [TestMethod]
    [DataRow(1, 2026, 8, 5, 23, 30, 2026, 8, 6, 0, 30)]
    [DataRow(2, 2026, 1, 31, 23, 30, 2026, 2, 1, 1, 30)]
    [DataRow(2, 2026, 12, 31, 23, 30, 2027, 1, 1, 1, 30)]
    public async Task TrayQuickMenuHandlesCalendarBoundaries(
        int hours,
        int sourceYear,
        int sourceMonth,
        int sourceDay,
        int sourceHour,
        int sourceMinute,
        int expectedYear,
        int expectedMonth,
        int expectedDay,
        int expectedHour,
        int expectedMinute)
    {
        PowerSchedule? saved = null;
        var clicked = new DateTime(
            sourceYear,
            sourceMonth,
            sourceDay,
            sourceHour,
            sourceMinute,
            59,
            999,
            DateTimeKind.Local);
        var controller = CreateController(
            (schedule, _) =>
            {
                saved = schedule;
                return Task.FromResult(ValidationResult.Success);
            },
            clicked);

        await controller.ExecuteAsync(hours);

        Assert.IsNotNull(saved);
        Assert.AreEqual(
            new DateTime(
                expectedYear,
                expectedMonth,
                expectedDay,
                expectedHour,
                expectedMinute,
                0,
                DateTimeKind.Unspecified),
            saved.ScheduledLocalDateTime);
        Assert.AreEqual(PowerActionType.Shutdown, saved.ActionType);
    }

    [TestMethod]
    public async Task SuccessfulCreationShowsLocalizedNonBlockingNotification()
    {
        var clicked = new DateTime(2030, 4, 5, 15, 26, 42, DateTimeKind.Local);
        var notifications = new List<Notification>();
        var controller = CreateController(
            (_, _) => Task.FromResult(ValidationResult.Success),
            clicked,
            notifications);

        var outcome = await controller.ExecuteAsync(1);

        Assert.AreEqual(TrayQuickShutdownOutcome.Created, outcome);
        AssertSuccessNotification(
            notifications.Single(),
            ScheduleTimePolicy.QuickPowerTransition(clicked, 1));
    }

    [TestMethod]
    public async Task ValidationFailureShowsErrorNotificationAndSkipsRefresh()
    {
        var notifications = new List<Notification>();
        var warnings = new List<string>();
        var refreshCount = 0;
        var validation = new ValidationResult([new ValidationIssue("duplicate", "완전히 동일한 예약이 이미 있습니다.")]);
        var controller = CreateController(
            (_, _) => Task.FromResult(validation),
            new DateTime(2030, 4, 5, 15, 26, 42),
            notifications,
            () =>
            {
                refreshCount++;
                return Task.CompletedTask;
            },
            warning: (_, detail) => warnings.Add(detail));

        var outcome = await controller.ExecuteAsync(1);

        Assert.AreEqual(TrayQuickShutdownOutcome.ValidationFailed, outcome);
        Assert.AreEqual(0, refreshCount);
        Assert.IsTrue(notifications.Single().IsError);
        Assert.AreEqual(AppText.T("완전 종료 예약 생성 실패"), notifications.Single().Title);
        StringAssert.Contains(warnings.Single(), "duplicate");
    }

    [TestMethod]
    public async Task PipelineFailureShowsErrorNotificationAndLogsTechnicalException()
    {
        var notifications = new List<Notification>();
        var errors = new List<Exception>();
        var refreshCount = 0;
        var injected = new InvalidOperationException("injected task registration failure");
        var controller = CreateController(
            (_, _) => Task.FromException<ValidationResult>(injected),
            new DateTime(2030, 4, 5, 15, 26, 42),
            notifications,
            () =>
            {
                refreshCount++;
                return Task.CompletedTask;
            },
            error: (_, exception) => errors.Add(exception));

        var outcome = await controller.ExecuteAsync(2);

        Assert.AreEqual(TrayQuickShutdownOutcome.Failed, outcome);
        Assert.AreEqual(0, refreshCount);
        Assert.IsTrue(notifications.Single().IsError);
        Assert.AreEqual(AppText.T("완전 종료 예약 생성 실패"), notifications.Single().Title);
        Assert.AreEqual(
            AppText.T("완전 종료 예약을 만들지 못했습니다. 진단 로그에서 자세한 내용을 확인할 수 있습니다."),
            notifications.Single().Message);
        Assert.AreSame(injected, errors.Single());
    }

    [TestMethod]
    public async Task RefreshFailureAfterSuccessfulSaveReportsSavedScheduleOnce()
    {
        await using var database = await SqliteStoreTests.TestDatabase.CreateAsync();
        var helper = new FakeHelper();
        var coordinator = new ScheduleCoordinator(database.Store, helper);
        var clicked = CurrentMinuteWithSubMinutePrecision();
        var notifications = new List<Notification>();
        var loggedErrors = new List<(string EventName, Exception Error)>();
        var refreshFailure = new InvalidOperationException("injected refresh failure");
        var refreshCount = 0;
        var controller = CreateController(
            coordinator.SaveAsync,
            clicked,
            notifications,
            () =>
            {
                refreshCount++;
                return Task.FromException(refreshFailure);
            },
            error: (eventName, exception) => loggedErrors.Add((eventName, exception)));

        var outcome = await controller.ExecuteAsync(1);

        Assert.AreEqual(TrayQuickShutdownOutcome.Created, outcome);
        var schedule = (await database.Store.GetAllSchedulesAsync()).Single();
        Assert.AreEqual(ScheduleTimePolicy.QuickPowerTransition(clicked, 1), schedule.ScheduledLocalDateTime);
        Assert.AreEqual(1, helper.RunCount);
        Assert.AreEqual(1, refreshCount);
        var notification = notifications.Single();
        Assert.IsFalse(notification.IsError);
        Assert.AreEqual(AppText.T("완전 종료 예약 생성 완료"), notification.Title);
        Assert.AreEqual(
            AppText.F(
                "완전 종료가 {0:t}으로 예약되었습니다. 화면을 새로고치지 못했지만 예약은 정상적으로 저장되었습니다.",
                schedule.ScheduledLocalDateTime),
            notification.Message);
        var logged = loggedErrors.Single();
        Assert.AreEqual("tray.quick-shutdown.refresh-failed", logged.EventName);
        Assert.AreSame(refreshFailure, logged.Error);
        Assert.IsFalse(controller.IsBusy);
    }

    [TestMethod]
    public async Task MenuRemainsUsableAfterRefreshFailure()
    {
        var refreshAttempts = 0;
        var busyStates = new List<bool>();
        var notifications = new List<Notification>();
        var controller = CreateController(
            (_, _) => Task.FromResult(ValidationResult.Success),
            new DateTime(2030, 4, 5, 15, 26, 42),
            notifications,
            () => ++refreshAttempts == 1
                ? Task.FromException(new InvalidOperationException("injected refresh failure"))
                : Task.CompletedTask);
        controller.BusyChanged += busyStates.Add;

        var first = await controller.ExecuteAsync(1);
        var second = await controller.ExecuteAsync(2);

        Assert.AreEqual(TrayQuickShutdownOutcome.Created, first);
        Assert.AreEqual(TrayQuickShutdownOutcome.Created, second);
        Assert.AreEqual(2, refreshAttempts);
        Assert.IsFalse(controller.IsBusy);
        CollectionAssert.AreEqual(ExpectedBusyStatesForTwoRuns, busyStates);
        Assert.HasCount(2, notifications);
        Assert.IsFalse(notifications[0].IsError);
        Assert.IsFalse(notifications[1].IsError);
    }

    [TestMethod]
    public async Task ConcurrentClickIsIgnoredUntilFirstCreationCompletes()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var saveCount = 0;
        var busyStates = new List<bool>();
        var controller = CreateController(
            async (_, _) =>
            {
                Interlocked.Increment(ref saveCount);
                entered.SetResult();
                await release.Task;
                return ValidationResult.Success;
            },
            new DateTime(2030, 4, 5, 15, 26, 42));
        controller.BusyChanged += busyStates.Add;

        var first = controller.ExecuteAsync(1);
        await entered.Task;
        var duplicate = await controller.ExecuteAsync(1);
        release.SetResult();
        var completed = await first;

        Assert.AreEqual(TrayQuickShutdownOutcome.IgnoredWhileBusy, duplicate);
        Assert.AreEqual(TrayQuickShutdownOutcome.Created, completed);
        Assert.AreEqual(1, saveCount);
        CollectionAssert.AreEqual(ExpectedBusyStates, busyStates);
        Assert.IsFalse(controller.IsBusy);
    }

    [TestMethod]
    public void KoreanAndEnglishTrayStringsMatchSelectedBuildLanguage()
    {
        Assert.AreEqual(
            AppText.IsEnglish ? "Shut down in 1 hour" : "1시간 후 완전 종료",
            AppText.T("1시간 후 완전 종료"));
        Assert.AreEqual(
            AppText.IsEnglish ? "Shut down in 2 hours" : "2시간 후 완전 종료",
            AppText.T("2시간 후 완전 종료"));

        var scheduled = new DateTime(2030, 4, 5, 16, 26, 0);
        var localizedTime = scheduled.ToString("t", AppText.Culture);
        Assert.AreEqual(
            AppText.IsEnglish
                ? $"Shutdown scheduled for {localizedTime}."
                : $"완전 종료가 {localizedTime}으로 예약되었습니다.",
            AppText.F("완전 종료가 {0:t}으로 예약되었습니다.", scheduled));
        Assert.AreEqual(
            AppText.IsEnglish
                ? $"Shutdown is scheduled for {localizedTime}. The schedule was saved, but the screen could not be refreshed."
                : $"완전 종료가 {localizedTime}으로 예약되었습니다. 화면을 새로고치지 못했지만 예약은 정상적으로 저장되었습니다.",
            AppText.F(
                "완전 종료가 {0:t}으로 예약되었습니다. 화면을 새로고치지 못했지만 예약은 정상적으로 저장되었습니다.",
                scheduled));
    }

    [TestMethod]
    public void QuickItemsAreTopLevelAndExistingTrayMenuOrderIsPreserved()
    {
        CollectionAssert.AreEqual(
            ExpectedTrayMenuCommands,
            TrayMenuLayout.Entries.Select(entry => entry.Command).ToArray());
        Assert.AreEqual("1시간 후 완전 종료", TrayMenuLayout.Entries[0].KoreanText);
        Assert.AreEqual(1, TrayMenuLayout.Entries[0].QuickShutdownHours);
        Assert.AreEqual("2시간 후 완전 종료", TrayMenuLayout.Entries[1].KoreanText);
        Assert.AreEqual(2, TrayMenuLayout.Entries[1].QuickShutdownHours);
    }

    [TestMethod]
    public void ExistingS3S4AndAutomaticStartPoliciesRemainAvailable()
    {
        Assert.AreEqual(
            PowerActionType.WakeFromSleep,
            PowerSchedulePolicy.ResolveWakeAction(WakeModePreference.SleepS3, true, true, true));
        Assert.AreEqual(
            PowerActionType.WakeFromHibernate,
            PowerSchedulePolicy.ResolveWakeAction(WakeModePreference.HibernateS4, true, true, true));
        Assert.IsFalse(PowerSchedulePolicy.IsPowerTransition(PowerActionType.WakeFromSleep));
        Assert.IsFalse(PowerSchedulePolicy.IsPowerTransition(PowerActionType.WakeFromHibernate));
        Assert.IsTrue(PowerSchedulePolicy.IsPowerTransition(PowerActionType.Sleep));
        Assert.IsTrue(PowerSchedulePolicy.IsPowerTransition(PowerActionType.Hibernate));
    }

    private static TrayQuickShutdownController CreateController(
        Func<PowerSchedule, CancellationToken, Task<ValidationResult>> save,
        DateTime clicked,
        List<Notification>? notifications = null,
        Func<Task>? refresh = null,
        Action<string, string>? information = null,
        Action<string, string>? warning = null,
        Action<string, Exception>? error = null) =>
        new(
            save,
            refresh ?? (() => Task.CompletedTask),
            (title, message, isError) => notifications?.Add(new Notification(title, message, isError)),
            information ?? ((_, _) => { }),
            warning ?? ((_, _) => { }),
            error ?? ((_, _) => { }),
            () => clicked);

    private static DateTime CurrentMinuteWithSubMinutePrecision()
    {
        var now = DateTime.Now;
        return new DateTime(now.Year, now.Month, now.Day, now.Hour, now.Minute, 42, 321, DateTimeKind.Local);
    }

    private static void AssertSuccessNotification(Notification notification, DateTime scheduledLocalTime)
    {
        Assert.IsFalse(notification.IsError);
        Assert.AreEqual(AppText.T("완전 종료 예약 생성 완료"), notification.Title);
        Assert.AreEqual(
            AppText.F("완전 종료가 {0:t}으로 예약되었습니다.", scheduledLocalTime),
            notification.Message);
    }

    private sealed record Notification(string Title, string Message, bool IsError);

    private sealed class FakeHelper : IPrivilegedHelperClient
    {
        public IReadOnlyList<string>? LastArguments { get; private set; }

        public int RunCount { get; private set; }

        public Task RunAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken = default)
        {
            LastArguments = arguments;
            RunCount++;
            return Task.CompletedTask;
        }
    }
}
