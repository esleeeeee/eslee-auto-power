using AutoPower.Core;
using AutoPower.Windows;

namespace AutoPower.Tests;

[TestClass]
public sealed class FollowUpProgramRunnerTests
{
    [TestMethod]
    public async Task SameZeroDelayProgramsBothRun()
    {
        await using var database = await SqliteStoreTests.TestDatabase.CreateAsync();
        var process = new FakeProcesses();
        var schedule = Schedule(@"C:\Apps\a.exe", @"C:\Apps\b.exe");
        var runner = new FollowUpProgramRunner(database.Store, process, new TechnicalLogger(Path.GetTempPath()));
        var results = await runner.RunAsync(schedule, DateTimeOffset.Now);
        Assert.AreEqual(2, results.Count(result => result.Result == ResultKind.Success));
        Assert.HasCount(2, process.Started);
        CollectionAssert.Contains(process.Started, @"C:\Apps\a.exe");
        CollectionAssert.Contains(process.Started, @"C:\Apps\b.exe");
    }

    [TestMethod]
    public async Task RunningProgramIsSkippedByFullPath()
    {
        await using var database = await SqliteStoreTests.TestDatabase.CreateAsync();
        var process = new FakeProcesses { RunningPath = @"C:\Apps\a.exe" };
        var schedule = Schedule(@"C:\Apps\a.exe");
        var result = await new FollowUpProgramRunner(database.Store, process, new TechnicalLogger(Path.GetTempPath()))
            .RunAsync(schedule, DateTimeOffset.Now);
        Assert.AreEqual(ResultKind.Skipped, result[0].Result);
        Assert.IsEmpty(process.Started);
    }

    [TestMethod]
    public async Task OneFailureDoesNotBlockOtherProgramsAndAttemptIsIdempotent()
    {
        await using var database = await SqliteStoreTests.TestDatabase.CreateAsync();
        var process = new FakeProcesses { FailingPath = @"C:\Apps\bad.exe" };
        var schedule = Schedule(@"C:\Apps\bad.exe", @"C:\Apps\good.exe");
        var runner = new FollowUpProgramRunner(database.Store, process, new TechnicalLogger(Path.GetTempPath()));
        var first = await runner.RunAsync(schedule, DateTimeOffset.Now);
        var second = await runner.RunAsync(schedule, DateTimeOffset.Now);
        Assert.AreEqual(1, first.Count(result => result.Result == ResultKind.Failure));
        Assert.AreEqual(1, first.Count(result => result.Result == ResultKind.Success));
        Assert.AreEqual(2, second.Count(result => result.Result == ResultKind.Skipped));
        Assert.HasCount(1, process.Started);
        Assert.AreEqual(@"C:\Apps\good.exe", process.Started[0]);
    }

    private static PowerSchedule Schedule(params string[] paths)
    {
        var id = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        return new PowerSchedule(id, DateTime.Now.AddMinutes(1), PowerActionType.WakeFromSleep, true, false, now, now,
            ScheduleStatus.Pending, paths.Select((path, index) => new FollowUpProgram(Guid.NewGuid(), id, path, 0, null, null, false, index)).ToArray());
    }

    private sealed class FakeProcesses : IProgramProcessService
    {
        public string? RunningPath { get; init; }
        public string? FailingPath { get; init; }
        public List<string> Started { get; } = [];
        public bool IsRunning(string executablePath) => string.Equals(executablePath, RunningPath, StringComparison.OrdinalIgnoreCase);
        public void Start(FollowUpProgram program)
        {
            if (string.Equals(program.ExecutablePath, FailingPath, StringComparison.OrdinalIgnoreCase)) throw new IOException("injected");
            Started.Add(program.ExecutablePath);
        }
    }
}
