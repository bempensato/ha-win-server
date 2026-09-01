using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace HaWinServer.Core;

/// <summary>
/// Opt-in, reversible writer for the two Home Assistant settings a Cloudflare
/// Tunnel origin needs: <c>http.use_x_forwarded_for</c>/<c>trusted_proxies</c>
/// (without them HA sees every visitor as the podman gateway IP, so its login
/// failure ban locks out the proxy itself and every real user behind it) and
/// <c>homeassistant.external_url</c>.
///
/// This reintroduces, on purpose, the "marked block in configuration.yaml"
/// mechanism this app deliberately removed when it dropped the old
/// pip/venv install (see MenuBuilder's Network submenu comment and
/// TrayContext.ChangePortAndBindAsync's doc comment). The difference that
/// makes it acceptable this time: it is opt-in (never runs on its own),
/// restricted to exactly two top-level keys, always backed up first, and
/// automatically rolled back if Home Assistant fails to come back up -
/// see <see cref="ApplyAsync"/>.
///
/// The project has no YAML parser (zero NuGet packages is a stated design
/// constraint - see HaWinServer.csproj), so "does this key already exist" is
/// answered with a deliberately narrow regex anchored to column 0
/// (top-level YAML keys are never indented), not a real parse. That is
/// exactly why a key already present elsewhere is never overwritten: it is
/// surfaced to the dialog as a conflict to merge by hand instead.
///
/// The YAML block alone is NOT always sufficient, though - confirmed on a
/// real instance whose config directory came from a restored backup: once
/// <c>.storage/http</c> exists, Home Assistant's http component reads
/// trusted_proxies/use_x_forwarded_for from THERE at every boot and never
/// re-imports configuration.yaml's http: block (its own
/// <c>yaml_migration_done</c> flag stayed false, and the stored proxy list
/// still had corrupted /32-narrowed CIDRs from whatever import happened
/// originally). So <see cref="ApplyAsync"/> also patches that JSON file
/// directly when present - see <see cref="PatchTrustedProxyInStorageAsync"/>.
/// This is inherently more fragile than the YAML edit (it depends on an
/// internal HA storage schema, not a documented public format), which is
/// exactly why it is additive and defensive: parse failure or an unexpected
/// shape just skips the storage patch and falls back to YAML-only,
/// rather than risking a corrupted file.
/// </summary>
public static class HaConfigPatcher
{
    public const string BeginMarker = "# BEGIN HaWinServer - managed block, do not edit by hand";
    public const string EndMarker = "# END HaWinServer";

    /// <summary>Anchored at column 0 (RegexOptions.Multiline's line-start), which is exactly what makes a YAML key top-level rather than nested.</summary>
    private static readonly Regex TopLevelKeyPattern =
        new(@"^(http|homeassistant)\s*:", RegexOptions.Multiline | RegexOptions.Compiled);

    public static string ConfigYamlPath(InstanceSettings instance) =>
        Path.Combine(instance.WindowsConfigDir, "configuration.yaml");

    public static string HttpStoragePath(InstanceSettings instance) =>
        Path.Combine(instance.WindowsConfigDir, ".storage", "http");

    public sealed record Analysis(
        string ContentWithoutManagedBlock,
        bool HttpKeyPresentElsewhere,
        bool HomeAssistantKeyPresentElsewhere,
        bool HasManagedBlock);

    /// <summary>
    /// Strips this app's own managed block first, then checks what remains
    /// for a conflicting top-level key - so re-running this on an instance
    /// that already has the block (e.g. to change the hostname) never
    /// reports a conflict against its own previous write.
    /// </summary>
    public static Analysis Analyze(string yamlContent)
    {
        var hadBlock = yamlContent.Contains(BeginMarker, StringComparison.Ordinal);
        var withoutBlock = RemoveManagedBlock(yamlContent);

        var presentKeys = TopLevelKeyPattern.Matches(withoutBlock)
            .Select(m => m.Groups[1].Value.ToLowerInvariant())
            .ToHashSet(StringComparer.Ordinal);

        return new Analysis(
            withoutBlock,
            presentKeys.Contains("http"),
            presentKeys.Contains("homeassistant"),
            hadBlock);
    }

