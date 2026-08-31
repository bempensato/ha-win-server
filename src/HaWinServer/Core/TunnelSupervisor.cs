using System.Diagnostics;

namespace HaWinServer.Core;

public enum TunnelState
{
    Stopped,
    Starting,
    Running,
    Stopping,
    Error,
}

/// <summary>
/// Owns the cloudflared connector's process lifecycle - the network-side twin
/// of HassSupervisor, which owns a Home Assistant container's lifecycle. One
/// instance per machine (not per Home Assistant instance): a single
/// cloudflared process carries every instance's Public Hostname as a
/// separate ingress rule, configured entirely on Cloudflare's side (see
/// CloudflareApi.SyncIngressAsync), so there is exactly one connector to
/// start, stop and watch here regardless of how many instances use it.
///
/// Unlike HassSupervisor, there is no daemon on the other end supervising the
/// process independently (conmon does that for podman containers) -
/// cloudflared here is a direct Windows child process of this app, so if the
/// tray app exits, the tunnel goes with it. That is a deliberate consequence
/// of the "no admin, no Windows service" constraint (see app.manifest):
/// cloudflared's own `service install` needs elevation, which this app never
/// requests.
///
/// Readiness is checked the same way HealthProbe checks Home Assistant - by
/// polling an HTTP endpoint rather than trusting "the process started" -
/// using cloudflared's own `--metrics` endpoint (`GET /ready`), which reports
/// how many edge connections are actually established.
/// </summary>
public sealed class TunnelSupervisor : IDisposable
{
    private const int ReadinessPollIntervalSeconds = 2;

    // Generous on purpose: confirmed on a real machine that the FIRST QUIC
    // handshake through WSL2's virtualized network can take longer than a
    // plain LAN connection would, so a tight timeout here reported "Error"
    // on a tunnel that went on to connect successfully seconds later with
    // nothing left watching to notice and recover.
    private const int InitialReadinessTimeoutSeconds = 60;
    private const int WatchdogIntervalSeconds = 15;

    // cloudflared cycles its edge connections periodically as part of normal
    // operation, which can make a single /ready check land on a brief,
    // harmless gap - confirmed on a real machine: a tunnel that was serving
    // traffic correctly still got flagged Error from one such check with no
    // debounce. Requiring several in a row before believing it separates a
    // real outage from that noise.
    private const int WatchdogFailureThreshold = 3;

    private readonly TunnelSettings _settings;
    private readonly object _sync = new();

    private static readonly HttpClient MetricsClient = new() { Timeout = TimeSpan.FromSeconds(3) };

    private CancellationTokenSource? _runCts;
    private CancellationTokenSource? _watchdogCts;
    private Task? _runTask;

    public TunnelState State { get; private set; } = TunnelState.Stopped;
    public string? LastErrorDetail { get; private set; }

    public event EventHandler? StateChanged;

    public TunnelSupervisor(TunnelSettings settings)
    {
        _settings = settings;
    }

    public bool IsRunningOrTransitioning =>
        State is TunnelState.Starting or TunnelState.Running or TunnelState.Stopping;

