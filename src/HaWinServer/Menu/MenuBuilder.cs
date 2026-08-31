using HaWinServer.Core;

namespace HaWinServer.Menu;

/// <summary>
/// Builds the tray context menu from scratch every time it is about to open
/// (see TrayContext's ContextMenuStrip.Opening handler). Rebuilding instead
/// of mutating a long-lived menu keeps state, port, version and the autostart
/// checkbox always accurate with zero separate refresh/polling logic.
///
/// Two layouts over one set of items: with a single instance the menu stays
/// exactly as flat as it was before multi-instance support (that is the
/// common case and it shouldn't cost an extra click), and with several the
/// same items move into one submenu per instance.
/// </summary>
public static class MenuBuilder
{
    public static void Populate(ContextMenuStrip menu, TrayContext ctx)
    {
        menu.Items.Clear();

        var multi = ctx.Instances.Count > 1;

        if (multi)
        {
            menu.Items.Add(new ToolStripMenuItem(
                $"● {ctx.Instances.Count(i => i.State == HassState.Running)} of {ctx.Instances.Count} running")
            { Enabled = false });
            menu.Items.Add(new ToolStripSeparator());

            foreach (var instance in ctx.Instances)
            {
                var root = new ToolStripMenuItem(InstanceLabel(instance));
                BuildInstanceItems(root.DropDownItems, ctx, instance);
                menu.Items.Add(root);
            }
        }
        else
        {
            var only = ctx.Instances[0];
            menu.Items.Add(new ToolStripMenuItem($"● {TrayContext.StateLabel(only.State)}") { Enabled = false });
            menu.Items.Add(new ToolStripSeparator());
            BuildInstanceItems(menu.Items, ctx, only);
        }

        menu.Items.Add(new ToolStripSeparator());
        BuildGlobalItems(menu.Items, ctx);
    }

    private static string InstanceLabel(HassInstance instance) =>
        $"● {instance.Name} - {TrayContext.StateLabel(instance.State)} ({instance.Settings.Port}) · {instance.Settings.ImageTag}";

