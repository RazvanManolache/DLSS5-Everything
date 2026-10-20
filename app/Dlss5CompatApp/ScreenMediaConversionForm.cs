namespace Dlss5CompatApp;

internal sealed class ScreenMediaConversionForm : Form
{
    readonly TextBox input = new() { Dock = DockStyle.Fill, ReadOnly = true, AccessibleName = "Input media" };
    readonly TextBox output = new() { Dock = DockStyle.Fill, ReadOnly = true, AccessibleName = "Output media" };
    readonly TextBox ffmpeg = new() { Dock = DockStyle.Fill, ReadOnly = true, AccessibleName = "FFmpeg executable" };
    readonly ComboBox encoder = new ThemedComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList, AccessibleName = "Video encoder" };
    readonly NumericUpDown quality = new ThemedNumericUpDown { Dock = DockStyle.Fill, Minimum = 0, Maximum = 51, Value = 20, AccessibleName = "Video quality" };
    readonly CheckBox audio = new() { Text = "Keep source audio", Checked = true, Dock = DockStyle.Fill };
    readonly Label state = new() { Text = "Ready", Dock = DockStyle.Fill, AutoEllipsis = true };
    readonly ProgressBar progress = new() { Dock = DockStyle.Fill };
    readonly Button start = new() { Text = "Convert", Dock = DockStyle.Fill };
    readonly Button cancel = new() { Text = "Close", Dock = DockStyle.Fill };
    readonly TableLayoutPanel fields;
    CancellationTokenSource? cancellation;
    bool running;
    public ScreenMediaConversionForm(ScreenStudioSettings settings, PayloadInfo payload, string model, UiPalette palette)
    {
        Text = "Image / Video Conversion"; MinimumSize = new(640, 470); Size = new(760, 480); StartPosition = FormStartPosition.CenterParent;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new(16), ColumnCount = 1, RowCount = 4 };
        layout.RowStyles.Add(new(SizeType.Percent, 100)); layout.RowStyles.Add(new(SizeType.Absolute, 28)); layout.RowStyles.Add(new(SizeType.Absolute, 24)); layout.RowStyles.Add(new(SizeType.Absolute, 40)); Controls.Add(layout);
        fields = new() { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 6 };
        fields.ColumnStyles.Add(new(SizeType.Absolute, 120)); fields.ColumnStyles.Add(new(SizeType.Percent, 100)); fields.ColumnStyles.Add(new(SizeType.Absolute, 90));
        for (var i = 0; i < 6; ++i) fields.RowStyles.Add(new(SizeType.Absolute, 44));
        void Row(int row, string label, Control control, Action? browse = null)
        {
            fields.Controls.Add(new Label { Text = label, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, row); fields.Controls.Add(control, 1, row);
            if (browse is not null) { var button = new Button { Text = "Browse", Dock = DockStyle.Fill }; button.Click += (_, _) => browse(); fields.Controls.Add(button, 2, row); }
            else fields.SetColumnSpan(control, 2);
        }
        Row(0, "Input", input, () => { using var dialog = new OpenFileDialog { Filter = "Images / videos|*.png;*.jpg;*.jpeg;*.bmp;*.mp4;*.mkv;*.avi;*.mov;*.webm|All files|*.*" }; if (dialog.ShowDialog(this) == DialogResult.OK) { input.Text = dialog.FileName; output.Text = ""; } });
        Row(1, "Output", output, () => { var video = new ScreenMediaOptions(input.Text, "").Video; using var dialog = new SaveFileDialog { Filter = video ? "MP4 video|*.mp4" : "PNG image|*.png", FileName = Path.GetFileNameWithoutExtension(input.Text) + "-phoenix", OverwritePrompt = true }; if (dialog.ShowDialog(this) == DialogResult.OK) output.Text = dialog.FileName; });
        Row(2, "FFmpeg", ffmpeg, () => { using var dialog = new OpenFileDialog { Filter = "FFmpeg|ffmpeg.exe" }; if (dialog.ShowDialog(this) == DialogResult.OK) ffmpeg.Text = dialog.FileName; });
        ffmpeg.Text = ScreenMediaConverter.FindFfmpeg(); encoder.Items.AddRange(["H.264 CPU (FFmpeg)", "H.264 NVIDIA DLSS5 NVENC"]); encoder.SelectedIndex = 0;
        Row(3, "Video encoder", encoder); Row(4, "Quality (0..51)", quality); Row(5, "Audio", audio);
        layout.Controls.Add(fields, 0, 0); layout.Controls.Add(state, 0, 1); layout.Controls.Add(progress, 0, 2);
        var actions = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2 }; actions.ColumnStyles.Add(new(SizeType.Percent, 50)); actions.ColumnStyles.Add(new(SizeType.Percent, 50)); actions.Controls.Add(start); actions.Controls.Add(cancel); layout.Controls.Add(actions, 0, 3);
        start.Click += async (_, _) =>
        {
            if (running) return;
            cancellation = new(); running = true; start.Enabled = fields.Enabled = false; cancel.Text = "Cancel";
            try
            {
                var report = new Progress<(string Stage, int Done, int Total)>(p => { state.Text = p.Stage + (p.Total > 0 ? $" - {p.Done}/{p.Total}" : ""); progress.Style = p.Total > 0 ? ProgressBarStyle.Continuous : ProgressBarStyle.Marquee; progress.Value = p.Total > 0 ? Math.Clamp(p.Done * 100 / p.Total, 0, 100) : 0; });
                await ScreenMediaConverter.ConvertAsync(new(input.Text, output.Text, ffmpeg.Text, encoder.SelectedIndex == 1, (int)quality.Value, audio.Checked), settings, payload, model, report, cancellation.Token);
            }
            catch (OperationCanceledException) { state.Text = "Cancelled"; }
            catch (Exception error) { state.Text = error.Message; MessageBox.Show(this, error.Message, "Conversion failed", MessageBoxButtons.OK, MessageBoxIcon.Error); }
            finally { running = false; cancellation.Dispose(); cancellation = null; start.Enabled = fields.Enabled = true; cancel.Text = "Close"; progress.Style = ProgressBarStyle.Continuous; }
        };
        cancel.Click += (_, _) => { if (running) cancellation?.Cancel(); else Close(); };
        FormClosing += (_, e) => { if (running) { cancellation?.Cancel(); e.Cancel = true; } };
        UiTheme.Apply(this, palette);
    }
}
