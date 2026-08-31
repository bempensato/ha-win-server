using HaWinServer.Core;

namespace HaWinServer.Menu;

/// <summary>
/// Walks the user along the USB passthrough chain and returns which devices an
/// instance should get.
///
/// The dialog is deliberately shaped like the chain itself, top to bottom:
/// what Windows can share (usbipd), then what has actually landed inside WSL,
/// then the assignment. Almost every failure here is "you skipped a link", so
/// showing all three states at once answers the question faster than an error
/// message afterwards would.
/// </summary>
public sealed class UsbDeviceDialog : Form
{
    private readonly string _instanceName;
    private readonly IReadOnlyDictionary<string, string> _assignedElsewhere; // device path -> owning instance name

    private readonly ListView _windowsList;
    private readonly CheckedListBox _wslList;
    private readonly Button _shareButton;
    private readonly Button _attachButton;
    private readonly Label _statusLabel;
    private readonly Label _windowsHint;

    private List<WslSerialDevice> _wslDevices = new();
    private bool _busy;

    // Private rather than public: a public collection property on a Form trips
    // the WinForms designer-serialization analyzer, and the only consumer is
    // the static Show() below.
    private IReadOnlyList<string> _selectedDevicePaths = Array.Empty<string>();

    private UsbDeviceDialog(
        string instanceName,
        IReadOnlyList<string> currentlyAssigned,
        IReadOnlyDictionary<string, string> assignedElsewhere)
    {
        _instanceName = instanceName;
        _assignedElsewhere = assignedElsewhere;

        // See TunnelSetupDialog's constructor for why both of these are
        // needed: AutoScaleMode.Dpi rescales child controls correctly, but
        // not the Form's own literal Width/Height, so LogicalToDeviceUnits
        // does that part explicitly.
        AutoScaleMode = AutoScaleMode.Dpi;

        Text = $"USB Devices - {instanceName}";
        Size = LogicalToDeviceUnits(new Size(720, 620));
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.Sizable;
        MinimizeBox = false;
        ShowInTaskbar = false;
        MinimumSize = LogicalToDeviceUnits(new Size(640, 520));

        var intro = new Label
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            Padding = new Padding(12, 10, 12, 0),
            Text = "WSL is a virtual machine and cannot see USB devices on its own. A device has to be "
                 + "shared with usbipd (once, as administrator), then attached to WSL (after every Windows "
                 + "restart), before an instance can be given it.",
        };

