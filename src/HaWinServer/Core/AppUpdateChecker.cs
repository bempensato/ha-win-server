using System.Text.Json;

namespace HaWinServer.Core;

/// <summary>
/// One GitHub release, reduced to what the updater needs. Requires both a
/// bare exe asset and its sha256 sidecar (both produced by
/// .github/workflows/release.yml) to be a candidate at all - a release
/// missing either is not something this app can self-update to.
/// </summary>
public sealed record AppRelease(
    string TagName,
    string Version,
    bool IsPrerelease,
    string HtmlUrl,
    string ReleaseNotes,
    string ExeUrl,
    string Sha256Url,
    DateTimeOffset PublishedAt);

public sealed record AppUpdateCheckResult(string InstalledVersion, AppRelease? Latest, bool UpdateAvailable);

/// <summary>
/// Checks GitHub Releases for this app's own updates - the counterpart to
/// UpdateChecker (which is about Home Assistant's version, via PyPI). Same
/// shape: a static HttpClient, no NuGet dependency, "no update info" treated
/// as non-fatal everywhere.
/// </summary>
public static class AppUpdateChecker
{
    private const string RepoApiBase = "https://api.github.com/repos/bempensato/ha-win-server";
    private const string ExeAssetName = "HaWinServer.exe";
    private const string Sha256AssetName = "HaWinServer.exe.sha256";

    private static readonly HttpClient Client = new()
    {
        Timeout = TimeSpan.FromSeconds(10),
    };

    static AppUpdateChecker()
    {
        Client.DefaultRequestHeaders.UserAgent.ParseAdd("HaWinServer/1.0 (+tray app)");
        Client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        Client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
    }

    /// <summary>
    /// Stable channel: GitHub's own "latest" endpoint, which already
    /// excludes drafts and prereleases. Beta channel: the newest release
    /// (by AppVersion.Compare, not by publish date) across the most recent
    /// releases, prerelease included - a stable patch published after a beta
    /// tag must still win.
    /// </summary>
    public static async Task<AppRelease?> GetLatestAsync(bool includePrereleases, CancellationToken ct = default)
    {
        try
        {
            if (!includePrereleases)
            {
                using var stream = await Client.GetStreamAsync($"{RepoApiBase}/releases/latest", ct);
                using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
                return ParseRelease(doc.RootElement);
            }

            using var listStream = await Client.GetStreamAsync($"{RepoApiBase}/releases?per_page=20", ct);
            using var listDoc = await JsonDocument.ParseAsync(listStream, cancellationToken: ct);

            AppRelease? best = null;
            foreach (var element in listDoc.RootElement.EnumerateArray())
            {
                if (element.TryGetProperty("draft", out var draft) && draft.GetBoolean()) continue;

                var release = ParseRelease(element);
                if (release is null) continue;

                if (best is null || AppVersion.Compare(release.Version, best.Version) > 0)
                {
                    best = release;
                }
            }

            return best;
        }
        catch (Exception)
        {
            // Network hiccup, rate limit, or schema change - "no update info" is not fatal.
            return null;
        }
    }

    public static async Task<AppUpdateCheckResult?> CheckAsync(
        bool includePrereleases, CancellationToken ct = default)
    {
        var latest = await GetLatestAsync(includePrereleases, ct);
        if (latest is null) return null;

        var installed = AppVersion.Current;
        var updateAvailable = !AppVersion.IsDevelopmentBuild
            && AppVersion.Compare(latest.Version, installed) > 0;

        return new AppUpdateCheckResult(installed, latest, updateAvailable);
    }

    /// <summary>Null when the release has no usable exe+sha256 asset pair - not a valid self-update candidate.</summary>
    private static AppRelease? ParseRelease(JsonElement element)
    {
        var tagName = element.GetProperty("tag_name").GetString();
        if (string.IsNullOrWhiteSpace(tagName)) return null;

        string? exeUrl = null;
        string? sha256Url = null;

        foreach (var asset in element.GetProperty("assets").EnumerateArray())
        {
            var name = asset.GetProperty("name").GetString();
            var url = asset.GetProperty("browser_download_url").GetString();
            if (name == ExeAssetName) exeUrl = url;
            else if (name == Sha256AssetName) sha256Url = url;
        }

        if (exeUrl is null || sha256Url is null) return null;

        var version = tagName.StartsWith('v') ? tagName[1..] : tagName;

        return new AppRelease(
            TagName: tagName,
            Version: version,
            IsPrerelease: element.TryGetProperty("prerelease", out var pre) && pre.GetBoolean(),
            HtmlUrl: element.GetProperty("html_url").GetString() ?? $"https://github.com/bempensato/ha-win-server/releases/tag/{tagName}",
            ReleaseNotes: element.TryGetProperty("body", out var body) ? body.GetString() ?? "" : "",
            ExeUrl: exeUrl,
            Sha256Url: sha256Url,
            PublishedAt: element.TryGetProperty("published_at", out var published) && published.TryGetDateTimeOffset(out var dto)
                ? dto
                : DateTimeOffset.UtcNow);
    }
}
