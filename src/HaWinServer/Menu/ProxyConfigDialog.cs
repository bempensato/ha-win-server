using HaWinServer.Core;

namespace HaWinServer.Menu;

/// <summary>What to write - the actual write/backup/restart/rollback happens afterward in TrayContext via HaConfigPatcher.ApplyAsync, run inside a progress window like every other operation that restarts an instance.</summary>
public sealed record ProxyConfigRequest(bool IncludeHttp, bool IncludeExternalUrl, string TrustedProxyCidr, string? ExternalUrl);

/// <summary>
/// Previews the "# BEGIN HaWinServer" block HaConfigPatcher would write into
/// an instance's configuration.yaml, and lets the user opt into applying it.
///
/// Deliberately narrow: only offers the two keys HaConfigPatcher understands
/// (http, homeassistant), and refuses to let a checkbox turn on a key that
/// already exists elsewhere in the file - see HaConfigPatcher.Analyze. That
/// case is shown as a conflict to merge by hand instead, with a Copy button,
/// rather than silently doing nothing or duplicating the key.
/// </summary>
public sealed class ProxyConfigDialog : Form
{
    private readonly string _configYamlPath;

    private readonly Label _statusLabel;
    private readonly CheckBox _httpCheck;
    private readonly Label _httpConflictNote;
    private readonly TextBox _cidrBox;
    private readonly CheckBox _externalUrlCheck;
    private readonly Label _externalUrlConflictNote;
    private readonly TextBox _externalUrlBox;
    private readonly TextBox _previewBox;
    private readonly Button _applyButton;

    private HaConfigPatcher.Analysis? _analysis;
    private ProxyConfigRequest? _result;

    private ProxyConfigDialog(string instanceName, string configYamlPath, string? suggestedExternalUrl)
    {
        _configYamlPath = configYamlPath;

        // See TunnelSetupDialog's constructor for why this is needed.
        AutoScaleMode = AutoScaleMode.Dpi;

        Text = $"Home Assistant Proxy Settings - {instanceName}";
        Width = 620;
        Height = 620;
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;

        var intro = new Label
        {
            Dock = DockStyle.Top,
            Height = 70,
            Padding = new Padding(12, 10, 12, 0),
            Text = "Without these settings, Home Assistant sees every visitor as the tunnel's internal " +
                   "gateway address - its login-failure ban would then lock out the tunnel itself, and " +
                   "\"last logged in from\" would always show the wrong address. This writes a small marked " +
                   "block at the end of configuration.yaml, after backing the file up, and rolls back " +
                   "automatically if Home Assistant fails to restart.",
        };

        _statusLabel = new Label { Dock = DockStyle.Top, Height = 20, Padding = new Padding(12, 0, 12, 0), ForeColor = SystemColors.GrayText };

        // ---- http: use_x_forwarded_for / trusted_proxies -------------------------
        // Stacked through one top-down FlowLayoutPanel rather than mixing
        // independent Dock=Top controls straight in the GroupBox: WinForms
        // resolves same-edge docked siblings in REVERSE of their
        // Controls.Add order, and an un-docked control (the checkbox here)
        // just sits at (0,0) ignoring layout entirely - together those two
        // things silently produced an invisible checkbox and reordered rows
        // in an earlier version of this dialog. A single Flow panel lays
        // children out in the order added, with no such surprise.
        _httpCheck = new CheckBox { Text = "Set use_x_forwarded_for / trusted_proxies", AutoSize = true, Margin = new Padding(0, 0, 0, 6) };
        _httpCheck.CheckedChanged += (_, _) => UpdatePreview();

        _httpConflictNote = new Label
        {
            AutoSize = true, MaximumSize = new Size(540, 0), Visible = false, ForeColor = Color.Firebrick,
            Text = "An \"http:\" key already exists elsewhere in configuration.yaml - merge the trusted_proxies line into it by hand instead.",
        };

        var cidrLabel = new Label { Text = "Trusted proxy subnet:", AutoSize = true, Margin = new Padding(0, 6, 6, 0) };
        _cidrBox = new TextBox { Width = 160, Text = "detecting..." };
        _cidrBox.TextChanged += (_, _) => UpdatePreview();

        var cidrRow = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight };
        cidrRow.Controls.Add(cidrLabel);
        cidrRow.Controls.Add(_cidrBox);