    /// <summary>Detects the podman bridge subnet HA should trust as a proxy - see the "Home Assistant proxy settings" dialog.</summary>
    public static async Task<string> DetectTrustedProxyCidrAsync(CancellationToken ct = default)
    {
        const string fallback = "10.88.0.0/16"; // podman's own default bridge subnet

        try
        {
            var result = await WslManager.RunScriptAsRootAsync(
                "podman network inspect podman --format '{{range .Subnets}}{{.Subnet}} {{end}}' 2>/dev/null || true",
                onOutputLine: null, ct);

            var subnet = result.StdOut
                .Split(new[] { ' ', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault();

            return string.IsNullOrWhiteSpace(subnet) ? fallback : subnet.Trim();
        }
        catch (Exception)
        {
            return fallback;
        }
    }

    /// <summary>Whether this instance has a pre-existing .storage/http - shown in the dialog so the user knows the storage patch will also run.</summary>
    public static bool HasHttpStorage(InstanceSettings instance) => File.Exists(HttpStoragePath(instance));

    /// <summary>Same check from a bare configuration.yaml path - for callers (the dialog) that only have the path, not the InstanceSettings.</summary>
    public static bool HasHttpStorage(string configYamlPath) =>
        File.Exists(Path.Combine(Path.GetDirectoryName(configYamlPath) ?? ".", ".storage", "http"));

    public static string BuildManagedBlock(string trustedProxyCidr, string? externalUrl, bool includeHttp, bool includeExternalUrl)
    {
        var sb = new StringBuilder();
        sb.Append(BeginMarker).Append('\n');

        if (includeHttp)
        {
            sb.Append("http:\n");
            sb.Append("  use_x_forwarded_for: true\n");
            sb.Append("  trusted_proxies:\n");
            sb.Append("    - ").Append(trustedProxyCidr).Append('\n');
        }

        if (includeExternalUrl && !string.IsNullOrWhiteSpace(externalUrl))
        {
            sb.Append("homeassistant:\n");
            sb.Append("  external_url: \"").Append(externalUrl).Append("\"\n");
        }

        sb.Append(EndMarker).Append('\n');
        return sb.ToString();
    }

    /// <summary>Removes the managed block if present; returns the content unchanged if it isn't, or if the markers are malformed (missing END).</summary>
    public static string RemoveManagedBlock(string yamlContent)
    {
        var beginIndex = yamlContent.IndexOf(BeginMarker, StringComparison.Ordinal);
        if (beginIndex < 0) return yamlContent;

        var endIndex = yamlContent.IndexOf(EndMarker, beginIndex, StringComparison.Ordinal);
        if (endIndex < 0) return yamlContent; // don't guess at a hand-edited/corrupted block

        var afterEnd = endIndex + EndMarker.Length;
        while (afterEnd < yamlContent.Length && (yamlContent[afterEnd] == '\r' || yamlContent[afterEnd] == '\n'))
        {
            afterEnd++;
        }

        return (yamlContent[..beginIndex].TrimEnd('\r', '\n') + "\n" + yamlContent[afterEnd..]).TrimEnd('\r', '\n') + "\n";
    }

    private static string ApplyBlock(string originalContent, string managedBlock)
    {
        var withoutBlock = RemoveManagedBlock(originalContent).TrimEnd('\r', '\n');
        return withoutBlock + "\n\n" + managedBlock;
    }

    private static string MakeBackupPath(string filePath) =>
        filePath + $".hawinserver-{DateTime.Now:yyyyMMdd-HHmmss}.bak";

    /// <summary>
    /// Writes the managed block (only for keys not already present elsewhere)
    /// and/or patches .storage/http (see the class doc comment for why both
    /// are needed), backs up whatever it touches first, restarts Home
    /// Assistant, and rolls every touched file back and restarts again if
    /// Home Assistant does not come back up within the same 5-minute window
    /// HassSupervisor itself uses to judge a normal start.
    /// </summary>
    public static async Task<(bool Success, string Detail)> ApplyAsync(
        HassInstance instance,
        string trustedProxyCidr,
        string? externalUrl,
        bool includeHttp,
        bool includeExternalUrl,
        Action<string>? log = null,
        CancellationToken ct = default)
    {
        var yamlPath = ConfigYamlPath(instance.Settings);
        if (!File.Exists(yamlPath))
        {
            return (false, $"configuration.yaml was not found at:\n{yamlPath}");
        }

        var original = await File.ReadAllTextAsync(yamlPath, ct);
        var analysis = Analyze(original);

        var writeHttpYaml = includeHttp && !analysis.HttpKeyPresentElsewhere;
        var writeExternalUrl = includeExternalUrl && !analysis.HomeAssistantKeyPresentElsewhere
                                && !string.IsNullOrWhiteSpace(externalUrl);

        string? yamlBackupPath = null;
        if (writeHttpYaml || writeExternalUrl)
        {
            var block = BuildManagedBlock(trustedProxyCidr, externalUrl, writeHttpYaml, writeExternalUrl);
            var newContent = ApplyBlock(original, block);

            yamlBackupPath = MakeBackupPath(yamlPath);
            File.Copy(yamlPath, yamlBackupPath, overwrite: false);
            log?.Invoke($"Backed up configuration.yaml to {Path.GetFileName(yamlBackupPath)}");

            await File.WriteAllTextAsync(yamlPath, newContent, ct);
            log?.Invoke("Wrote the HA Win Server managed block to configuration.yaml.");
            instance.Settings.ProxyConfigApplied = true;
        }
        else if (includeHttp)
        {
            log?.Invoke(
                "configuration.yaml already has an \"http:\" key elsewhere - left untouched. " +
                "Merge the shown trusted_proxies line into it by hand if .storage/http (below) doesn't exist for this instance.");
        }

        string? storageBackupPath = null;
        if (includeHttp)
        {
            var storageResult = await PatchTrustedProxyInStorageAsync(instance.Settings, trustedProxyCidr, log, ct);
            storageBackupPath = storageResult.BackupPath;
            if (storageResult.Changed) instance.Settings.ProxyConfigApplied = true;
        }

        if (yamlBackupPath is null && storageBackupPath is null)
        {
            return (false,
                "Nothing was written: the relevant YAML key(s) already exist elsewhere and there is no " +
                ".storage/http to patch. Merge the shown snippet into configuration.yaml by hand.");
        }

        log?.Invoke("Restarting Home Assistant to apply the change...");
        return await RestartAndVerifyAsync(
            instance,
            RollbackAsync: () => RollbackFilesAsync(yamlPath, yamlBackupPath, HttpStoragePath(instance.Settings), storageBackupPath),
            log, ct);
    }

    /// <summary>Removes the managed YAML block and the CIDR entry this app added to .storage/http (if any), restarts, and verifies - the reverse of ApplyAsync.</summary>
    public static async Task<(bool Success, string Detail)> RemoveAsync(
        HassInstance instance, string trustedProxyCidr, Action<string>? log = null, CancellationToken ct = default)
    {
        var yamlPath = ConfigYamlPath(instance.Settings);
        string? yamlBackupPath = null;

        if (File.Exists(yamlPath))
        {
            var original = await File.ReadAllTextAsync(yamlPath, ct);
            if (original.Contains(BeginMarker, StringComparison.Ordinal))
            {
                yamlBackupPath = MakeBackupPath(yamlPath);
                File.Copy(yamlPath, yamlBackupPath, overwrite: false);
                log?.Invoke($"Backed up configuration.yaml to {Path.GetFileName(yamlBackupPath)}");

                var newContent = RemoveManagedBlock(original);
                await File.WriteAllTextAsync(yamlPath, newContent, ct);
                log?.Invoke("Removed the HA Win Server managed block from configuration.yaml.");
            }
        }
        instance.Settings.ProxyConfigApplied = false;

        var storageResult = await RemoveTrustedProxyFromStorageAsync(instance.Settings, trustedProxyCidr, log, ct);

        if (yamlBackupPath is null && storageResult.BackupPath is null)
        {
            return (true, "No HA Win Server managed block or matching .storage/http entry was present.");
        }

        log?.Invoke("Restarting Home Assistant...");
        return await RestartAndVerifyAsync(
            instance,
            RollbackAsync: () => RollbackFilesAsync(yamlPath, yamlBackupPath, HttpStoragePath(instance.Settings), storageResult.BackupPath),
            log, ct);
    }

    // ---- .storage/http: see the class doc comment for why this exists ------------

    public sealed record StoragePatchResult(bool Changed, string? BackupPath, string Detail);

    /// <summary>
    /// Merges a trusted proxy CIDR and use_x_forwarded_for=true into
    /// .storage/http's "stable" section, if that file exists for this
    /// instance. Deliberately narrow and defensive: it only ADDS the given
    /// CIDR if missing (never removes or reorders any proxy the user or HA
    /// itself already trusts) and backs the file up first. Any unexpected
    /// shape (missing data.stable, unparsable JSON) is treated as "nothing to
    /// patch" rather than guessed at - .storage files are an internal HA
    /// format, not a stable public one, so silently doing nothing on a
    /// surprise is safer than a best-effort write that could corrupt it.
    /// </summary>
    public static async Task<StoragePatchResult> PatchTrustedProxyInStorageAsync(
        InstanceSettings instance, string trustedProxyCidr, Action<string>? log = null, CancellationToken ct = default)
    {
        var path = HttpStoragePath(instance);
        if (!File.Exists(path))
        {
            return new StoragePatchResult(false, null, "No .storage/http for this instance - nothing to patch there.");
        }

        var original = await File.ReadAllTextAsync(path, ct);
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(original);
        }
        catch (JsonException ex)
        {
            log?.Invoke(".storage/http could not be parsed as JSON - left untouched: " + ex.Message);
            return new StoragePatchResult(false, null, "Could not parse .storage/http - left untouched.");
        }

        if (root?["data"]?["stable"] is not JsonObject stable)
        {
            log?.Invoke(".storage/http has an unexpected structure (no data.stable) - left untouched.");
            return new StoragePatchResult(false, null, "Unexpected .storage/http structure - left untouched.");
        }

        var trustedProxies = stable["trusted_proxies"] as JsonArray ?? new JsonArray();
        var existing = trustedProxies
            .Select(n => n?.GetValue<string>())
            .Where(s => s is not null)
            .ToList();

        var alreadyPresent = existing.Contains(trustedProxyCidr, StringComparer.OrdinalIgnoreCase);
        var alreadyForwarding = stable["use_x_forwarded_for"]?.GetValue<bool>() == true;

        if (alreadyPresent && alreadyForwarding)
        {
            return new StoragePatchResult(false, null, ".storage/http already trusts this proxy - nothing to patch.");
        }

        var backupPath = MakeBackupPath(path);
        File.Copy(path, backupPath, overwrite: false);
        log?.Invoke($"Backed up .storage/http to {Path.GetFileName(backupPath)}");

        if (!alreadyPresent)
        {
            trustedProxies.Add(JsonValue.Create(trustedProxyCidr));
        }
        stable["trusted_proxies"] = trustedProxies;
        stable["use_x_forwarded_for"] = true;

        await File.WriteAllTextAsync(path, root!.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), ct);
        log?.Invoke($".storage/http updated: trusted_proxies now includes {trustedProxyCidr}.");

        return new StoragePatchResult(true, backupPath, $"Added {trustedProxyCidr} to .storage/http.");
    }

