using AutoPower.Windows;

namespace AutoPower.Tests;

[TestClass]
public sealed class InstallationLayoutTests
{
    [TestMethod]
    public void BuildOutputContainsEveryExecutableInstallationLayoutExpects()
    {
        // ScheduleCoordinator launches the elevated helper from the directory of the running
        // app. The build output must therefore always ship AutoPower.Helper.exe and
        // AutoPower.Agent.exe beside the app, exactly like the installed layout; a missing
        // file here is the v1.0.6 "관리자 권한 도우미를 찾을 수 없습니다" regression.
        var layout = InstallationLayout.FromBaseDirectory(AppContext.BaseDirectory);

        Assert.IsTrue(File.Exists(layout.AppPath), $"Missing {layout.AppPath}");
        Assert.IsTrue(File.Exists(layout.HelperPath), $"Missing {layout.HelperPath}");
        Assert.IsTrue(File.Exists(layout.AgentPath), $"Missing {layout.AgentPath}");
    }
}
