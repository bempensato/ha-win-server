using System.Diagnostics;
using System.Text.Json;
using HaWinServer.Core;
using HaWinServer.Menu;

namespace HaWinServer;

/// <summary>
/// Root of the application: owns the tray icon, the context menu, and every
/// long-lived component (settings, one supervisor per instance). Runs as a
/// WinForms ApplicationContext with no main window - the process lives
/// entirely in the tray, matching the reference (Homey-style) UX from the
/// plan.
///
/// Home Assistant itself runs as the official Container image inside a
/// dedicated WSL distro (see WslManager), not as a Python venv - see that
/// file for why.
///
/// One tray process hosts N instances rather than one process per instance:
/// WSL, podman and the image store are machine-level and shared, so
/// provisioning happens once and adding an instance is just a directory and
/// a container. Everything below that is per-instance - separate config
/// directory, separate port, separate pinned version - which is what makes
/// it safe to test an upgrade next to a live instance.
/// </summary>
public sealed class TrayContext : ApplicationContext
{
    private readonly NotifyIcon _notifyIcon;
    private readonly TrayIcons _trayIcons;
    private readonly ContextMenuStrip _menu;
    private readonly List<HassInstance> _instances = new();

    public Settings RootSettings { get; }

    // Cached rather than queried live: MenuBuilder reads these synchronously
    // every time the menu opens, and an "is it installed" check now means an
    // actual wsl.exe/podman round-trip, not a cheap local file check.
    private bool _isInstalled;
    private bool _isBusy; // true while a provisioning/destructive op is running - menu actions are disabled

    public bool IsHomeAssistantInstalled => _isInstalled;
    public bool IsBusy => _isBusy;

    public IReadOnlyList<HassInstance> Instances => _instances;

    /// <summary>The instance the tray icon's double-click targets, and the one started after first-run setup.</summary>
    public HassInstance Selected =>
        _instances.FirstOrDefault(i => i.Id.Equals(RootSettings.SelectedInstanceId, StringComparison.Ordinal))
        ?? _instances[0];

    public TrayContext()
    {
        RootSettings = Settings.Load();
        foreach (var instanceSettings in RootSettings.Instances)
        {
            AddInstanceObject(instanceSettings);
        }

        _trayIcons = new TrayIcons();
        _menu = new ContextMenuStrip();
        _menu.Opening += (_, _) => RebuildMenu();

        _notifyIcon = new NotifyIcon
        {
            Icon = _trayIcons.For(HassState.Stopped),
            Text = "HA Win Server",
            ContextMenuStrip = _menu,
            Visible = true,
        };
        _notifyIcon.DoubleClick += (_, _) => OpenWebUi(Selected);

        LogAppEvent("HA Win Server started.");

        _ = InitializeAsync();
    }

    private HassInstance AddInstanceObject(InstanceSettings settings)
    {
        var instance = new HassInstance(settings);
        instance.Supervisor.StateChanged += (_, _) => OnInstanceStateChanged(instance);
        _instances.Add(instance);
        return instance;
    }

    // ---- startup / provisioning -------------------------------------------------

    private async Task InitializeAsync()
    {
        _isInstalled = await WslManager.IsAvailableAsync() && await WslManager.AnyImageExistsAsync();

        if (_isInstalled)
        {
            await PinImageVersionsAsync();

            // Containers may already be running (or not) independently of this
            // app's own process lifetime - sync displayed state to reality
            // instead of assuming Stopped.
            foreach (var instance in _instances)
            {
                await instance.Supervisor.SyncWithContainerAsync();
            }
        }
        else
        {
            RunSetupFlow();
        }

        RefreshTrayIcon();
    }

    /// <summary>
    /// Moves any instance still pointing at the moving "stable" tag onto the
    /// concrete version that tag currently resolves to. `podman tag` is local
    /// and instant - the same image ID gains a second name - so this costs no
    /// download and cannot change the version an existing instance is running.
    /// It is what stops a later "pull stable to test an update" on one
    /// instance from dragging every other instance along on its next restart.
    /// </summary>
    private async Task PinImageVersionsAsync()
    {
        var unpinned = _instances
            .Where(i => i.Settings.ImageTag.Equals(WslManager.StableTag, StringComparison.Ordinal))
            .ToList();
        if (unpinned.Count == 0) return;

        var version = await WslManager.GetImageVersionAsync(WslManager.StableTag);
        if (version is null)
        {
            LogAppEvent(
                "Could not read a version from the \"stable\" image - leaving instance(s) on that tag. " +
                "They are not protected against a shared-tag version jump until this succeeds.");
            return;
        }

        var tagResult = await WslManager.TagImageAsync(WslManager.StableTag, version);
        if (!tagResult.Succeeded)
        {
            LogAppEvent($"Could not pin the \"stable\" image to {version}: {tagResult.StdErr.Trim()}");
            return;
        }

        foreach (var instance in unpinned)
        {
            instance.Settings.ImageTag = version;
            LogAppEvent($"Pinned instance \"{instance.Name}\" to Home Assistant {version}.");
        }

        RootSettings.Save();
    }

    private void RunSetupFlow()
    {
        var window = new SetupWindow();
        window.RetryRequested += (_, _) =>
        {
            window.ResetForRetry();
            _ = ExecuteSetupAsync(window);
        };
        window.FormClosed += (_, _) => RefreshTrayIcon();
        window.Show();
        _ = ExecuteSetupAsync(window);
    }