    /// <summary>
    /// Removes the specific CIDR this app added, if present. Deliberately
    /// does not touch use_x_forwarded_for or any other trusted_proxies entry
    /// - there is no reliable way to know whether the user relies on
    /// forwarded-header processing for something else, so turning it back
    /// off is not this app's call to make.
    /// </summary>
    public static async Task<StoragePatchResult> RemoveTrustedProxyFromStorageAsync(
        InstanceSettings instance, string trustedProxyCidr, Action<string>? log = null, CancellationToken ct = default)
    {
        var path = HttpStoragePath(instance);
        if (!File.Exists(path))
        {
            return new StoragePatchResult(false, null, "No .storage/http for this instance.");
        }

        var original = await File.ReadAllTextAsync(path, ct);
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(original);
        }
        catch (JsonException)
        {
            return new StoragePatchResult(false, null, "Could not parse .storage/http - left untouched.");
        }

        if (root?["data"]?["stable"] is not JsonObject stable || stable["trusted_proxies"] is not JsonArray trustedProxies)
        {
            return new StoragePatchResult(false, null, "Unexpected .storage/http structure - left untouched.");
        }

        var index = -1;
        for (var i = 0; i < trustedProxies.Count; i++)
        {
            if (string.Equals(trustedProxies[i]?.GetValue<string>(), trustedProxyCidr, StringComparison.OrdinalIgnoreCase))
            {
                index = i;
                break;
            }
        }
        if (index < 0)
        {
            return new StoragePatchResult(false, null, ".storage/http did not have this entry.");
        }