    /// <summary>
    /// Everything that acts on one instance. Used both as the whole menu body
    /// (single instance) and as one instance's submenu (several), so the two
    /// layouts can never drift apart.
    /// </summary>
    private static void BuildInstanceItems(ToolStripItemCollection target, TrayContext ctx, HassInstance instance)
    {
        var busy = ctx.IsBusy;
        var installed = ctx.IsHomeAssistantInstalled;
        var state = instance.State;
        var multi = ctx.Instances.Count > 1;

        target.Add(new ToolStripMenuItem("Open Web UI", null, (_, _) => ctx.OpenWebUi(instance))
        {
            Enabled = installed && !busy,
        });

        target.Add(new ToolStripMenuItem("Copy LAN URL", null, (_, _) => ctx.CopyLanUrl(instance))
        {
            Enabled = installed && instance.LanUrl is not null && !busy,
        });

        target.Add(new ToolStripSeparator());

        var canStart = installed && !busy && state is HassState.Stopped or HassState.Error;
        var canStopOrRestart = installed && !busy && state is HassState.Running or HassState.Starting;

        target.Add(new ToolStripMenuItem("Start", null, async (_, _) => await ctx.StartAsync(instance)) { Enabled = canStart });
        target.Add(new ToolStripMenuItem("Stop", null, async (_, _) => await ctx.StopAsync(instance)) { Enabled = canStopOrRestart });
        target.Add(new ToolStripMenuItem("Restart", null, async (_, _) => await ctx.RestartAsync(instance)) { Enabled = canStopOrRestart });

        target.Add(new ToolStripSeparator());
        target.Add(BuildNetworkSubmenu(ctx, instance, installed, busy));

        target.Add(new ToolStripSeparator());
        target.Add(new ToolStripMenuItem("Open Config Folder", null, (_, _) => ctx.OpenConfigFolder(instance))
        {
            Enabled = installed,
        });
        target.Add(new ToolStripMenuItem(
            "Restore from Backup...", null, async (_, _) => await ctx.RestoreFromBackupAsync(instance))
        {
            Enabled = installed && !busy,
        });
        target.Add(new ToolStripMenuItem(
            "View Home Assistant Log", null, (_, _) => ctx.ViewHomeAssistantLog(instance)));

        target.Add(new ToolStripSeparator());

        // Devices are per instance and exclusive: a Zigbee/Z-Wave coordinator
        // is a serial port, and a serial port opens exactly once.
        var devices = instance.Settings.UsbDevices;
        target.Add(new ToolStripMenuItem(
            devices.Count == 0
                ? "USB devices: none"
                : "USB devices: " + string.Join(", ", devices.Select(Path.GetFileName)))
        { Enabled = false });
        target.Add(new ToolStripMenuItem(
            "Assign USB Device...", null, async (_, _) => await ctx.AssignUsbDeviceAsync(instance))
        {
            Enabled = installed && !busy,
        });

        target.Add(new ToolStripSeparator());

        // Version is per instance: each one is pinned to a concrete image tag,
        // so updating one never moves another (see InstanceSettings.ImageTag).
        target.Add(new ToolStripMenuItem($"Version: {instance.Settings.ImageTag}") { Enabled = false });
        target.Add(new ToolStripMenuItem(
            "Check for Updates...", null, async (_, _) => await ctx.CheckForUpdatesAsync(instance))
        {
            Enabled = installed && !busy,
        });
        target.Add(new ToolStripMenuItem(
            "Change Version...", null, async (_, _) => await ctx.ChangeVersionAsync(instance))
        {
            Enabled = installed && !busy,
        });

        target.Add(new ToolStripSeparator());
        target.Add(new ToolStripMenuItem("Rename Instance...", null, (_, _) => ctx.RenameInstance(instance))
        {
            Enabled = !busy,
        });
        target.Add(new ToolStripMenuItem(
            "Clone Instance...", null, async (_, _) => await ctx.CloneInstanceAsync(instance))
        {
            Enabled = installed && !busy,
        });
        target.Add(new ToolStripMenuItem(
            "Reset Instance...", null, async (_, _) => await ctx.ResetInstanceAsync(instance))
        {
            Enabled = installed && !busy,
        });
        target.Add(new ToolStripMenuItem(
            "Delete Instance...", null, async (_, _) => await ctx.DeleteInstanceAsync(instance))
        {
            // The last remaining instance can only be reset, never deleted -
            // the app always has one to show.
            Enabled = installed && !busy && multi,
        });

        if (multi)
        {
            target.Add(new ToolStripMenuItem(
                "Open on Double-Click", null, (_, _) => ctx.SetSelectedInstance(instance))
            {
                Checked = ctx.Selected.Id == instance.Id,
                CheckOnClick = false,
                Enabled = !busy,
            });
        }
    }

    private static void BuildGlobalItems(ToolStripItemCollection target, TrayContext ctx)
    {
        var busy = ctx.IsBusy;
        var installed = ctx.IsHomeAssistantInstalled;

        target.Add(new ToolStripMenuItem("Add Instance...", null, async (_, _) => await ctx.AddInstanceAsync())
        {
            Enabled = installed && !busy,
        });
        target.Add(new ToolStripMenuItem(
            "Remove Unused Versions...", null, async (_, _) => await ctx.PruneUnusedImagesAsync())
        {
            Enabled = installed && !busy,
        });

        if (!installed)
        {
            target.Add(new ToolStripMenuItem("Not installed yet") { Enabled = false });
        }

        target.Add(new ToolStripSeparator());
        target.Add(BuildTunnelSubmenu(ctx, busy));

        target.Add(new ToolStripSeparator());

        if (ctx.PendingAppUpdate is { } pending)
        {
            target.Add(new ToolStripMenuItem(
                $"Update available: {pending.TagName}", null, async (_, _) => await ctx.CheckForAppUpdatesAsync())
            {
                Font = new Font(SystemFonts.MenuFont ?? SystemFonts.DefaultFont, FontStyle.Bold),
            });
        }

        target.Add(BuildAppUpdatesSubmenu(ctx, busy));

        target.Add(new ToolStripSeparator());
        target.Add(new ToolStripMenuItem("View HA Win Server Log", null, (_, _) => ctx.ViewAppLog()));

        var runAtLogin = new ToolStripMenuItem("Run at Login", null, (_, _) => ctx.ToggleRunAtLogin())
        {
            Checked = AutoStart.IsEnabled(),
            CheckOnClick = false,
        };
        target.Add(runAtLogin);

        target.Add(new ToolStripSeparator());
        target.Add(new ToolStripMenuItem("About", null, (_, _) => ctx.ShowAbout()));
        target.Add(new ToolStripMenuItem("Quit", null, async (_, _) => await ctx.QuitAsync()));
    }

