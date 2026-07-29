using AutoPower.Core;
using AutoPower.Windows;

namespace AutoPower.Tests;

[TestClass]
public sealed class ShutdownPowerTransitionWorkflowTests
{
    [TestMethod]
    public async Task PrimaryAcceptedKeepsSchedulePendingForPowerEvidence()
    {
        await using var fixture = await WorkflowFixture.CreateAsync();
        fixture.Controller.Primary.Enqueue(Result(ShutdownStage.Primary, 0));

        await fixture.Workflow.StartPrimaryAsync(fixture.Schedule, fixture.Operation);

        var schedule = await fixture.Database.Store.GetScheduleAsync(fixture.Schedule.Id);
        var operation = (await fixture.Database.Store.GetIncompleteOperationsAsync()).Single();
        Assert.AreEqual(ScheduleStatus.PendingPowerTransition, schedule?.Status);
        Assert.AreEqual(PendingOperationState.Pending, operation.State);
        StringAssert.Contains(operation.Detail, "PrimaryAccepted");
        Assert.AreEqual(1, fixture.Registrar.RegisterFallbackCount);
        Assert.AreEqual(0, fixture.Registrar.RemoveCount);
        Assert.IsTrue((await fixture.Database.Store.GetHistoryAsync())
            .Any(item => item.EventType == "ShutdownPrimaryAccepted"));
    }

    [TestMethod]
    public async Task PrimaryRejectedKeepsFallbackAndMovesJournalToCompensating()
    {
        await using var fixture = await WorkflowFixture.CreateAsync();
        fixture.Controller.Primary.Enqueue(Result(
            ShutdownStage.Primary,
            ShutdownNativeConstants.ErrorAccessDenied));

        var result = await fixture.Workflow.StartPrimaryAsync(fixture.Schedule, fixture.Operation);

        Assert.IsNotNull(result);
        Assert.IsFalse(result.Accepted);
        var schedule = await fixture.Database.Store.GetScheduleAsync(fixture.Schedule.Id);
        var operation = (await fixture.Database.Store.GetIncompleteOperationsAsync()).Single();
        Assert.AreEqual(ScheduleStatus.PendingPowerTransition, schedule?.Status);
        Assert.AreEqual(PendingOperationState.Compensating, operation.State);
        StringAssert.Contains(operation.Detail, "PrimaryRejected");
        StringAssert.Contains(operation.Detail, "FallbackPending");
        Assert.AreEqual(1, fixture.Registrar.RegisterFallbackCount);
        Assert.AreEqual(0, fixture.Registrar.RemoveCount);
        var history = await fixture.Database.Store.GetHistoryAsync();
        var rejected = history.Single(item => item.EventType == "ShutdownPrimaryRejected");
        Assert.AreEqual("5", rejected.ErrorCode);
    }

    [TestMethod]
    public async Task FallbackAcceptedKeepsPendingUntilReconciliation()
    {
        await using var fixture = await WorkflowFixture.CreateAsync();
        fixture.Controller.Primary.Enqueue(Result(
            ShutdownStage.Primary,
            ShutdownNativeConstants.ErrorInvalidParameter));
        fixture.Controller.Fallback.Enqueue(Result(ShutdownStage.Fallback, 0));
        await fixture.Workflow.StartPrimaryAsync(fixture.Schedule, fixture.Operation);

        var result = await fixture.Workflow.ExecuteFallbackAsync(fixture.Schedule);

        Assert.IsNotNull(result);
        Assert.IsTrue(result.Accepted);
        Assert.AreEqual(
            ScheduleStatus.PendingPowerTransition,
            (await fixture.Database.Store.GetScheduleAsync(fixture.Schedule.Id))?.Status);
        var operation = (await fixture.Database.Store.GetIncompleteOperationsAsync()).Single();
        Assert.AreEqual(PendingOperationState.Compensating, operation.State);
        StringAssert.Contains(operation.Detail, "FallbackAccepted");
        Assert.AreEqual(0, fixture.Registrar.RemoveCount);
        Assert.IsTrue((await fixture.Database.Store.GetHistoryAsync())
            .Any(item => item.EventType == "ShutdownFallbackAccepted"));
    }

    [TestMethod]
    public async Task FallbackRejectedIsTheOnlyPointThatFinalizesFailure()
    {
        await using var fixture = await WorkflowFixture.CreateAsync();
        fixture.Controller.Primary.Enqueue(Result(
            ShutdownStage.Primary,
            ShutdownNativeConstants.ErrorAccessDenied));
        fixture.Controller.Fallback.Enqueue(Result(
            ShutdownStage.Fallback,
            ShutdownNativeConstants.ErrorInvalidFunction));
        await fixture.Workflow.StartPrimaryAsync(fixture.Schedule, fixture.Operation);

        var result = await fixture.Workflow.ExecuteFallbackAsync(fixture.Schedule);

        Assert.IsNotNull(result);
        Assert.IsFalse(result.Accepted);
        Assert.AreEqual(
            ScheduleStatus.Failed,
            (await fixture.Database.Store.GetScheduleAsync(fixture.Schedule.Id))?.Status);
        var operation = (await fixture.Database.Store.GetIncompleteOperationsAsync())
            .Single(item => item.ScheduleId == fixture.Schedule.Id);
        Assert.AreEqual(PendingOperationState.Failed, operation.State);
        Assert.AreEqual(1, fixture.Registrar.RemoveCount);
        var failure = (await fixture.Database.Store.GetHistoryAsync())
            .Single(item => item.EventType == "PowerTransitionFailed");
        Assert.AreEqual("1", failure.ErrorCode);
    }

