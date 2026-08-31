namespace HaWinServer.Menu;

/// <summary>
/// Progress window shown during first-run provisioning (install the WSL
/// distro, Python and Home Assistant inside it, generate config). Runs the
/// actual work on a background thread and just renders whatever it reports -
/// never blocks the tray's message loop.
/// </summary>
public sealed class SetupWindow : Form
{
    private readonly Label _statusLabel;
    private readonly ProgressBar _progressBar;
    private readonly TextBox _logBox;
    private readonly Button _retryButton;
    private readonly Button _closeButton;

    public event EventHandler? RetryRequested;

    public SetupWindow()
    {
        // See TunnelSetupDialog's constructor for why both of these are
        // needed: AutoScaleMode.Dpi rescales child controls correctly, but
        // not the Form's own literal Width/Height, so LogicalToDeviceUnits
        // does that part explicitly.
        AutoScaleMode = AutoScaleMode.Dpi;

        Text = "HA Win Server - Setup";
        Size = LogicalToDeviceUnits(new Size(640, 440));
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = true;
        ShowInTaskbar = true;

        _statusLabel = new Label
        {
            Text = "Setting up Home Assistant...",
            Dock = DockStyle.Top,
            Height = LogicalToDeviceUnits(32),
            Font = new Font(Font.FontFamily, 10f, FontStyle.Bold),
            Padding = new Padding(12, 10, 12, 0),
        };

        _progressBar = new ProgressBar
        {
            Dock = DockStyle.Top,
            Height = LogicalToDeviceUnits(18),
            Style = ProgressBarStyle.Marquee,
            MarqueeAnimationSpeed = 30,
            Margin = new Padding(12, 0, 12, 0),
        };

        _logBox = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            Font = new Font(FontFamily.GenericMonospace, 9f),
            BackColor = Color.Black,
            ForeColor = Color.Gainsboro,
            BorderStyle = BorderStyle.FixedSingle,
        };

        var buttonPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = LogicalToDeviceUnits(44),
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(12, 6, 12, 6),
        };

        _closeButton = new Button { Text = "Close", Width = 90, Enabled = false };
        _closeButton.Click += (_, _) => Close();

        _retryButton = new Button { Text = "Retry", Width = 90, Visible = false };
        _retryButton.Click += (_, _) => RetryRequested?.Invoke(this, EventArgs.Empty);

        buttonPanel.Controls.Add(_closeButton);
        buttonPanel.Controls.Add(_retryButton);

        var logPanel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(12, 8, 12, 0) };
        logPanel.Controls.Add(_logBox);

        Controls.Add(logPanel);
        Controls.Add(buttonPanel);
        Controls.Add(_progressBar);
        Controls.Add(_statusLabel);
    }

    public void SetStatus(string text) => RunOnUiThread(() => _statusLabel.Text = text);

    public void AppendLine(string text) => RunOnUiThread(() =>
    {
        _logBox.AppendText(text + Environment.NewLine);
    });

    /// <summary>Failure of a step that can be picked up again from where it left off - offers Retry.</summary>
    public void ShowRetryableFailure(string summary) => ShowFailureCore(summary, retryable: true);

    /// <summary>
    /// Failure of a one-shot operation (reset, clone, version change). No
    /// Retry button: those flows re-run from the menu after the user has read
    /// what went wrong, and a Retry with no handler behind it would look like
    /// a dead button.
    /// </summary>
    public void ShowFailure(string summary) => ShowFailureCore(summary, retryable: false);

    private void ShowFailureCore(string summary, bool retryable)
    {
        RunOnUiThread(() =>
        {
            _statusLabel.Text = summary;
            _statusLabel.ForeColor = Color.Firebrick;
            _progressBar.Style = ProgressBarStyle.Blocks;
            _progressBar.Value = 0;
            _retryButton.Visible = retryable;
            _closeButton.Enabled = true;

            // _statusLabel is a single fixed-height line - a long summary
            // (a raw stderr dump, a chain of Cloudflare error messages) is
            // silently clipped there with nothing on screen to say more was
            // cut off. The log box is scrollable and already visible, so the
            // full text always lands there too, readable in full.
            AppendLine("FAILED: " + summary);
        });
    }

    public void ShowSuccess(string summary)
    {
        RunOnUiThread(() =>
        {
            _statusLabel.Text = summary;
            _statusLabel.ForeColor = Color.SeaGreen;
            _progressBar.Style = ProgressBarStyle.Blocks;
            _progressBar.Value = 100;
            _retryButton.Visible = false;
            _closeButton.Enabled = true;

            AppendLine(summary);
        });
    }

    public void ResetForRetry()
    {
        RunOnUiThread(() =>
        {
            _statusLabel.ForeColor = SystemColors.ControlText;
            _progressBar.Style = ProgressBarStyle.Marquee;
            _retryButton.Visible = false;
            _closeButton.Enabled = false;
        });
    }

    private void RunOnUiThread(Action action)
    {
        if (IsDisposed) return;

        if (InvokeRequired)
        {
            try
            {
                BeginInvoke(action);
            }
            catch (ObjectDisposedException)
            {
                // Window closed while a background task was still reporting progress.
            }
            return;
        }

        action();
    }
}
