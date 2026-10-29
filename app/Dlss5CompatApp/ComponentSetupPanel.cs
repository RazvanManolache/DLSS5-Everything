namespace Dlss5CompatApp;

// Shared by the game inspector, empty library and streamer. Feedback stays
// beside the button that started the download, even when the user changes tabs.
sealed class ComponentSetupPanel : UserControl
{
    internal Button DownloadButton { get; } = new() { Text = "Download all components", Height = 34, Dock = DockStyle.Fill,
        AccessibleName = "Download all runtimes and models" };
    internal Button CancelButton { get; } = new() { Text = "Cancel", Dock = DockStyle.Fill,
        AccessibleName = "Cancel component downloads" };
    internal ThemedProgressBar ProgressBar { get; } = new() { Dock = DockStyle.Fill, Height = 16, Minimum = 0, Maximum = 100,
        AccessibleName = "Component download progress" };
    internal Label Status { get; } = new() { AutoSize = true, Dock = DockStyle.Fill, MaximumSize = new Size(280, 0),
        AccessibleName = "Component download status" };
    readonly TableLayoutPanel feedback;
    bool running;

    public ComponentSetupPanel(Action download, Action cancel)
    {
        AutoSize = true; AutoSizeMode = AutoSizeMode.GrowAndShrink; Dock = DockStyle.Top; Width = 280;
        MinimumSize = new Size(220, 0);
        var layout = new TableLayoutPanel { AutoSize = true, Dock = DockStyle.Top, ColumnCount = 1, RowCount = 3, Margin = Padding.Empty };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(DownloadButton, 0, 0);
        feedback = new TableLayoutPanel { Height = 32, Dock = DockStyle.Top, ColumnCount = 2, RowCount = 1, Visible = false, Margin = Padding.Empty };
        feedback.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        feedback.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        feedback.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 66));
        feedback.Controls.Add(ProgressBar, 0, 0); feedback.Controls.Add(CancelButton, 1, 0);
        ProgressBar.Margin = new Padding(0, 7, 6, 7);
        layout.Controls.Add(feedback, 0, 1); layout.Controls.Add(Status, 0, 2);
        Controls.Add(layout);
        DownloadButton.Click += (_, _) => download();
        CancelButton.Click += (_, _) => cancel();
        SizeChanged += (_, _) => Status.MaximumSize = new Size(Math.Max(100, ClientSize.Width - 6), 0);
    }

    internal void BeginDownload()
    {
        running = true; DownloadButton.Enabled = false; DownloadButton.Text = "Downloading components…";
        feedback.Visible = true; CancelButton.Visible = true; CancelButton.Enabled = true;
        ProgressBar.Value = 0; Status.Text = "Starting downloads…";
    }

    internal void Report(BootstrapProgress item)
    {
        ProgressBar.Value = Math.Clamp(item.Percent, 0, 100);
        Status.Text = ProgressBar.Value + "% · " + item.Message;
    }

    internal void Finish(bool success, string message)
    {
        running = false; DownloadButton.Text = success ? "Check components again" : "Retry download";
        DownloadButton.Enabled = true; CancelButton.Visible = false;
        ProgressBar.Value = success ? 100 : Math.Min(99, ProgressBar.Value);
        Status.Text = message;
    }

    internal void SetAvailable(bool available) => DownloadButton.Enabled = !running && available;
}
