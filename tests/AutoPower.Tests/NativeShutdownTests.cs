using AutoPower.Core;
using AutoPower.Windows;

namespace AutoPower.Tests;

[TestClass]
public sealed class NativeShutdownTests
{
    [TestMethod]
    public async Task PrimarySuccessUsesDocumentedFullShutdownContract()
    {
        using var fixture = new ControllerFixture();
        fixture.Native.Results.Enqueue(ShutdownNativeConstants.ErrorSuccess);

        var result = await fixture.Controller.StartGracefulShutdownAsync();

        Assert.IsTrue(result.Accepted);
        Assert.AreEqual(ShutdownDisposition.Accepted, result.Disposition);
        Assert.AreEqual(30u, result.Request.GraceSeconds);
        Assert.AreEqual(
            ShutdownNativeConstants.ShutdownPowerOff |
            ShutdownNativeConstants.ShutdownForceSelf |
            ShutdownNativeConstants.ShutdownForceOthers,
            result.Request.Flags);
        Assert.AreEqual(0u, result.Request.Flags & ShutdownNativeConstants.ShutdownHybrid);
        Assert.AreEqual(0x80040001u, result.Request.Reason);
        Assert.HasCount(1, fixture.Native.Calls);
    }

    [TestMethod]
    [DataRow(1, "ERROR_INVALID_FUNCTION")]
    [DataRow(5, "ERROR_ACCESS_DENIED")]
    [DataRow(21, "ERROR_NOT_READY")]
    [DataRow(87, "ERROR_INVALID_PARAMETER")]
    [DataRow(1191, "ERROR_SHUTDOWN_USERS_LOGGED_ON")]
    [DataRow(1190, "ERROR_SHUTDOWN_IS_SCHEDULED")]
    [DataRow(1115, "ERROR_SHUTDOWN_IN_PROGRESS")]
    [DataRow(424242, "WIN32_ERROR_424242")]
    public async Task PrimaryPreservesEveryNativeRejection(int nativeCode, string symbolic)
    {
        using var fixture = new ControllerFixture();
        fixture.Native.Results.Enqueue(checked((uint)nativeCode));

        var result = await fixture.Controller.StartGracefulShutdownAsync();

        Assert.IsFalse(result.Accepted);
        Assert.AreEqual(checked((uint)nativeCode), result.NativeErrorCode);
        Assert.AreEqual(symbolic, result.SymbolicError);
    }

    [TestMethod]
    public async Task RejectedNativeCodeSurvivesPowerExecutorExceptionWithoutHResultConversion()
    {
        using var fixture = new ControllerFixture();
        fixture.Native.Results.Enqueue(ShutdownNativeConstants.ErrorAccessDenied);
        var executor = new PowerActionExecutor(fixture.Controller);

        var exception = await Assert.ThrowsAsync<ShutdownRequestRejectedException>(
            () => executor.ExecuteAsync(PowerActionType.Shutdown));

        Assert.AreEqual(ShutdownNativeConstants.ErrorAccessDenied, exception.NativeErrorCode);
        Assert.AreEqual("ERROR_ACCESS_DENIED", exception.Result.SymbolicError);
    }

    [TestMethod]
    public async Task FallbackIsImmediateAndOverridesAnAlreadyScheduledShutdown()
    {
        using var fixture = new ControllerFixture();
        fixture.Native.Results.Enqueue(ShutdownNativeConstants.ErrorShutdownIsScheduled);
        fixture.Native.Results.Enqueue(ShutdownNativeConstants.ErrorSuccess);

        var result = await fixture.Controller.ForceShutdownAsync();

        Assert.IsTrue(result.Accepted);
        Assert.AreEqual(0u, result.Request.GraceSeconds);
        Assert.HasCount(2, result.Attempts);
        Assert.AreEqual(0u, result.Attempts[0].Flags & ShutdownNativeConstants.ShutdownGraceOverride);
        Assert.AreNotEqual(0u, result.Attempts[1].Flags & ShutdownNativeConstants.ShutdownGraceOverride);
        Assert.AreEqual(0u, result.Attempts[1].Flags & ShutdownNativeConstants.ShutdownHybrid);
    }

    [TestMethod]
    public async Task FallbackInProgressIsPendingEvidenceInsteadOfFailure()
    {
        using var fixture = new ControllerFixture();
        fixture.Native.Results.Enqueue(ShutdownNativeConstants.ErrorShutdownInProgress);

        var result = await fixture.Controller.ForceShutdownAsync();

        Assert.IsTrue(result.Accepted);
        Assert.AreEqual(ShutdownDisposition.AlreadyInProgress, result.Disposition);
        Assert.AreEqual(ShutdownNativeConstants.ErrorShutdownInProgress, result.NativeErrorCode);
    }

    [TestMethod]
    public async Task FallbackStillScheduledAfterOverrideRemainsPendingEvidence()
    {
        using var fixture = new ControllerFixture();
        fixture.Native.Results.Enqueue(ShutdownNativeConstants.ErrorShutdownIsScheduled);
        fixture.Native.Results.Enqueue(ShutdownNativeConstants.ErrorShutdownIsScheduled);

        var result = await fixture.Controller.ForceShutdownAsync();

        Assert.IsTrue(result.Accepted);
        Assert.AreEqual(ShutdownDisposition.ScheduledPending, result.Disposition);
        Assert.AreEqual(ShutdownNativeConstants.ErrorShutdownIsScheduled, result.NativeErrorCode);
        Assert.HasCount(2, result.Attempts);
    }

