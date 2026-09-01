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

        rootLabel.SetBounds(12, 15, 120, 22);
        _scanRoot.SetBounds(12, 38, 910, 27);
        browseRoot.SetBounds(930, 37, 95, 29);
        scan.SetBounds(1032, 37, 120, 29);
        addExe.SetBounds(1032, 72, 120, 29);

        payloadLabel.SetBounds(12, 78, 260, 22);
        _payloadRoot.SetBounds(12, 101, 910, 27);
        browsePayload.SetBounds(930, 100, 95, 29);
        _payloadStatus.SetBounds(12, 131, 1140, 24);

        _games.SetBounds(12, 160, 1140, 390);
        _games.Columns.Add("Game", 190);
        _games.Columns.Add("EXE", 250);
        _games.Columns.Add("Arch", 70);
        _games.Columns.Add("API", 100);
        _games.Columns.Add("Route", 390);
        _games.Columns.Add("Detected by", 120);

        _install.SetBounds(12, 560, 135, 34);
        _restore.SetBounds(155, 560, 135, 34);
        _openFolder.SetBounds(298, 560, 120, 34);
        _log.SetBounds(12, 604, 1140, 105);

        Controls.AddRange([
            rootLabel, _scanRoot, browseRoot, scan, addExe,
            payloadLabel, _payloadRoot, browsePayload, _payloadStatus,
            _games, _install, _restore, _openFolder, _log
        ]);

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
}
