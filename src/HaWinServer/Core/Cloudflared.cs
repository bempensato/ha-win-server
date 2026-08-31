using System.Security.Cryptography.X509Certificates;

namespace HaWinServer.Core;

/// <summary>
/// Locates or acquires cloudflared.exe, the Cloudflare Tunnel connector.
///
/// Search order mirrors UsbDevices.FindUsbipd() - a third-party exe this app
/// depends on but doesn't ship: an app-owned copy first, then the usual
/// Program Files install location, then PATH. Unlike usbipd, there's a
/// fallback beyond "tell the user to install it": cloudflared ships as a
/// single self-contained .exe with no installer, so this app can fetch it
/// straight from GitHub Releases into its own %LOCALAPPDATA% bin directory -
/// no admin, no MSI, consistent with every other "this app owns it" file.
///
/// `--no-autoupdate` is always passed when running it (see TunnelSupervisor):
/// cloudflared's built-in auto-update tries to overwrite its own exe, which
/// this app's own version-pinning/update-checking should own instead, the
/// same way Home Assistant's image tag is never left on a moving target.
/// </summary>
public static class Cloudflared
{
    public const string DownloadUrl =
        "https://github.com/cloudflare/cloudflared/releases/latest/download/cloudflared-windows-amd64.exe";

    // Cloudflare's Authenticode signer as it appears in the certificate
    // subject - the only thing checked against a freshly downloaded exe
    // before it is ever executed. Not a full pinned-hash check (the file
    // changes on every cloudflared release), but enough to catch a
    // GitHub-outage / DNS-hijack / MITM substitution before running an
    // executable with network access as this user.
    private const string ExpectedSignerFragment = "Cloudflare, Inc.";

    public static string OwnedBinaryPath =>
        Path.Combine(AppPaths.Root, "bin", "cloudflared.exe");

    /// <summary>cloudflared.exe from an app-owned copy, Program Files, or PATH - null if none exists.</summary>
    public static string? Find()
    {
        if (File.Exists(OwnedBinaryPath)) return OwnedBinaryPath;

        var programFiles = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "cloudflared", "cloudflared.exe");
        if (File.Exists(programFiles)) return programFiles;

        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(dir.Trim(), "cloudflared.exe");
                if (File.Exists(candidate)) return candidate;
            }
            catch (Exception)
            {
                // Malformed PATH entry - skip it.
            }
        }

        return null;
    }

    /// <summary>
    /// Downloads the latest cloudflared release into this app's own bin
    /// directory, verifies its Authenticode signature is Cloudflare's, and
    /// deletes the file again if that check fails. Returns the path on
    /// success, or throws with a user-facing reason on failure - the wizard
    /// shows that message directly rather than treating it as a bug.
    /// </summary>
    public static async Task<string> DownloadAsync(
        Action<string>? onProgress = null, CancellationToken ct = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(OwnedBinaryPath)!);

        onProgress?.Invoke("Downloading cloudflared from GitHub Releases...");

        using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("HaWinServer/1.0 (+tray app)");

        var tempPath = OwnedBinaryPath + ".download";
        try
        {
            using (var response = await client.GetAsync(DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                response.EnsureSuccessStatusCode();
                await using var fileStream = File.Create(tempPath);
                await response.Content.CopyToAsync(fileStream, ct);
            }

            onProgress?.Invoke("Verifying the download is signed by Cloudflare...");
            var (verified, detail) = VerifySignature(tempPath);
            if (!verified)
            {
                throw new InvalidOperationException(
                    "The downloaded file's signature could not be verified as Cloudflare's - refusing to run it. " +
                    detail);
            }

            File.Move(tempPath, OwnedBinaryPath, overwrite: true);
            onProgress?.Invoke("cloudflared downloaded and verified.");
            return OwnedBinaryPath;
        }
        finally
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch (Exception) { /* best-effort cleanup */ }
        }
    }

    /// <summary>
    /// Authenticode check via the in-framework X509Certificate APIs (no
    /// signtool.exe, no extra dependency): the file must carry an embedded
    /// signing certificate, its chain must build and validate, and the
    /// subject must name Cloudflare. This is a plausibility check, not a
    /// full WinVerifyTrust equivalent (it doesn't re-verify countersignature
    /// timestamps), but it is what stands between "GitHub Releases returned
    /// something else" and running an unverified network-facing binary.
    /// </summary>
    private static (bool Verified, string Detail) VerifySignature(string filePath)
    {
        try
        {
            // X509Certificate.CreateFromSignedFile is the only in-framework API
            // that extracts the embedded Authenticode signer from a signed PE
            // file - X509CertificateLoader (the SYSLIB0057-recommended
            // replacement) loads standalone certificate files, not a signer
            // embedded in an executable, so there is no non-obsolete
            // equivalent for this specific use.
#pragma warning disable SYSLIB0057
            using var cert = new X509Certificate2(X509Certificate.CreateFromSignedFile(filePath));
#pragma warning restore SYSLIB0057

            using var chain = new X509Chain();
            chain.ChainPolicy.RevocationMode = X509RevocationMode.Online;
            chain.ChainPolicy.VerificationFlags = X509VerificationFlags.NoFlag;

            var chainBuilt = chain.Build(cert);
            if (!chainBuilt)
            {
                var statuses = string.Join(", ", chain.ChainStatus.Select(s => s.StatusInformation.Trim()));
                return (false, $"Certificate chain did not validate: {statuses}");
            }

            if (!cert.Subject.Contains(ExpectedSignerFragment, StringComparison.OrdinalIgnoreCase))
            {
                return (false, $"Signed by \"{cert.Subject}\", not Cloudflare.");
            }

            return (true, string.Empty);
        }
        catch (Exception ex)
        {
            return (false, "No valid Authenticode signature found: " + ex.Message);
        }
    }

    public static async Task<string?> TryGetVersionAsync(string cloudflaredPath, CancellationToken ct = default)
    {
        try
        {
            var result = await ProcRunner.RunAsync(cloudflaredPath, new[] { "--version" }, cancellationToken: ct);
            return result.Succeeded ? result.StdOut.Trim() : null;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