        // ---- step 1/2: the Windows side -------------------------------------
        _windowsList = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            MultiSelect = false,
            HideSelection = false,
        };
        _windowsList.Columns.Add("Bus", 60);
        _windowsList.Columns.Add("Device", 420);
        _windowsList.Columns.Add("State", 160);
        _windowsList.SelectedIndexChanged += (_, _) => UpdateButtons();

        _shareButton = new Button { Text = "Share (admin)", Width = 130, Enabled = false };
        _shareButton.Click += async (_, _) => await OnShareAsync();

        _attachButton = new Button { Text = "Attach to WSL", Width = 130, Enabled = false };
        _attachButton.Click += async (_, _) => await OnAttachAsync();

        var refreshButton = new Button { Text = "Refresh", Width = 90 };
        refreshButton.Click += async (_, _) => await RefreshAsync();

        var windowsButtons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = LogicalToDeviceUnits(36),
            FlowDirection = FlowDirection.LeftToRight,
            Padding = new Padding(0, 4, 0, 0),
        };
        windowsButtons.Controls.Add(_shareButton);
        windowsButtons.Controls.Add(_attachButton);
        windowsButtons.Controls.Add(refreshButton);

        _windowsHint = new Label { Dock = DockStyle.Bottom, Height = LogicalToDeviceUnits(32), ForeColor = SystemColors.GrayText };

        var windowsGroup = new GroupBox
        {
            Dock = DockStyle.Top,
            Height = LogicalToDeviceUnits(230),
            Text = "1. Windows USB devices (usbipd)",
            Padding = new Padding(10, 6, 10, 8),
        };
        windowsGroup.Controls.Add(_windowsList);
        windowsGroup.Controls.Add(windowsButtons);
        windowsGroup.Controls.Add(_windowsHint);

        // ---- step 3: the WSL side, i.e. what is assignable -------------------
        _wslList = new CheckedListBox
        {
            Dock = DockStyle.Fill,
            CheckOnClick = true,
            IntegralHeight = false,
        };
        _wslList.ItemCheck += OnItemCheck;

        var wslGroup = new GroupBox
        {
            Dock = DockStyle.Fill,
            Text = $"2. Devices available in WSL - tick the ones \"{instanceName}\" should get",
            Padding = new Padding(10, 6, 10, 8),
        };
        wslGroup.Controls.Add(_wslList);

        _statusLabel = new Label
        {
            Dock = DockStyle.Bottom,
            Height = LogicalToDeviceUnits(40),
            Padding = new Padding(12, 4, 12, 0),
        };

        var okButton = new Button { Text = "OK", DialogResult = DialogResult.OK, Width = 90 };
        var cancelButton = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Width = 90 };
        okButton.Click += (_, _) => _selectedDevicePaths = CheckedDevicePaths();

        var buttonPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = LogicalToDeviceUnits(44),
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(12, 6, 12, 6),
        };
        buttonPanel.Controls.Add(cancelButton);
        buttonPanel.Controls.Add(okButton);

        var body = new Panel { Dock = DockStyle.Fill, Padding = new Padding(12, 4, 12, 4) };
        body.Controls.Add(wslGroup);
        body.Controls.Add(windowsGroup);

        Controls.Add(body);
        Controls.Add(_statusLabel);
        Controls.Add(buttonPanel);
        Controls.Add(intro);

        AcceptButton = okButton;
        CancelButton = cancelButton;

        _initiallyAssigned = currentlyAssigned;
        Shown += async (_, _) => await RefreshAsync();
    }

    private readonly IReadOnlyList<string> _initiallyAssigned;

    /// <summary>Returns the chosen device paths, or null if the user cancelled.</summary>
    public static IReadOnlyList<string>? Show(
        string instanceName,
        IReadOnlyList<string> currentlyAssigned,
        IReadOnlyDictionary<string, string> assignedElsewhere)
    {
        using var dialog = new UsbDeviceDialog(instanceName, currentlyAssigned, assignedElsewhere);
        return dialog.ShowDialog() == DialogResult.OK ? dialog._selectedDevicePaths : null;
    }

    private List<string> CheckedDevicePaths()
    {
        var paths = new List<string>();
        for (var i = 0; i < _wslList.Items.Count; i++)
        {
            if (_wslList.GetItemChecked(i) && i < _wslDevices.Count)
            {
                paths.Add(_wslDevices[i].DevicePath);
            }
        }
        return paths;
    }

    /// <summary>A device already held by another instance can't be ticked - a serial coordinator only opens once.</summary>
    private void OnItemCheck(object? sender, ItemCheckEventArgs e)
    {
        if (e.NewValue != CheckState.Checked || e.Index >= _wslDevices.Count) return;

        var path = _wslDevices[e.Index].DevicePath;
        if (_assignedElsewhere.TryGetValue(path, out var owner))
        {
            e.NewValue = CheckState.Unchecked;
            MessageBox.Show(
                $"This device is already assigned to the instance \"{owner}\".\n\n" +
                "A serial device can only be opened by one process, so it stays with one instance. " +
                $"Remove it from \"{owner}\" first if you want \"{_instanceName}\" to have it.",
                "Already assigned",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }
    }

    private async Task RefreshAsync()
    {
        SetBusy(true, "Looking for devices...");
        try
        {
            var usbipd = UsbDevices.FindUsbipd();
            _windowsList.Items.Clear();

            if (usbipd is null)
            {
                _windowsHint.Text = "usbipd-win is not installed. Install it once from an administrator "
                                  + "prompt, then reopen this window:\n" + UsbDevices.InstallCommand;
            }
            else
            {
                var windowsDevices = await UsbDevices.ListWindowsUsbDevicesAsync();
                foreach (var device in windowsDevices)
                {
                    var item = new ListViewItem(new[] { device.BusId, device.Description, device.StateLabel })
                    {
                        Tag = device,
                    };
                    _windowsList.Items.Add(item);
                }

                _windowsHint.Text = windowsDevices.Count == 0
                    ? "usbipd reported no connected devices."
                    : UsbDevices.IsProcessElevated()
                        ? "Running as administrator - sharing a device happens directly."
                        : "Sharing needs administrator rights: Windows will ask for confirmation, and if that "
                        + "isn't possible you'll be given the exact command to run.";
            }

            _wslDevices = (await UsbDevices.ListWslSerialDevicesAsync()).ToList();
            _wslList.Items.Clear();
            foreach (var device in _wslDevices)
            {
                var suffix = _assignedElsewhere.TryGetValue(device.DevicePath, out var owner)
                    ? $"   [assigned to \"{owner}\"]"
                    : device.HasStableName ? $"   [{device.RealPath}]" : $"   [{device.RealPath} - no stable name, may change after a replug]";
                var index = _wslList.Items.Add(device.Name + suffix);
                if (_initiallyAssigned.Contains(device.DevicePath, StringComparer.Ordinal))
                {
                    _wslList.SetItemChecked(index, true);
                }
            }

            // An assignment whose device is currently absent must survive this
            // dialog, or simply opening the window after a reboot would quietly
            // unassign the coordinator.
            foreach (var missing in _initiallyAssigned.Where(p => _wslDevices.All(d => d.DevicePath != p)))
            {
                var index = _wslList.Items.Add(Path.GetFileName(missing) + "   [assigned, not currently attached]");
                _wslList.SetItemChecked(index, true);
                _wslDevices.Add(new WslSerialDevice(missing, "(not attached)", UsbDevices.IsStableDevicePath(missing)));
            }

            SetBusy(false, _wslDevices.Count == 0
                ? "Nothing is available to WSL yet. Share a device above, then attach it."
                : $"{_wslDevices.Count} device(s) available to WSL.");
        }
        catch (Exception ex)
        {
            SetBusy(false, "Could not read the device list: " + ex.Message);
        }
    }

    private async Task OnShareAsync()
    {
        if (SelectedWindowsDevice() is not { } device) return;

        SetBusy(true, $"Sharing {device.BusId}...");
        var (ok, detail) = await UsbDevices.ShareAsync(device.BusId);
        SetBusy(false, detail);

        if (!ok)
        {
            var command = UsbDevices.ShareCommand(device.BusId);
            var copy = MessageBox.Show(
                detail + "\n\n" +
                "Run this once from a Command Prompt or PowerShell opened as administrator:\n\n" +
                command + "\n\n" +
                "Copy the command to the clipboard?",
                "Administrator rights needed",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Information);

            if (copy == DialogResult.Yes)
            {
                try { Clipboard.SetText(command); } catch (Exception) { /* clipboard busy - not fatal */ }
            }
            return;
        }

        await RefreshAsync();
    }

    private async Task OnAttachAsync()
    {
        if (SelectedWindowsDevice() is not { } device) return;

        SetBusy(true, $"Attaching {device.BusId} to WSL...");
        var (ok, detail) = await UsbDevices.AttachAsync(device.BusId);

        if (!ok)
        {
            SetBusy(false, detail);
            MessageBox.Show(
                detail + "\n\nA device has to be shared before it can be attached.",
                "Attach failed",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return;
        }

        // usbipd returns as soon as the device is handed to the VM, but Linux
        // still has to bind a driver and udev still has to name the node. That
        // takes a moment - and sometimes never happens at all, which is the
        // case worth explaining rather than showing an empty list for.
        var appeared = false;
        for (var i = 0; i < 10 && !appeared; i++)
        {
            SetBusy(true, $"Attached. Waiting for the device to appear in WSL... ({(i + 1)}s)");
            await Task.Delay(1000);
            appeared = (await UsbDevices.ListWslSerialDevicesAsync()).Count > 0;
        }

        await RefreshAsync();

        if (!appeared)
        {
            var diagnosis = await UsbDevices.DescribeDeviceStateAsync();
            MessageBox.Show(
                "usbipd attached the device, but no serial port appeared inside WSL.\n\n" +
                diagnosis + "\n\n" +
                "What this usually means:\n" +
                "  • \"USB devices seen by the kernel: 0\" - the device did not reach the VM. " +
                "Unplug and replug it, then attach again.\n" +
                "  • Kernel sees it but no serial driver loaded - this device may not be a USB " +
                "serial adapter, or the WSL kernel has no matching driver.\n" +
                "  • Serial node present but udev not running - the device is still usable here; " +
                "it will be listed by its /dev/ttyACM name instead of a stable one.",
                "Attached, but nothing appeared",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }
    }

    private WindowsUsbDevice? SelectedWindowsDevice() =>
        _windowsList.SelectedItems.Count > 0 ? _windowsList.SelectedItems[0].Tag as WindowsUsbDevice : null;

    private void UpdateButtons()
    {
        var device = SelectedWindowsDevice();
        _shareButton.Enabled = !_busy && device is { IsShared: false };
        _attachButton.Enabled = !_busy && device is { IsShared: true, IsAttached: false };
    }

    private void SetBusy(bool busy, string status)
    {
        _busy = busy;
        _statusLabel.Text = status;
        UseWaitCursor = busy;
        UpdateButtons();
    }
}