    /// <summary>App self-update: manual check, plus the two toggles that live in Core.AppUpdateSettings.</summary>
    private static ToolStripMenuItem BuildAppUpdatesSubmenu(TrayContext ctx, bool busy)
    {
        var root = new ToolStripMenuItem("App Updates");
        var settings = ctx.RootSettings.AppUpdates;

        root.DropDownItems.Add(new ToolStripMenuItem($"Version {AppVersion.Current}") { Enabled = false });
        root.DropDownItems.Add(new ToolStripSeparator());

        root.DropDownItems.Add(new ToolStripMenuItem(
            "Check for App Updates...", null, async (_, _) => await ctx.CheckForAppUpdatesAsync())
        {
            Enabled = !busy,
        });

        root.DropDownItems.Add(new ToolStripSeparator());

        root.DropDownItems.Add(new ToolStripMenuItem(
            "Check Automatically", null, (_, _) => ctx.ToggleAutoCheckAppUpdates())
        {
            Checked = settings.AutoCheck,
            CheckOnClick = false,
        });
        root.DropDownItems.Add(new ToolStripMenuItem(
            "Include Beta Releases", null, (_, _) => ctx.ToggleIncludeBetaReleases())
        {
            Checked = settings.IncludePrereleases,
            CheckOnClick = false,
            Enabled = !busy,
        });

        return root;
    }

    private static ToolStripMenuItem BuildNetworkSubmenu(
        TrayContext ctx, HassInstance instance, bool installed, bool busy)
    {
        var root = new ToolStripMenuItem("Network") { Enabled = installed };
        var settings = instance.Settings;

        var listening = NetworkInfo.IsPortListening(settings.Port);
        root.DropDownItems.Add(new ToolStripMenuItem(
            $"Listening on: {(settings.BindAllInterfaces ? "0.0.0.0" : "127.0.0.1")}:{settings.Port}")
        { Enabled = false });

        var lanText = instance.LanUrl is { } lan ? $"LAN: {lan}" : "LAN: no address detected";
        root.DropDownItems.Add(new ToolStripMenuItem(lanText) { Enabled = false });

        root.DropDownItems.Add(new ToolStripMenuItem(
            $"Port status: {(listening ? "Listening" : "Not listening")}")
        { Enabled = false });

        root.DropDownItems.Add(new ToolStripSeparator());

        // Port/bind take effect on next container (re)creation - podman
        // fixes port publishing at `run` time, so a change here just updates
        // Settings and prompts to restart if currently running. No config
        // file to keep in sync, unlike the old YAML-managed-block approach.
        var canEditNetwork = !busy && installed;

        root.DropDownItems.Add(new ToolStripMenuItem(
            "Change Port...", null, async (_, _) => await OnChangePort(ctx, instance))
        { Enabled = canEditNetwork });

        var localhostOnly = new ToolStripMenuItem(
            "Bind: Localhost only", null,
            async (_, _) => await ctx.ChangePortAndBindAsync(instance, settings.Port, false))
        {
            Checked = !settings.BindAllInterfaces,
            Enabled = canEditNetwork,
        };
        var allInterfaces = new ToolStripMenuItem(
            "Bind: All interfaces", null,
            async (_, _) => await ctx.ChangePortAndBindAsync(instance, settings.Port, true))
        {
            Checked = settings.BindAllInterfaces,
            Enabled = canEditNetwork,
        };
        root.DropDownItems.Add(localhostOnly);
        root.DropDownItems.Add(allInterfaces);

        root.DropDownItems.Add(new ToolStripSeparator());
        BuildRemoteAccessItems(root.DropDownItems, ctx, instance, installed, busy);

        return root;
    }

