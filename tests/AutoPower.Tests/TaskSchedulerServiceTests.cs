using AutoPower.Core;
using AutoPower.Windows;

namespace AutoPower.Tests;

[TestClass]
public sealed class TaskSchedulerServiceTests
{
    [TestMethod]
    public void MissingFolderWrappedAsFileNotFoundIsRecognized()
    {
        Assert.IsTrue(TaskSchedulerService.IsMissingSchedulerObject(new FileNotFoundException()));
    }

    [TestMethod]
    public void MissingTaskComCodeIsRecognized()
    {
        Assert.IsTrue(TaskSchedulerService.IsMissingSchedulerObject(
            new SchedulerHResultException(unchecked((int)0x8004130F))));
    }

    [TestMethod]
    public void AccessDeniedIsNotTreatedAsMissing()
    {
        Assert.IsFalse(TaskSchedulerService.IsMissingSchedulerObject(new UnauthorizedAccessException()));
    }

    [TestMethod]
    public void OneTimeTriggerIncludesEndBoundaryRequiredForExpirationCleanup()
    {
        var localTime = new DateTime(2026, 7, 10, 21, 45, 0, DateTimeKind.Local);

        var boundaries = TaskSchedulerService.CreateTimeTriggerBoundaries(localTime);

        Assert.AreEqual("2026-07-10T21:45:00", boundaries.StartBoundary);
        Assert.AreEqual("2026-07-11T21:45:00", boundaries.EndBoundary);
    }

    [TestMethod]
    public void ElevatedFollowUpTaskKeepsBothScheduleAndProgramIdentity()
    {
        var scheduleId = Guid.NewGuid();
        var programId = Guid.NewGuid();

        var taskName = TaskSchedulerService.ElevatedFollowUpTaskName(scheduleId, programId);

        Assert.AreEqual($"elevated-{scheduleId:D}-{programId:D}", taskName);
        Assert.IsTrue(IntegrityReconciler.TryGetScheduleIdFromOwnedTaskName(taskName, out var parsed));
        Assert.AreEqual(scheduleId, parsed);
    }

    [TestMethod]
    public void ElevatedProgramUsesPreauthorizedTaskInsteadOfStartingDirectly()
    {
        var directory = Path.Combine(Path.GetTempPath(), "AutoPowerTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var executable = Path.Combine(directory, "admin-tool.exe");
        File.WriteAllBytes(executable, []);
        try
        {
            var scheduleId = Guid.NewGuid();
            var programId = Guid.NewGuid();
            var launcher = new FakeElevatedLauncher();
            var service = new ProgramProcessService(launcher);
            var program = new FollowUpProgram(
                programId, scheduleId, executable, 0, "--test", directory, true, 0);

            service.Start(program);

            Assert.AreEqual(scheduleId, launcher.ScheduleId);
            Assert.AreEqual(programId, launcher.ProgramId);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [TestMethod]
    public void RecentHelperErrorReturnsMessageWithoutStackTrace()
    {
        var directory = Path.Combine(Path.GetTempPath(), "AutoPowerTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var startedAt = DateTimeOffset.Now;
            var line = $"{startedAt:O}\tERROR\thelper.command-failed\tcommand=register-schedule\t" +
                       "System.IO.FileNotFoundException: 지정된 파일을 찾을 수 없습니다. (0x80070002)     at Example.Method()";
            File.WriteAllText(Path.Combine(directory, $"autopower-{DateTime.Today:yyyy-MM-dd}.log"), line);

            var result = ElevatedHelperClient.TryReadRecentHelperError(directory, startedAt, "register-schedule");

            Assert.AreEqual("지정된 파일을 찾을 수 없습니다. (0x80070002)", result);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    private sealed class SchedulerHResultException : Exception
    {
        public SchedulerHResultException(int hResult) => HResult = hResult;
    }

    private sealed class FakeElevatedLauncher : IElevatedProgramLauncher
    {
        public Guid ScheduleId { get; private set; }
        public Guid ProgramId { get; private set; }

        public void RunElevatedFollowUp(Guid scheduleId, Guid programId)
        {
            ScheduleId = scheduleId;
            ProgramId = programId;
        }
    }
}