    private async Task ExecuteSetupAsync(SetupWindow window)
    {
        _isBusy = true;
        try
        {
            void Log(string line)
            {
                window.AppendLine(line);
                LogAppEvent(line);
            }

            window.SetStatus("Checking for WSL...");
            if (!await WslManager.IsAvailableAsync())
            {
                window.ShowRetryableFailure(
                    "WSL (Windows Subsystem for Linux) isn't set up on this machine. Enabling it for the " +
                    "first time is a one-time Windows feature change that requires administrator rights - " +
                    "this app can't do that on its own. Ask an administrator to run \"wsl --install\", " +
                    "or run it yourself if you have admin rights, then click Retry.");
                return;
            }
            Log("WSL is available.");

            if (!await WslManager.IsDistroInstalledAsync())
            {
                window.SetStatus($"Installing the {WslManager.DistroName} WSL distro...");
                Log($"Running: wsl --install {WslManager.DistroName} --no-launch");
                var installResult = await WslManager.InstallDistroAsync(Log);
                if (!installResult.Succeeded)
                {
                    window.ShowRetryableFailure(
                        "Failed to install the WSL distro. See the log above, then click Retry.");
                    return;
                }
            }
            else
            {
                Log($"{WslManager.DistroName} is already installed.");
            }

            window.SetStatus("Installing podman inside WSL...");
            var bootstrapResult = await WslManager.BootstrapAsync(Log);
            if (!bootstrapResult.Succeeded)
            {
                window.ShowRetryableFailure(
                    "Failed to set up podman inside WSL. See the log above, then click Retry.");
                return;
            }

            window.SetStatus("Pulling the Home Assistant container image (this can take a few minutes)...");
            var pullResult = await WslManager.PullImageAsync(WslManager.StableTag, Log);
            if (!pullResult.Succeeded)
            {
                window.ShowRetryableFailure(
                    "Failed to pull the Home Assistant image. See the log above, then click Retry.");
                return;
            }

            _isInstalled = true;
            await PinImageVersionsAsync();

            var instance = Selected;
            await WslManager.EnsureConfigDirAsync(instance.Settings);

            window.SetStatus("Starting Home Assistant for the first time...");
            await instance.Supervisor.StartAsync();

            if (instance.State == HassState.Running)
            {
                window.ShowSuccess("Home Assistant is up. Opening the web UI...");
                OpenWebUi(instance);
            }
            else
            {
                window.ShowRetryableFailure(
                    "Home Assistant was installed but did not come up. " +
                    (instance.Supervisor.LastErrorDetail ?? "Check the logs, then click Retry."));
            }
        }
        catch (Exception ex)
        {
            LogAppEvent("Setup failed: " + ex);
            window.ShowRetryableFailure("Unexpected error during setup: " + ex.Message);
        }
        finally
        {
            _isBusy = false;
            RefreshTrayIcon();
        }
    }

    // ---- menu / tray icon ----------------------------------------------------

    private void RebuildMenu() => MenuBuilder.Populate(_menu, this);

    private void OnInstanceStateChanged(HassInstance instance)
    {
        RefreshTrayIcon();
        if (instance.State == HassState.Error && instance.Supervisor.LastErrorDetail is { Length: > 0 } detail)
        {
            _notifyIcon.BalloonTipTitle = _instances.Count > 1
                ? $"\"{instance.Name}\" stopped unexpectedly"
                : "Home Assistant stopped unexpectedly";
            _notifyIcon.BalloonTipText = Truncate(detail, 240);
            _notifyIcon.ShowBalloonTip(6000);
            LogAppEvent($"[{instance.Id}] Home Assistant error: {detail}");
        }
    }

    /// <summary>
    /// Worst-of across instances: one broken instance should be visible from
    /// the icon even while the others are healthy.
    /// </summary>
    public HassState AggregateState
    {
        get
        {
            if (_instances.Any(i => i.State == HassState.Error)) return HassState.Error;
            if (_instances.Any(i => i.State == HassState.Running)) return HassState.Running;
            if (_instances.Any(i => i.State is HassState.Starting or HassState.Stopping)) return HassState.Starting;
            return HassState.Stopped;
        }
    }

    private void RefreshTrayIcon()
    {
        _notifyIcon.Icon = _trayIcons.For(AggregateState);

        var text = _instances.Count == 1
            ? $"HA Win Server - {StateLabel(AggregateState)}"
            : $"HA Win Server - {_instances.Count(i => i.State == HassState.Running)}/{_instances.Count} running";
        _notifyIcon.Text = Truncate(text, 127);
    }

    public static string StateLabel(HassState state) => state switch
    {
        HassState.Stopped => "Stopped",
        HassState.Starting => "Starting...",
        HassState.Running => "Running",
        HassState.Stopping => "Stopping...",
        HassState.Error => "Error",
        _ => state.ToString(),
    };

    private static string Truncate(string text, int maxLength) =>
        text.Length <= maxLength ? text : text[..(maxLength - 1)] + "...";

    // ---- per-instance actions invoked from the menu --------------------------

    public async Task StartAsync(HassInstance instance) => await instance.Supervisor.StartAsync();

    public async Task StopAsync(HassInstance instance) => await instance.Supervisor.StopAsync();

    public async Task RestartAsync(HassInstance instance) => await instance.Supervisor.RestartAsync();

    public void OpenWebUi(HassInstance instance) => TryOpenUrl(instance.WebUiUrl);

    public void CopyLanUrl(HassInstance instance)
    {
        if (instance.LanUrl is { } url)
        {
            Clipboard.SetText(url);
        }
    }

    public void OpenConfigFolder(HassInstance instance)
    {
        if (!IsHomeAssistantInstalled) return;
        Process.Start(new ProcessStartInfo { FileName = instance.Settings.WindowsConfigDir, UseShellExecute = true });
    }

    public void ViewHomeAssistantLog(HassInstance instance) => TryOpenFile(instance.Settings.HomeAssistantLogFile);

    public void ViewAppLog() => TryOpenFile(AppPaths.AppLogFile);

    public void SetSelectedInstance(HassInstance instance)
    {
        RootSettings.SelectedInstanceId = instance.Id;
        RootSettings.Save();
    }

