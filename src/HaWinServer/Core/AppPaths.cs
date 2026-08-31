namespace HaWinServer.Core;

/// <summary>
/// Every file this app owns directly lives under %LOCALAPPDATA%\HaWinServer,
/// so it's trivial to back up or delete. Home Assistant itself runs inside a
/// dedicated WSL distro (see WslManager) - each instance's config directory is
/// exposed back to Windows via a \\wsl.localhost UNC path rather than living
/// here, since ordinary File I/O works transparently against that path. Those
/// paths are per-instance and therefore live on InstanceSettings; what remains
/// here is only what is genuinely global to the app.
/// </summary>
public static class AppPaths
{
    public static string Root { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "HaWinServer");

    public static string LogsDir { get; } = Path.Combine(Root, "logs");

    public static string AppLogFile { get; } = Path.Combine(LogsDir, "hawinserver.log");

    public static string SettingsFile { get; } = Path.Combine(Root, "settings.json");

    public static void EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(LogsDir);
    }
}
