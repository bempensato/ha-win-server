using System.Diagnostics;
using System.Security.Principal;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace HaWinServer.Core;

/// <summary>
/// A serial device visible inside the WSL distro, i.e. one a container can
/// actually be given. <see cref="HasStableName"/> is false for a raw
/// /dev/ttyACM0-style node, which works but can point at a different physical
/// device after a replug.
/// </summary>
public sealed record WslSerialDevice(string DevicePath, string RealPath, bool HasStableName)
{
    public string Name => System.IO.Path.GetFileName(DevicePath);
}

/// <summary>A USB device as Windows sees it, plus how far along the usbipd chain it is.</summary>
public sealed record WindowsUsbDevice(string BusId, string Description, bool IsShared, bool IsAttached)
{
    public string StateLabel => IsAttached ? "attached to WSL" : IsShared ? "shared, not attached" : "not shared";
}

/// <summary>
/// USB passthrough into the WSL distro, which is a three-link chain and fails
/// at a different link for everyone:
///
///   1. usbipd-win installed on Windows        (one-time, needs admin to install)
///   2. `usbipd bind`   - share the device     (one-time PER DEVICE, needs admin)
///   3. `usbipd attach` - hand it to WSL       (after every Windows reboot, NO admin)
///
/// Only step 2 genuinely needs elevation, and this app's manifest is asInvoker
/// on purpose, so that step is the one place where it either re-launches
/// usbipd through UAC or hands the user the exact command to run themselves.
///
/// There is a fourth link that is easy to miss because usbipd reports success
/// without it: once the device is in the VM, Linux still has to bind a driver
/// to it (cdc-acm for a ConBee-style coordinator) and udev still has to create
/// the /dev/serial/by-id symlink. Either can be absent - a WSL kernel whose
/// modules don't match, or a distro booted without systemd, so without udev -
/// and the symptom is identical to "attach did nothing": an empty list. That
/// is why enumeration below falls back to raw tty nodes, and why
/// DescribeDeviceStateAsync exists to say which link actually broke.
///
/// Confirmed on the WSL kernel this app targets (6.6.87.2-microsoft-standard-WSL2):
/// vhci-hcd (USB/IP client) and cdc-acm (the serial class driver) both ship as
/// modules, and Ubuntu-24.04 boots with systemd, so udev is running.
///
/// A device attached with usbipd lands in the shared WSL2 VM, so it is visible
/// to every running distro, not just ours - which is why attach doesn't need
/// to name a distribution.
/// </summary>
public static class UsbDevices
{
    public const string InstallCommand = "winget install --exact --id dorssel.usbipd-win";

    /// <summary>Stable per-device symlinks, created by udev. Preferred, because the name encodes vendor/product/serial.</summary>
    public const string ByIdDir = "/dev/serial/by-id";

    // A by-id name, or a raw tty node as a fallback when udev hasn't produced
    // one. Both are interpolated into shell commands and both can arrive from
    // a hand-edited settings.json, so both are pattern-checked.
    private static readonly Regex ByIdPattern = new(@"^/dev/serial/by-id/[A-Za-z0-9._:+-]+$", RegexOptions.Compiled);
    private static readonly Regex RawNodePattern = new(@"^/dev/tty(ACM|USB)[0-9]{1,3}$", RegexOptions.Compiled);

    /// <summary>Guard for anything interpolated into a shell command line.</summary>
    public static bool IsAssignableDevicePath(string path) =>
        ByIdPattern.IsMatch(path) || RawNodePattern.IsMatch(path);

    /// <summary>True for a path that survives a replug; false for a raw node, which is positional.</summary>
    public static bool IsStableDevicePath(string path) => ByIdPattern.IsMatch(path);

