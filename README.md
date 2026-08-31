# HA Win Server

A system tray app for Windows that installs and manages a local [Home Assistant](https://www.home-assistant.io/) Core instance, without administrator rights (with one caveat - see Requirements). It lives in the tray next to the clock: click the icon, get a dropdown, start/stop/inspect the service from there. No installer, no Windows service, no Docker.

Under the hood, Home Assistant runs inside a dedicated WSL (Windows Subsystem for Linux) distro rather than natively on Windows. That's not the obvious choice - see "Why WSL" below - but it's the one that actually works reliably.

## Requirements

- Windows 10 or 11
- **WSL already enabled on this machine.** This is the one real caveat: turning WSL on for the very first time is a Windows optional-component change that requires administrator rights, and this app can't do that on its own. If you've ever used Docker Desktop, a previous WSL-based tool, or run `wsl --install` yourself, it's almost certainly already on. If not, ask an admin to run `wsl --install` once - after that, everything else in this app runs with zero elevation.
- .NET 9 desktop runtime, **only** if you use the framework-dependent publish profile below. The self-contained publish needs nothing preinstalled.

## Why WSL, not native Windows

The first version of this app ran Home Assistant natively on Windows via pipx. Getting that to actually work meant fixing six separate, unrelated incompatibilities in Home Assistant's own code - none of which are Windows' fault exactly, but all of which are real:

- A core dependency (`bluetooth-data-tools`) ships no Windows wheel at all, and its sdist's pip build fails outright regardless of compiler availability.
- Home Assistant's own OS check rejects native Windows unless told not to.
- Two POSIX-only stdlib modules (`fcntl`, `resource`) are imported unconditionally and don't exist on Windows in any CPython build.
- `asyncio`'s `add_signal_handler()` isn't implemented on Windows at all, and Home Assistant's signal setup doesn't catch the resulting error - a guaranteed crash on every native Windows startup.
- The `hass --script ensure_config` helper has an upstream bug that makes it incompatible with the OS-check bypass.

Every one of those needed a targeted workaround, all confirmed by actually running the result on a real machine (not just reading source). They're preserved in git history if you want to see them. But the honest conclusion after building and testing all of it: this is fighting the platform, and it will need re-fighting every time an upstream dependency shifts underneath it.

Running the exact same `pip install homeassistant` inside a plain Ubuntu WSL distro instead: works in about 90 seconds, no workarounds, because it's genuine Linux. That's what this app does now. The trade-off is the WSL-already-enabled requirement above, and needing your own judgment on the LAN-access caveat below.

## What happens on first launch

1. Checks that WSL itself is available (`wsl --status`). If it isn't, shows a message explaining that an admin needs to run `wsl --install` once - this app stops here rather than attempting anything that would need elevation.
2. Installs a dedicated WSL distro (`wsl --install Ubuntu-24.04 --no-launch`) if it isn't already present. This is a normal, non-elevated per-user operation once the WSL platform itself is enabled.
3. Inside that distro, as root (WSL's root has no relationship to Windows admin rights - it's just a Linux user in an isolated VM, no UAC, no password): installs Python 3.14 via the [deadsnakes PPA](https://launchpad.net/~deadsnakes/+archive/ubuntu/ppa) (Ubuntu 24.04's own repos only go up to 3.12) plus a C/C++ toolchain (`build-essential`, for any optional dependency that needs to compile - a plain `apt install`, no MSVC Build Tools dance), then creates a venv and `pip install`s `homeassistant` into it.
4. Generates a default `configuration.yaml` (and its companion `secrets.yaml`/`automations.yaml`/`scripts.yaml`/`scenes.yaml`) matching exactly what Home Assistant's own normal startup would create, then edits a small marked block inside it to set the port and network binding you've configured.
5. Starts Home Assistant and opens the web UI once it responds.

All of this is visible, line by line, in a progress window while it runs. It's idempotent - if something fails partway (no network, WSL install blocked, etc.) you get a **Retry** button and the app picks up from whatever step didn't finish yet.

## Instances

You can run several Home Assistant instances side by side, each on its own port, from the single tray app. That is what makes it possible to try a Home Assistant upgrade, a risky integration, or a config change on a scratch instance without stopping or endangering the one actually running your house.

Every instance is fully separate: its own container, its own config directory, its own port, and **its own pinned Home Assistant version**. That last part matters more than it sounds. The official image's `stable` tag is a moving target - if two instances both followed it, pulling a new version to test on one would silently move the other to that version on its next restart. So each instance is pinned to a concrete version tag (`2026.8.3`) instead, and updating one instance never touches another.

The workflow that falls out of that:

1. **Clone Instance...** on your live instance - a copy of its real data on a new port. (Cloning a running instance can leave the *copy* with a torn recorder database; the app offers to stop the original for the duration. The original is never at risk either way.)
2. **Check for Updates...** on the copy. Only the copy is stopped, upgraded and restarted.
3. If it looks good, **Change Version...** on the live instance and pick that same version. The image is already downloaded, so this is just a container restart.
4. If it doesn't, **Change Version...** back to the previous version - also still local - or **Delete Instance...** the copy and move on.

Old images are kept precisely so that rollback is instant, which costs roughly 1.5 GB per version. **Remove Unused Versions...** lists the ones no instance is pinned to and removes the ones you pick.

**Reset Instance...** wipes one instance's Home Assistant data - users, tokens, devices, automations, history, backups - and restarts it empty, which puts Home Assistant back at its own onboarding wizard. It keeps the instance's port and version, doesn't re-download anything, and doesn't touch any other instance. Because it is unrecoverable, it asks twice: an explicit warning, then the instance's name typed back.

The first instance keeps the layout this app used before multi-instance support (container `hawinserver-hass`, config in `config/`), so upgrading the app adopts an existing Home Assistant in place - no data is moved and the running container is not recreated.

## USB devices (Zigbee / Z-Wave sticks)

WSL is a virtual machine, so it sees no USB device on its own. Getting a ConBee, SkyConnect or Z-Wave stick to an instance is a three-link chain, and **Assign USB Device...** in the instance's menu shows all three states at once, because almost every failure here is "a link was skipped":

1. **usbipd-win installed** - once per machine, needs administrator: `winget install --exact --id dorssel.usbipd-win`
2. **Share the device** - once per device, needs administrator. The dialog does it for you: if the app is already elevated it runs directly, otherwise Windows asks for confirmation, and if that isn't possible you get the exact `usbipd bind --busid ...` command with a copy button.
3. **Attach to WSL** - after every Windows restart, **no** administrator needed. The dialog's Attach button does this.

Once attached, the device shows up in the lower list and can be ticked for one instance. The stock WSL kernel already carries what's needed (`vhci-hcd` for USB/IP and `cdc-acm` for the serial class these coordinators present as) - verified on 6.6.87.2-microsoft-standard-WSL2, so no custom kernel build.

There is a fourth link that is easy to miss, because usbipd reports success without it: once the device is inside the VM, Linux still has to bind a driver, and udev still has to create the `/dev/serial/by-id` symlink. Either can be absent - a kernel whose modules don't match, or a distro booted without systemd and therefore without udev - and the symptom is identical to "attach did nothing": an empty list. So the lower list also falls back to raw `/dev/ttyACM*` / `/dev/ttyUSB*` nodes when no by-id name exists, and if nothing at all appears after an attach the app reports which link actually broke (kernel sees the device? driver loaded? udev running?) rather than leaving you with a blank box.

Assignments prefer `/dev/serial/by-id/...` over `/dev/ttyACM0`, because the by-id name encodes the vendor, product and serial number and therefore survives a replug; the real node is resolved fresh each time the container starts. A raw node is accepted when that is all there is, and is labelled as not stable.

**One instance per stick.** A serial port can only be opened by one process, so the app refuses to assign the same device to a second instance. This is enforced rather than advised, because the failure is expensive: if an instance that has **not** completed Home Assistant's onboarding is given a Zigbee coordinator, ZHA sets it up from scratch and writes a new network key to the stick - and every device paired to it, on this machine or any other, stops working until it is re-paired by hand. Assigning a stick to a not-yet-onboarded instance therefore requires confirming past an explicit warning.

**Restoring a backup.** **Restore from Backup...** reads the backup's own `backup.json` before doing anything, so it only asks for a password when the backup is actually encrypted, tells you which Home Assistant version made it and whether it contains the database, and refuses up front if the backup needs a newer Home Assistant than the instance is pinned to (Home Assistant would reject it anyway - raise the instance with **Change Version...** first). The restore itself is performed by Home Assistant at startup, and the app reads back the result it writes, so a failed restore says why instead of quietly landing you back on the onboarding screen.

**Migrating an existing setup: restore first, then attach.** Create the instance, let it reach the onboarding screen, restore your backup, let it come back up as your old system - and only then hand it the stick. Done in that order nothing is re-paired: the network key and channel live in the coordinator's own flash, the device registry comes from the backup, and the two meet again. Expect to have to point ZHA at the new serial path, which is a settings change, not a re-pairing. Also check that the instance's pinned version is not older than the version that produced the backup (`tar -xOf backup.tar backup.json` shows it) - **Change Version...** can raise it first.

If a device is assigned but not currently attached - the normal state after a Windows restart - the instance refuses to start and says which device is missing, instead of failing with a raw podman error.

## Data locations

```
%LOCALAPPDATA%\HaWinServer\
  settings.json          the instance list: id, name, port, bind address, pinned version
  logs\
    hawinserver.log       the tray app's own event log

\\wsl.localhost\Ubuntu-24.04\root\hawinserver\
  config\                 first instance's --config directory (configuration.yaml, home-assistant.log, .storage, recorder DB, ...)
  instances\
    <id>\config\          every other instance's --config directory, same layout
```

Config directories live inside the WSL distro's own filesystem (better I/O performance - Microsoft's own guidance is to avoid running project files off `/mnt/c`), exposed back to Windows via that `\\wsl.localhost\...` UNC path. Windows Explorer, and every `File.*` call in this app, read and write them exactly like local folders.

## Menu

With a single instance the menu is flat, exactly as below. Add a second and the same per-instance items move into one submenu per instance, with the global items (Add Instance, Run at Login, About, Quit) staying at the top level.

Per instance:

- **Status line** - current state (Stopped / Starting / Running / Stopping / Error). The tray icon's color dot shows the worst state across all instances.
- **Open Web UI** / **Copy LAN URL**
- **Start** / **Stop** / **Restart**
- **Network** submenu - listening address, detected LAN URL, port status, Change Port, and Localhost-only vs All-interfaces binding.
- **Open Config Folder**, **Restore from Backup...**, **View Home Assistant Log**
- **Assign USB Device...** - Zigbee/Z-Wave coordinators, one instance per stick (see above)
- **Version**, **Check for Updates...** (compares against PyPI, then pulls and restarts just this instance), **Change Version...** (any version, including ones already downloaded - this is both "promote what I tested" and rollback)
- **Rename Instance...**, **Clone Instance...**, **Reset Instance...**, **Delete Instance...** (disabled for the last remaining instance - reset it instead)
- **Open on Double-Click** - which instance the tray icon's double-click opens. Only shown when there is more than one.

Global:

- **Add Instance...**, **Remove Unused Versions...**
- **View HA Win Server Log**
- **Run at Login** - adds/removes a value under `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`. No admin, no scheduled task.
- **About**, **Quit**

## Process lifetime

Home Assistant runs as `wsl.exe -d Ubuntu-24.04 -u root -- <venv>/bin/hass --config <dir>`, a normal attached Windows child process, assigned to a Job Object configured with kill-on-close - confirmed on a real machine: killing that `wsl.exe` process (crash, Task Manager, whatever) correctly tears down the Linux-side `hass` process too. No orphaned processes, no port left bound on the next launch.

## Firewall / LAN access

This is the part that's genuinely different from a native Windows process, and worth understanding before relying on it.

By default, WSL2 uses NAT networking: Windows can reach `localhost:8123` (WSL forwards that automatically), but other devices on your LAN cannot reach it, because WSL's virtual network isn't the same network as your real adapter. Setting Home Assistant's bind address to "All interfaces" (0.0.0.0) via the Network submenu only controls binding *inside* the WSL network namespace - it doesn't by itself make the service LAN-reachable.

To get real LAN access, WSL2 supports **mirrored networking mode**, where the distro shares your host's actual network interfaces and IP address directly - confirmed on a real machine: with it enabled, WSL's interface gets the exact same IP as the Windows host's real adapter. It's a per-user, no-admin change:

```
# %USERPROFILE%\.wslconfig
[wsl2]
networkingMode=mirrored
```

...followed by `wsl --shutdown` to apply it. **This app does not do this automatically** - `.wslconfig` and `wsl --shutdown` affect every WSL distro on the machine, including anything unrelated (Docker Desktop, other dev environments), and restarting WSL stops whatever else was running in it. That's a machine-wide trade-off only you can decide on, not something this app should do silently as a side effect of a Network menu click.

Even with mirrored networking on, actual reachability from another device on the LAN wasn't independently confirmed during development (no second physical device was available to test from) - self-testing from the same machine against its own LAN IP is a known unreliable check (NAT hairpin behavior varies). If LAN access matters to you, verify it from an actual second device after enabling mirrored mode, and check Windows Firewall's inbound rules for the configured port if it doesn't work.

## Security note

`settings.json` holds no secrets - only instance names, ports, bind addresses and pinned versions. Home Assistant's own credentials (users, sessions, long-lived tokens) live in each instance's `.storage` directory inside the WSL distro, protected by your Windows user profile's file permissions.

Two consequences worth knowing:

- **Clone Instance...** copies `.storage` verbatim, so the clone starts with the same users, passwords and valid long-lived tokens as the original. Treat a clone as a second copy of those credentials, not as a blank instance.
- **Reset Instance...** deletes them along with everything else, which is why it asks you to type the instance name back.

## Building

```bash
dotnet build "src/HaWinServer/HaWinServer.csproj"
```

Zero NuGet packages - everything comes from the .NET/WinForms base class library (`System.Text.Json`, `HttpClient`, `System.Net.NetworkInformation`, `Microsoft.Win32.Registry`, WinForms controls).

### Publishing

Framework-dependent, single-file (needs the .NET 9 desktop runtime on the target machine):

```bash
dotnet publish src/HaWinServer/HaWinServer.csproj -c Release -p:PublishProfile=FolderProfile
```

Self-contained, single-file (no runtime prerequisite on the target machine, larger output):

```bash
dotnet publish src/HaWinServer/HaWinServer.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

Output lands in `src/HaWinServer/bin/Release/net9.0-windows/win-x64/publish/`.

### Regenerating the app icon

`src/HaWinServer/Resources/app.ico` is generated, not hand-drawn - a small dev-only tool builds a proper multi-resolution (16/32/48/256px) PNG-in-ICO file using nothing but `System.Drawing`, so there's no separate image-editing dependency for a one-off asset:

```bash
dotnet run --project tools/GenerateIcon
```

The tray icon itself (the colored dot per state) is drawn at runtime in `Core/TrayIcons.cs` and isn't a file at all.

## Manual test checklist

There's no automated test suite for this - it's a tray app orchestrating external processes (`wsl.exe`, `podman`), which isn't productively unit-testable. Test manually, in this order, after a build or publish. Run 1 first if you already have a real instance: it's the one that touches data you care about.

1. **Upgrade in place** - launch the new build with an existing pre-multi-instance install. Confirm a single instance "Main" appears with its real data on its original port, that `podman ps` shows the same container (it should not have been recreated), and that `settings.json` now lists that instance with a concrete `ImageTag` rather than `stable`. `podman images` should show the same image ID under both the version tag and `stable` - a local retag, not a download.
2. **Add Instance** on a free port - confirm two containers in `podman ps`, the browser opening onboarding on the new port, and the original instance still serving on its own.
3. **Reset Instance** on the test instance - confirm the two-step prompt (warning, then typing the name), an empty config dir afterwards, Home Assistant restarting at onboarding, and the other instance untouched and uninterrupted throughout.
4. **Isolated update** - with both running, update only the test instance. Confirm the live one is never stopped, and - the decisive check - that restarting the live instance afterwards leaves it on its *old* version.
5. **Promote and roll back** - **Change Version...** on the live instance to the version just verified (no download, restart only), then back to the previous one.
6. **Clone Instance** from a running instance, both with and without letting the app stop the source, and confirm the copy comes up with the source's data.
7. **Delete Instance** - the clone disappears from the menu, its container and directory are gone, and the last remaining instance has Delete disabled.
8. **Remove Unused Versions** - confirm no tag an instance is pinned to is ever offered, and that instances still start after a prune.
9. **Restart the tray app** with several instances - displayed states match `podman ps`, versions match `settings.json`, and the icon shows the worst state across instances.
10. **Change Port** - switch an instance to another port and confirm it comes back on it; confirm a port already assigned to another instance is refused.
11. **Run at Login** - toggle on, verify the `HKCU\...\Run` registry value, log out/in.
12. **USB assignment without hardware or admin rights** - the two usbipd steps need a device and (for `bind`) an administrator, but everything below them can be tested without either, because root inside the distro is not Windows admin. Create a fake coordinator and assign it:

    ```bash
    wsl -d Ubuntu-24.04 -u root --exec bash -c 'mknod /dev/ttyACM9 c 166 9; mkdir -p /dev/serial/by-id; ln -sf ../../ttyACM9 /dev/serial/by-id/usb-Simulated_ConBee_II_TEST-if00'
    ```

    It then appears in **Assign USB Device...** and can be assigned, and `podman inspect <container> --format '{{.HostConfig.Devices}}'` shows it was really passed through. The nodes live in a tmpfs, so they vanish on the next `wsl --shutdown`; `rm -f /dev/ttyACM9 /dev/serial/by-id/usb-Simulated_ConBee_II_TEST-if00` removes them sooner.
13. **Failure paths** - confirm the "WSL isn't set up" message appears correctly on a machine where WSL is genuinely unavailable; interrupt the network mid-bootstrap and confirm Retry resumes correctly.
14. **LAN access** - from a second physical device, with mirrored networking enabled and bind set to "All interfaces", confirm (or rule out) actual reachability - this wasn't verified during development for lack of a second device.