        var backupPath = MakeBackupPath(path);
        File.Copy(path, backupPath, overwrite: false);
        log?.Invoke($"Backed up .storage/http to {Path.GetFileName(backupPath)}");

        trustedProxies.RemoveAt(index);
        await File.WriteAllTextAsync(path, root!.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), ct);
        log?.Invoke($"Removed {trustedProxyCidr} from .storage/http.");

        return new StoragePatchResult(true, backupPath, $"Removed {trustedProxyCidr} from .storage/http.");
    }

    private static Task RollbackFilesAsync(string yamlPath, string? yamlBackupPath, string storagePath, string? storageBackupPath)
    {
        if (yamlBackupPath is not null) File.Copy(yamlBackupPath, yamlPath, overwrite: true);
        if (storageBackupPath is not null) File.Copy(storageBackupPath, storagePath, overwrite: true);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Shared tail of Apply/Remove: restart and check whether Home Assistant
    /// came back up, then if not, run the caller's rollback (restoring
    /// whichever backups it actually made) and restart once more so the
    /// worst case is self-healing rather than a stuck instance.
    ///
    /// Reads <see cref="HassSupervisor.State"/> rather than polling
    /// HealthProbe itself: StartAsync already runs that same probe (up to
    /// its own 5-minute timeout) before returning, so a second wait here
    /// would just double it - and, worse, for a start that fails
    /// synchronously (e.g. a USB device missing from WSL, checked before
    /// podman is even touched - see HassSupervisor.StartAsync) it used to
    /// burn a full 5 minutes waiting on a container that was never started,
    /// twice over once rollback retried it, all while the whole app menu
    /// stayed disabled (TrayContext._isBusy) - including "Assign USB
    /// Device...", the one action that would have fixed it.
    /// </summary>
    private static async Task<(bool Success, string Detail)> RestartAndVerifyAsync(
        HassInstance instance, Func<Task> RollbackAsync, Action<string>? log, CancellationToken ct)
    {
        await instance.Supervisor.RestartAsync(ct);

        if (instance.Supervisor.State == HassState.Running)
        {
            return (true, "Home Assistant restarted successfully.");
        }

        log?.Invoke("Home Assistant did not come back up - rolling back...");
        await RollbackAsync();
        await instance.Supervisor.RestartAsync(ct);

        return instance.Supervisor.State == HassState.Running
            ? (false, "Home Assistant did not accept the change - it has been rolled back automatically and is running again.")
            : (false, "Home Assistant did not come back up even after rolling back.\n" +
                      (instance.Supervisor.LastErrorDetail ?? "Check the Home Assistant log."));
    }
}
