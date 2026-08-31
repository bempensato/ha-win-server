using System.Diagnostics;

namespace HaWinServer.Core;

/// <summary>
/// The "second half" of a self-update, run by the newly downloaded exe
/// itself (see Program.cs's --apply-update handling and
/// AppUpdater.LaunchApplyAsync). A running Windows process cannot overwrite
/// its own file, so the new exe waits for the old one to exit, then copies
/// itself over it and restarts it - after which this helper invocation
/// exits, leaving the restarted app as the only surviving process.
/// </summary>
public static class UpdateApplier
{
    /// <summary>Returns the process exit code.</summary>
    public static int Run(string targetPath, int waitForPid)
    {
        WaitForProcessExit(waitForPid, TimeSpan.FromSeconds(30));

        var backupPath = targetPath + ".bak";
        var sourcePath = Environment.ProcessPath!;

        try
        {
            File.Copy(targetPath, backupPath, overwrite: true);
        }
        catch (Exception)
        {
            // If even the backup copy fails, the original is still intact -
            // safe to just give up here rather than risk the copy below.
        }

        if (!TryCopyWithRetry(sourcePath, targetPath))
        {
            TryRestoreBackup(backupPath, targetPath);
            ShowFailureMessage(
                "HA Win Server could not finish updating itself. The previous version has been restored.\n\n" +
                $"The new version is still available at:\n{sourcePath}");
            return 1;
        }

        try { File.Delete(backupPath); } catch (Exception) { /* leftover .bak is harmless */ }

        try
        {
            Process.Start(new ProcessStartInfo { FileName = targetPath, UseShellExecute = false });
        }
        catch (Exception)
        {
            ShowFailureMessage(
                $"HA Win Server was updated, but could not be restarted automatically. " +
                $"Please start it again from:\n{targetPath}");
            return 1;
        }

        return 0;
    }

    private static void WaitForProcessExit(int pid, TimeSpan timeout)
    {
        try
        {
            var process = Process.GetProcessById(pid);
            process.WaitForExit((int)timeout.TotalMilliseconds);
        }
        catch (ArgumentException)
        {
            // Process already gone - nothing to wait for.
        }
    }

    private static bool TryCopyWithRetry(string sourcePath, string targetPath)
    {
        const int maxAttempts = 10;
        const int delayMs = 500;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                File.Copy(sourcePath, targetPath, overwrite: true);
                return true;
            }
            catch (IOException) when (attempt < maxAttempts)
            {
                // The old exe's file lock can linger briefly after the process exits.
                Thread.Sleep(delayMs);
            }
            catch (UnauthorizedAccessException) when (attempt < maxAttempts)
            {
                Thread.Sleep(delayMs);
            }
        }

        return false;
    }

    private static void TryRestoreBackup(string backupPath, string targetPath)
    {
        try
        {
            if (File.Exists(backupPath))
            {
                File.Copy(backupPath, targetPath, overwrite: true);
                File.Delete(backupPath);
            }
        }
        catch (Exception)
        {
            // Nothing more we can do here - the failure message already
            // points the user at the downloaded exe as a manual fallback.
        }
    }

    private static void ShowFailureMessage(string text) =>
        MessageBox.Show(text, "HA Win Server - Update Failed", MessageBoxButtons.OK, MessageBoxIcon.Warning);
}