    public async Task StartAsync(CancellationToken ct = default)
    {
        lock (_sync)
        {
            if (IsRunningOrTransitioning) return;
        }

        SetState(TunnelState.Starting);

        var cloudflaredPath = Cloudflared.Find();
        if (cloudflaredPath is null)
        {
            LastErrorDetail = "cloudflared.exe was not found. Run \"Set Up Remote Access...\" again to download it.";
            SetState(TunnelState.Error);
            return;
        }

        var token = SecretStore.TryLoadTunnelToken();
        if (token is null)
        {
            LastErrorDetail = "No tunnel token is stored. Run \"Set Up Remote Access...\" again.";
            SetState(TunnelState.Error);
            return;
        }

        // Unlike a Home Assistant container (its own daemon, conmon,
        // supervises it independently of this app), cloudflared has no such
        // safety net - if the tray app was force-killed, crashed, or lost
        // power last time rather than exiting through QuitAsync/StopAsync,
        // a previous cloudflared.exe can be left running as an orphan.
        // Confirmed on a real machine: that orphan keeps serving traffic
        // fine (which is why the tunnel stays reachable), but it also holds
        // the metrics port, so the fresh process this call is about to spawn
        // fails to bind it and exits immediately - reporting Error for a
        // tunnel that, from the outside, still looks like it's working.
        // Clearing out anything already running under this app's own
        // cloudflared.exe path first guarantees a clean, single, trackable
        // process every time.
        KillOrphanedCloudflaredProcesses(cloudflaredPath);

        AppPaths.EnsureCreated();
        var logFile = Path.Combine(AppPaths.LogsDir, "cloudflared.log");

        var arguments = new List<string>
        {
            "tunnel", "--no-autoupdate",
            "--loglevel", "info",
            "--logfile", logFile,
            "--metrics", $"127.0.0.1:{_settings.MetricsPort}",
        };
        if (_settings.Protocol.Equals("http2", StringComparison.OrdinalIgnoreCase))
        {
            // Fallback for networks that block outbound UDP (QUIC, cloudflared's
            // default transport) - the single most common reason a tunnel never
            // connects on a corporate or hotel network.
            arguments.Add("--protocol");
            arguments.Add("http2");
        }
        arguments.Add("run");

        _runCts = new CancellationTokenSource();
        var runToken = _runCts.Token;

        // The token is passed as an environment variable, never as a command
        // line argument - a process's command line is readable by any other
        // process running as this user (Task Manager, Process Explorer,
        // WMI), while its environment block is not exposed the same way.
        var environment = new Dictionary<string, string?> { ["TUNNEL_TOKEN"] = token };

        _runTask = Task.Run(async () =>
        {
            try
            {
                var result = await ProcRunner.RunAsync(
                    cloudflaredPath, arguments, environment, cancellationToken: runToken);

                // Reaching here without our own cancellation means cloudflared
                // exited on its own - a crash or a fatal config error, not a
                // requested stop (StopAsync always cancels runToken first).
                if (!runToken.IsCancellationRequested)
                {
                    LastErrorDetail = "cloudflared exited unexpectedly" +
                        (result.StdErr.Trim().Length > 0 ? ": " + result.StdErr.Trim() : ".");
                    StopWatchdog();
                    SetState(TunnelState.Error);
                }
            }
            catch (Exception ex)
            {
                if (!runToken.IsCancellationRequested)
                {
                    LastErrorDetail = ex.Message;
                    StopWatchdog();
                    SetState(TunnelState.Error);
                }
            }
        }, runToken);

        var isUp = await WaitUntilReadyAsync(
            TimeSpan.FromSeconds(ReadinessPollIntervalSeconds), TimeSpan.FromSeconds(InitialReadinessTimeoutSeconds), ct);

        // The run task's own failure path may have already moved state to
        // Error (e.g. cloudflared rejected a malformed token immediately) -
        // don't overwrite that with a generic timeout message, and don't
        // start a watchdog over a process that already exited.
        if (State != TunnelState.Starting) return;

        if (isUp)
        {
            SetState(TunnelState.Running);
        }
        else
        {
            // Not a dead end: cloudflared is very likely still trying to
            // connect in the background (its own process didn't exit, or
            // the run task's failure path above would have already fired),
            // so the watchdog below is started regardless and will flip
            // this to Running the moment it actually reports ready - rather
            // than leaving the tray showing a stale "Error" for a tunnel
            // that quietly finished connecting seconds later.
            LastErrorDetail =
                "cloudflared did not report ready within the initial timeout - it may still be connecting. " +
                "If this persists, check View cloudflared Log; a blocked outbound connection (UDP 7844) is the " +
                "most common cause, try switching Protocol to http2.";
            SetState(TunnelState.Error);
        }

        StartWatchdog();
    }

    public async Task StopAsync()
    {
        lock (_sync)
        {
            if (State is TunnelState.Stopped) return;
        }

        StopWatchdog();
        SetState(TunnelState.Stopping);

        _runCts?.Cancel();
        if (_runTask is not null)
        {
            try { await _runTask; }
            catch (Exception) { /* the run task reports its own failures via LastErrorDetail */ }
        }

        _runCts?.Dispose();
        _runCts = null;
        _runTask = null;

        SetState(TunnelState.Stopped);
    }

