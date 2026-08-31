using System.Text.RegularExpressions;

namespace HaWinServer.Core;

/// <summary>
/// Runs Home Assistant as the official "Container" installation - the image
/// Home Assistant itself builds, tests, and supports - inside a lightweight
/// container runtime hosted in a dedicated WSL2 distro.
///
/// Why this over a plain pip/venv install (what this app used to do): as of
/// mid-2025 Home Assistant deprecated the "Core" installation method (a
/// Python venv, which is exactly what pip/pipx/uv builds) in favor of two
/// supported methods only - Home Assistant OS and Home Assistant Container.
/// Confirmed: a Core install on 2026.8.3 is already past that method's
/// announced end-of-support date. Container is the practical one of the two
/// to build here (OS wants a dedicated VM/appliance).
///
/// Why podman over Docker Desktop: confirmed on a real machine - Docker
/// Desktop's own Windows-side processes alone add ~700MB of overhead on top
/// of a *second*, separate WSL VM (docker-desktop / docker-desktop-data)
/// beyond the one this app already needs. podman installed with a plain
/// `apt install` inside the SAME dedicated distro this app already
/// provisions needs none of that: no Windows GUI app, no extra VM, and as a
/// bonus it's daemonless (conmon supervises each container directly, no
/// always-on background service consuming RAM when nothing is running).
///
/// Process model: containers are NOT supervised the way the old venv-based
/// hass.exe was. Confirmed on a real machine: killing the wsl.exe process
/// that launched `podman run` does NOT stop the container - conmon keeps it
/// alive independently, by design (this is what lets a container survive a
/// client disconnect). So instead of "kill the wrapper process to stop
/// everything" (the old Job Object model), this app issues explicit
/// `podman run -d` / `podman stop` commands and polls container state - see
/// HassSupervisor. This is actually more correct for a home server: Home
/// Assistant now survives this tray app itself crashing, rather than dying
/// with it.
///
/// Multi-instance: one distro and one podman hold N instances side by side,
/// each with its own container name, config dir and pinned image tag - all
/// derived in InstanceSettings, which is why every per-instance call here
/// takes an InstanceSettings rather than assembling names itself.
///
/// All wsl.exe calls set WSL_UTF8=1 - wsl.exe emits UTF-16LE by default when
/// its output isn't attached to a real console, which silently mangles
/// anything read back naively as UTF-8 (confirmed: this is exactly what
/// produced "W S L   v e r s i o n" style garbling during manual testing).
/// </summary>
public static class WslManager
{
    public const string DistroName = "Ubuntu-24.04";

    public const string ImageRepo = "ghcr.io/home-assistant/home-assistant";

    /// <summary>The upstream moving tag. Only ever pulled, never left pinned to an instance - see InstanceSettings.ImageTag.</summary>
    public const string StableTag = "stable";

    /// <summary>Container name of the pre-multi-instance layout; also the prefix every other instance's container name is built from.</summary>
    public const string LegacyContainerName = "hawinserver-hass";

    private const string LinuxAppRoot = "/root/hawinserver";

    /// <summary>Config dir of the pre-multi-instance layout, kept in place for the first instance so no real data ever has to move.</summary>
    public const string LegacyConfigDir = LinuxAppRoot + "/config";

    /// <summary>Parent of every non-legacy instance's directory.</summary>
    public const string LinuxInstancesRoot = LinuxAppRoot + "/instances";

    private static readonly IDictionary<string, string?> Utf8Environment = new Dictionary<string, string?>
    {
        ["WSL_UTF8"] = "1",
    };

    public static string LinuxPathToUnc(string linuxPath) =>
        $@"\\wsl.localhost\{DistroName}{linuxPath.Replace('/', '\\')}";

    // ---- machine-level: WSL and the distro itself -------------------------------