        var httpStack = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        httpStack.Controls.Add(_httpCheck);
        httpStack.Controls.Add(cidrRow);
        httpStack.Controls.Add(_httpConflictNote);

        var httpGroup = new GroupBox { Dock = DockStyle.Top, Height = 130, Text = "http:", Padding = new Padding(10, 6, 10, 6) };
        httpGroup.Controls.Add(httpStack);

        // ---- homeassistant: external_url -----------------------------------------
        _externalUrlCheck = new CheckBox { Text = "Set homeassistant.external_url", AutoSize = true, Margin = new Padding(0, 0, 0, 6) };
        _externalUrlCheck.CheckedChanged += (_, _) => UpdatePreview();

        _externalUrlConflictNote = new Label
        {
            AutoSize = true, MaximumSize = new Size(540, 0), Visible = false, ForeColor = Color.Firebrick,
            Text = "A \"homeassistant:\" key already exists elsewhere in configuration.yaml - merge external_url into it by hand instead.",
        };

        _externalUrlBox = new TextBox { Width = 400, Margin = new Padding(0, 0, 0, 6), Text = suggestedExternalUrl ?? string.Empty };
        _externalUrlBox.TextChanged += (_, _) => UpdatePreview();

        var externalUrlNote = new Label
        {
            AutoSize = true, MaximumSize = new Size(540, 0), ForeColor = SystemColors.GrayText,
            Text = "Can also be set from Home Assistant's own Settings → System → Network instead - " +
                   "writing it here locks that field in the UI.",
        };

        var externalUrlStack = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        externalUrlStack.Controls.Add(_externalUrlCheck);
        externalUrlStack.Controls.Add(_externalUrlBox);
        externalUrlStack.Controls.Add(_externalUrlConflictNote);
        externalUrlStack.Controls.Add(externalUrlNote);

        var externalUrlGroup = new GroupBox { Dock = DockStyle.Top, Height = 150, Text = "homeassistant:", Padding = new Padding(10, 6, 10, 6) };
        externalUrlGroup.Controls.Add(externalUrlStack);

