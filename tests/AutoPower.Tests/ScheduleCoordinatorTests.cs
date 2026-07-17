using AutoPower.Core;
using AutoPower.Windows;

namespace AutoPower.Tests;

[TestClass]
public sealed class ScheduleCoordinatorTests
{
    [TestMethod]
    public async Task HelperFailureRollsBackNewDatabaseSchedule()
    {
        await using var database = await SqliteStoreTests.TestDatabase.CreateAsync();
        var coordinator = new ScheduleCoordinator(database.Store, new FakeHelper { Fail = true });
        var schedule = PowerSchedule.Create(DateTime.Now.AddHours(1), PowerActionType.Sleep);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => coordinator.SaveAsync(schedule));
        Assert.IsNull(await database.Store.GetScheduleAsync(schedule.Id));
        Assert.AreEqual(PendingOperationState.Failed, (await database.Store.GetIncompleteOperationsAsync()).Single().State);
    }

    [TestMethod]
    public async Task SuccessfulSaveCompletesJournalAndPersistsSchedule()
    {
        await using var database = await SqliteStoreTests.TestDatabase.CreateAsync();
        var helper = new FakeHelper();
        var coordinator = new ScheduleCoordinator(database.Store, helper);
        var schedule = PowerSchedule.Create(DateTime.Now.AddHours(1), PowerActionType.Sleep);
        var result = await coordinator.SaveAsync(schedule);
        Assert.IsTrue(result.IsValid);
        Assert.IsNotNull(await database.Store.GetScheduleAsync(schedule.Id));
        Assert.AreEqual("register-schedule", helper.LastArguments?[0]);
        Assert.IsEmpty(await database.Store.GetIncompleteOperationsAsync());
    }

    [TestMethod]
    public void OwnedTaskParserKeepsTheEntireHyphenatedGuid()
    {
        var id = Guid.NewGuid();

        var parsed = IntegrityReconciler.TryGetScheduleIdFromOwnedTaskName($"wake-{id:D}", out var actual);

        Assert.IsTrue(parsed);
        Assert.AreEqual(id, actual);
        Assert.IsFalse(IntegrityReconciler.TryGetScheduleIdFromOwnedTaskName("startup-reconcile", out _));
    }

    private sealed class FakeHelper : IPrivilegedHelperClient
    {
        public bool Fail { get; init; }
        public IReadOnlyList<string>? LastArguments { get; private set; }
        public Task RunAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken = default)
        {
            LastArguments = arguments;
            return Fail ? Task.FromException(new InvalidOperationException("injected helper failure")) : Task.CompletedTask;
        }
    }
}
