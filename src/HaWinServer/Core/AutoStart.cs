using Microsoft.Win32;

namespace HaWinServer.Core;

/// <summary>
/// "Run at login" via the per-user Run key - no admin required, no COM
/// (avoids IWshRuntimeLibrary / .lnk generation entirely). The registry is
/// treated as the single source of truth: we never cache "is autostart on"
/// anywhere, so the menu checkbox can never drift from what Windows will
/// actually do at next logon.
/// </summary>
public static class AutoStart
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "HaWinServer";

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        return key?.GetValue(ValueName) is string;
    }

    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
        if (enabled)
        {
            var exePath = Environment.ProcessPath
                ?? throw new InvalidOperationException("Could not determine the running executable's path.");
            key.SetValue(ValueName, $"\"{exePath}\"");
        }
        else
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }
}
