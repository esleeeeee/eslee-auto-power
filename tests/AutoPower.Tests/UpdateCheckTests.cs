using System.Net.Http;
using AutoPower.App;
using AutoPower.Core;

namespace AutoPower.Tests;

[TestClass]
public sealed class UpdateCheckTests
{
    private static readonly Version Current = new(1, 0, 6);

    [TestMethod]
    [DataRow("v1.0.7", UpdateCheckStatus.UpdateAvailable)]
    [DataRow("v1.1.0", UpdateCheckStatus.UpdateAvailable)]
    [DataRow("v2.0.0", UpdateCheckStatus.UpdateAvailable)]
    [DataRow("v1.0.6", UpdateCheckStatus.UpToDate)]
    [DataRow("v1.0.5", UpdateCheckStatus.UpToDate)]
    [DataRow("1.0.7", UpdateCheckStatus.UpdateAvailable)]
    public void EvaluateComparesLatestReleaseTagWithCurrentVersion(string tag, UpdateCheckStatus expected)
    {
        var result = UpdateCheckPolicy.Evaluate(Current, ReleaseJson(tag));

        Assert.AreEqual(expected, result.Status);
        Assert.AreEqual("https://github.com/esleeeeee/eslee-auto-power/releases/tag/" + tag, result.ReleaseUrl);
    }

    [TestMethod]
    public void EvaluateNeverOffersDraftOrPrereleaseBuilds()
    {
        var draft = UpdateCheckPolicy.Evaluate(Current, ReleaseJson("v9.9.9", draft: true));
        var prerelease = UpdateCheckPolicy.Evaluate(Current, ReleaseJson("v9.9.9", prerelease: true));

        Assert.AreEqual(UpdateCheckStatus.Failed, draft.Status);
        Assert.AreEqual(UpdateCheckStatus.Failed, prerelease.Status);
        Assert.IsNull(draft.LatestVersion);
        Assert.IsNull(prerelease.LatestVersion);
    }

    [TestMethod]
    public void EvaluateTreatsMissingOrInvalidTagAsFailure()
    {
        Assert.AreEqual(UpdateCheckStatus.Failed, UpdateCheckPolicy.Evaluate(Current, "{}").Status);
        Assert.AreEqual(
            UpdateCheckStatus.Failed,
            UpdateCheckPolicy.Evaluate(Current, """{"tag_name":"latest","draft":false,"prerelease":false}""").Status);
    }

    [TestMethod]
    public void ParseCurrentVersionPrefersInformationalVersionWithoutBuildMetadata()
    {
        Assert.AreEqual(new Version(1, 0, 6), UpdateCheckPolicy.ParseCurrentVersion("1.0.6+abcdef012345", new Version(9, 9, 9, 9)));
        Assert.AreEqual(new Version(1, 0, 6), UpdateCheckPolicy.ParseCurrentVersion(null, new Version(1, 0, 6, 0)));
        Assert.AreEqual(new Version(0, 0, 0), UpdateCheckPolicy.ParseCurrentVersion(null, null));
    }

    [TestMethod]
    public async Task ServiceTurnsNetworkFailureIntoFailedResultWithoutThrowing()
    {
        var logs = new List<string>();
        using var service = new UpdateCheckService(
            Current,
            (eventName, _) => logs.Add(eventName),
            _ => Task.FromException<string>(new HttpRequestException("offline")));

        var result = await service.CheckNowAsync();

        Assert.AreEqual(UpdateCheckStatus.Failed, result.Status);
        Assert.AreEqual(result, service.Latest);
        Assert.Contains("update-check.failed", logs);
    }

    [TestMethod]
    public async Task ServicePublishesLatestResultAndRaisesUpdatedEvent()
    {
        var received = new List<UpdateCheckResult>();
        using var service = new UpdateCheckService(
            Current,
            (_, _) => { },
            _ => Task.FromResult(ReleaseJson("v1.0.7")));
        service.Updated += received.Add;

        var result = await service.CheckNowAsync();

        Assert.AreEqual(UpdateCheckStatus.UpdateAvailable, result.Status);
        Assert.AreEqual(new Version(1, 0, 7), result.LatestVersion);
        Assert.AreEqual(result, service.Latest);
        Assert.AreEqual(result, received.Single());
    }

    [TestMethod]
    public void KoreanAndEnglishUpdateStringsMatchSelectedBuildLanguage()
    {
        Assert.AreEqual(
            AppText.IsEnglish ? "You are using the latest version." : "최신 버전을 사용하고 있습니다.",
            AppText.T("최신 버전을 사용하고 있습니다."));
        Assert.AreEqual(
            AppText.IsEnglish ? "A new version v1.0.7 is available." : "새 버전 v1.0.7을 사용할 수 있습니다.",
            AppText.F("새 버전 v{0}을 사용할 수 있습니다.", "1.0.7"));
        Assert.AreEqual(
            AppText.IsEnglish
                ? "The update check failed. Check your network connection."
                : "업데이트 확인에 실패했습니다. 네트워크 연결을 확인하세요.",
            AppText.T("업데이트 확인에 실패했습니다. 네트워크 연결을 확인하세요."));
        Assert.AreEqual(
            AppText.IsEnglish ? "Check for updates" : "업데이트 확인",
            AppText.T("업데이트 확인"));
        Assert.AreEqual(
            AppText.IsEnglish ? "Open the release page" : "Release 페이지 열기",
            AppText.T("Release 페이지 열기"));
    }

    private static string ReleaseJson(string tag, bool draft = false, bool prerelease = false) =>
        $$"""
        {
            "tag_name": "{{tag}}",
            "draft": {{(draft ? "true" : "false")}},
            "prerelease": {{(prerelease ? "true" : "false")}},
            "html_url": "https://github.com/esleeeeee/eslee-auto-power/releases/tag/{{tag}}"
        }
        """;
}
