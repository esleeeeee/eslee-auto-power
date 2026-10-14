using System.Net.Http;
using AutoPower.App;
using AutoPower.Core;

namespace AutoPower.Tests;

[TestClass]
[DoNotParallelize]
public sealed class UpdateCheckTests
{
    private static readonly Version Current = new(1, 0, 6);
    private AppLanguagePreference _previousLanguage;

    [TestInitialize]
    public void InitializeLanguage()
    {
        _previousLanguage = AppText.Preference;
        AppText.SetLanguage(AppLanguagePreference.Korean);
    }

    [TestCleanup]
    public void RestoreLanguage() => AppText.SetLanguage(_previousLanguage);

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
    [DataRow(AppLanguagePreference.Korean)]
    [DataRow(AppLanguagePreference.English)]
    public void KoreanAndEnglishUpdateStringsMatchRuntimeLanguage(AppLanguagePreference preference)
    {
        AppText.SetLanguage(preference);
        var isEnglish = preference == AppLanguagePreference.English;
        Assert.AreEqual(
            isEnglish ? "You are using the latest version." : "최신 버전을 사용하고 있습니다.",
            AppText.T("최신 버전을 사용하고 있습니다."));
        Assert.AreEqual(
            isEnglish ? "A new version v1.0.7 is available." : "새 버전 v1.0.7을 사용할 수 있습니다.",
            AppText.F("새 버전 v{0}을 사용할 수 있습니다.", "1.0.7"));
        Assert.AreEqual(
            isEnglish
                ? "The update check failed. Check your network connection."
                : "업데이트 확인에 실패했습니다. 네트워크 연결을 확인하세요.",
            AppText.T("업데이트 확인에 실패했습니다. 네트워크 연결을 확인하세요."));
        Assert.AreEqual(
            isEnglish ? "Check for updates" : "업데이트 확인",
            AppText.T("업데이트 확인"));
        Assert.AreEqual(
            isEnglish ? "Open the release page" : "Release 페이지 열기",
            AppText.T("Release 페이지 열기"));
    }

    [TestMethod]
    [DataRow(AppLanguagePreference.Korean, "ko", "ko-KR", "확인")]
    [DataRow(AppLanguagePreference.English, "en", "en-US", "OK")]
    public void ExplicitLanguageSelectsCultureAndTranslation(
        AppLanguagePreference preference, string code, string cultureName, string expected)
    {
        AppText.SetLanguage(preference);

        Assert.AreEqual(preference, AppText.Preference);
        Assert.AreEqual(code, AppText.LanguageCode);
        Assert.AreEqual(preference == AppLanguagePreference.English, AppText.IsEnglish);
        Assert.AreEqual(cultureName, AppText.Culture.Name);
        Assert.AreEqual(expected, AppText.T("확인"));
        Assert.AreEqual("untranslated", AppText.T("untranslated"));
    }

    [TestMethod]
    [DataRow(null, AppLanguagePreference.System)]
    [DataRow("", AppLanguagePreference.System)]
    [DataRow("unknown", AppLanguagePreference.System)]
    [DataRow("system", AppLanguagePreference.System)]
    [DataRow("ko", AppLanguagePreference.Korean)]
    [DataRow("en", AppLanguagePreference.English)]
    public void SettingParserFallsBackToSystem(string? value, AppLanguagePreference expected) =>
        Assert.AreEqual(expected, AppLanguageSetting.Parse(value));

    [TestMethod]
    [DataRow(AppLanguagePreference.System, "system")]
    [DataRow(AppLanguagePreference.Korean, "ko")]
    [DataRow(AppLanguagePreference.English, "en")]
    public void SettingValuesRoundTrip(AppLanguagePreference preference, string expected)
    {
        Assert.AreEqual(expected, AppLanguageSetting.ToSettingValue(preference));
        Assert.AreEqual(preference, AppLanguageSetting.Parse(expected));
    }

    [TestMethod]
    public void SystemLanguageUsesOsUiCultureAndRemainsStableAfterApplyCulture()
    {
        var culture = System.Globalization.CultureInfo.CurrentCulture;
        var uiCulture = System.Globalization.CultureInfo.CurrentUICulture;
        var defaultCulture = System.Globalization.CultureInfo.DefaultThreadCurrentCulture;
        var defaultUiCulture = System.Globalization.CultureInfo.DefaultThreadCurrentUICulture;
        var expected = System.Globalization.CultureInfo.InstalledUICulture.TwoLetterISOLanguageName == "ko" ? "ko" : "en";
        try
        {
            AppText.SetLanguage(AppLanguagePreference.System);
            Assert.AreEqual(expected, AppText.LanguageCode);

            AppText.SetLanguage(expected == "ko" ? AppLanguagePreference.English : AppLanguagePreference.Korean);
            AppText.ApplyCulture();
            AppText.SetLanguage(AppLanguagePreference.System);
            Assert.AreEqual(expected, AppText.LanguageCode);
            AppText.ApplyCulture();
            AppText.SetLanguage(AppLanguagePreference.System);
            Assert.AreEqual(expected, AppText.LanguageCode);
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = culture;
            System.Globalization.CultureInfo.CurrentUICulture = uiCulture;
            System.Globalization.CultureInfo.DefaultThreadCurrentCulture = defaultCulture;
            System.Globalization.CultureInfo.DefaultThreadCurrentUICulture = defaultUiCulture;
        }
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
