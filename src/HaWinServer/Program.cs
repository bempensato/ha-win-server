using HaWinServer.Core;

namespace HaWinServer;

internal static class Program
{
    // Fixed GUID so the mutex name is stable across builds/publishes.
    private const string SingleInstanceMutexName = "HaWinServer.SingleInstance.{6F1D8B2E-6C63-4E9C-9A6D-6E6E9C7B1B2E}";

    [STAThread]
    private static void Main()
    {
        using var singleInstanceMutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out var createdNew);
        if (!createdNew)
        {
            MessageBox.Show(
                "HA Win Server is already running. Look for its icon in the system tray.",
                "HA Win Server",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        ApplicationConfiguration.Initialize();

        // TrayContext kicks off async provisioning (wsl.exe/hass) from its
        // constructor, which runs before Application.Run() starts pumping
        // messages. WinForms only auto-installs its SynchronizationContext
        // when a Control's handle is created (NotifyIcon's native window
        // doesn't count), so without this line every `await` continuation in
        // that startup work would resume on a random thread-pool thread
        // instead of the UI thread - breaking Form.Show/MessageBox/Clipboard
        // calls made from it. Messages posted before the loop starts just sit
        // in the queue until Application.Run begins dispatching them.
        SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());

        AppPaths.EnsureCreated();

        using var trayContext = new TrayContext();
        Application.Run(trayContext);

        // Keep the mutex alive for the whole process lifetime.
        GC.KeepAlive(singleInstanceMutex);
    }
}