    /// <summary>
    /// The Cloudflare Tunnel half of the Network submenu. Two shapes:
    /// nothing set up yet (just "Set Up Remote Access..."), or a hostname
    /// already configured (status line, Copy Public URL, Change Hostname,
    /// the Home Assistant proxy settings dialog, and Disable).
    /// </summary>
    private static void BuildRemoteAccessItems(
        ToolStripItemCollection target, TrayContext ctx, HassInstance instance, bool installed, bool busy)
    {
        var settings = instance.Settings;
        var canEdit = installed && !busy;

        target.Add(new ToolStripMenuItem(ctx.RemoteAccessStatusLabel(instance)) { Enabled = false });

        if (settings.TunnelEnabled && !string.IsNullOrEmpty(settings.Hostname))
        {
            target.Add(new ToolStripMenuItem("Copy Public URL", null, (_, _) => ctx.CopyPublicUrl(instance))
            {
                Enabled = installed,
            });
            target.Add(new ToolStripMenuItem(
                "Change Hostname...", null, async (_, _) => await ctx.SetUpRemoteAccessAsync(instance))
            {
                Enabled = canEdit,
            });
            target.Add(new ToolStripMenuItem(
                "Home Assistant Proxy Settings...", null, async (_, _) => await ctx.ShowProxyConfigDialogAsync(instance))
            {
                Enabled = canEdit,
            });
            if (settings.ProxyConfigApplied)
            {
                target.Add(new ToolStripMenuItem(
                    "Remove Home Assistant Proxy Settings", null, async (_, _) => await ctx.RemoveProxyConfigAsync(instance))
                {
                    Enabled = canEdit,
                });
            }
            target.Add(new ToolStripMenuItem(
                "Disable Remote Access...", null, async (_, _) => await ctx.DisableRemoteAccessAsync(instance))
            {
                Enabled = canEdit,
            });
        }
        else
        {
            target.Add(new ToolStripMenuItem(
                "Set Up Remote Access...", null, async (_, _) => await ctx.SetUpRemoteAccessAsync(instance))
            {
                Enabled = canEdit,
            });
        }
    }

    /// <summary>Machine-level connector status and maintenance actions - one tunnel serves every instance, see TunnelSupervisor.</summary>
    private static ToolStripMenuItem BuildTunnelSubmenu(TrayContext ctx, bool busy)
    {
        var root = new ToolStripMenuItem("Cloudflare Tunnel");
        var configured = ctx.RootSettings.Tunnel.TunnelId is not null;

        root.DropDownItems.Add(new ToolStripMenuItem(
            configured ? $"Status: {TrayContext.TunnelStateLabel(ctx.TunnelState)}" : "Not set up yet - use an instance's Network menu")
        { Enabled = false });

        root.DropDownItems.Add(new ToolStripSeparator());
        root.DropDownItems.Add(new ToolStripMenuItem(
            "Restart Connector", null, async (_, _) => await ctx.RestartTunnelAsync())
        {
            Enabled = configured && !busy,
        });
        root.DropDownItems.Add(new ToolStripMenuItem("View cloudflared Log", null, (_, _) => ctx.ViewTunnelLog()));
        root.DropDownItems.Add(new ToolStripMenuItem(
            "Remove Tunnel...", null, async (_, _) => await ctx.RemoveTunnelAsync())
        {
            Enabled = configured && !busy,
        });

        root.DropDownItems.Add(new ToolStripSeparator());
        root.DropDownItems.Add(new ToolStripMenuItem(
            "Forget Saved API Token", null, (_, _) => ctx.ForgetSavedApiToken())
        {
            // Independent of whether a tunnel exists yet - a token can be
            // saved from the setup wizard before any instance has remote
            // access configured.
            Enabled = SecretStore.HasApiToken() && !busy,
        });

        return root;
    }

    private static async Task OnChangePort(TrayContext ctx, HassInstance instance)
    {
        if (!ctx.TryPromptForPort("Change Port", instance.Settings.Port, instance.Id, out var port)) return;
        if (port == instance.Settings.Port) return;

        await ctx.ChangePortAndBindAsync(instance, port, instance.Settings.BindAllInterfaces);
    }
}
