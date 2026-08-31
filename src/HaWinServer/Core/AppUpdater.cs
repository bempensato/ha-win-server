using System.Security.Cryptography;

namespace HaWinServer.Core;

/// <summary>
/// Downloads and stages an app self-update. Modelled on Cloudflared's
/// download-from-GitHub-Releases flow, with one necessary difference: this
/// app's exe is not Authenticode-signed, so integrity is checked against the
/// sha256 sidecar the release workflow publishes alongside it, not a
/// certificate chain.
///
/// The actual "replace the running exe" step cannot happen here - a Windows
/// process cannot overwrite its own file while running - so this only stages
/// the verified download; UpdateApplier (run from the newly-downloaded exe
/// itself, via --apply-update) does the swap. See Program.cs.
/// </summary>
public static class AppUpdater
{
    private static readonly HttpClient Client = new()
    {
        Timeout = TimeSpan.FromMinutes(3),
    };

    static AppUpdater()
    {
        Client.DefaultRequestHeaders.UserAgent.ParseAdd("HaWinServer/1.0 (+tray app)");
    }

    /// <summary>
    /// Whether this process can plausibly replace its own exe in place -
    /// false when it lives somewhere that needs elevation (e.g. Program
    /// Files). Checked before offering the download/install flow at all.
    /// </summary>
    public static bool CanSelfUpdate(out string reason)
    {
        var exePath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exePath))
        {
            reason = "Could not determine the running executable's path.";
            return false;
        }

        var directory = Path.GetDirectoryName(exePath)!;
        var probePath = Path.Combine(directory, $".hawinserver-write-probe-{Guid.NewGuid():N}");

        try
        {
            File.WriteAllBytes(probePath, Array.Empty<byte>());
            File.Delete(probePath);
            reason = "";
            return true;
        }
        catch (Exception)
        {
            reason = $"\"{directory}\" is not writable by this app - an admin needs to replace the exe manually.";
            return false;
        }
    }

    /// <summary>
    /// Downloads the release exe, verifies it against its sha256 sidecar,
    /// and returns the path to the verified copy under AppPaths.UpdateDir.
    /// Throws with a user-facing message on any verification failure - the
    /// caller's progress window shows that message directly.
    /// </summary>
    public static async Task<string> DownloadAsync(
        AppRelease release, Action<string>? onProgress = null, CancellationToken ct = default)
    {
        Directory.CreateDirectory(AppPaths.UpdateDir);

        var finalPath = Path.Combine(AppPaths.UpdateDir, $"HaWinServer-{release.Version}.exe");
        var tempPath = finalPath + ".part";

        try
        {
            onProgress?.Invoke($"Downloading HA Win Server {release.Version}...");

            using (var response = await Client.GetAsync(release.ExeUrl, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                response.EnsureSuccessStatusCode();

                var totalBytes = response.Content.Headers.ContentLength;
                await using var httpStream = await response.Content.ReadAsStreamAsync(ct);
                await using var fileStream = File.Create(tempPath);

                var buffer = new byte[81920];
                long totalRead = 0;
                var lastReportedPercent = -1;
                int read;
                while ((read = await httpStream.ReadAsync(buffer, ct)) > 0)
                {
                    await fileStream.WriteAsync(buffer.AsMemory(0, read), ct);
                    totalRead += read;

                    if (totalBytes is > 0)
                    {
                        var percent = (int)(totalRead * 100 / totalBytes.Value);
                        if (percent >= lastReportedPercent + 5)
                        {
                            onProgress?.Invoke($"Downloading HA Win Server {release.Version}... {percent}%");
                            lastReportedPercent = percent;
                        }
                    }
                }
            }

            onProgress?.Invoke("Verifying download integrity...");
            await VerifyChecksumAsync(release, tempPath, ct);
            VerifyLooksLikeAnExe(tempPath);

            File.Move(tempPath, finalPath, overwrite: true);
            onProgress?.Invoke("Download verified.");
            return finalPath;
        }
        finally
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch (Exception) { /* best-effort cleanup */ }
        }
    }

    /// <summary>
    /// Launches the newly downloaded exe with --apply-update, telling it to
    /// wait for this process to exit and then replace it. The caller is
    /// responsible for exiting (Application.Exit) right after this returns.
    /// </summary>
    public static void LaunchApply(string downloadedExePath, string targetExePath)
    {
        var currentPid = Environment.ProcessId;

        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = downloadedExePath,
            Arguments = $"--apply-update \"{targetExePath}\" {currentPid}",
            UseShellExecute = false,
        });
    }

    /// <summary>Best-effort cleanup of a previous run's leftovers - called once on startup, never throws.</summary>
    public static void CleanupStaleFiles()
    {
        try
        {
            if (!Directory.Exists(AppPaths.UpdateDir)) return;

            foreach (var file in Directory.EnumerateFiles(AppPaths.UpdateDir))
            {
                try { File.Delete(file); } catch (Exception) { /* still in use, or already gone - leave it */ }
            }
        }
        catch (Exception)
        {
            // Never fatal - a stray file here just wastes a little disk space.
        }
    }

    private static async Task VerifyChecksumAsync(AppRelease release, string filePath, CancellationToken ct)
    {
        var sha256Text = await Client.GetStringAsync(release.Sha256Url, ct);
        var expectedHash = sha256Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (string.IsNullOrEmpty(expectedHash))
        {
            throw new InvalidOperationException("Could not read the expected checksum for this release.");
        }

        await using var stream = File.OpenRead(filePath);
        var actualHashBytes = await SHA256.HashDataAsync(stream, ct);
        var actualHash = Convert.ToHexString(actualHashBytes);

        if (!actualHash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "The downloaded file's checksum does not match the release's published checksum - refusing to install it.");
        }
    }

    /// <summary>
    /// Sanity check against a GitHub outage or redirect returning an HTML
    /// error page saved with a ".exe" name instead of the real asset: a
    /// Windows PE file starts with "MZ" and a real self-contained build is
    /// well over 1 MB.
    /// </summary>
    private static void VerifyLooksLikeAnExe(string filePath)
    {
        var info = new FileInfo(filePath);
        if (info.Length < 1_000_000)
        {
            throw new InvalidOperationException("The downloaded file is too small to be a valid HA Win Server build.");
        }

        using var stream = File.OpenRead(filePath);
        var header = new byte[2];
        if (stream.Read(header, 0, 2) != 2 || header[0] != 'M' || header[1] != 'Z')
        {
            throw new InvalidOperationException("The downloaded file is not a valid Windows executable.");
        }
    }
}
