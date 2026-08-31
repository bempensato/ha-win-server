namespace HaWinServer.Core;

/// <summary>
/// This app's own update preferences - separate from Home Assistant version
/// tracking (InstanceSettings.ImageTag) and from Cloudflare Tunnel settings.
/// No secrets here, same rule as the rest of Settings.
/// </summary>
public sealed class AppUpdateSettings
{
    public bool AutoCheck { get; set; } = true;

    /// <summary>When true, "latest" also considers prerelease ("-beta.N") tags.</summary>
    public bool IncludePrereleases { get; set; }

    public DateTimeOffset? LastCheckUtc { get; set; }

    /// <summary>The version last announced via a tray balloon, so a still-pending update isn't re-announced every day.</summary>
    public string? LastNotifiedVersion { get; set; }

    /// <summary>Version the user explicitly chose to skip via "Skip This Version".</summary>
    public string? SkippedVersion { get; set; }
}