    public static bool IsProcessElevated()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>usbipd.exe, from PATH or its default install location; null if it isn't installed.</summary>
    public static string? FindUsbipd()
    {
        foreach (var candidate in new[]
                 {
                     System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "usbipd-win", "usbipd.exe"),
                     System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "usbipd-win", "usbipd.exe"),
                 })
        {
            if (File.Exists(candidate)) return candidate;
        }

        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var dir in pathEnv.Split(System.IO.Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = System.IO.Path.Combine(dir.Trim(), "usbipd.exe");
                if (File.Exists(candidate)) return candidate;
            }
            catch (Exception)
            {
                // Malformed PATH entry - skip it.
            }
        }

        return null;
    }

    // ---- the WSL side: what a container can actually be given --------------------

    /// <summary>
    /// Serial devices present inside the distro right now. Prefers the stable
    /// by-id symlinks; falls back to raw /dev/ttyACM* and /dev/ttyUSB* nodes
    /// that no by-id link points at, so a distro without udev still yields a
    /// usable list instead of an unexplained empty one.
    /// </summary>
    public static async Task<IReadOnlyList<WslSerialDevice>> ListWslSerialDevicesAsync(CancellationToken ct = default)
    {
        var script = $$"""
            for f in {{ByIdDir}}/*; do
              [ -e "$f" ] || continue
              printf 'byid\t%s\t%s\n' "$f" "$(readlink -f "$f")"
            done
            for n in /dev/ttyACM* /dev/ttyUSB*; do
              [ -e "$n" ] || continue
              linked=""
              for f in {{ByIdDir}}/*; do
                [ -e "$f" ] || continue
                [ "$(readlink -f "$f")" = "$n" ] && linked=1
              done
              [ -n "$linked" ] || printf 'raw\t%s\t%s\n' "$n" "$n"
            done
            """;

        var result = await WslManager.RunScriptAsRootAsync(script, onOutputLine: null, ct);
        if (!result.Succeeded) return Array.Empty<WslSerialDevice>();

        var devices = new List<WslSerialDevice>();
        foreach (var line in result.StdOut.Split('\n'))
        {
            var parts = line.Trim().Split('\t');
            if (parts.Length != 3 || parts[1].Length == 0) continue;
            devices.Add(new WslSerialDevice(parts[1], parts[2], HasStableName: parts[0] == "byid"));
        }

        return devices;
    }

    /// <summary>
    /// Human-readable state of the last two links in the chain, for when the
    /// device list is empty but usbipd says the device is attached. Answers
    /// "did it reach the VM at all", "did a driver bind", "is udev even
    /// running" - the three things that produce an identical empty list.
    /// </summary>
    public static async Task<string> DescribeDeviceStateAsync(CancellationToken ct = default)
    {
        var script = """
            printf 'USB/IP client (vhci_hcd) loaded: '
            lsmod 2>/dev/null | grep -q '^vhci_hcd' && echo yes || echo no
            printf 'Serial driver (cdc_acm) loaded:  '
            lsmod 2>/dev/null | grep -q '^cdc_acm' && echo yes || echo no
            printf 'udev running (creates by-id):    '
            (pgrep -x systemd-udevd >/dev/null 2>&1 || pgrep -x udevd >/dev/null 2>&1) && echo yes || echo no
            printf 'USB devices seen by the kernel:  '
            n=$(ls /sys/bus/usb/devices 2>/dev/null | grep -c '^[0-9]*-[0-9]' || true)
            echo "${n:-0}"
            printf 'Serial nodes present:            '
            ls /dev/ttyACM* /dev/ttyUSB* 2>/dev/null | tr '\n' ' ' || true
            echo
            """;

        try
        {
            var result = await WslManager.RunScriptAsRootAsync(script, onOutputLine: null, ct);
            return result.StdOut.Trim();
        }
        catch (Exception ex)
        {
            return "Could not inspect the distro: " + ex.Message;
        }
    }

    // ---- the Windows side: usbipd ------------------------------------------------

    /// <summary>
    /// Reads usbipd's device table. Prefers the machine-readable `usbipd state`
    /// and falls back to parsing `usbipd list`, because the two have swapped
    /// roles across usbipd-win major versions and neither is guaranteed here.
    /// </summary>
    public static async Task<IReadOnlyList<WindowsUsbDevice>> ListWindowsUsbDevicesAsync(CancellationToken ct = default)
    {
        var usbipd = FindUsbipd();
        if (usbipd is null) return Array.Empty<WindowsUsbDevice>();

        try
        {
            var state = await ProcRunner.RunAsync(usbipd, new[] { "state" }, cancellationToken: ct);
            if (state.Succeeded && state.StdOut.TrimStart().StartsWith('{'))
            {
                var parsed = ParseStateJson(state.StdOut);
                if (parsed.Count > 0) return parsed;
            }
        }
        catch (Exception)
        {
            // Older usbipd has no `state` verb - fall through to `list`.
        }

        try
        {
            var list = await ProcRunner.RunAsync(usbipd, new[] { "list" }, cancellationToken: ct);
            if (list.Succeeded) return ParseListText(list.StdOut);
        }
        catch (Exception)
        {
            // Report "nothing found" rather than failing the dialog outright.
        }

        return Array.Empty<WindowsUsbDevice>();
    }

    private static List<WindowsUsbDevice> ParseStateJson(string json)
    {
        var devices = new List<WindowsUsbDevice>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("Devices", out var array)) return devices;

            foreach (var element in array.EnumerateArray())
            {
                var busId = TryGetString(element, "BusId");
                if (string.IsNullOrWhiteSpace(busId)) continue; // persisted but not currently plugged in

                devices.Add(new WindowsUsbDevice(
                    busId,
                    TryGetString(element, "Description") ?? "(unnamed device)",
                    IsShared: TryGetString(element, "PersistedGuid") is not null,
                    IsAttached: TryGetString(element, "ClientIPAddress") is not null));
            }
        }
        catch (Exception)
        {
            // Unexpected schema - the caller falls back to the text parser.
            devices.Clear();
        }

        return devices;
    }

    private static string? TryGetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static readonly Regex ListRowPattern = new(
        @"^(?<busid>\d+-\d+)\s+(?<vidpid>[0-9a-fA-F]{4}:[0-9a-fA-F]{4})\s+(?<rest>.+)$",
        RegexOptions.Compiled);

    /// <summary>
    /// Parses the "Connected:" table of `usbipd list`. The STATE column is the
    /// tail of the line and has no delimiter separating it from the free-text
    /// device description, so it is matched against the known state strings
    /// rather than split on whitespace.
    /// </summary>
    private static List<WindowsUsbDevice> ParseListText(string text)
    {
        var devices = new List<WindowsUsbDevice>();
        var inConnected = false;

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd();
            if (line.StartsWith("Connected:", StringComparison.OrdinalIgnoreCase)) { inConnected = true; continue; }
            if (line.StartsWith("Persisted:", StringComparison.OrdinalIgnoreCase)) break;
            if (!inConnected || line.Trim().Length == 0) continue;

            var match = ListRowPattern.Match(line.Trim());
            if (!match.Success) continue; // header row, or something we don't recognise

            var rest = match.Groups["rest"].Value.Trim();
            var attached = rest.EndsWith("Attached", StringComparison.OrdinalIgnoreCase);
            var shared = attached || rest.EndsWith("Shared", StringComparison.OrdinalIgnoreCase)
                                  || rest.EndsWith("Shared (forced)", StringComparison.OrdinalIgnoreCase);

            var description = rest;
            foreach (var suffix in new[] { "Shared (forced)", "Not shared", "Attached", "Shared" })
            {
                if (rest.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                {
                    description = rest[..^suffix.Length].TrimEnd();
                    break;
                }
            }

            devices.Add(new WindowsUsbDevice(match.Groups["busid"].Value, description, shared, attached));
        }

        return devices;
    }

    /// <summary>The command a user has to run themselves when this app can't elevate.</summary>
    public static string ShareCommand(string busId) => $"usbipd bind --busid {busId}";

    /// <summary>
    /// Step 2: share the device. This is the only step that needs admin. If the
    /// app is already elevated it runs directly; otherwise it asks Windows to
    /// re-launch usbipd through UAC, and reports back if that is declined so
    /// the caller can fall back to showing the command.
    /// </summary>
    public static async Task<(bool Succeeded, string Detail)> ShareAsync(string busId, CancellationToken ct = default)
    {
        var usbipd = FindUsbipd();
        if (usbipd is null) return (false, "usbipd-win is not installed.");

        if (IsProcessElevated())
        {
            var result = await ProcRunner.RunAsync(
                usbipd, new[] { "bind", "--busid", busId }, cancellationToken: ct);
            return (result.Succeeded, FirstNonEmpty(result.StdErr, result.StdOut, "Device shared."));
        }

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = usbipd,
                UseShellExecute = true, // required for Verb, and it means no output to capture
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            psi.ArgumentList.Add("bind");
            psi.ArgumentList.Add("--busid");
            psi.ArgumentList.Add(busId);

            using var process = Process.Start(psi);
            if (process is null) return (false, "Windows did not start the elevated command.");

            await process.WaitForExitAsync(ct);
            return process.ExitCode == 0
                ? (true, "Device shared (elevated).")
                : (false, $"usbipd exited with code {process.ExitCode}.");
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            // 1223 = ERROR_CANCELLED: the UAC prompt was declined, or this
            // account can't elevate at all. Both mean "show them the command".
            return (false, ex.NativeErrorCode == 1223
                ? "The administrator prompt was declined."
                : "Could not run the command as administrator: " + ex.Message);
        }
    }

    /// <summary>
    /// Step 3: hand the shared device to WSL. No elevation. The verb moved
    /// between usbipd-win 3.x (`usbipd wsl attach`) and 4.x (`usbipd attach
    /// --wsl`), so both are tried rather than pinning a version requirement.
    /// </summary>
    public static async Task<(bool Succeeded, string Detail)> AttachAsync(string busId, CancellationToken ct = default)
    {
        var usbipd = FindUsbipd();
        if (usbipd is null) return (false, "usbipd-win is not installed.");

        var modern = await ProcRunner.RunAsync(
            usbipd, new[] { "attach", "--wsl", "--busid", busId }, cancellationToken: ct);
        if (modern.Succeeded) return (true, "Attached to WSL.");

        var legacy = await ProcRunner.RunAsync(
            usbipd, new[] { "wsl", "attach", "--busid", busId }, cancellationToken: ct);
        if (legacy.Succeeded) return (true, "Attached to WSL.");

        return (false, FirstNonEmpty(modern.StdErr, modern.StdOut, "usbipd attach failed."));
    }

    private static string FirstNonEmpty(params string[] candidates) =>
        candidates.FirstOrDefault(c => !string.IsNullOrWhiteSpace(c))?.Trim() ?? string.Empty;
}
