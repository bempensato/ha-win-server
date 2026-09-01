using System.Diagnostics;
using HaWinServer.Core;

namespace HaWinServer.Menu;

/// <summary>What the wizard collected - the actual Cloudflare provisioning (create tunnel, sync ingress, DNS, Access) happens afterward in TrayContext's progress window, matching how every other multi-step operation here works (see e.g. AddInstanceAsync).</summary>
public sealed record TunnelSetupResult(
    string ApiToken,
    string AccountId,
    string AccountName,
    string ZoneId,
    string ZoneName,
    string Hostname,
    bool ProtectWithAccess,
    IReadOnlyList<string> AccessEmails);

/// <summary>
/// Collects everything needed to give one instance a Public Hostname:
/// an API token, the zone it can act on, a single-level subdomain of that
/// zone, and whether to gate it behind Cloudflare Access.
///
/// Only ever builds "{instance}.{zone}" - see HostnameSlug's doc comment for
/// why a two-level subdomain or a wildcard is deliberately not offered: both
/// fall outside what Cloudflare's free Universal SSL certificate covers and
/// produced a confirmed SSL error for every visitor, not a config mistake.
///
/// The API token is saved to disk the same way the tunnel run token is - see
/// SecretStore's doc comment - so <paramref name="initialApiToken"/> (the
/// value the caller pre-fills the box with) is not just this run's token but
/// whatever was last saved, surviving an app restart. Persistence itself
/// happens on the caller's side (TrayContext.RememberCloudflareApiToken);
/// this dialog only ever reads/returns the token value.
///
/// When <paramref name="currentInstanceSettings"/> already has remote access
/// configured (the "Change Hostname..." menu entry), its hostname and Access
/// settings are shown as-is instead of being recomputed from scratch, so
/// re-opening this dialog to tweak one thing doesn't reset the others.
///
/// There is no separate account picker: Cloudflare's own GET /zones response
/// already nests each zone's account id/name, and reading it from there
/// avoids ever calling GET /accounts, which needs the extra "Account
/// Settings: Read" permission that none of this app's requested permissions
/// imply (confirmed against a real token: zones listed fine, accounts came
/// back empty).
/// </summary>
public sealed class TunnelSetupDialog : Form
{
    private const string TokenCreatePageUrl = "https://dash.cloudflare.com/profile/api-tokens";

    private readonly string _instanceName;
    private readonly TunnelSettings _existingTunnel;

    private readonly TextBox _tokenBox;
    private readonly Button _connectButton;
    private readonly Label _connectStatus;
    private readonly ComboBox _zoneCombo;
    private readonly TextBox _hostnameBox;
    private readonly CheckBox _accessCheck;
    private readonly TextBox _accessEmailsBox;
    private readonly Button _okButton;

    private IReadOnlyList<CloudflareZone> _zones = Array.Empty<CloudflareZone>();
    private bool _connected;

    // True while the hostname box shows a value this dialog did not compute
    // itself (pre-filled from an already-configured instance) - suppresses
    // the auto-recompute that would otherwise fire the moment Connect
    // repopulates the zone combo, silently overwriting a hostname the user
    // may have customized away from the naming scheme's pattern. Cleared the
    // instant the user actually clicks a scheme radio button, which is an
    // explicit request to recompute.
    private bool _suppressHostnameAutoFill;

    private TunnelSetupResult? _result;

    private TunnelSetupDialog(
        string instanceName,
        TunnelSettings existingTunnel,
        string? initialApiToken,
        InstanceSettings? currentInstanceSettings)
    {
        _instanceName = instanceName;
        _existingTunnel = existingTunnel;

        // PerMonitorV2 is declared in the app manifest, and AutoScaleMode.Dpi
        // makes WinForms' own PerformAutoScale rescale every CHILD control's
        // Location/Size/Font by the real DPI ratio. It does NOT, in
        // practice, rescale the Form's own Width/Height when those are
        // assigned directly as literal numbers below (rather than left to
        // the designer's usual AutoScaleDimensions dance) - confirmed by a
        // real Windows 10 screenshot where every child had visibly scaled
        // up (bigger font, a wrapped URL overflowing its row, the Fetch
        // Zones button pushed out of its GroupBox) while the window itself
        // stayed sized for the un-scaled 640x640. LogicalToDeviceUnits below
        // converts that literal, 96-DPI-designed size to whatever the
        // current monitor's DPI actually needs, so the container catches up
        // to its already-correctly-scaled children instead of clipping them.
        AutoScaleMode = AutoScaleMode.Dpi;

        Text = $"Set Up Remote Access - {instanceName}";
        Size = LogicalToDeviceUnits(new Size(640, 640));
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;

        var intro = new Label
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            Padding = new Padding(12, 10, 12, 0),
            Text = "This exposes Home Assistant to the internet through a Cloudflare Tunnel - no port " +
                   "forwarding, no public IP needed. It requires a domain already added to your Cloudflare " +
                   "account, and an API token scoped to: Account → Cloudflare Tunnel: Edit, " +
                   "Zone → DNS: Edit, Zone → Zone: Read (add Account → Access: Apps and " +
                   "Policies: Edit too if you use the Access option below).",
        };