    public static async Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        try
        {
            var result = await ProcRunner.RunAsync(
                "wsl.exe", new[] { "--status" }, Utf8Environment, cancellationToken: ct);
            return result.Succeeded;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public static async Task<bool> IsDistroInstalledAsync(CancellationToken ct = default)
    {
        var result = await ProcRunner.RunAsync(
            "wsl.exe", new[] { "-l", "-q" }, Utf8Environment, cancellationToken: ct);
        if (!result.Succeeded) return false;

        return result.StdOut
            .Split('\n')
            .Select(line => line.Trim())
            .Any(line => line.Equals(DistroName, StringComparison.OrdinalIgnoreCase));
    }

    public static Task<ProcResult> InstallDistroAsync(Action<string>? onOutputLine, CancellationToken ct = default)
    {
        return ProcRunner.RunAsync(
            "wsl.exe",
            new[] { "--install", DistroName, "--no-launch" },
            Utf8Environment,
            onOutputLine: onOutputLine,
            cancellationToken: ct);
    }

    /// <summary>Idempotent: installs podman via apt only if it isn't already present, and ensures the instance roots exist.</summary>
    public static Task<ProcResult> BootstrapAsync(Action<string>? onOutputLine, CancellationToken ct = default)
    {
        var script = $"""
            set -e
            if ! command -v podman >/dev/null 2>&1; then
              export DEBIAN_FRONTEND=noninteractive
              apt-get update -qq
              apt-get install -y -qq podman
            fi
            mkdir -p {LegacyConfigDir}
            mkdir -p {LinuxInstancesRoot}
            """;

        return RunAsRootAsync(script, onOutputLine, ct);
    }

    // ---- images: one local store, many pinned versions ---------------------------

    public static Task<ProcResult> PullImageAsync(string tag, Action<string>? onOutputLine, CancellationToken ct = default)
    {
        return RunAsRootAsync($"podman pull {ImageRepo}:{tag}", onOutputLine, ct);
    }

    public static async Task<bool> ImageExistsAsync(string tag, CancellationToken ct = default)
    {
        var result = await RunAsRootAsync($"podman image exists {ImageRepo}:{tag}", onOutputLine: null, ct);
        return result.Succeeded;
    }

    /// <summary>
    /// "Is Home Assistant installed at all" - true if ANY version is present
    /// locally. Deliberately not a check for the "stable" tag: once instances
    /// are pinned to concrete versions, nothing references that tag any more.
    /// </summary>
    public static async Task<bool> AnyImageExistsAsync(CancellationToken ct = default)
    {
        var result = await RunAsRootAsync($"podman images -q {ImageRepo}", onOutputLine: null, ct);
        return result.Succeeded && result.StdOut.Trim().Length > 0;
    }

    /// <summary>Reads an image's own version label - no container needs to run for this.</summary>
    public static async Task<string?> GetImageVersionAsync(string tag, CancellationToken ct = default)
    {
        var result = await RunAsRootAsync(
            $"podman image inspect {ImageRepo}:{tag}" +
            " --format '{{index .Config.Labels \"io.hass.version\"}}'",
            onOutputLine: null,
            ct);

        if (!result.Succeeded) return null;
        var version = result.StdOut.Trim();
        return version.Length > 0 ? version : null;
    }

    /// <summary>Tags an already-pulled image under a second name. Local and instant - no network, same image ID.</summary>
    public static Task<ProcResult> TagImageAsync(string fromTag, string toTag, CancellationToken ct = default)
    {
        return RunAsRootAsync($"podman tag {ImageRepo}:{fromTag} {ImageRepo}:{toTag}", onOutputLine: null, ct);
    }

    /// <summary>Tags of every locally available Home Assistant image, newest-looking first.</summary>
    public static async Task<IReadOnlyList<string>> ListLocalTagsAsync(CancellationToken ct = default)
    {
        var result = await RunAsRootAsync(
            "podman images --format '{{.Tag}}' " + ImageRepo, onOutputLine: null, ct);

        if (!result.Succeeded) return Array.Empty<string>();

        return result.StdOut
            .Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Length > 0 && line != "<none>")
            .Distinct(StringComparer.Ordinal)
            // Newest first, by version rather than by string: "2026.10.1"
            // sorts BELOW "2026.8.3" alphabetically, which would hand the
            // wrong default to every "pick the latest local version" caller.
            .OrderByDescending(ParseTagOrZero)
            .ThenByDescending(t => t, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Home Assistant uses calendar versioning ("2026.8.3"); anything else (e.g. "stable") sorts last.</summary>
    private static Version ParseTagOrZero(string tag) =>
        Version.TryParse(tag, out var version) ? version : new Version(0, 0);

    public static Task<ProcResult> RemoveImageAsync(string tag, CancellationToken ct = default)
    {
        return RunAsRootAsync($"podman rmi {ImageRepo}:{tag}", onOutputLine: null, ct);
    }

    // ---- per-instance: containers ------------------------------------------------

    /// <summary>True if this instance's container currently exists and is running.</summary>
    public static async Task<bool> IsContainerRunningAsync(InstanceSettings instance, CancellationToken ct = default)
    {
        var result = await RunAsRootAsync(
            $"podman inspect {instance.ContainerName}" + " --format '{{.State.Running}}'",
            onOutputLine: null,
            ct);

        return result.Succeeded && result.StdOut.Trim().Equals("true", StringComparison.OrdinalIgnoreCase);
    }

    public static Task<ProcResult> GetContainerLogsAsync(
        InstanceSettings instance, int tailLines, CancellationToken ct = default)
    {
        return RunAsRootAsync(
            $"podman logs --tail {tailLines} {instance.ContainerName} 2>&1", onOutputLine: null, ct);
    }

    /// <summary>
    /// Starts (or re-creates, if a stale non-running container with this
    /// instance's name is left over) the container detached, with the
    /// instance's port/bind settings baked into the port mapping and its
    /// pinned image tag - podman/Docker fix both port publishing and the
    /// image at container-creation time, so changing either means recreating
    /// the container, not editing a running one. --rm means the container
    /// cleans itself up when stopped; the config volume is what actually
    /// persists data.
    /// </summary>
    public static Task<ProcResult> RunContainerAsync(
        InstanceSettings instance, Action<string>? onOutputLine, CancellationToken ct = default)
    {
        var portMapping = instance.BindAllInterfaces
            ? $"{instance.Port}:8123"
            : $"127.0.0.1:{instance.Port}:8123";
        var tz = GetIanaTimeZoneOrDefault();

        // Assigned devices are stored as stable /dev/serial/by-id/... paths,
        // but podman needs a real node, and the by-id link can point at a
        // different ttyACM number after a replug. Resolving inside the same
        // script that starts the container keeps those two facts one step
        // apart instead of racing across a second wsl.exe round trip.
        var deviceResolution = string.Join("\n", instance.UsbDevices
            .Where(UsbDevices.IsAssignableDevicePath)
            .Select(path => $"""
                if [ -e "{path}" ]; then
                  node=$(readlink -f "{path}")
                  DEVICE_ARGS="$DEVICE_ARGS --device $node:$node"
                else
                  echo "Assigned USB device is not present: {path}" >&2
                  exit 3
                fi
                """));

        var script = $"""
            DEVICE_ARGS=""
            {deviceResolution}
            podman rm -f {instance.ContainerName} >/dev/null 2>&1 || true
            mkdir -p {instance.LinuxConfigDir}
            podman run -d --rm --name {instance.ContainerName} \
              -p {portMapping} \
              -v {instance.LinuxConfigDir}:/config \
              -e TZ={tz} \
              $DEVICE_ARGS \
              {instance.ImageRef}
            """;

        return RunAsRootAsync(script, onOutputLine, ct);
    }

    /// <summary>
    /// Assigned devices that aren't currently present in the distro - almost
    /// always "usbipd attach hasn't been run since the last Windows reboot",
    /// which deserves that answer rather than a raw podman error.
    /// </summary>
    public static async Task<IReadOnlyList<string>> FindMissingDevicesAsync(
        InstanceSettings instance, CancellationToken ct = default)
    {
        if (instance.UsbDevices.Count == 0) return Array.Empty<string>();

        // A path that fails validation is never passed to the shell, so it
        // would otherwise be dropped silently and the instance would start
        // without the device it appears to have been given. Report it as
        // missing instead - wrong, but visibly wrong.
        var missing = instance.UsbDevices
            .Where(path => !UsbDevices.IsAssignableDevicePath(path))
            .ToList();

        var checkable = instance.UsbDevices.Where(UsbDevices.IsAssignableDevicePath).ToList();
        if (checkable.Count > 0)
        {
            var checks = string.Join("\n", checkable
                .Select(path => $"[ -e \"{path}\" ] || echo \"{path}\""));

            var result = await RunAsRootAsync(checks, onOutputLine: null, ct);
            if (result.Succeeded)
            {
                missing.AddRange(result.StdOut
                    .Split('\n')
                    .Select(line => line.Trim())
                    .Where(line => line.Length > 0));
            }
        }

        return missing;
    }

    /// <summary>
    /// True if somebody has actually completed Home Assistant's onboarding on
    /// this instance. Assigning a Zigbee coordinator to an instance that
    /// hasn't is the dangerous case: ZHA sets it up from scratch and rewrites
    /// the coordinator's network key, un-pairing every physical device.
    ///
    /// Deliberately NOT "does .storage exist": Home Assistant creates .storage
    /// and a dozen files in it on its very first boot, before anyone has seen
    /// the onboarding screen (verified on a real instance: 13 files present,
    /// no owner account). The .storage/onboarding record listing "user" as
    /// done is the point at which the instance becomes somebody's, so that is
    /// what is checked.
    /// </summary>
    public static async Task<bool> IsOnboardedAsync(
        InstanceSettings instance, CancellationToken ct = default)
    {
        var result = await RunAsRootAsync(
            $"grep -q '\"user\"' \"{instance.LinuxConfigDir}/.storage/onboarding\" 2>/dev/null",
            onOutputLine: null,
            ct);
        return result.Succeeded;
    }

    /// <summary>Runs an arbitrary read-only script in the distro. For callers outside this class that need to inspect state (see UsbDevices).</summary>
    public static Task<ProcResult> RunScriptAsRootAsync(
        string bashScript, Action<string>? onOutputLine = null, CancellationToken ct = default) =>
        RunAsRootAsync(bashScript, onOutputLine, ct);

    /// <summary>
    /// Checks whether each of the given CONTAINER-SIDE paths exists as a
    /// directory, using a short-lived throwaway container built from the
    /// same image and the same /config bind mount the real instance uses -
    /// the only way to know for certain, since anything outside /config is
    /// the image's own filesystem and cannot be inspected from the
    /// Windows/WSL side. Used to validate homeassistant.media_dirs entries
    /// before starting an instance - see MediaDirsFixer, which this exists
    /// for.
    ///
    /// Fails safe: if the check itself can't run (podman error, etc.), every
    /// path is reported as existing rather than missing, so a transient
    /// failure here can never cause a real, working media_dirs entry to be
    /// stripped out.
    /// </summary>
    public static async Task<IReadOnlyDictionary<string, bool>> CheckDirectoriesExistAsync(
        InstanceSettings instance, IReadOnlyList<string> containerPaths, CancellationToken ct = default)
    {
        var result = new Dictionary<string, bool>(StringComparer.Ordinal);
        if (containerPaths.Count == 0) return result;

        var checks = string.Join("\n", containerPaths
            .Select(p => $"[ -d \"{p}\" ] && echo \"OK|{p}\" || echo \"MISSING|{p}\""));

        var script = $"""
            podman run --rm -v {instance.LinuxConfigDir}:/config {instance.ImageRef} sh -c '
            {checks}
            '
            """;

        ProcResult runResult;
        try
        {
            runResult = await RunAsRootAsync(script, onOutputLine: null, ct);
        }
        catch (Exception)
        {
            runResult = new ProcResult(1, "", "");
        }

        if (runResult.Succeeded)
        {
            foreach (var line in runResult.StdOut.Split('\n'))
            {
                var parts = line.Trim().Split('|', 2);
                if (parts.Length == 2) result[parts[1]] = parts[0] == "OK";
            }
        }

        // Anything the check couldn't confirm one way or the other (podman
        // failure, or a path that didn't show up in the output) defaults to
        // "exists" - fail safe, never delete something we couldn't verify.
        foreach (var path in containerPaths)
        {
            result.TryAdd(path, true);
        }

        return result;
    }

    /// <summary>
    /// `podman stop` sends SIGTERM (then SIGKILL after a grace period) to
    /// the container's own PID 1 - since this is real Linux inside the
    /// container, Home Assistant's normal POSIX signal handling just works,
    /// no Windows asyncio gaps to route around. Combined with --rm this also
    /// removes the container, so a subsequent start is a clean create.
    /// </summary>
    public static Task<ProcResult> StopContainerAsync(
        InstanceSettings instance, Action<string>? onOutputLine = null, CancellationToken ct = default)
    {
        return RunAsRootAsync($"podman stop {instance.ContainerName}", onOutputLine, ct);
    }

    /// <summary>
    /// Force-removes the container, ignoring "no such container". Needed
    /// before wiping a config dir: --rm covers the normal stop path, but a
    /// container left in Created/Exited state after a failed start would
    /// otherwise keep the old bind mount alive.
    /// </summary>
    public static Task<ProcResult> RemoveContainerAsync(
        InstanceSettings instance, Action<string>? onOutputLine = null, CancellationToken ct = default)
    {
        return RunAsRootAsync(
            $"podman rm -f {instance.ContainerName} >/dev/null 2>&1 || true", onOutputLine, ct);
    }

    // ---- per-instance: config directories ----------------------------------------

    public static Task<ProcResult> EnsureConfigDirAsync(
        InstanceSettings instance, Action<string>? onOutputLine = null, CancellationToken ct = default)
    {
        return RunAsRootAsync($"mkdir -p {instance.LinuxConfigDir}", onOutputLine, ct);
    }

    /// <summary>
    /// Wipes the instance's config dir and leaves an empty one in its place:
    /// Home Assistant boots into its own onboarding wizard when /config is
    /// empty, which is precisely what "reset this instance" means. The
    /// container MUST already be stopped and removed - deleting the directory
    /// under a live container leaves its bind mount attached to the old,
    /// now-unlinked inode.
    /// </summary>
    public static Task<ProcResult> ResetConfigDirAsync(
        InstanceSettings instance, Action<string>? onOutputLine = null, CancellationToken ct = default)
    {
        var dir = instance.LinuxConfigDir;
        AssertDeletablePath(dir);
        return RunAsRootAsync($"rm -rf {dir} && mkdir -p {dir}", onOutputLine, ct);
    }

    /// <summary>Removes the instance's directory entirely - used by "delete instance", not by reset.</summary>
    public static Task<ProcResult> DeleteInstanceDirAsync(
        InstanceSettings instance, Action<string>? onOutputLine = null, CancellationToken ct = default)
    {
        var dir = instance.LinuxInstanceDir;
        AssertDeletablePath(dir);
        return RunAsRootAsync($"rm -rf {dir}", onOutputLine, ct);
    }

    /// <summary>Copies one instance's config dir into another's, preserving ownership, timestamps and hidden entries (.storage, .cloud).</summary>
    public static Task<ProcResult> CloneConfigDirAsync(
        InstanceSettings source, InstanceSettings target, Action<string>? onOutputLine = null, CancellationToken ct = default)
    {
        var script = $"""
            set -e
            mkdir -p {target.LinuxConfigDir}
            cp -a {source.LinuxConfigDir}/. {target.LinuxConfigDir}/
            """;

        return RunAsRootAsync(script, onOutputLine, ct);
    }

    /// <summary>
    /// Last line of defence before an `rm -rf` running as root inside the
    /// distro. Instance ids are already restricted to [a-z0-9-] when they are
    /// created (Settings.MakeUniqueId), but this check is what stands between
    /// a hand-edited settings.json and a recursive delete of something that
    /// isn't ours, so it is written here rather than trusted upstream.
    /// </summary>
    private static void AssertDeletablePath(string linuxPath)
    {
        var isLegacyConfigDir = linuxPath == LegacyConfigDir;
        var isUnderInstancesRoot =
            linuxPath.StartsWith(LinuxInstancesRoot + "/", StringComparison.Ordinal)
            && linuxPath.Length > LinuxInstancesRoot.Length + 1;

        if (!isLegacyConfigDir && !isUnderInstancesRoot)
        {
            throw new InvalidOperationException(
                $"Refusing to delete \"{linuxPath}\": outside the directories this app owns.");
        }

        if (linuxPath.Contains("..", StringComparison.Ordinal)
            || !SafePathPattern.IsMatch(linuxPath))
        {
            throw new InvalidOperationException(
                $"Refusing to delete \"{linuxPath}\": unsafe characters in path.");
        }
    }

    private static readonly Regex SafePathPattern = new(@"^[A-Za-z0-9/._-]+$", RegexOptions.Compiled);

    private static string GetIanaTimeZoneOrDefault()
    {
        try
        {
            if (TimeZoneInfo.TryConvertWindowsIdToIanaId(TimeZoneInfo.Local.Id, out var iana) && iana is not null)
            {
                return iana;
            }
        }
        catch (Exception)
        {
            // Fall through to UTC - a wrong display timezone isn't worth failing setup over.
        }

        return "UTC";
    }

    private static Task<ProcResult> RunAsRootAsync(
        string bashScript, Action<string>? onOutputLine, CancellationToken ct = default)
    {
        // These scripts are C# raw string literals, so they carry whatever line
        // endings this source file happens to be saved with - and under CRLF
        // every one of them breaks in bash, in ways that don't look like a line
        // ending problem at all. A trailing \r escapes the carriage return
        // rather than the newline, so `podman run ... \` stops continuing the
        // line and bash reports "-p: command not found"; elsewhere the \r just
        // becomes part of the last argument, so `mkdir -p /x/config` silently
        // creates a directory literally named "config\r". Normalizing here
        // means no caller, and no future editor or git setting, has to get this
        // right.
        bashScript = bashScript.Replace("\r\n", "\n");

        // --exec, not "--": `wsl.exe <command>` runs the command THROUGH the
        // distro's default shell, which expands the script once before bash
        // ever sees it. Command substitution survives that (it just runs a
        // step early), but every ordinary variable arrives already substituted
        // to empty - confirmed on a real machine: `x=hello; echo "x=$x"` prints
        // "x=", and `for f in a b; do echo "$f"; done` loops twice printing
        // nothing. Scripts here got away with it until one needed a variable.
        // --exec hands argv straight to the distro with no shell in between.
        return ProcRunner.RunAsync(
            "wsl.exe",
            new[] { "-d", DistroName, "-u", "root", "--exec", "bash", "-c", bashScript },
            Utf8Environment,
            onOutputLine: onOutputLine,
            cancellationToken: ct);
    }
}
