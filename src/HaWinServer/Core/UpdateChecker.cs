using System.Text.Json;

namespace HaWinServer.Core;

public sealed record UpdateCheckResult(string InstalledVersion, string LatestVersion, bool UpdateAvailable);

/// <summary>
/// Checks PyPI directly for the latest published homeassistant version.
/// No package manager lock-in, no extra dependency - just the same JSON API
/// pip/pipx themselves use to resolve versions.
/// </summary>
public static class UpdateChecker
{
    private static readonly HttpClient Client = new()
    {
        Timeout = TimeSpan.FromSeconds(10),
    };

    static UpdateChecker()
    {
        Client.DefaultRequestHeaders.UserAgent.ParseAdd("HaWinServer/1.0 (+tray app)");
    }

    public static async Task<string?> GetLatestVersionAsync(CancellationToken ct = default)
    {
        try
        {
            using var stream = await Client.GetStreamAsync("https://pypi.org/pypi/homeassistant/json", ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            return doc.RootElement.GetProperty("info").GetProperty("version").GetString();
        }
        catch (Exception)
        {
            // Network hiccup or PyPI schema change - "no update info" is not fatal.
            return null;
        }
    }

    public static async Task<UpdateCheckResult?> CheckAsync(string installedVersion, CancellationToken ct = default)
    {
        var latest = await GetLatestVersionAsync(ct);
        if (latest is null) return null;

        var updateAvailable = Version.TryParse(NormalizeForComparison(installedVersion), out var installed)
            && Version.TryParse(NormalizeForComparison(latest), out var latestParsed)
            && latestParsed > installed;

        return new UpdateCheckResult(installedVersion, latest, updateAvailable);
    }

    /// <summary>
    /// HA versions look like "2026.8.3" (calendar versioning, always 3 parts) -
    /// System.Version parses that directly, but guard against occasional
    /// pre-release suffixes like "2026.9.0.dev0" by trimming anything after
    /// the third numeric segment.
    /// </summary>
    private static string NormalizeForComparison(string version)
    {
        var parts = version.Split('.');
        return parts.Length >= 3 ? string.Join('.', parts[0], parts[1], parts[2]) : version;
    }
}
