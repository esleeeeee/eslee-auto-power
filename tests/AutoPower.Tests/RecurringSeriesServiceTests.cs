using AutoPower.Core;
using AutoPower.Windows;

namespace AutoPower.Tests;

[TestClass]
public sealed class RecurringSeriesServiceTests
{
    [TestMethod]
    public async Task SkippedOccurrenceAdvancesAndRegistersTheNextOccurrence()
    {
        await using var fixture = await SeriesFixture.CreateAsync();
        var skipped = await RecurringSeriesStoreTests.SaveExecutedOccurrenceAsync(
            fixture.Database.Store, fixture.Now.AddMinutes(4), PowerActionType.Sleep, ScheduleStatus.Skipped);

        var next = await fixture.Service().AdvanceAfterOccurrenceAsync(skipped.Id);

        Assert.IsNotNull(next);
        Assert.AreEqual(skipped.ScheduledLocalDateTime.AddDays(1), next.ScheduledLocalDateTime);
        CollectionAssert.AreEqual(new[] { next.Id }, fixture.Registrar.Registered.ToArray());
    }

    [TestMethod]
    public async Task RegistrationFailureKeepsTheRowAndRecordsWarning()
    {
        await using var fixture = await SeriesFixture.CreateAsync();
        fixture.Registrar.FailRegister = true;
        var executed = await RecurringSeriesStoreTests.SaveExecutedOccurrenceAsync(
            fixture.Database.Store, fixture.Now.AddMinutes(-1), PowerActionType.Shutdown);

        var next = await fixture.Service().AdvanceAfterOccurrenceAsync(executed.Id);

        Assert.IsNotNull(next);
        Assert.AreEqual(ScheduleStatus.Pending, (await fixture.Database.Store.GetScheduleAsync(next.Id))?.Status);
        var warning = (await fixture.Database.Store.GetHistoryAsync())
            .Single(item => item.EventType == HistoryEventTypes.RecurrenceAdvanceFailed);
        Assert.AreEqual(ResultKind.Warning, warning.Result);
        Assert.AreEqual(next.Id, warning.ScheduleId);
    }

    [TestMethod]
    public async Task NonRecurringOccurrenceIsNotAdvanced()
    {
        await using var fixture = await SeriesFixture.CreateAsync();
        var oneTime = PowerSchedule.Create(fixture.Now.AddMinutes(-1), PowerActionType.Shutdown) with
        {
            IsEnabled = false,
            Status = ScheduleStatus.Completed
        };
        await fixture.Database.Store.SaveScheduleAsync(oneTime);

        Assert.IsNull(await fixture.Service().AdvanceAfterOccurrenceAsync(oneTime.Id));
        Assert.IsNull(await fixture.Service().AdvanceAfterOccurrenceAsync(Guid.NewGuid()));
        Assert.HasCount(1, await fixture.Database.Store.GetAllSchedulesAsync());
        Assert.IsEmpty(fixture.Registrar.Registered);
    }

    [TestMethod]
    public async Task HealAdvancesFinishedSeriesButNotPausedOrEndedSeries()
    {
        await using var fixture = await SeriesFixture.CreateAsync();
        var store = fixture.Database.Store;
        var missed = await RecurringSeriesStoreTests.SaveExecutedOccurrenceAsync(
            store, fixture.Now.AddDays(-3), PowerActionType.Hibernate, ScheduleStatus.Missed);
        var paused = await RecurringSeriesStoreTests.SaveExecutedOccurrenceAsync(
            store, fixture.Now.AddDays(-2), PowerActionType.Sleep, ScheduleStatus.Completed);
        await store.SaveScheduleAsync(paused.CreateNextOccurrence(fixture.Now.AddDays(-1), DateTimeOffset.UtcNow) with
        {
            IsEnabled = false,
            Status = ScheduleStatus.Disabled
        });
        var ended = await RecurringSeriesStoreTests.SaveExecutedOccurrenceAsync(
            store, fixture.Now.AddDays(-2).AddHours(1), PowerActionType.Shutdown, ScheduleStatus.Completed);
        var endedLive = await store.TryAdvanceSeriesAsync(ended.Id, fixture.Now);
        Assert.IsNotNull(endedLive);
        await store.DeleteScheduleAsync(endedLive.Id);

        var created = await fixture.Service(registrar: false).HealSeriesAsync();
        var again = await fixture.Service(registrar: false).HealSeriesAsync();

        Assert.HasCount(1, created);
        Assert.IsEmpty(again);
        Assert.AreEqual(missed.SeriesId, created[0].SeriesId);
        Assert.IsGreaterThan(fixture.Now, created[0].ScheduledLocalDateTime);
        Assert.AreEqual(missed.ScheduledLocalDateTime.TimeOfDay, created[0].ScheduledLocalDateTime.TimeOfDay);
        Assert.IsEmpty(fixture.Registrar.Registered);
    }

    internal sealed class SeriesFixture : IAsyncDisposable
    {
        private readonly string _logDirectory;

        private SeriesFixture(SqliteStoreTests.TestDatabase database, string logDirectory)
        {
            Database = database;
            _logDirectory = logDirectory;
            Logger = new TechnicalLogger(logDirectory);
        }

        public SqliteStoreTests.TestDatabase Database { get; }
        public RecordingRegistrar Registrar { get; } = new();
        public TechnicalLogger Logger { get; }
        public DateTime Now { get; set; } = DateTime.Now;

        public static async Task<SeriesFixture> CreateAsync() =>
            new(await SqliteStoreTests.TestDatabase.CreateAsync(),
                Path.Combine(Path.GetTempPath(), "AutoPower.Tests", Guid.NewGuid().ToString("N"), "logs"));

        public RecurringSeriesService Service(bool registrar = true) =>
            new(Database.Store, registrar ? Registrar : null, Logger, () => Now);

        public async ValueTask DisposeAsync()
        {
            await Database.DisposeAsync();
            var parent = Directory.GetParent(_logDirectory)?.FullName;
            if (parent is not null && Directory.Exists(parent))
            {
                Directory.Delete(parent, true);
            }
        }
    }

    internal sealed class RecordingRegistrar : ISystemScheduleRegistrar
    {
        public bool FailRegister { get; set; }
        public List<Guid> Registered { get; } = [];
        public List<Guid> Removed { get; } = [];

        public void Register(PowerSchedule schedule)
        {
            if (FailRegister)
            {
                throw new InvalidOperationException("injected registration failure");
            }

            Registered.Add(schedule.Id);
        }

        public void Remove(Guid scheduleId) => Removed.Add(scheduleId);
        public void ConsumePowerTask(Guid scheduleId) { }
        public PowerTaskSnapshot GetPowerTaskSnapshot(Guid scheduleId) => PowerTaskSnapshot.Missing;
        public IReadOnlySet<string> ListOwnedTasks() => new HashSet<string>();
        public void RegisterStartupAgent(string userName) { }
        public void RemoveAllOwnedTasks() { }
    }
}
