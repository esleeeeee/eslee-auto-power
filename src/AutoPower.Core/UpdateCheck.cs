using System.Text.Json;

namespace AutoPower.Core;

public enum UpdateCheckStatus
{
    UpToDate,
    UpdateAvailable,
    Failed
}

public sealed record UpdateCheckResult(UpdateCheckStatus Status, Version? LatestVersion, string? ReleaseUrl)
{
    public static UpdateCheckResult Failure { get; } = new(UpdateCheckStatus.Failed, null, null);
}

/// <summary>
/// Pure evaluation logic for the GitHub latest-release update check. Network access
/// stays in the caller so every branch can be tested without real HTTP.
/// </summary>
public static class UpdateCheckPolicy
{
    public const string ReleasesPageUrl = "https://github.com/esleeeeee/eslee-auto-power/releases/latest";
    public const string LatestReleaseApiUrl = "https://api.github.com/repos/esleeeeee/eslee-auto-power/releases/latest";

    public static Version ParseCurrentVersion(string? informationalVersion, Version? assemblyVersion)
    {
        var plain = informationalVersion?.Split('+', 2)[0].Trim();
        if (!string.IsNullOrWhiteSpace(plain) && Version.TryParse(plain, out var parsed))
        {
            return Normalize(parsed);
        }

        return assemblyVersion is null ? new Version(0, 0, 0) : Normalize(assemblyVersion);
    }

    public static UpdateCheckResult Evaluate(Version currentVersion, string latestReleaseJson)
    {
        ArgumentNullException.ThrowIfNull(currentVersion);
        using var document = JsonDocument.Parse(latestReleaseJson);
        var root = document.RootElement;

        // The releases/latest endpoint already excludes drafts and prereleases; this guard
        // keeps the contract explicit even if the payload ever changes.
        if (ReadBoolean(root, "draft") || ReadBoolean(root, "prerelease"))
        {
            return UpdateCheckResult.Failure;
        }

        if (!root.TryGetProperty("tag_name", out var tagElement) || tagElement.ValueKind != JsonValueKind.String)
        {
            return UpdateCheckResult.Failure;
        }

        var tag = tagElement.GetString()!.Trim();
        var plain = tag.StartsWith('v') || tag.StartsWith('V') ? tag[1..] : tag;
        if (!Version.TryParse(plain, out var latest))
        {
            return UpdateCheckResult.Failure;
        }

        latest = Normalize(latest);
        var url = root.TryGetProperty("html_url", out var urlElement) && urlElement.ValueKind == JsonValueKind.String
            ? urlElement.GetString()
            : ReleasesPageUrl;
        return latest > Normalize(currentVersion)
            ? new UpdateCheckResult(UpdateCheckStatus.UpdateAvailable, latest, url)
            : new UpdateCheckResult(UpdateCheckStatus.UpToDate, latest, url);
    }

    private static bool ReadBoolean(JsonElement root, string property) =>
        root.TryGetProperty(property, out var element) && element.ValueKind == JsonValueKind.True;

    private static Version Normalize(Version version) =>
        new(version.Major, Math.Max(version.Minor, 0), Math.Max(version.Build, 0));
}