    [TestMethod]
    public async Task InProgressFallbackIsNotFinalFailure()
    {
        await using var fixture = await WorkflowFixture.CreateAsync();
        fixture.Controller.Primary.Enqueue(Result(ShutdownStage.Primary, 0));
        fixture.Controller.Fallback.Enqueue(Result(
            ShutdownStage.Fallback,
            ShutdownNativeConstants.ErrorShutdownInProgress,
            ShutdownDisposition.AlreadyInProgress));
        await fixture.Workflow.StartPrimaryAsync(fixture.Schedule, fixture.Operation);

        await fixture.Workflow.ExecuteFallbackAsync(fixture.Schedule);

        Assert.AreEqual(
            ScheduleStatus.PendingPowerTransition,
            (await fixture.Database.Store.GetScheduleAsync(fixture.Schedule.Id))?.Status);
        Assert.IsFalse((await fixture.Database.Store.GetHistoryAsync())
            .Any(item => item.EventType == "PowerTransitionFailed"));
    }

    private static ShutdownExecutionResult Result(
        ShutdownStage stage,
        uint nativeCode,
        ShutdownDisposition? disposition = null)
    {
        var now = DateTimeOffset.UtcNow;
        var request = new ShutdownRequest(
            stage,
            stage == ShutdownStage.Primary ? 30u : 0u,
            ShutdownNativeConstants.RequiredShutdownFlags,
            ShutdownNativeConstants.PlannedApplicationMaintenance);
        var symbolic = ShutdownErrorCatalog.SymbolicName(nativeCode);
        return new ShutdownExecutionResult(
            request,
            disposition ?? (nativeCode == 0 ? ShutdownDisposition.Accepted : ShutdownDisposition.Rejected),
            new ShutdownPrivilegeResult(true, false, true, "AdjustTokenPrivileges", 0, "ERROR_SUCCESS", "success"),
            new ShutdownExecutionContext(100, 0, true, true),
            [new ShutdownNativeAttempt(request.Flags, nativeCode, symbolic, $"message-{nativeCode}")],
            now,
            now);
    }

    private sealed class WorkflowFixture : IAsyncDisposable
    {
        private readonly string _logRoot;

        private WorkflowFixture(
            SqliteStoreTests.TestDatabase database,
            PowerSchedule schedule,
            PendingOperation operation,
            FakeFallbackRegistrar registrar,
            FakeShutdownController controller,
            ShutdownPowerTransitionWorkflow workflow,
            string logRoot)
        {
            Database = database;
            Schedule = schedule;
            Operation = operation;
            Registrar = registrar;
            Controller = controller;
            Workflow = workflow;
            _logRoot = logRoot;
        }

        public SqliteStoreTests.TestDatabase Database { get; }
        public PowerSchedule Schedule { get; }
        public PendingOperation Operation { get; }
        public FakeFallbackRegistrar Registrar { get; }
        public FakeShutdownController Controller { get; }
        public ShutdownPowerTransitionWorkflow Workflow { get; }

        public static async Task<WorkflowFixture> CreateAsync()
        {
            var database = await SqliteStoreTests.TestDatabase.CreateAsync();
            var now = new DateTime(2026, 7, 29, 12, 0, 0, DateTimeKind.Local);
            var schedule = PowerSchedule.Create(now, PowerActionType.Shutdown);
            await database.Store.SaveScheduleAsync(schedule);
            var operation = await database.Store.TryBeginPowerTransitionAsync(
                schedule,
                "완전 종료 실행 시작",
                new DateTimeOffset(now).ToUniversalTime());
            Assert.IsNotNull(operation);
            var registrar = new FakeFallbackRegistrar();
            var controller = new FakeShutdownController();
            var logRoot = Path.Combine(Path.GetTempPath(), "AutoPower.Tests", Guid.NewGuid().ToString("N"));
            var workflow = new ShutdownPowerTransitionWorkflow(
                database.Store,
                registrar,
                controller,
                new TechnicalLogger(Path.Combine(logRoot, "logs")),
                () => now);
            return new WorkflowFixture(
                database,
                schedule,
                operation,
                registrar,
                controller,
                workflow,
                logRoot);
        }

        public async ValueTask DisposeAsync()
        {
            await Database.DisposeAsync();
            if (Directory.Exists(_logRoot))
            {
                Directory.Delete(_logRoot, true);
            }
        }
    }

    private sealed class FakeShutdownController : IShutdownController
    {
        public Queue<ShutdownExecutionResult> Primary { get; } = new();
        public Queue<ShutdownExecutionResult> Fallback { get; } = new();

        public Task<ShutdownExecutionResult> StartGracefulShutdownAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Primary.Dequeue());

        public Task<ShutdownExecutionResult> ForceShutdownAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Fallback.Dequeue());
    }

    private sealed class FakeFallbackRegistrar : IShutdownFallbackRegistrar
    {
        public int RegisterFallbackCount { get; private set; }
        public int RemoveCount { get; private set; }
        public DateTime? FallbackTime { get; private set; }

        public void RegisterShutdownFallback(Guid scheduleId, DateTime localTime)
        {
            RegisterFallbackCount++;
            FallbackTime = localTime;
        }

        public void Remove(Guid scheduleId) => RemoveCount++;
    }
}