        // ---- preview ------------------------------------------------------------------
        var previewLabel = new Label { Dock = DockStyle.Top, Height = 20, Text = "What will be written:" };
        _previewBox = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            Font = new Font(FontFamily.GenericMonospace, 9f),
        };

        var copyButton = new Button { Text = "Copy Snippet", Width = 110 };
        copyButton.Click += (_, _) =>
        {
            try { Clipboard.SetText(_previewBox.Text); } catch (Exception) { /* clipboard busy - not fatal */ }
        };

        var previewPanel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(0, 0, 0, 0) };
        previewPanel.Controls.Add(_previewBox);
        previewPanel.Controls.Add(previewLabel);

        // ---- buttons --------------------------------------------------------------------
        _applyButton = new Button { Text = "Apply", DialogResult = DialogResult.OK, Width = 90, Enabled = false };
        var closeButton = new Button { Text = "Close", DialogResult = DialogResult.Cancel, Width = 90 };
        _applyButton.Click += (_, _) => _result = BuildRequest();

        var buttonPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 44,
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(12, 6, 12, 6),
        };
        buttonPanel.Controls.Add(_applyButton);
        buttonPanel.Controls.Add(closeButton);
        buttonPanel.Controls.Add(copyButton);

        var body = new Panel { Dock = DockStyle.Fill, Padding = new Padding(12, 4, 12, 4) };
        body.Controls.Add(previewPanel);
        body.Controls.Add(externalUrlGroup);
        body.Controls.Add(httpGroup);

        Controls.Add(body);
        Controls.Add(buttonPanel);
        Controls.Add(_statusLabel);
        Controls.Add(intro);

        AcceptButton = _applyButton;
        CancelButton = closeButton;

        Shown += async (_, _) => await LoadAsync();
    }

    public static ProxyConfigRequest? Show(string instanceName, string configYamlPath, string? suggestedExternalUrl)
    {
        using var dialog = new ProxyConfigDialog(instanceName, configYamlPath, suggestedExternalUrl);
        return dialog.ShowDialog() == DialogResult.OK ? dialog._result : null;
    }

    private async Task LoadAsync()
    {
        _statusLabel.Text = "Reading configuration.yaml...";

        if (!File.Exists(_configYamlPath))
        {
            _statusLabel.ForeColor = Color.Firebrick;
            _statusLabel.Text = "configuration.yaml was not found - is the instance running yet?";
            return;
        }

        try
        {
            var content = await File.ReadAllTextAsync(_configYamlPath);
            _analysis = HaConfigPatcher.Analyze(content);

            // The http: checkbox stays enabled even when YAML has a
            // conflicting http: key elsewhere: on an instance whose config
            // directory came from a restored backup, Home Assistant may be
            // reading trusted_proxies from .storage/http instead and
            // ignoring the YAML entirely - see HaConfigPatcher's doc
            // comment. That path is still worth taking even when the YAML
            // one is blocked.
            _httpCheck.Checked = true;
            _httpConflictNote.Visible = _analysis.HttpKeyPresentElsewhere;

            var hasStorage = HaConfigPatcher.HasHttpStorage(_configYamlPath);
            _httpConflictNote.Text = hasStorage
                ? "An \"http:\" key already exists elsewhere in configuration.yaml, so that block won't be touched - " +
                  "but this instance also has a Home Assistant-managed .storage/http, which will be patched directly instead."
                : "An \"http:\" key already exists elsewhere in configuration.yaml - merge the trusted_proxies line into it by hand instead.";

            var hasExternalUrlText = _externalUrlBox.Text.Trim().Length > 0;
            _externalUrlCheck.Checked = !_analysis.HomeAssistantKeyPresentElsewhere && hasExternalUrlText;
            _externalUrlCheck.Enabled = !_analysis.HomeAssistantKeyPresentElsewhere;
            _externalUrlConflictNote.Visible = _analysis.HomeAssistantKeyPresentElsewhere;

            _statusLabel.ForeColor = SystemColors.GrayText;
            _statusLabel.Text = _analysis.HasManagedBlock
                ? "A HA Win Server managed block is already present - re-applying will replace it."
                : "No existing HA Win Server managed block found.";
        }
        catch (Exception ex)
        {
            _statusLabel.ForeColor = Color.Firebrick;
            _statusLabel.Text = "Could not read configuration.yaml: " + ex.Message;
            return;
        }

        _statusLabel.Text += "  Detecting the podman network subnet...";
        try
        {
            _cidrBox.Text = await HaConfigPatcher.DetectTrustedProxyCidrAsync();
        }
        catch (Exception)
        {
            _cidrBox.Text = "10.88.0.0/16";
        }

        UpdatePreview();
    }

    private void UpdatePreview()
    {
        var includeHttp = _httpCheck.Checked;
        var includeExternalUrl = _externalUrlCheck.Enabled && _externalUrlCheck.Checked
                                  && _externalUrlBox.Text.Trim().Length > 0;

        // The YAML block only ever contains the http: key when it is NOT
        // blocked by a conflict - showing it there when it will actually be
        // skipped (in favor of a direct .storage/http patch, or nothing at
        // all) would misrepresent what Apply is about to do.
        var writeHttpYaml = includeHttp && _analysis is { HttpKeyPresentElsewhere: false };
        var hasStorage = HaConfigPatcher.HasHttpStorage(_configYamlPath);

        var preview = writeHttpYaml || includeExternalUrl
            ? HaConfigPatcher.BuildManagedBlock(_cidrBox.Text.Trim(), _externalUrlBox.Text.Trim(), writeHttpYaml, includeExternalUrl)
            : string.Empty;

        if (includeHttp && !writeHttpYaml)
        {
            preview += hasStorage
                ? $"\n(configuration.yaml's http: key is left alone; .storage/http is patched directly to trust {_cidrBox.Text.Trim()})"
                : "\n(configuration.yaml already has an http: key - nothing will be written; merge the snippet above into it by hand)";
        }

        _previewBox.Text = preview.Length > 0 ? preview.TrimStart('\n') : "(nothing selected)";
        _applyButton.Enabled = _analysis is not null && (includeHttp || includeExternalUrl);
    }

    private ProxyConfigRequest BuildRequest() => new(
        _httpCheck.Checked,
        _externalUrlCheck.Enabled && _externalUrlCheck.Checked && _externalUrlBox.Text.Trim().Length > 0,
        _cidrBox.Text.Trim(),
        _externalUrlBox.Text.Trim().Length > 0 ? _externalUrlBox.Text.Trim() : null);
}
