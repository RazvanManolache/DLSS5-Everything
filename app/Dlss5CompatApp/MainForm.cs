using System.Diagnostics;

namespace Dlss5CompatApp;

sealed class MainForm : Form
{
    readonly TextBox _scanRoot = new() { Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top };
    readonly TextBox _payloadRoot = new() { Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top };
    readonly Label _payloadStatus = new() { AutoSize = false, Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top };
    readonly ListView _games = new() { View = View.Details, FullRowSelect = true, MultiSelect = false, Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right };
    readonly TextBox _log = new() { Multiline = true, ScrollBars = ScrollBars.Vertical, ReadOnly = true, Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom };
    readonly Button _install = new() { Text = "Install selected" };
    readonly Button _restore = new() { Text = "Restore selected" };
    readonly Button _openFolder = new() { Text = "Open folder" };

    PayloadInfo? _payload;
    CancellationTokenSource? _scanCts;

    public MainForm()
    {
        Text = "DLSS5 x86/x64 Compatibility Installer";
        Width = 1180;
        Height = 760;
        MinimumSize = new Size(920, 600);
        StartPosition = FormStartPosition.CenterScreen;

        BuildUi();
        WireEvents();

        _scanRoot.Text = DefaultGameRoot();
        var bundledPayload = Path.Combine(AppContext.BaseDirectory, "Payload");
        if (Directory.Exists(bundledPayload))
            _payloadRoot.Text = bundledPayload;
        RefreshPayload();
    }

