namespace HaWinServer.Core;

public enum HassState
{
    Stopped,
    Starting,
    Running,
    Stopping,
    Error,
}

/// <summary>
/// Owns Home Assistant's container lifecycle: starting it, waiting for it to
/// actually come up (health probe, not just "podman run returned"), stopping
/// it, and reporting state transitions for the tray icon/menu.
///
/// Unlike the old venv-based hass.exe, a container isn't tied to any Windows
/// process we hold open - conmon supervises it independently inside the WSL
/// VM (confirmed on a real machine: killing the wsl.exe that launched
/// `podman run` does not stop the container). So there's no Job Object here
/// - state is tracked by issuing explicit `podman run`/`stop` commands and,
/// while Running, a lightweight polling watchdog that notices if the
/// container stops on its own (crash, `podman stop` from outside this app,
/// etc.) and reports it as HassState.Error.
///
/// `podman stop` sends SIGTERM to the container's own PID 1. Since that's
/// real Linux, Home Assistant's normal signal handling works without any of
/// the Windows asyncio gaps the old native/venv approach had to route
/// around - this alone is a clean, sufficient graceful shutdown, which is
/// why there's no REST-API-based stop path here (the old Access Token
/// feature existed only to work around that Windows-specific gap).
/// </summary>
public sealed class HassSupervisor : IDisposable
{
    private const int WatchdogIntervalSeconds = 20;

    private readonly InstanceSettings _settings;
    private readonly object _sync = new();

    private CancellationTokenSource? _watchdogCts;

    public HassState State { get; private set; } = HassState.Stopped;
    public string? LastErrorDetail { get; private set; }

    public event EventHandler? StateChanged;

    public HassSupervisor(InstanceSettings settings)
    {
        _settings = settings;
    }

    public bool IsRunningOrTransitioning =>
        State is HassState.Starting or HassState.Running or HassState.Stopping;

    /// <summary>
    /// Syncs this supervisor's state with whatever the container is actually
    /// doing right now - used at app startup, since the container may have
    /// kept running (or been stopped) entirely independently of this app's
    /// own process lifetime.
    /// </summary>
    public async Task SyncWithContainerAsync(CancellationToken ct = default)
    {
        var running = await WslManager.IsContainerRunningAsync(_settings, ct);
        if (running)
        {
            SetState(HassState.Running);
            StartWatchdog();
        }
        else
        {
            SetState(HassState.Stopped);
        }
    }

    public async Task StartAsync(CancellationToken ct = default)
    {
        lock (_sync)
        {
            if (IsRunningOrTransitioning) return;
        }

        SetState(HassState.Starting);

        try
        {
            // Checked before podman is asked to start: an assigned device that
            // isn't attached yet is by far the most common reason a start fails
            // on a machine with a Zigbee stick, and "podman: no such file or
            // directory" is a poor way to say that.
            var missingDevices = await WslManager.FindMissingDevicesAsync(_settings, ct);
            if (missingDevices.Count > 0)
            {
                LastErrorDetail =
                    "These USB devices are assigned to this instance but are not currently present in WSL:\n" +
                    string.Join("\n", missingDevices.Select(d => "  " + d)) +
                    "\n\nAfter a Windows restart a device has to be handed back to WSL with usbipd. " +
                    "Use \"Assign USB Device...\" to reattach it.";
                SetState(HassState.Error);
                return;
            }

            var runResult = await WslManager.RunContainerAsync(_settings, onOutputLine: null, ct);

            if (!runResult.Succeeded)
            {
                LastErrorDetail = FirstNonEmpty(runResult.StdErr, runResult.StdOut, "podman run failed.");
                SetState(HassState.Error);
                return;
            }

            var isUp = await HealthProbe.WaitUntilUpAsync(
                _settings.Port,
                pollInterval: TimeSpan.FromSeconds(2),
                overallTimeout: TimeSpan.FromMinutes(5),
                ct);

            if (!isUp)
            {
                LastErrorDetail = "Home Assistant did not respond in time.\n" + await FetchRecentLogsAsync();
                SetState(HassState.Error);
                return;
            }

            SetState(HassState.Running);
            StartWatchdog();
        }
        catch (Exception ex)
        {
            LastErrorDetail = ex.Message;
            SetState(HassState.Error);
        }
    }

    public async Task StopAsync(CancellationToken ct = default)
    {
        lock (_sync)
        {
            if (State is HassState.Stopped) return;
        }

        StopWatchdog();
        SetState(HassState.Stopping);

        try
        {
            await WslManager.StopContainerAsync(_settings, ct: ct);
        }
        catch (Exception)
        {
            // Best-effort - fall through to Stopped regardless so the UI
            // never gets stuck on "Stopping" forever from a transient error.
        }

        SetState(HassState.Stopped);
    }

    public async Task RestartAsync(CancellationToken ct = default)
    {
        await StopAsync(ct);
        await StartAsync(ct);
    }

    private void StartWatchdog()
    {
        StopWatchdog();
        _watchdogCts = new CancellationTokenSource();
        var token = _watchdogCts.Token;

        _ = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(WatchdogIntervalSeconds), token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                if (token.IsCancellationRequested) return;

                bool stillRunning;
                try
                {
                    stillRunning = await WslManager.IsContainerRunningAsync(_settings, token);
                }
                catch (Exception)
                {
                    continue; // transient wsl.exe hiccup - don't flap state over it
                }

                if (!stillRunning && State == HassState.Running)
                {
                    LastErrorDetail = "Home Assistant's container stopped unexpectedly.\n" + await FetchRecentLogsAsync();
                    SetState(HassState.Error);
                    return;
                }
            }
        }, token);
    }

    private void StopWatchdog()
    {
        _watchdogCts?.Cancel();
        _watchdogCts?.Dispose();
        _watchdogCts = null;
    }

    private async Task<string> FetchRecentLogsAsync()
    {
        try
        {
            var result = await WslManager.GetContainerLogsAsync(_settings, tailLines: 20);
            return result.Succeeded ? result.StdOut : string.Empty;
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    private static string FirstNonEmpty(params string[] candidates) =>
        candidates.FirstOrDefault(c => !string.IsNullOrWhiteSpace(c)) ?? string.Empty;

    private void SetState(HassState newState)
    {
        State = newState;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        StopWatchdog();
    }
}