    [TestMethod]
    public async Task FormatMessageFailureStillPreservesRawCode()
    {
        using var fixture = new ControllerFixture();
        fixture.Native.FormatMessages = false;
        fixture.Native.Results.Enqueue(987654u);

        var result = await fixture.Controller.StartGracefulShutdownAsync();

        Assert.AreEqual(987654u, result.NativeErrorCode);
        Assert.AreEqual("WIN32_ERROR_987654", result.SymbolicError);
        Assert.AreEqual("Win32 error 987654", result.SystemMessage);
    }

    [TestMethod]
    public void PrivilegeAdjustmentSuccessRequiresErrorSuccess()
    {
        var result = ShutdownPrivilegeEvaluator.Adjustment(
            true,
            ShutdownNativeConstants.ErrorSuccess,
            false,
            code => $"message-{code}");

        Assert.IsNotNull(result.HasPrivilege);
        Assert.IsTrue(result.HasPrivilege.Value);
        Assert.IsTrue(result.Enabled);
        Assert.IsNotNull(result.WasEnabled);
        Assert.IsFalse(result.WasEnabled.Value);
        Assert.AreEqual(ShutdownNativeConstants.ErrorSuccess, result.NativeErrorCode);
    }

    [TestMethod]
    public void AdjustTokenPrivilegesTrueWithNotAllAssignedIsFailure()
    {
        var result = ShutdownPrivilegeEvaluator.Adjustment(
            true,
            ShutdownNativeConstants.ErrorNotAllAssigned,
            false,
            code => $"message-{code}");

        Assert.IsNotNull(result.HasPrivilege);
        Assert.IsFalse(result.HasPrivilege.Value);
        Assert.IsFalse(result.Enabled);
        Assert.AreEqual(ShutdownNativeConstants.ErrorNotAllAssigned, result.NativeErrorCode);
        Assert.AreEqual("ERROR_NOT_ALL_ASSIGNED", result.SymbolicError);
    }

    [TestMethod]
    public async Task PrivilegeApiFailurePreventsNativeShutdownCall()
    {
        using var fixture = new ControllerFixture();
        fixture.Native.Privilege = ShutdownPrivilegeEvaluator.ApiFailure(
            "OpenProcessToken",
            ShutdownNativeConstants.ErrorAccessDenied,
            code => $"message-{code}");

        var result = await fixture.Controller.StartGracefulShutdownAsync();

        Assert.IsFalse(result.Accepted);
        Assert.AreEqual("OpenProcessToken", result.Privilege.Operation);
        Assert.AreEqual(ShutdownNativeConstants.ErrorAccessDenied, result.NativeErrorCode);
        Assert.IsEmpty(fixture.Native.Calls);
    }

    [TestMethod]
    public async Task DiagnosticLogContainsNoCredentialOrUserIdentity()
    {
        using var fixture = new ControllerFixture();
        fixture.Native.Results.Enqueue(ShutdownNativeConstants.ErrorSuccess);

        await fixture.Controller.StartGracefulShutdownAsync();

        var log = string.Join(Environment.NewLine, Directory.EnumerateFiles(fixture.LogDirectory).Select(File.ReadAllText));
        StringAssert.Contains(log, "api=InitiateShutdownW");
        Assert.IsFalse(log.Contains("password", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(log.Contains("credential", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(log.Contains(Environment.UserName, StringComparison.OrdinalIgnoreCase));
    }

    private sealed class ControllerFixture : IDisposable
    {
        private readonly string _parent;

        public ControllerFixture()
        {
            _parent = Path.Combine(Path.GetTempPath(), "AutoPower.Tests", Guid.NewGuid().ToString("N"));
            LogDirectory = Path.Combine(_parent, "logs");
            Native = new FakeNativeApi();
            Controller = new WindowsShutdownController(Native, new TechnicalLogger(LogDirectory));
        }

        public string LogDirectory { get; }
        public FakeNativeApi Native { get; }
        public WindowsShutdownController Controller { get; }

        public void Dispose()
        {
            if (Directory.Exists(_parent))
            {
                Directory.Delete(_parent, true);
            }
        }
    }

    private sealed class FakeNativeApi : IWindowsShutdownNativeApi
    {
        public FakeNativeApi()
        {
            Privilege = ShutdownPrivilegeEvaluator.Adjustment(
                true,
                ShutdownNativeConstants.ErrorSuccess,
                false,
                FormatMessage);
        }

        public Queue<uint> Results { get; } = new();
        public List<(uint Grace, uint Flags, uint Reason)> Calls { get; } = [];
        public bool FormatMessages { get; set; } = true;
        public ShutdownPrivilegeResult Privilege { get; set; }

        public ShutdownPrivilegeResult EnableShutdownPrivilege() => Privilege;

        public uint InitiateShutdown(uint graceSeconds, uint flags, uint reason)
        {
            Calls.Add((graceSeconds, flags, reason));
            return Results.Dequeue();
        }

        public string? FormatMessage(uint errorCode) =>
            FormatMessages ? $"formatted-{errorCode}" : null;

        public ShutdownExecutionContext CaptureExecutionContext() =>
            new(1234, 0, true, true);
    }
}
