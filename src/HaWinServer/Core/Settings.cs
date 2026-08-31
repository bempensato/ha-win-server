using System.Text.Json;
using System.Text.Json.Serialization;

namespace HaWinServer.Core;

/// <summary>
/// One Home Assistant instance: its own container, its own config directory,
/// its own port, and its own pinned image version. Everything that identifies
/// an instance on disk or to podman is DERIVED from <see cref="Id"/> here, so
/// there is exactly one place where those names are formed.
/// </summary>
public sealed class InstanceSettings
{
    /// <summary>Immutable slug ([a-z0-9-]) - part of the container name and the config path. Renaming changes Name, never this.</summary>
    public string Id { get; set; } = "";

    /// <summary>Display label shown in the menu. Free text, renameable.</summary>
    public string Name { get; set; } = "";

    public int Port { get; set; } = 8123;

    /// <summary>true = 0.0.0.0 (LAN reachable), false = 127.0.0.1 (localhost only).</summary>
    public bool BindAllInterfaces { get; set; } = true;

    /// <summary>
    /// True for the very first instance, which keeps the single-instance
    /// layout this app used before multi-instance support: container
    /// "hawinserver-hass" and config dir /root/hawinserver/config. That is
    /// what makes the upgrade free - an existing, populated Home Assistant is
    /// adopted in place, with no data move and no container recreation.
    /// </summary>
    public bool UseLegacyPaths { get; set; }

    /// <summary>
    /// The image tag this instance runs. Deliberately a CONCRETE version
    /// ("2026.8.3"), never "stable": a moving tag is shared state, so pulling
    /// it to test an update on one instance would silently move every other
    /// instance to that version on its next restart. "stable" only appears
    /// here between a fresh pull and the pin that immediately follows it
    /// (see TrayContext.PinImageVersionsAsync).
    /// </summary>
    public string ImageTag { get; set; } = WslManager.StableTag;

    /// <summary>
    /// USB devices handed to this instance's container, as stable
    /// /dev/serial/by-id/... paths. A serial coordinator can only be opened by
    /// one process, so the app enforces that no device is assigned to more
    /// than one instance - see TrayContext.AssignUsbDeviceAsync.
    /// </summary>
    public List<string> UsbDevices { get; set; } = new();

    // ---- Cloudflare Tunnel: remote access for this instance ------------------
    // See TunnelSettings for the machine-level (one per machine) side of this.
    // Everything here is either user input (Hostname, AccessEnabled,
    // AccessEmails) or a cache of Cloudflare's own state (ZoneId,
    // DnsRecordId, AccessAppId) kept so the menu doesn't need an API round
    // trip just to show status - re-resolved from Cloudflare, not trusted
    // blindly, if a sync call gets a 404 back for a cached id.

    /// <summary>Whether this instance has a Public Hostname configured and should appear in the tunnel's ingress rules.</summary>
    public bool TunnelEnabled { get; set; }

    /// <summary>The fully-qualified Public Hostname, e.g. "casa.pc-salotto.example.com".</summary>
    public string? Hostname { get; set; }

    /// <summary>Cached Cloudflare zone id the hostname's DNS record lives in.</summary>
    public string? ZoneId { get; set; }

    /// <summary>Cached id of the CNAME record backing this hostname.</summary>
    public string? DnsRecordId { get; set; }

    /// <summary>Whether a Cloudflare Access application gates this hostname.</summary>
    public bool AccessEnabled { get; set; }

    /// <summary>Email addresses allowed through Access's one-time-PIN challenge, when AccessEnabled.</summary>
    public List<string> AccessEmails { get; set; } = new();

    /// <summary>Cached id of the Access application created for this hostname.</summary>
    public string? AccessAppId { get; set; }

    /// <summary>
    /// True once the opt-in "# BEGIN HaWinServer" block has been written into
    /// this instance's configuration.yaml - see HaConfigPatcher. Drives
    /// whether "Remove Home Assistant proxy settings" is offered.
    /// </summary>
    public bool ProxyConfigApplied { get; set; }

    [JsonIgnore]
    public string ContainerName =>
        UseLegacyPaths ? WslManager.LegacyContainerName : $"{WslManager.LegacyContainerName}-{Id}";

    [JsonIgnore]
    public string LinuxConfigDir =>
        UseLegacyPaths ? WslManager.LegacyConfigDir : $"{WslManager.LinuxInstancesRoot}/{Id}/config";

    /// <summary>
    /// What "delete this instance entirely" removes. The legacy instance has
    /// no directory of its own above its config dir (that level also holds
    /// unrelated state), so there it is the config dir itself.
    /// </summary>
    [JsonIgnore]
    public string LinuxInstanceDir =>
        UseLegacyPaths ? WslManager.LegacyConfigDir : $"{WslManager.LinuxInstancesRoot}/{Id}";

    /// <summary>The config dir as Windows sees it - actually inside the WSL distro's filesystem.</summary>
    [JsonIgnore]
    public string WindowsConfigDir => WslManager.LinuxPathToUnc(LinuxConfigDir);