        var tokenLink = new LinkLabel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            Padding = new Padding(12, 0, 12, 0),
            Text = "Create a token at " + TokenCreatePageUrl,
        };
        tokenLink.Links.Add(tokenLink.Text.IndexOf("http", StringComparison.Ordinal), TokenCreatePageUrl.Length, TokenCreatePageUrl);
        tokenLink.LinkClicked += (_, _) => TryOpenUrl(TokenCreatePageUrl);

        // ---- 1. API token -----------------------------------------------------
        _tokenBox = new TextBox { Width = 380, UseSystemPasswordChar = true, Text = initialApiToken ?? string.Empty };
        _connectButton = new Button { Text = "Fetch Zones", Width = 170, Height = LogicalToDeviceUnits(28), FlatStyle = FlatStyle.System };
        _connectButton.Click += async (_, _) => await OnConnectAsync();
        _connectStatus = new Label { Dock = DockStyle.Bottom, Height = LogicalToDeviceUnits(24), ForeColor = SystemColors.GrayText };

        var tokenRow = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.LeftToRight,
        };
        tokenRow.Controls.Add(_tokenBox);
        tokenRow.Controls.Add(_connectButton);

        var tokenGroup = new GroupBox
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Text = "1. Cloudflare API token",
            Padding = new Padding(10, 6, 10, 6),
        };
        tokenGroup.Controls.Add(tokenRow);
        tokenGroup.Controls.Add(_connectStatus);

        // ---- 2. zone --------------------------------------------------------------
        _zoneCombo = new ComboBox { Width = 280, DropDownStyle = ComboBoxStyle.DropDownList, Enabled = false };
        _zoneCombo.SelectedIndexChanged += (_, _) => UpdateHostnamePreview();

        var zoneLabel = new Label { Text = "Zone (domain):", AutoSize = true, Margin = new Padding(0, 6, 6, 0) };

        // Top, not Fill: this GroupBox is AutoSize (see comment on tokenGroup
        // above for why), and an AutoSize container cannot also have a Fill
        // child - Fill wants to expand to whatever the container decides,
        // AutoSize wants to shrink to whatever the content decides, and
        // fighting that circularity silently freezes the container at
        // whatever size it last had. Top still stretches to the full
        // available width, just without that conflict.
        var zoneFlow = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = true,
        };
        zoneFlow.Controls.Add(zoneLabel);
        zoneFlow.Controls.Add(_zoneCombo);

        var zoneGroup = new GroupBox
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Text = "2. Zone",
            Padding = new Padding(10, 6, 10, 6),
        };
        zoneGroup.Controls.Add(zoneFlow);

        // ---- 3. hostname --------------------------------------------------------
        // Always "{instance}.{zone}" - a single-level subdomain, the one
        // shape covered by Cloudflare's free Universal SSL certificate on
        // every plan. Still editable, so a custom subdomain name is fine;
        // what is deliberately not offered is a second subdomain level,
        // which produced a confirmed SSL error for every visitor.
        var hostnameLabel = new Label { AutoSize = true, Margin = new Padding(0, 0, 0, 2), Text = "Public Hostname (editable):" };
        _hostnameBox = new TextBox { Width = 400, Margin = new Padding(0, 0, 0, 4) };
        _hostnameBox.TextChanged += (_, _) => UpdateOkEnabled();

        var hostnameHint = new Label
        {
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
            Text = "Must be a single-level subdomain of the zone above - Cloudflare's free SSL certificate does not cover a second subdomain level.",
        };

        var hostnameStack = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
        };
        hostnameStack.Controls.Add(hostnameLabel);
        hostnameStack.Controls.Add(_hostnameBox);
        hostnameStack.Controls.Add(hostnameHint);

        var hostnameGroup = new GroupBox
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Text = "3. Public Hostname",
            Padding = new Padding(10, 6, 10, 6),
        };
        hostnameGroup.Controls.Add(hostnameStack);

        // ---- 4. protection --------------------------------------------------------
        _accessCheck = new CheckBox { Text = "Protect with Cloudflare Access (recommended)", AutoSize = true, Margin = new Padding(0, 0, 0, 6) };
        _accessCheck.CheckedChanged += (_, _) => UpdateAccessEnabled();

        var emailsLabel = new Label { AutoSize = true, Margin = new Padding(0, 0, 0, 2), Text = "Allowed email addresses (one-time PIN, comma-separated):" };
        _accessEmailsBox = new TextBox { Width = 400, Margin = new Padding(0, 0, 0, 10), PlaceholderText = "you@example.com, other@example.com" };
        _accessEmailsBox.TextChanged += (_, _) => UpdateOkEnabled();

        var accessWarning = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(560, 0),
            ForeColor = SystemColors.GrayText,
            Text = "With Access on, every visitor sees a Cloudflare login page before Home Assistant. The " +
                   "Companion App needs extra setup, and Alexa/Google Assistant cloud integrations stop " +
                   "working. Webhook calls (automations) bypass Access automatically.",
        };

        var accessStack = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
        };
        accessStack.Controls.Add(_accessCheck);
        accessStack.Controls.Add(emailsLabel);
        accessStack.Controls.Add(_accessEmailsBox);
        accessStack.Controls.Add(accessWarning);

        var accessGroup = new GroupBox
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Text = "4. Protection",
            Padding = new Padding(10, 6, 10, 6),
        };
        accessGroup.Controls.Add(accessStack);

        // ---- buttons ----------------------------------------------------------------
        // Plain buttons, not DialogResult-driven: OK needs to validate (and,
        // on failure, keep the dialog open with a message) before closing -
        // setting DialogResult directly on the button would close the form
        // before that check ever ran.
        //
        // FlatStyle.System on every Button in this file (and the other four
        // hand-built dialogs): the default FlatStyle.Standard is GDI+-drawn,
        // and a real Windows 10 screenshot after the sizing fix above showed
        // that rendering garbled - button labels sliced by a stray line -
        // once these dialogs were actually being resized post-construction
        // by AutoScaleMode.Dpi at a non-96 DPI. FlatStyle.System hands the
        // button to native Windows theming instead, which paints correctly
        // at any DPI because Windows itself, not GDI+, owns the redraw.
        _okButton = new Button { Text = "OK", Width = 90, Height = LogicalToDeviceUnits(28), Enabled = false, FlatStyle = FlatStyle.System };
        _okButton.Click += (_, _) => OnOkClicked();
        var cancelButton = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Width = 90, Height = LogicalToDeviceUnits(28), FlatStyle = FlatStyle.System };

        var buttonPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = LogicalToDeviceUnits(44),
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(12, 6, 12, 6),
        };
        buttonPanel.Controls.Add(_okButton);
        buttonPanel.Controls.Add(cancelButton);

        var body = new Panel { Dock = DockStyle.Fill, Padding = new Padding(12, 4, 12, 4), AutoScroll = true };
        body.Controls.Add(accessGroup);
        body.Controls.Add(hostnameGroup);
        body.Controls.Add(zoneGroup);
        body.Controls.Add(tokenGroup);

        Controls.Add(body);
        Controls.Add(buttonPanel);
        Controls.Add(tokenLink);
        Controls.Add(intro);

        AcceptButton = _okButton;
        CancelButton = cancelButton;

        UpdateAccessEnabled();

        // A tunnel already exists on this machine: pre-fill the zone this
        // instance will most likely want, so the common case (adding a
        // second instance) is "paste token, click Connect, OK".
        if (!string.IsNullOrEmpty(existingTunnel.Zone))
        {
            _zoneCombo.Items.Add(existingTunnel.Zone);
        }

        // "Change Hostname..." on an instance that already has remote access:
        // show what is actually configured instead of a freshly computed
        // default - see _suppressHostnameAutoFill's doc comment above.
        if (currentInstanceSettings is { TunnelEnabled: true })
        {
            if (!string.IsNullOrEmpty(currentInstanceSettings.Hostname))
            {
                _hostnameBox.Text = currentInstanceSettings.Hostname;
                _suppressHostnameAutoFill = true;
            }

            _accessCheck.Checked = currentInstanceSettings.AccessEnabled;
            _accessEmailsBox.Text = string.Join(", ", currentInstanceSettings.AccessEmails);
        }
    }

    public static TunnelSetupResult? Show(
        string instanceName,
        TunnelSettings existingTunnel,
        string? initialApiToken = null,
        InstanceSettings? currentInstanceSettings = null)
    {
        using var dialog = new TunnelSetupDialog(instanceName, existingTunnel, initialApiToken, currentInstanceSettings);
        return dialog.ShowDialog() == DialogResult.OK ? dialog._result : null;
    }

    private async Task OnConnectAsync()
    {
        var token = _tokenBox.Text.Trim();
        if (token.Length == 0)
        {
            _connectStatus.ForeColor = Color.Firebrick;
            _connectStatus.Text = "Enter an API token first.";
            return;
        }

        _connectButton.Enabled = false;
        _connectStatus.ForeColor = SystemColors.GrayText;
        _connectStatus.Text = "Connecting to Cloudflare...";

        try
        {
            var api = new CloudflareApi(token);
            _zones = await api.ListActiveZonesAsync();

            _zoneCombo.Items.Clear();
            foreach (var zone in _zones) _zoneCombo.Items.Add(zone.Name);
            var preselect = _zoneCombo.Items.IndexOf(_existingTunnel.Zone ?? "");
            _zoneCombo.SelectedIndex = preselect >= 0 ? preselect : (_zoneCombo.Items.Count > 0 ? 0 : -1);
            _zoneCombo.Enabled = _zoneCombo.Items.Count > 0;

            if (_zones.Count == 0)
            {
                _connectStatus.ForeColor = Color.Firebrick;
                _connectStatus.Text = "No active zone found for this token. Add a domain to Cloudflare and/or check the token's Zone permissions, then reconnect.";
                _connected = false;
            }
            else
            {
                _connectStatus.ForeColor = Color.SeaGreen;
                _connectStatus.Text = $"Connected: {_zones.Count} active zone(s).";
                _connected = true;
            }

            UpdateHostnamePreview();
            UpdateOkEnabled();
        }
        catch (CloudflareApiException ex)
        {
            _connected = false;
            _connectStatus.ForeColor = Color.Firebrick;
            _connectStatus.Text = "Cloudflare rejected the request: " + ex.Message;
        }
        catch (Exception ex)
        {
            _connected = false;
            _connectStatus.ForeColor = Color.Firebrick;
            _connectStatus.Text = "Could not reach Cloudflare: " + ex.Message;
        }
        finally
        {
            _connectButton.Enabled = true;
            UpdateOkEnabled();
        }
    }

    private void UpdateHostnamePreview()
    {
        if (_zoneCombo.SelectedItem is not string zone || zone.Length == 0)
        {
            UpdateOkEnabled();
            return;
        }

        if (_suppressHostnameAutoFill)
        {
            UpdateOkEnabled();
            return;
        }

        _hostnameBox.Text = HostnameSlug.BuildHostname(_instanceName, zone);
        UpdateOkEnabled();
    }

    private void UpdateAccessEnabled()
    {
        _accessEmailsBox.Enabled = _accessCheck.Checked;
        UpdateOkEnabled();
    }

    /// <summary>
    /// Only the things that make the form fillable at all gate OK - a
    /// connected token, a zone, a non-empty hostname. Whether the Access
    /// email list is actually filled in is checked at click time instead
    /// (see OnOkClicked), with a message that says exactly what's missing:
    /// a silently disabled button gives no clue which of several fields is
    /// the problem.
    /// </summary>
    private void UpdateOkEnabled()
    {
        var hasHostname = _hostnameBox.Text.Trim().Length > 0;
        _okButton.Enabled = _connected && hasHostname && _zoneCombo.SelectedIndex >= 0;
    }

    private void OnOkClicked()
    {
        if (_accessCheck.Checked && SplitEmails(_accessEmailsBox.Text).Count == 0)
        {
            MessageBox.Show(
                "Enter at least one allowed email address, or uncheck \"Protect with Cloudflare Access\".",
                "Set Up Remote Access",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            _accessEmailsBox.Focus();
            return;
        }

        _result = BuildResult();
        DialogResult = DialogResult.OK;
        Close();
    }

    private static List<string> SplitEmails(string text) =>
        text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    private TunnelSetupResult BuildResult()
    {
        var zoneName = (string)_zoneCombo.SelectedItem!;
        var zone = _zones.First(z => z.Name == zoneName);

        return new TunnelSetupResult(
            _tokenBox.Text.Trim(),
            zone.AccountId,
            zone.AccountName,
            zone.Id,
            zone.Name,
            _hostnameBox.Text.Trim(),
            _accessCheck.Checked,
            _accessCheck.Checked ? SplitEmails(_accessEmailsBox.Text) : Array.Empty<string>());
    }

    private static void TryOpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
        }
        catch (Exception)
        {
            // No default browser association or similar - not fatal.
        }
    }
}