    public async Task RestartAsync()
    {
        await StopAsync();
        await StartAsync();
    }

    /// <summary>Connections currently reported by cloudflared's metrics endpoint - 0 while starting, N while Running.</summary>
    public async Task<int?> GetReadyConnectionCountAsync(CancellationToken ct = default)
    {
        try
        {
            var json = await MetricsClient.GetStringAsync(
                $"http://127.0.0.1:{_settings.MetricsPort}/ready", ct);
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("readyConnections", out var value) ? value.GetInt32() : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private async Task<bool> IsReadyAsync(CancellationToken ct)
    {
        try
        {
            using var response = await MetricsClient.GetAsync($"http://127.0.0.1:{_settings.MetricsPort}/ready", ct);
            return response.IsSuccessStatusCode;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private async Task<bool> WaitUntilReadyAsync(TimeSpan pollInterval, TimeSpan overallTimeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(overallTimeout);

        try
        {
            while (!cts.IsCancellationRequested)
            {
                if (await IsReadyAsync(cts.Token)) return true;
                await Task.Delay(pollInterval, cts.Token);
            }
        }
        catch (OperationCanceledException)
        {
            // Overall timeout - report "not ready" rather than throw.
        }

        return false;
    }

    /// <summary>
    /// Runs for as long as the connector process does - not just while
    /// State is Running - so it can both detect a real outage (several
    /// consecutive failed checks, see WatchdogFailureThreshold) AND recover
    /// automatically from a spurious Error (the initial-timeout case in
    /// StartAsync, or a transient blip caught below) the moment cloudflared
    /// next reports ready, with no manual restart needed either way.
    /// </summary>
    private void StartWatchdog()
    {
        StopWatchdog();
        _watchdogCts = new CancellationTokenSource();
        var token = _watchdogCts.Token;

        _ = Task.Run(async () =>
        {
            var consecutiveFailures = 0;

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

                var ready = await IsReadyAsync(token);
                if (ready)
                {
                    consecutiveFailures = 0;
                    if (State == TunnelState.Error)
                    {
                        LastErrorDetail = null;
                        SetState(TunnelState.Running);
                    }
                    continue;
                }

                consecutiveFailures++;
                if (consecutiveFailures >= WatchdogFailureThreshold && State == TunnelState.Running)
                {
                    LastErrorDetail = "cloudflared stopped reporting ready connections.";
                    SetState(TunnelState.Error);
                }
            }
        }, token);
    }

    /// <summary>
    /// Kills any running process that IS this app's own cloudflared.exe
    /// (matched by exact file path, never by name alone - never touch a
    /// cloudflared the user might be running for something unrelated).
    /// Best-effort throughout: a process that has already exited, or one
    /// this user account can't inspect/kill, is simply skipped rather than
    /// failing the whole start.
    /// </summary>
    private static void KillOrphanedCloudflaredProcesses(string cloudflaredPath)
    {
        Process[] candidates;
        try
        {
            candidates = Process.GetProcessesByName("cloudflared");
        }
        catch (Exception)
        {
            return;
        }

        foreach (var process in candidates)
        {
            try
            {
                if (string.Equals(process.MainModule?.FileName, cloudflaredPath, StringComparison.OrdinalIgnoreCase))
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(5000);
                }
            }
            catch (Exception)
            {
                // Already exited between the snapshot and here, access
                // denied, or some other transient issue - not fatal, the
                // upcoming bind will simply fail again if it really is
                // still holding the port.
            }
            finally
            {
                process.Dispose();
            }
        }
    }

    private void StopWatchdog()
    {
        _watchdogCts?.Cancel();
        _watchdogCts?.Dispose();
        _watchdogCts = null;
    }

    private void SetState(TunnelState newState)
    {
        State = newState;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        StopWatchdog();
        _runCts?.Cancel();
        _runCts?.Dispose();
    }
}