    /// <summary>Home Assistant writes this itself inside its config dir.</summary>
    [JsonIgnore]
    public string HomeAssistantLogFile => Path.Combine(WindowsConfigDir, "home-assistant.log");

    [JsonIgnore]
    public string ImageRef => $"{WslManager.ImageRepo}:{ImageTag}";
}

/// <summary>
/// Persisted app configuration: the list of instances, plus which one the
/// tray icon's double-click targets. Plain JSON, no external dependency.
/// Anything that mirrors OS state (e.g. "run at login") is deliberately NOT
/// stored here - it is read live from the registry so the menu can never
/// drift from reality. See AutoStart.
/// </summary>
public sealed class Settings
{
    public List<InstanceSettings> Instances { get; set; } = new();

    public string? SelectedInstanceId { get; set; }

    /// <summary>This machine's single Cloudflare Tunnel connector - see TunnelSettings.</summary>
    public TunnelSettings Tunnel { get; set; } = new();

    // Pre-multi-instance fields, read once so an existing settings.json
    // migrates into Instances[0] instead of being silently reset to defaults.
    // Written back as absent (never as null) once migration has happened.
    [JsonPropertyName("Port")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? LegacyPort { get; set; }

    [JsonPropertyName("BindAllInterfaces")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? LegacyBindAllInterfaces { get; set; }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    public static Settings Load()
    {
        Settings settings;
        try
        {
            settings = File.Exists(AppPaths.SettingsFile)
                ? JsonSerializer.Deserialize<Settings>(File.ReadAllText(AppPaths.SettingsFile), JsonOptions) ?? new Settings()
                : new Settings();
        }
        catch (Exception)
        {
            // Corrupt or unreadable settings file: fall back to defaults rather
            // than crash the tray app on startup.
            settings = new Settings();
        }

        try
        {
            if (settings.Migrate())
            {
                settings.Save();
            }
        }
        catch (Exception)
        {
            // A failed migration must not be fatal either: an in-memory default
            // instance still points at the legacy paths, which is exactly where
            // an existing install already lives.
            settings.Instances ??= new List<InstanceSettings>();
            if (settings.Instances.Count == 0)
            {
                settings.Instances.Add(CreateFirstInstance());
            }
        }

        return settings;
    }

    /// <summary>Returns true if anything changed and the file should be rewritten.</summary>
    private bool Migrate()
    {
        var changed = false;

        // A hand-edited settings.json can legitimately deserialize this as
        // null; everything below (and the whole app) assumes a list.
        if (Instances is null)
        {
            Instances = new List<InstanceSettings>();
            changed = true;
        }

        if (Instances.Count == 0)
        {
            var first = CreateFirstInstance();
            first.Port = LegacyPort ?? first.Port;
            first.BindAllInterfaces = LegacyBindAllInterfaces ?? first.BindAllInterfaces;
            Instances.Add(first);
            changed = true;
        }

        if (LegacyPort is not null || LegacyBindAllInterfaces is not null)
        {
            LegacyPort = null;
            LegacyBindAllInterfaces = null;
            changed = true;
        }

        if (SelectedInstanceId is null || Find(SelectedInstanceId) is null)
        {
            SelectedInstanceId = Instances[0].Id;
            changed = true;
        }

        return changed;
    }

    private static InstanceSettings CreateFirstInstance() => new()
    {
        Id = "main",
        Name = "Main",
        UseLegacyPaths = true,
    };

    public InstanceSettings? Find(string id) =>
        Instances.FirstOrDefault(i => i.Id.Equals(id, StringComparison.Ordinal));

    public bool IsPortInUse(int port, string? exceptInstanceId = null) =>
        Instances.Any(i => i.Port == port && !i.Id.Equals(exceptInstanceId, StringComparison.Ordinal));

    /// <summary>First free port at or above the highest one already assigned.</summary>
    public int SuggestFreePort()
    {
        var port = Instances.Count == 0 ? 8123 : Instances.Max(i => i.Port) + 1;
        while (port < 65535 && IsPortInUse(port))
        {
            port++;
        }
        return port;
    }

    /// <summary>
    /// Turns a display name into an id that is safe both as a Linux path
    /// segment and as a container name, and unique across instances. The
    /// restricted character set here is also the first half of the guard on
    /// every rm -rf this app runs - see WslManager.AssertDeletablePath.
    /// </summary>
    public string MakeUniqueId(string name)
    {
        var slug = new string(name.ToLowerInvariant()
            .Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-')
            .ToArray())
            .Trim('-');

        while (slug.Contains("--", StringComparison.Ordinal))
        {
            slug = slug.Replace("--", "-", StringComparison.Ordinal);
        }

        if (slug.Length > 32) slug = slug[..32].Trim('-');
        if (slug.Length == 0) slug = "instance";

        var candidate = slug;
        var suffix = 2;
        while (Find(candidate) is not null)
        {
            candidate = $"{slug}-{suffix++}";
        }

        return candidate;
    }

    public void Save()
    {
        AppPaths.EnsureCreated();
        var json = JsonSerializer.Serialize(this, JsonOptions);
        File.WriteAllText(AppPaths.SettingsFile, json);
    }
}