    /// <summary>
    /// Stages a backup for restore via Home Assistant's own filesystem-level
    /// restore mechanism (backup_restore.py, checked at startup before the
    /// HTTP server exists) instead of the web UI's upload button - which
    /// caps uploads at a hardcoded, unconfigurable 16 MiB
    /// (components/http/server.py's MAX_CLIENT_SIZE) and blocks larger
    /// backups outright, including during onboarding where there's no
    /// alternative "place it in the backups folder" option either. Copies
    /// the file into the instance's config dir (reachable from Windows via
    /// the same \\wsl.localhost UNC path everything else here uses) and
    /// writes the .HA_RESTORE instruction file Home Assistant looks for on
    /// next boot.
    ///
    /// The path inside that instruction file is the one detail that has to be
    /// right: it is opened by the Home Assistant process INSIDE the container,
    /// so it must be the container-side /config/... path, never the WSL-side
    /// one this app uses for its own file I/O. Getting that wrong fails
    /// silently from the user's point of view - Home Assistant logs "Backup
    /// file ... does not exist", deletes .HA_RESTORE (it always does, in a
    /// finally, to avoid a boot loop) and carries on into onboarding, so the
    /// next start looks like nothing was ever staged.
    /// </summary>
    public async Task RestoreFromBackupAsync(HassInstance instance)
    {
        if (!IsHomeAssistantInstalled || _isBusy) return;

        using var dialog = new OpenFileDialog
        {
            Title = $"Select a Home Assistant backup file to restore into \"{instance.Name}\"",
            Filter = "Home Assistant backup (*.tar)|*.tar|All files (*.*)|*.*",
        };
        if (dialog.ShowDialog() != DialogResult.OK) return;

        var metadata = BackupFile.TryReadMetadata(dialog.FileName);
        if (metadata is null)
        {
            MessageBox.Show(
                $"\"{Path.GetFileName(dialog.FileName)}\" doesn't look like a Home Assistant backup - " +
                "its backup.json could not be read.\n\n" +
                "Pick the .tar file Home Assistant produced, not an archive of it.",
                "Restore from Backup",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return;
        }

        // Home Assistant refuses to restore a backup made by a newer version
        // than the one running, and instances here are pinned to a version -
        // so this is answerable now rather than after a long restart.
        if (BackupFile.NeedsNewerHomeAssistant(metadata.HomeAssistantVersion, instance.Settings.ImageTag) == true)
        {
            MessageBox.Show(
                $"This backup was made with Home Assistant {metadata.HomeAssistantVersion}, but " +
                $"\"{instance.Name}\" is pinned to {instance.Settings.ImageTag}.\n\n" +
                "Home Assistant refuses to restore a backup from a newer version than the one running. " +
                $"Use \"Change Version...\" to put this instance on {metadata.HomeAssistantVersion} or later, " +
                "then restore again.",
                "Restore from Backup",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return;
        }

        // Only ask for a password when the backup actually has one. The
        // backup itself says so, so there is no reason to make the user guess.
        string? password = null;
        if (metadata.IsProtected)
        {
            password = PromptDialog.Show(
                "Restore from Backup",
                "This backup is encrypted. Enter the password that was set when it was created:",
                masked: true);
            if (password is null) return; // cancelled
        }

        var describe =
            $"Backup:  {metadata.Name ?? Path.GetFileName(dialog.FileName)}\n" +
            (metadata.Date is { } date ? $"Created: {date.LocalDateTime:g}\n" : "") +
            $"Made with Home Assistant {metadata.HomeAssistantVersion ?? "(unknown version)"}\n" +
            (metadata.ExcludesDatabase
                ? "Contains NO database - history and long-term statistics will not come back.\n"
                : "Includes the database (history).\n");

        var confirmed = MessageBox.Show(
            describe + "\n" +
            $"This will ERASE the configuration of \"{instance.Name}\" and replace it with the backup's. " +
            "This cannot be undone, and no other instance is affected.\n\n" +
            "Continue?",
            "Restore from Backup",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2);
        if (confirmed != DialogResult.Yes) return;

        await RunProgressAsync($"Restore \"{instance.Name}\"", async (window, log) =>
        {
            var configDir = instance.Settings.WindowsConfigDir;
            var destFileName = "restore-" + Path.GetFileName(dialog.FileName);
            var resultFile = Path.Combine(configDir, RestoreResultFileName);

            window.SetStatus("Staging the backup...");

            // Any result file still lying around is from an earlier attempt;
            // clearing it first is what makes the one found afterwards
            // unambiguously this restore's.
            try { File.Delete(resultFile); } catch (Exception) { /* absent, or busy - neither is fatal */ }

            log($"Copying {dialog.FileName} into {configDir}");
            File.Copy(dialog.FileName, Path.Combine(configDir, destFileName), overwrite: true);

            var instruction = new
            {
                // Container-side path: this is read by Home Assistant inside
                // the container, where the config dir is mounted at /config.
                path = $"/config/{destFileName}",
                password = string.IsNullOrEmpty(password) ? null : password,
                remove_after_restore = true,
                restore_database = true,
                restore_homeassistant = true,
            };
            File.WriteAllText(
                Path.Combine(configDir, ".HA_RESTORE"),
                JsonSerializer.Serialize(instruction));

            log("Wrote .HA_RESTORE (path: /config/" + destFileName + ")");
            LogAppEvent($"[{instance.Id}] Staged backup restore from {dialog.FileName}.");

            // The watcher starts BEFORE the restart, and that ordering is the
            // whole trick. Home Assistant writes .HA_RESTORE_RESULT during the
            // boot that performs the restore, then restarts itself - and on
            // that second boot the backup integration reads the file and
            // deletes it (manager.py unlinks it in a finally) while coming up,
            // before the HTTP server answers. Waiting for RestartAsync to
            // return first therefore always looks at a directory the file has
            // already left.
            using var pollCts = new CancellationTokenSource();
            var resultWatcher = WaitForRestoreResultAsync(resultFile, log, pollCts.Token);

            window.SetStatus("Restarting Home Assistant to apply the restore (this can take a few minutes)...");
            await instance.Supervisor.RestartAsync();

            window.SetStatus("Waiting for the restore result...");

            // If the container came up unusually fast, the file may still be
            // moments away; past that, it is not coming.
            if (await Task.WhenAny(resultWatcher, Task.Delay(TimeSpan.FromSeconds(30))) != resultWatcher)
            {
                pollCts.Cancel();
            }

            var result = await resultWatcher;

            if (result is null)
            {
                window.ShowFailure(
                    "Home Assistant restarted, but the restore result could not be read. Check the Home " +
                    "Assistant log - if the backup was restored, the instance will already show your " +
                    "own configuration.");
                return;
            }

            if (!result.Value.Success)
            {
                LogAppEvent($"[{instance.Id}] Restore failed: {result.Value.Error}");
                window.ShowFailure("Home Assistant could not restore this backup: " + result.Value.Error);
                return;
            }

            LogAppEvent($"[{instance.Id}] Restore succeeded.");
            window.ShowSuccess("Restore complete. Opening Home Assistant...");
            OpenWebUi(instance);
        });
    }

    private const string RestoreResultFileName = ".HA_RESTORE_RESULT";

    /// <summary>
    /// Home Assistant writes .HA_RESTORE_RESULT next to the config on its way
    /// through a restore, whether it worked or not. Reading it is the
    /// difference between telling the user what happened and leaving them to
    /// guess from a log.
    ///
    /// It is a short-lived file - Home Assistant deletes it on the boot that
    /// follows the restore - so this polls frequently and must be started
    /// before the restart, not after it.
    /// </summary>
    private static async Task<(bool Success, string? Error)?> WaitForRestoreResultAsync(
        string resultFile, Action<string> log, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (File.Exists(resultFile))
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(resultFile));
                    var root = doc.RootElement;
                    var success = root.TryGetProperty("success", out var s) && s.ValueKind == JsonValueKind.True;
                    var error = root.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String
                        ? e.GetString()
                        : null;

                    log($"Restore result: success={success}{(error is null ? "" : ", error=" + error)}");
                    return (success, error ?? "no details given");
                }
            }
            catch (Exception)
            {
                // Half-written, or deleted between the Exists check and the
                // read: try again on the next tick.
            }

            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(500), ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        return null;
    }

    /// <summary>
    /// Port/bind changes take effect on the next container (re)creation -
    /// podman fixes port publishing at `run` time, so there's no running
    /// config to edit in place, unlike the old YAML-managed-block approach.
    /// </summary>
    public async Task ChangePortAndBindAsync(HassInstance instance, int newPort, bool bindAllInterfaces)
    {
        if (RootSettings.IsPortInUse(newPort, instance.Id))
        {
            var owner = RootSettings.Instances.First(i => i.Port == newPort && i.Id != instance.Id);
            MessageBox.Show(
                $"Port {newPort} is already assigned to the instance \"{owner.Name}\". " +
                "Two instances cannot publish the same port - pick another one.",
                "HA Win Server",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return;
        }

        instance.Settings.Port = newPort;
        instance.Settings.BindAllInterfaces = bindAllInterfaces;
        RootSettings.Save();

        if (instance.State is HassState.Running or HassState.Starting)
        {
            var confirmed = MessageBox.Show(
                $"\"{instance.Name}\" needs to restart to apply the new network settings. Restart now?",
                "HA Win Server",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question) == DialogResult.Yes;

            if (confirmed)
            {
                await instance.Supervisor.RestartAsync();
            }
        }
    }

    /// <summary>
    /// Assigns USB devices (in practice: a Zigbee or Z-Wave coordinator) to one
    /// instance. Two rules are enforced here rather than left to the user,
    /// because both failure modes are expensive and neither is obvious:
    ///
    /// - Exclusivity. A serial device opens once; the dialog refuses to hand
    ///   the same coordinator to a second instance.
    /// - Empty-state warning. An instance with no .storage that is given a
    ///   Zigbee stick will have ZHA form a NEW network and rewrite the
    ///   coordinator's network key, which un-pairs every physical device on the
    ///   real network. Restoring a backup first is the safe order, so a fresh
    ///   or just-reset instance has to confirm past an explicit warning.
    /// </summary>
    public async Task AssignUsbDeviceAsync(HassInstance instance)
    {
        if (!IsHomeAssistantInstalled || _isBusy) return;

        var assignedElsewhere = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var other in _instances.Where(i => !i.Id.Equals(instance.Id, StringComparison.Ordinal)))
        {
            foreach (var device in other.Settings.UsbDevices)
            {
                assignedElsewhere[device] = other.Name;
            }
        }

        var chosen = UsbDeviceDialog.Show(instance.Name, instance.Settings.UsbDevices, assignedElsewhere);
        if (chosen is null) return;

        var added = chosen.Except(instance.Settings.UsbDevices, StringComparer.Ordinal).ToList();
        if (added.Count > 0 && !await ConfirmCoordinatorRiskAsync(instance, added))
        {
            return;
        }

        var removed = instance.Settings.UsbDevices.Except(chosen, StringComparer.Ordinal).ToList();
        if (added.Count == 0 && removed.Count == 0) return;

        instance.Settings.UsbDevices = chosen.ToList();
        RootSettings.Save();
        LogAppEvent(
            $"[{instance.Id}] USB devices: " +
            (chosen.Count == 0 ? "(none)" : string.Join(", ", chosen)));

        // podman fixes --device at container-creation time, exactly like port
        // publishing, so a change only lands on the next (re)creation.
        if (instance.State is HassState.Running or HassState.Starting)
        {
            var restart = MessageBox.Show(
                $"\"{instance.Name}\" needs to restart for the device change to take effect. Restart now?",
                "HA Win Server",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question);

            if (restart == DialogResult.Yes)
            {
                await instance.Supervisor.RestartAsync();
            }
        }
    }

    private static async Task<bool> ConfirmCoordinatorRiskAsync(HassInstance instance, IReadOnlyList<string> added)
    {
        if (await WslManager.IsOnboardedAsync(instance.Settings))
        {
            return true;
        }

        var answer = MessageBox.Show(
            $"Nobody has completed Home Assistant's onboarding on \"{instance.Name}\" yet - it is a new or " +
            "freshly reset instance.\n\n" +
            "If the device you are assigning is a Zigbee coordinator, Home Assistant will set it up from " +
            "scratch and WRITE A NEW NETWORK KEY TO THE STICK. Every device already paired to it - on this " +
            "machine or on another one - stops working and has to be re-paired by hand.\n\n" +
            "If you are migrating an existing setup, restore the backup FIRST and assign the device " +
            "afterwards.\n\n" +
            "Device:\n" + string.Join("\n", added.Select(d => "  " + Path.GetFileName(d))) + "\n\n" +
            "Assign it anyway?",
            "This can break an existing Zigbee network",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2);

        return answer == DialogResult.Yes;
    }

    // ---- instance lifecycle: reset, add, clone, rename, delete ---------------

    /// <summary>
    /// Wipes this instance's Home Assistant data and brings it back up empty,
    /// which puts Home Assistant back into its own onboarding wizard - there
    /// is no app-side "configured" flag to clear, an empty /config IS the
    /// signal. Scoped strictly to the one instance: the podman image, the WSL
    /// distro, this instance's port/bind, and every other instance are left
    /// untouched.
    /// </summary>
    public async Task ResetInstanceAsync(HassInstance instance)
    {
        if (!IsHomeAssistantInstalled || _isBusy) return;

        var confirmed = ConfirmDestructive(
            "Reset Instance",
            $"This will PERMANENTLY DELETE all Home Assistant data of the instance \"{instance.Name}\":\n\n" +
            "  • users, logins and long-lived tokens (.storage)\n" +
            "  • all devices, entities and integrations\n" +
            "  • automations, scenes, scripts and dashboards\n" +
            "  • the recorder database (all history)\n" +
            "  • backups stored inside this instance\n\n" +
            $"Folder: {instance.Settings.WindowsConfigDir}\n\n" +
            "The instance then restarts empty and begins Home Assistant's onboarding again. " +
            $"Its port ({instance.Settings.Port}) and version ({instance.Settings.ImageTag}) are kept, " +
            "and no other instance is affected.\n\n" +
            "This cannot be undone. Continue?",
            instance.Name);
        if (!confirmed) return;

        await RunProgressAsync($"Reset \"{instance.Name}\"", async (window, log) =>
        {
            window.SetStatus($"Stopping \"{instance.Name}\"...");
            await instance.Supervisor.StopAsync();

            // The container must be gone before the directory is: deleting it
            // underneath a live container leaves the bind mount attached to
            // the old, now-unlinked inode, and the "fresh" instance would come
            // back up on invisible leftovers.
            await WslManager.RemoveContainerAsync(instance.Settings, log);

            window.SetStatus("Deleting configuration...");
            var resetResult = await WslManager.ResetConfigDirAsync(instance.Settings, log);
            if (!resetResult.Succeeded)
            {
                window.ShowFailure("Could not delete the configuration. See the log above - nothing was started.");
                return;
            }
            LogAppEvent($"[{instance.Id}] Reset: wiped {instance.Settings.LinuxConfigDir}.");

            window.SetStatus("Starting Home Assistant...");
            await instance.Supervisor.StartAsync();

            if (instance.State == HassState.Running)
            {
                window.ShowSuccess("Instance reset. Opening Home Assistant's onboarding in the browser...");
                OpenWebUi(instance);
            }
            else
            {
                window.ShowFailure(
                    "The instance was reset, but did not come back up. " +
                    (instance.Supervisor.LastErrorDetail ?? "Check the logs, then start it from the menu."));
            }
        });
    }

    public async Task AddInstanceAsync()
    {
        if (!IsHomeAssistantInstalled || _isBusy) return;

        var name = PromptForInstanceName("Add Instance", "Name for the new instance:", $"Test {RootSettings.Instances.Count + 1}");
        if (name is null) return;

        if (!TryPromptForPort("Add Instance", RootSettings.SuggestFreePort(), exceptInstanceId: null, out var port)) return;

        var settings = new InstanceSettings
        {
            Id = RootSettings.MakeUniqueId(name),
            Name = name,
            Port = port,
            BindAllInterfaces = true,
            UseLegacyPaths = false,
            ImageTag = await DefaultImageTagForNewInstanceAsync(),
        };

        RootSettings.Instances.Add(settings);
        RootSettings.Save();
        var instance = AddInstanceObject(settings);

        await RunProgressAsync($"Add \"{name}\"", async (window, log) =>
        {
            window.SetStatus("Creating the instance directory...");
            var mkdirResult = await WslManager.EnsureConfigDirAsync(settings, log);
            if (!mkdirResult.Succeeded)
            {
                window.ShowFailure("Could not create the instance directory. See the log above.");
                return;
            }
            LogAppEvent($"[{settings.Id}] Created instance \"{name}\" on port {port}, version {settings.ImageTag}.");

            window.SetStatus("Starting Home Assistant...");
            await instance.Supervisor.StartAsync();

            if (instance.State == HassState.Running)
            {
                window.ShowSuccess($"\"{name}\" is up on port {port}. Opening onboarding in the browser...");
                OpenWebUi(instance);
            }
            else
            {
                window.ShowFailure(
                    "The instance was created but did not come up. " +
                    (instance.Supervisor.LastErrorDetail ?? "Check the logs, then start it from the menu."));
            }
        });
    }

    /// <summary>
    /// Copies an existing instance's config onto a new port - the point of
    /// the whole multi-instance feature: try an upgrade or a risky change
    /// against real data without touching the live instance.
    /// </summary>
    public async Task CloneInstanceAsync(HassInstance source)
    {
        if (!IsHomeAssistantInstalled || _isBusy) return;

        var name = PromptForInstanceName("Clone Instance", $"Name for the copy of \"{source.Name}\":", $"{source.Name} copy");
        if (name is null) return;

        if (!TryPromptForPort("Clone Instance", RootSettings.SuggestFreePort(), exceptInstanceId: null, out var port)) return;

        // Copying a live instance means copying a SQLite database that is
        // being written to, which can produce a torn recorder DB in the copy.
        // Stopping the source for the duration is the safe answer, but it is
        // exactly what the user may be trying to avoid, so it's their call.
        var stopSource = false;
        if (source.State is HassState.Running or HassState.Starting)
        {
            var answer = MessageBox.Show(
                $"\"{source.Name}\" is running. Copying it while it writes to its database can leave the " +
                "COPY with a corrupt recorder database (the original is never at risk).\n\n" +
                "Yes - stop it for the duration of the copy, then start it again (recommended)\n" +
                "No - copy it while it keeps running\n\n" +
                "Note: the copy inherits the original's users, logins and tokens.",
                "Clone Instance",
                MessageBoxButtons.YesNoCancel,
                MessageBoxIcon.Question);

            if (answer == DialogResult.Cancel) return;
            stopSource = answer == DialogResult.Yes;
        }

        var settings = new InstanceSettings
        {
            Id = RootSettings.MakeUniqueId(name),
            Name = name,
            Port = port,
            BindAllInterfaces = source.Settings.BindAllInterfaces,
            UseLegacyPaths = false,
            // Same version as the source: a clone is a baseline to compare
            // against, so it should start as one changed variable, not two.
            ImageTag = source.Settings.ImageTag,
        };

        RootSettings.Instances.Add(settings);
        RootSettings.Save();
        var clone = AddInstanceObject(settings);

        await RunProgressAsync($"Clone \"{source.Name}\"", async (window, log) =>
        {
            if (stopSource)
            {
                window.SetStatus($"Stopping \"{source.Name}\"...");
                await source.Supervisor.StopAsync();
            }

            window.SetStatus("Copying configuration (this can take a while for a large database)...");
            var copyResult = await WslManager.CloneConfigDirAsync(source.Settings, settings, log);

            if (stopSource)
            {
                window.SetStatus($"Starting \"{source.Name}\" again...");
                await source.Supervisor.StartAsync();
            }

            if (!copyResult.Succeeded)
            {
                window.ShowFailure("Could not copy the configuration. See the log above.");
                return;
            }
            LogAppEvent($"[{settings.Id}] Cloned from \"{source.Name}\" onto port {port}.");

            window.SetStatus($"Starting \"{name}\"...");
            await clone.Supervisor.StartAsync();

            if (clone.State == HassState.Running)
            {
                window.ShowSuccess($"\"{name}\" is up on port {port} with a copy of \"{source.Name}\".");
                OpenWebUi(clone);
            }
            else
            {
                window.ShowFailure(
                    "The clone was created but did not come up. " +
                    (clone.Supervisor.LastErrorDetail ?? "Check the logs, then start it from the menu."));
            }
        });
    }

    public void RenameInstance(HassInstance instance)
    {
        var name = PromptForInstanceName(
            "Rename Instance",
            "New display name (its data, port and container are not affected):",
            instance.Name);
        if (name is null) return;

        var oldName = instance.Name;
        instance.Settings.Name = name;
        RootSettings.Save();
        LogAppEvent($"[{instance.Id}] Renamed \"{oldName}\" to \"{name}\".");
    }

    public async Task DeleteInstanceAsync(HassInstance instance)
    {
        if (!IsHomeAssistantInstalled || _isBusy) return;

        if (_instances.Count <= 1)
        {
            MessageBox.Show(
                "This is the only instance - it can't be deleted. Use \"Reset Instance...\" to wipe its data instead.",
                "HA Win Server",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        var confirmed = ConfirmDestructive(
            "Delete Instance",
            $"This will PERMANENTLY DELETE the instance \"{instance.Name}\" and all of its Home Assistant " +
            "data - configuration, database, backups, everything.\n\n" +
            $"Folder: {instance.Settings.WindowsConfigDir}\n\n" +
            "No other instance is affected. This cannot be undone. Continue?",
            instance.Name);
        if (!confirmed) return;

        await RunProgressAsync($"Delete \"{instance.Name}\"", async (window, log) =>
        {
            window.SetStatus($"Stopping \"{instance.Name}\"...");
            await instance.Supervisor.StopAsync();
            await WslManager.RemoveContainerAsync(instance.Settings, log);

            window.SetStatus("Deleting data...");
            var deleteResult = await WslManager.DeleteInstanceDirAsync(instance.Settings, log);
            if (!deleteResult.Succeeded)
            {
                window.ShowFailure("Could not delete the instance's data. See the log above - the instance was kept.");
                return;
            }

            _instances.Remove(instance);
            RootSettings.Instances.RemoveAll(i => i.Id.Equals(instance.Id, StringComparison.Ordinal));
            if (RootSettings.SelectedInstanceId == instance.Id)
            {
                RootSettings.SelectedInstanceId = _instances[0].Id;
            }
            RootSettings.Save();
            instance.Dispose();

            LogAppEvent($"[{instance.Id}] Deleted instance \"{instance.Name}\".");
            window.ShowSuccess($"\"{instance.Name}\" has been deleted.");
        });
    }

    // ---- versions: pinned per instance ---------------------------------------

    /// <summary>
    /// Updates ONE instance. The pull happens first, while the instance keeps
    /// serving on its current image, and only that instance is then recreated
    /// on the new tag - every other instance stays on the version it is
    /// pinned to, which is what makes "test the upgrade here first" real.
    /// </summary>
    public async Task CheckForUpdatesAsync(HassInstance instance)
    {
        if (!IsHomeAssistantInstalled || _isBusy) return;

        var currentVersion = await ResolveInstanceVersionAsync(instance);
        if (currentVersion is null)
        {
            MessageBox.Show(
                "Could not determine which Home Assistant version this instance is running.",
                "HA Win Server",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return;
        }

        var result = await UpdateChecker.CheckAsync(currentVersion);
        if (result is null)
        {
            MessageBox.Show(
                "Could not reach PyPI to check for updates. Check your network connection.",
                "HA Win Server",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return;
        }

        if (!result.UpdateAvailable)
        {
            MessageBox.Show(
                $"\"{instance.Name}\" is up to date (version {result.InstalledVersion}).",
                "HA Win Server",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        var others = _instances.Count - 1;
        var confirmed = MessageBox.Show(
            $"Home Assistant {result.LatestVersion} is available (\"{instance.Name}\" runs {result.InstalledVersion}).\n\n" +
            $"Update \"{instance.Name}\" now? It will be stopped and restarted on the new version." +
            (others > 0
                ? $"\n\nThe other {(others == 1 ? "instance stays" : $"{others} instances stay")} on their own " +
                  "pinned version and won't be touched."
                : string.Empty),
            "Update available",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question);

        if (confirmed != DialogResult.Yes) return;

        await ApplyVersionAsync(instance, result.LatestVersion, $"Update \"{instance.Name}\"");
    }

    /// <summary>
    /// Moves an instance to any locally available (or downloadable) version.
    /// This is both the rollback path and the "promote the version I already
    /// verified on the test instance" path - when the tag is already local,
    /// switching costs nothing but a container restart.
    /// </summary>
    public async Task ChangeVersionAsync(HassInstance instance)
    {
        if (!IsHomeAssistantInstalled || _isBusy) return;

        var localTags = await WslManager.ListLocalTagsAsync();
        var available = localTags.Count > 0
            ? "Available without downloading: " + string.Join(", ", localTags.Take(8))
            : "No local versions found.";

        var input = PromptDialog.Show(
            $"Version of \"{instance.Name}\"",
            $"Home Assistant version tag. {available}",
            instance.Settings.ImageTag);
        if (input is null) return;

        var tag = input.Trim();
        if (tag.Length == 0 || tag.Equals(instance.Settings.ImageTag, StringComparison.Ordinal)) return;

        await ApplyVersionAsync(instance, tag, $"Version of \"{instance.Name}\"");
    }

    private async Task ApplyVersionAsync(HassInstance instance, string tag, string windowTitle)
    {
        await RunProgressAsync(windowTitle, async (window, log) =>
        {
            if (!await WslManager.ImageExistsAsync(tag))
            {
                window.SetStatus($"Downloading Home Assistant {tag} (this can take a few minutes)...");
                var pullResult = await WslManager.PullImageAsync(tag, log);
                if (!pullResult.Succeeded)
                {
                    window.ShowFailure(
                        $"Could not download version {tag}. See the log above - \"{instance.Name}\" is unchanged " +
                        $"and still on {instance.Settings.ImageTag}.");
                    return;
                }
            }
            else
            {
                log($"Version {tag} is already available locally - no download needed.");
            }

            var previousTag = instance.Settings.ImageTag;
            var wasRunning = instance.State is HassState.Running or HassState.Starting;

            instance.Settings.ImageTag = tag;
            RootSettings.Save();
            LogAppEvent($"[{instance.Id}] Version {previousTag} -> {tag}.");

            if (wasRunning)
            {
                window.SetStatus($"Restarting \"{instance.Name}\" on {tag}...");
                await instance.Supervisor.RestartAsync();

                if (instance.State != HassState.Running)
                {
                    window.ShowFailure(
                        $"\"{instance.Name}\" is now pinned to {tag} but did not come back up. " +
                        (instance.Supervisor.LastErrorDetail ??
                         $"You can switch it back to {previousTag} with \"Change Version...\"."));
                    return;
                }
            }

            window.ShowSuccess(
                wasRunning
                    ? $"\"{instance.Name}\" is running Home Assistant {tag}."
                    : $"\"{instance.Name}\" is pinned to {tag} and will use it on next start.");
        });
    }

    /// <summary>
    /// Pinned versions mean old images are never overwritten - that is what
    /// makes rollback instant, and also what makes them pile up at roughly
    /// 1.5 GB each. Only tags no instance references are ever offered here.
    /// </summary>
    public async Task PruneUnusedImagesAsync()
    {
        if (!IsHomeAssistantInstalled || _isBusy) return;

        var localTags = await WslManager.ListLocalTagsAsync();
        var inUse = _instances.Select(i => i.Settings.ImageTag).ToHashSet(StringComparer.Ordinal);
        var unused = localTags.Where(t => !inUse.Contains(t)).ToList();

        if (unused.Count == 0)
        {
            MessageBox.Show(
                "Every Home Assistant image on this machine is in use by an instance - nothing to remove.",
                "HA Win Server",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        var confirmed = MessageBox.Show(
            "These Home Assistant images are not used by any instance and can be removed:\n\n" +
            string.Join("\n", unused.Select(t => "  • " + t)) + "\n\n" +
            "Removing them frees disk space, but a later rollback to one of these versions would " +
            "have to download it again. Remove them?",
            "Remove Unused Versions",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question,
            MessageBoxDefaultButton.Button2);
        if (confirmed != DialogResult.Yes) return;

        await RunProgressAsync("Remove Unused Versions", async (window, log) =>
        {
            var removed = 0;
            foreach (var tag in unused)
            {
                window.SetStatus($"Removing {tag}...");
                var result = await WslManager.RemoveImageAsync(tag);
                if (result.Succeeded)
                {
                    removed++;
                    log($"Removed {tag}.");
                }
                else
                {
                    log($"Could not remove {tag}: {result.StdErr.Trim()}");
                }
            }

            LogAppEvent($"Removed {removed} unused Home Assistant image(s).");
            window.ShowSuccess($"Removed {removed} of {unused.Count} unused version(s).");
        });
    }

    /// <summary>The concrete version an instance runs: its pinned tag, or the label of whatever "stable" currently is.</summary>
    private static async Task<string?> ResolveInstanceVersionAsync(HassInstance instance)
    {
        var tag = instance.Settings.ImageTag;
        return tag.Equals(WslManager.StableTag, StringComparison.Ordinal)
            ? await WslManager.GetImageVersionAsync(tag)
            : tag;
    }

    /// <summary>Newest version already on this machine, so creating an instance never triggers a download.</summary>
    private static async Task<string> DefaultImageTagForNewInstanceAsync()
    {
        var tags = await WslManager.ListLocalTagsAsync();
        return tags.FirstOrDefault(t => !t.Equals(WslManager.StableTag, StringComparison.Ordinal))
            ?? WslManager.StableTag;
    }

    // ---- global actions -------------------------------------------------------

    public void ToggleRunAtLogin()
    {
        AutoStart.SetEnabled(!AutoStart.IsEnabled());
    }

    public void ShowAbout()
    {
        MessageBox.Show(
            "HA Win Server\n\n" +
            "A tray app that runs Home Assistant's official Container image inside a dedicated " +
            "WSL (Windows Subsystem for Linux) distro, without administrator rights - as long as " +
            "WSL is already set up on this machine.\n\n" +
            "Uses podman rather than Docker Desktop: same official image, far less overhead " +
            "(confirmed: no separate GUI app or extra VM).\n\n" +
            "Instances run side by side on their own ports, each with its own configuration and " +
            "its own pinned Home Assistant version, so an upgrade can be tried on one without " +
            "affecting the others. See the README for details.",
            "About HA Win Server",
            MessageBoxButtons.OK,
            MessageBoxIcon.Information);
    }

    public async Task QuitAsync()
    {
        var active = _instances.Where(i => i.State is HassState.Running or HassState.Starting).ToList();
        if (active.Count > 0)
        {
            var what = active.Count == 1
                ? $"\"{active[0].Name}\" is running."
                : $"{active.Count} instances are running ({string.Join(", ", active.Select(i => i.Name))}).";

            var result = MessageBox.Show(
                what + " Stop " + (active.Count == 1 ? "it" : "them") + " and quit?\n\n" +
                "Choosing \"No\" leaves Home Assistant running in the background - " +
                "it does not depend on this tray app staying open.",
                "HA Win Server",
                MessageBoxButtons.YesNoCancel,
                MessageBoxIcon.Question);

            if (result == DialogResult.Cancel) return;
            if (result == DialogResult.Yes)
            {
                foreach (var instance in active)
                {
                    await instance.Supervisor.StopAsync();
                }
            }
        }

        LogAppEvent("HA Win Server exiting.");
        _notifyIcon.Visible = false;
        Application.Exit();
    }

    // ---- shared UI plumbing ---------------------------------------------------

    /// <summary>
    /// Two-step confirmation for anything that destroys data: an explicit
    /// warning, then the instance's own name typed back. A misclick can pass
    /// the first step; it cannot pass the second.
    /// </summary>
    private static bool ConfirmDestructive(string title, string body, string phrase)
    {
        var warned = MessageBox.Show(
            body,
            title,
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2);
        if (warned != DialogResult.Yes) return false;

        var typed = PromptDialog.Show(title, $"Type the instance name \"{phrase}\" to confirm:");
        if (typed is null) return false;

        if (!typed.Trim().Equals(phrase, StringComparison.Ordinal))
        {
            MessageBox.Show(
                "The name didn't match - nothing has been changed.",
                title,
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return false;
        }

        return true;
    }

    /// <summary>Runs a long operation against the setup/progress window, with the whole menu disabled for the duration.</summary>
    private async Task RunProgressAsync(string title, Func<SetupWindow, Action<string>, Task> work)
    {
        var window = new SetupWindow { Text = "HA Win Server - " + title };
        window.FormClosed += (_, _) => RefreshTrayIcon();
        window.Show();

        _isBusy = true;
        try
        {
            void Log(string line)
            {
                window.AppendLine(line);
                LogAppEvent(line);
            }

            await work(window, Log);
        }
        catch (Exception ex)
        {
            LogAppEvent($"{title} failed: {ex}");
            window.ShowFailure("Unexpected error: " + ex.Message);
        }
        finally
        {
            _isBusy = false;
            RefreshTrayIcon();
        }
    }

    private string? PromptForInstanceName(string title, string label, string initialValue)
    {
        while (true)
        {
            var input = PromptDialog.Show(title, label, initialValue);
            if (input is null) return null;

            var name = input.Trim();
            if (name.Length == 0)
            {
                MessageBox.Show("Enter a name.", title, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                continue;
            }

            if (RootSettings.Instances.Any(i => i.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
            {
                MessageBox.Show(
                    $"An instance named \"{name}\" already exists. Names must be unique - they are what " +
                    "the confirmation prompts ask you to type.",
                    title,
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                initialValue = name;
                continue;
            }

            return name;
        }
    }

    public bool TryPromptForPort(string title, int initialPort, string? exceptInstanceId, out int port)
    {
        port = initialPort;

        while (true)
        {
            var input = PromptDialog.Show(title, "Home Assistant port (1-65535):", port.ToString());
            if (input is null) return false;

            if (!int.TryParse(input, out var parsed) || parsed is < 1 or > 65535)
            {
                MessageBox.Show(
                    "Enter a valid port number between 1 and 65535.",
                    title,
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                continue;
            }

            if (RootSettings.IsPortInUse(parsed, exceptInstanceId))
            {
                var owner = RootSettings.Instances.First(i => i.Port == parsed && i.Id != exceptInstanceId);
                MessageBox.Show(
                    $"Port {parsed} is already assigned to the instance \"{owner.Name}\". Pick another one.",
                    title,
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                port = parsed;
                continue;
            }

            // An instance that is running is of course listening on its own
            // port - only warn about a port somebody ELSE holds.
            var ownPort = exceptInstanceId is null ? null : RootSettings.Find(exceptInstanceId)?.Port;
            if (parsed != ownPort && NetworkInfo.IsPortListening(parsed))
            {
                var proceed = MessageBox.Show(
                    $"Something on this machine is already listening on port {parsed}. Home Assistant " +
                    "will fail to start if that port stays taken. Use it anyway?",
                    title,
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Warning,
                    MessageBoxDefaultButton.Button2);
                if (proceed != DialogResult.Yes)
                {
                    port = parsed;
                    continue;
                }
            }

            port = parsed;
            return true;
        }
    }

    private static void TryOpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
        }
        catch (Exception)
        {
            // No default browser association or similar - not fatal, user can copy the URL instead.
        }
    }

    private static void TryOpenFile(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                MessageBox.Show(
                    $"Log file not found yet:\n{path}",
                    "HA Win Server",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                return;
            }

            Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
        }
        catch (Exception)
        {
            // No default text editor association or similar - not fatal.
        }
    }

    private static readonly object AppLogSync = new();

    public static void LogAppEvent(string message)
    {
        try
        {
            AppPaths.EnsureCreated();
            lock (AppLogSync)
            {
                File.AppendAllText(
                    AppPaths.AppLogFile,
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}");
            }
        }
        catch (Exception)
        {
            // Logging must never crash the app.
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
            _menu.Dispose();
            _trayIcons.Dispose();
            foreach (var instance in _instances)
            {
                instance.Dispose();
            }
        }
        base.Dispose(disposing);
    }
}