    void BuildUi()
    {
        var rootLabel = new Label { Text = "Game scan root", AutoSize = true };
        var payloadLabel = new Label { Text = "External DLSS/ReShade payload folder", AutoSize = true };
        var browseRoot = new Button { Text = "Browse..." };
        var browsePayload = new Button { Text = "Browse..." };
        var scan = new Button { Text = "Scan" };
        var addExe = new Button { Text = "Add EXE..." };

        _games.Columns.Add("Game", 190);
        _games.Columns.Add("EXE", 250);
        _games.Columns.Add("Arch", 70);
        _games.Columns.Add("API", 100);
        _games.Columns.Add("Route", 390);
        _games.Columns.Add("Detected by", 120);

        var main = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(12),
            ColumnCount = 1,
            RowCount = 8
        };
        main.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));
        main.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        main.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));
        main.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        main.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        main.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        main.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        main.RowStyles.Add(new RowStyle(SizeType.Absolute, 112));

        var rootRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, Margin = Padding.Empty };
        rootRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        rootRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 104));
        rootRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 96));
        rootRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 116));
        rootRow.Controls.Add(_scanRoot, 0, 0);
        rootRow.Controls.Add(browseRoot, 1, 0);
        rootRow.Controls.Add(scan, 2, 0);
        rootRow.Controls.Add(addExe, 3, 0);

        var payloadRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Margin = Padding.Empty };
        payloadRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        payloadRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 104));
        payloadRow.Controls.Add(_payloadRoot, 0, 0);
        payloadRow.Controls.Add(browsePayload, 1, 0);

        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Margin = new Padding(0, 5, 0, 5)
        };
        _install.Width = 135;
        _restore.Width = 135;
        _openFolder.Width = 120;
        _install.Height = _restore.Height = _openFolder.Height = 32;
        actions.Controls.AddRange([_install, _restore, _openFolder]);

        foreach (var control in new Control[] { rootLabel, payloadLabel, _scanRoot, _payloadRoot, _payloadStatus, _games, _log })
            control.Dock = DockStyle.Fill;

        main.Controls.Add(rootLabel, 0, 0);
        main.Controls.Add(rootRow, 0, 1);
        main.Controls.Add(payloadLabel, 0, 2);
        main.Controls.Add(payloadRow, 0, 3);
        main.Controls.Add(_payloadStatus, 0, 4);
        main.Controls.Add(_games, 0, 5);
        main.Controls.Add(actions, 0, 6);
        main.Controls.Add(_log, 0, 7);
        Controls.Add(main);
        _games.SizeChanged += (_, _) => ResizeGameColumns();

        browseRoot.Click += (_, _) => PickFolder(_scanRoot);
        browsePayload.Click += (_, _) => { PickFolder(_payloadRoot); RefreshPayload(); };
        scan.Click += async (_, _) => await ScanAsync();
        addExe.Click += (_, _) => AddExe();
    }

    void WireEvents()
    {
        _payloadRoot.TextChanged += (_, _) => RefreshPayload();
        _install.Click += async (_, _) => await InstallSelectedAsync();
        _restore.Click += async (_, _) => await RestoreSelectedAsync();
        _openFolder.Click += (_, _) => OpenSelectedFolder();
        _games.SelectedIndexChanged += (_, _) => UpdateButtons();
        UpdateButtons();
    }

    async Task ScanAsync()
    {
        if (!Directory.Exists(_scanRoot.Text))
        {
            MessageBox.Show(this, "Scan root does not exist.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        _scanCts?.Cancel();
        _scanCts = new CancellationTokenSource();
        SetBusy(true);
        _games.Items.Clear();
        Log("Scanning " + _scanRoot.Text);

        try
        {
            var progress = new Progress<string>(Log);
            var found = await GameScanner.ScanAsync(_scanRoot.Text, progress, _scanCts.Token);
            foreach (var game in found) AddGameRow(game);
            Log($"Scan complete: {found.Count} candidate(s).");
        }
        catch (OperationCanceledException)
        {
            Log("Scan cancelled.");
        }
        catch (Exception ex)
        {
            Log("Scan failed: " + ex.Message);
        }
        finally
        {
            SetBusy(false);
        }
    }

    void AddExe()
    {
        using var dialog = new OpenFileDialog
        {
            Filter = "Executable (*.exe)|*.exe",
            Title = "Choose game executable"
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        var game = GameScanner.ScanSingleExe(dialog.FileName);
        if (game is null)
        {
            MessageBox.Show(this, "Could not detect a supported DirectX executable.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        AddGameRow(game);
    }

    async Task InstallSelectedAsync()
    {
        var game = SelectedGame();
        if (game is null || _payload is null) return;

        if (game.Route == InstallRoute.Unsupported)
        {
            MessageBox.Show(this, "Selected game route is unsupported.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        SetBusy(true);
        try
        {
            Log("Installing " + game.Name + " using " + game.DisplayRoute);
            await new InstallerEngine(Log).InstallAsync(game, _payload);
        }
        catch (Exception ex)
        {
            Log("Install failed: " + ex.Message);
            MessageBox.Show(this, ex.Message, "Install failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    async Task RestoreSelectedAsync()
    {
        var game = SelectedGame();
        if (game is null) return;

        SetBusy(true);
        try
        {
            Log("Restoring " + game.Root);
            await new InstallerEngine(Log).RestoreAsync(game.Root);
        }
        catch (Exception ex)
        {
            Log("Restore failed: " + ex.Message);
            MessageBox.Show(this, ex.Message, "Restore failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    void OpenSelectedFolder()
    {
        var game = SelectedGame();
        if (game is null) return;
        Process.Start(new ProcessStartInfo { FileName = game.Root, UseShellExecute = true });
    }

    void RefreshPayload()
    {
        _payload = PayloadScanner.Scan(_payloadRoot.Text);
        _payloadStatus.Text = _payload.Summary;
        _payloadStatus.ForeColor = _payload.ReShadeSetup is not null && _payload.RenoDxDlss5Addon is not null && _payload.HasCoreDlss
            ? Color.DarkGreen
            : Color.DarkRed;
        UpdateButtons();
    }

    void AddGameRow(GameCandidate game)
    {
        var item = new ListViewItem(Path.GetFileName(game.Root));
        item.SubItems.Add(Path.GetRelativePath(game.Root, game.ExePath));
        item.SubItems.Add(game.Arch.ToString());
        item.SubItems.Add(game.DisplayApi);
        item.SubItems.Add(game.DisplayRoute);
        item.SubItems.Add(game.Detection);
        item.Tag = game;
        _games.Items.Add(item);
        ResizeGameColumns();
    }

    GameCandidate? SelectedGame()
    {
        return _games.SelectedItems.Count == 0 ? null : _games.SelectedItems[0].Tag as GameCandidate;
    }

    void UpdateButtons()
    {
        var game = SelectedGame();
        var hasGame = game is not null;
        var payloadOk = _payload?.ReShadeSetup is not null && _payload.RenoDxDlss5Addon is not null && _payload.HasCoreDlss;
        _install.Enabled = hasGame && payloadOk && game!.Route != InstallRoute.Unsupported;
        _restore.Enabled = hasGame;
        _openFolder.Enabled = hasGame;
    }

    void SetBusy(bool busy)
    {
        UseWaitCursor = busy;
        foreach (Control control in Controls)
            control.Enabled = !busy;
        _log.Enabled = true;
        if (!busy) UpdateButtons();
        Application.DoEvents();
    }

    void Log(string message)
    {
        if (InvokeRequired)
        {
            BeginInvoke(new Action<string>(Log), message);
            return;
        }

        _log.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}");
    }

    void PickFolder(TextBox target)
    {
        using var dialog = new FolderBrowserDialog
        {
            SelectedPath = Directory.Exists(target.Text) ? target.Text : "",
            UseDescriptionForTitle = true,
            Description = "Choose folder"
        };
        if (dialog.ShowDialog(this) == DialogResult.OK)
            target.Text = dialog.SelectedPath;
    }

    static string DefaultGameRoot()
    {
        foreach (var drive in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady))
        {
            var games = Path.Combine(drive.RootDirectory.FullName, "Games");
            if (Directory.Exists(games)) return games;
        }

        return Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
    }

    void ResizeGameColumns()
    {
        if (_games.Columns.Count == 0 || _games.ClientSize.Width <= 0) return;
        var width = _games.ClientSize.Width - SystemInformation.VerticalScrollBarWidth - 6;
        _games.Columns[0].Width = Math.Max(150, (int)(width * 0.17));
        _games.Columns[1].Width = Math.Max(220, (int)(width * 0.23));
        _games.Columns[2].Width = 70;
        _games.Columns[3].Width = 100;
        _games.Columns[5].Width = 120;
        _games.Columns[4].Width = Math.Max(240, width - _games.Columns[0].Width - _games.Columns[1].Width - _games.Columns[2].Width - _games.Columns[3].Width - _games.Columns[5].Width);
    }
}
