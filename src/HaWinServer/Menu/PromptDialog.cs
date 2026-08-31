namespace HaWinServer.Menu;

/// <summary>
/// Minimal single-field input dialog. WinForms has no built-in InputBox
/// without pulling in Microsoft.VisualBasic, so this small form replaces it
/// for the places we need free text from the user (port number, version tag,
/// instance name, backup password) - keeps the "zero extra dependencies"
/// promise.
///
/// The layout measures its own prompt instead of assuming one short line.
/// The first version had a fixed 24px label in a 150px window, which silently
/// clipped every prompt longer than that - and the prompts that matter most
/// here are the long ones: the list of locally available versions, and the
/// "type the instance name to confirm" on a destructive action.
/// </summary>
public sealed class PromptDialog : Form
{
    private const int ContentWidth = 460;
    private const int Pad = 14;

    private readonly TextBox _textBox;

    private PromptDialog(string title, string label, string initialValue, bool masked)
    {
        // See TunnelSetupDialog's constructor for the full explanation of why
        // AutoScaleMode.Dpi alone is not enough here. It matters even more
        // for this dialog than for the others: every position and size below
        // is computed by hand from the Pad/ContentWidth constants rather
        // than left to Dock/Anchor, including the Form's own final
        // ClientSize - so every one of those literals is run through
        // LogicalToDeviceUnits explicitly instead of hoping the framework's
        // post-hoc rescale catches all of them consistently. That mismatch
        // is what hid the password field on the restore flow.
        AutoScaleMode = AutoScaleMode.Dpi;

        Text = title;
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;

        var pad = LogicalToDeviceUnits(Pad);
        var contentWidth = LogicalToDeviceUnits(ContentWidth);
        var buttonSize = LogicalToDeviceUnits(new Size(90, 28));
        var textWidth = contentWidth - (pad * 2);

        // Measured rather than guessed: the prompt can be one line or five.
        var labelHeight = TextRenderer.MeasureText(
            label,
            Font,
            new Size(textWidth, int.MaxValue),
            TextFormatFlags.WordBreak).Height;

        var promptLabel = new Label
        {
            Text = label,
            AutoSize = false,
            Location = new Point(pad, pad),
            Size = new Size(textWidth, labelHeight),
        };

        _textBox = new TextBox
        {
            Location = new Point(pad, promptLabel.Bottom + LogicalToDeviceUnits(8)),
            Width = textWidth,
            Text = initialValue,
            UseSystemPasswordChar = masked,
        };

        var buttonTop = _textBox.Bottom + LogicalToDeviceUnits(14);

        var okButton = new Button
        {
            Text = "OK",
            DialogResult = DialogResult.OK,
            Size = buttonSize,
            Location = new Point(contentWidth - pad - buttonSize.Width, buttonTop),
        };
        var cancelButton = new Button
        {
            Text = "Cancel",
            DialogResult = DialogResult.Cancel,
            Size = buttonSize,
            Location = new Point(contentWidth - pad - buttonSize.Width - LogicalToDeviceUnits(100), buttonTop),
        };

        Controls.Add(promptLabel);
        Controls.Add(_textBox);
        Controls.Add(okButton);
        Controls.Add(cancelButton);

        ClientSize = new Size(contentWidth, okButton.Bottom + pad);

        AcceptButton = okButton;
        CancelButton = cancelButton;

        // The caller usually pre-fills a value to edit (a port, a version), so
        // land in the field with it selected rather than making them clear it.
        Shown += (_, _) =>
        {
            _textBox.Focus();
            _textBox.SelectAll();
        };
    }

    /// <summary>Returns the entered text, or null if the user cancelled.</summary>
    public static string? Show(string title, string label, string initialValue = "", bool masked = false)
    {
        using var dialog = new PromptDialog(title, label, initialValue, masked);
        return dialog.ShowDialog() == DialogResult.OK ? dialog._textBox.Text : null;
    }
}
