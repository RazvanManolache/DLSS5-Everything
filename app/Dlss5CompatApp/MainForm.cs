using System.Diagnostics;

namespace Dlss5CompatApp;

sealed class MainForm : Form
{
    static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Dlss5DxCompat",
        "settings.ini");
    static readonly string[] GameColumnTitles = ["Game", "EXE", "Path", "Arch", "APIs", "Route", "Detected by"];

    readonly TextBox _scanRoot = new() { Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top };
    readonly TextBox _payloadRoot = new() { Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top, ReadOnly = true, TabStop = false };
    readonly Label _payloadStatus = new() { AutoSize = false, Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top };
    readonly ProgressBar _payloadProgress = new() { Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top, Minimum = 0, Maximum = 100 };
    readonly TextBox _search = new() { Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top, PlaceholderText = "Search game, EXE, API, route, path..." };
    readonly CheckBox _hideIncompatible = new() { Text = "Hide incompatible", AutoSize = true, Checked = true, Anchor = AnchorStyles.Left, TextAlign = ContentAlignment.MiddleLeft };
    readonly CheckBox _forceVrEyeSplit = new() { Text = "Force VR eye split", AutoSize = true, Anchor = AnchorStyles.Left, TextAlign = ContentAlignment.MiddleLeft };
    readonly ComboBox _engineChoice = new() { DropDownStyle = ComboBoxStyle.DropDownList, DrawMode = DrawMode.OwnerDrawFixed, Width = 380 };
    readonly ListView _games = new() { View = View.Details, FullRowSelect = true, MultiSelect = false, ShowItemToolTips = true, Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right };
    readonly TextBox _log = new() { Multiline = true, ScrollBars = ScrollBars.Vertical, ReadOnly = true, Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom };
    readonly Button _install = new() { Text = "Install selected" };
    readonly Button _restore = new() { Text = "Restore selected" };
    readonly Button _runExe = new() { Text = "Run EXE" };
    readonly Button _openFolder = new() { Text = "Open folder" };
    readonly Button _updatePayload = new() { Text = "Update payload" };

    PayloadInfo? _payload;
    CancellationTokenSource? _scanCts;
    readonly List<GameCandidate> _allGames = [];
    int _sortColumn;
    bool _sortAscending = true;

    public MainForm()
    {
        Text = "DLSS5 Everything";
        Width = 1180;
        Height = 760;
        MinimumSize = new Size(920, 600);
        StartPosition = FormStartPosition.CenterScreen;

        BuildUi();
        WireEvents();

        _scanRoot.Text = LoadLastGameRoot() ?? DefaultGameRoot();
        _payloadRoot.Text = @".\Payload";
        RefreshPayload();
        Shown += async (_, _) => await BootstrapPayloadAsync();
    }

    void BuildUi()
    {
        var rootLabel = new Label { Text = "Game scan root", AutoSize = true };
        var payloadLabel = new Label { Text = "External DLSS/ReShade payload folder", AutoSize = true };
        var browseRoot = new Button { Text = "Browse..." };
        var browsePayload = new Button { Text = "Browse..." };
        var scan = new Button { Text = "Scan" };
        var addExe = new Button { Text = "Add EXE..." };

        _games.Columns.Add(GameColumnTitles[0], 190);
        _games.Columns.Add(GameColumnTitles[1], 170);
        _games.Columns.Add(GameColumnTitles[2], 360);
        _games.Columns.Add(GameColumnTitles[3], 70);
        _games.Columns.Add(GameColumnTitles[4], 100);
        _games.Columns.Add(GameColumnTitles[5], 300);
        _games.Columns.Add(GameColumnTitles[6], 120);

        var main = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(12),
            ColumnCount = 1,
            RowCount = 10
        };
        main.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));
        main.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        main.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));
        main.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        main.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        main.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
        main.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
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

        var payloadRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, Margin = Padding.Empty };
        payloadRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        payloadRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 104));
        payloadRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 132));
        payloadRow.Controls.Add(_payloadRoot, 0, 0);
        payloadRow.Controls.Add(browsePayload, 1, 0);
        payloadRow.Controls.Add(_updatePayload, 2, 0);

        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Margin = new Padding(0, 5, 0, 5)
        };
        _install.Width = 135;
        _restore.Width = 135;
        _runExe.Width = 100;
        _openFolder.Width = 120;
        _install.Height = _restore.Height = _runExe.Height = _openFolder.Height = 32;
        _engineChoice.Height = 28;
        actions.Controls.AddRange([
            new Label { Text = "Engine", AutoSize = true, Height = 32, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(0, 8, 4, 0) },
            _engineChoice,
            _install,
            _restore,
            _runExe,
            _openFolder
        ]);

        var searchRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, Margin = Padding.Empty };
        searchRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 80));
        searchRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        searchRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 160));
        searchRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 170));
        searchRow.Controls.Add(new Label { Text = "Search", AutoSize = true, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 0);
        searchRow.Controls.Add(_search, 1, 0);
        searchRow.Controls.Add(_hideIncompatible, 2, 0);
        searchRow.Controls.Add(_forceVrEyeSplit, 3, 0);

        foreach (var control in new Control[] { rootLabel, payloadLabel, _scanRoot, _payloadRoot, _payloadStatus, _payloadProgress, _search, _games, _log })
            control.Dock = DockStyle.Fill;

        main.Controls.Add(rootLabel, 0, 0);
        main.Controls.Add(rootRow, 0, 1);
        main.Controls.Add(payloadLabel, 0, 2);
        main.Controls.Add(payloadRow, 0, 3);
        main.Controls.Add(_payloadStatus, 0, 4);
        main.Controls.Add(_payloadProgress, 0, 5);
        main.Controls.Add(searchRow, 0, 6);
        main.Controls.Add(_games, 0, 7);
        main.Controls.Add(actions, 0, 8);
        main.Controls.Add(_log, 0, 9);
        Controls.Add(main);
        _games.SizeChanged += (_, _) => ResizeGameColumns();

        browseRoot.Click += (_, _) =>
        {
            if (PickFolder(_scanRoot))
                SaveLastGameRoot(_scanRoot.Text);
        };
        browsePayload.Click += (_, _) => { PickPayloadFolder(); RefreshPayload(); };
        scan.Click += async (_, _) => await ScanAsync();
        addExe.Click += (_, _) => AddExe();
    }

    void WireEvents()
    {
        _install.Click += async (_, _) => await InstallSelectedAsync();
        _restore.Click += async (_, _) => await RestoreSelectedAsync();
        _runExe.Click += (_, _) => RunSelectedExe();
        _openFolder.Click += (_, _) => OpenSelectedFolder();
        _updatePayload.Click += async (_, _) => await BootstrapPayloadAsync();
        _games.SelectedIndexChanged += (_, _) => UpdateEngineChoices();
        _engineChoice.SelectedIndexChanged += (_, _) =>
        {
            UpdateSelectedRouteDisplay();
            UpdateButtons();
        };
        _engineChoice.DrawItem += DrawEngineChoice;
        _games.ColumnClick += (_, e) => SortByColumn(e.Column);
        _search.TextChanged += (_, _) => RenderGameRows();
        _hideIncompatible.CheckedChanged += (_, _) => RenderGameRows();
        UpdateButtons();
    }

    async Task ScanAsync()
    {
        if (!Directory.Exists(_scanRoot.Text))
        {
            MessageBox.Show(this, "Scan root does not exist.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        SaveLastGameRoot(_scanRoot.Text);

        _scanCts?.Cancel();
        _scanCts = new CancellationTokenSource();
        SetBusy(true);
        _allGames.Clear();
        RenderGameRows();
        Log("Scanning " + _scanRoot.Text);

        try
        {
            var progress = new Progress<string>(Log);
            var found = await GameScanner.ScanAsync(_scanRoot.Text, progress, _scanCts.Token);
            _allGames.AddRange(found);
            RenderGameRows();
            Log($"Scan complete: {found.Count} candidate(s), {_games.Items.Count} shown.");
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

    async Task BootstrapPayloadAsync()
    {
        _updatePayload.Enabled = false;
        _payloadProgress.Value = 0;
        var payloadPath = ResolvePayloadPath();
        var progress = new Progress<BootstrapProgress>(item =>
        {
            _payloadProgress.Value = Math.Clamp(item.Percent, _payloadProgress.Minimum, _payloadProgress.Maximum);
            if (!string.IsNullOrWhiteSpace(item.Message))
                Log(item.Message);
        });

        try
        {
            Log("Checking payload downloads in " + DisplayPath(payloadPath));
            await PayloadBootstrapper.EnsureCurrentAsync(payloadPath, progress, CancellationToken.None);
        }
        catch (Exception ex)
        {
            Log("Payload update failed: " + ex.Message);
        }
        finally
        {
            RefreshPayload();
            _updatePayload.Enabled = true;
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

        _scanRoot.Text = Path.GetDirectoryName(dialog.FileName) ?? _scanRoot.Text;
        SaveLastGameRoot(_scanRoot.Text);
        _allGames.RemoveAll(x => x.ExePath.Equals(game.ExePath, StringComparison.OrdinalIgnoreCase));
        _allGames.Add(game);
        RenderGameRows(game.ExePath);
    }

    async Task InstallSelectedAsync()
    {
        var game = SelectedGame();
        if (game is null || _payload is null) return;

        if (game.Route == InstallRoute.Unsupported)
        {
            var fallback = BestSupportedFallback(game, _payload);
            if (fallback is null)
            {
                MessageBox.Show(this,
                    "That engine is visible because the scanner found it, but this app does not have an install backend for it yet.",
                    Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            var answer = MessageBox.Show(this,
                "The selected engine is not directly supported:\n\n" +
                game.DisplayApi + "\n" + game.DisplayRoute + "\n\n" +
                "Install the closest supported route instead?\n\n" +
                fallback.DisplayApi + "\n" + fallback.DisplayRoute + "\n\n" +
                "This may not work if the game is forced to use the unsupported engine.",
                "Unsupported engine selected",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2);
            if (answer != DialogResult.Yes)
                return;

            game = fallback;
        }

        SetBusy(true);
        try
        {
            Log("Installing " + game.Name + " using " + game.DisplayRoute);
            await new InstallerEngine(Log).InstallAsync(game, _payload, _forceVrEyeSplit.Checked);
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
        try
        {
            var target = File.Exists(game.ExePath) ? game.ExePath : game.Root;
            var arguments = File.Exists(target)
                ? $"/select,\"{target}\""
                : $"\"{target}\"";
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = arguments,
                UseShellExecute = false
            });
            Log("Opened folder for " + game.Name);
        }
        catch (Exception ex)
        {
            Log("Open folder failed: " + ex.Message);
            MessageBox.Show(this, ex.Message, "Open folder failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    void RunSelectedExe()
    {
        var game = SelectedGame();
        if (game is null) return;
        try
        {
            var workingDirectory = Path.GetDirectoryName(game.ExePath) ?? game.Root;
            var start = new ProcessStartInfo
            {
                FileName = game.ExePath,
                WorkingDirectory = workingDirectory,
                UseShellExecute = true
            };
            if (!string.IsNullOrWhiteSpace(game.SuggestedArguments))
                start.Arguments = game.SuggestedArguments;
            Process.Start(start);
            Log("Launched " + game.Name + (string.IsNullOrWhiteSpace(game.SuggestedArguments) ? "" : " " + game.SuggestedArguments));
        }
        catch (Exception ex)
        {
            Log("Launch failed: " + ex.Message);
            MessageBox.Show(this, ex.Message, "Launch failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    void RefreshPayload()
    {
        _payload = PayloadScanner.Scan(ResolvePayloadPath());
        _payloadStatus.Text = _payload.Summary;
        _payloadStatus.ForeColor = _payload.RenoDxDlss5Addon is not null && _payload.HasCoreDlss && _payload.HasReShade64
            ? Color.DarkGreen
            : Color.DarkRed;
        UpdateButtons();
    }

    void RenderGameRows(string? selectExePath = null)
    {
        selectExePath ??= SelectedGame()?.ExePath;
        UpdateColumnHeaders();
        _games.BeginUpdate();
        try
        {
            _games.Items.Clear();
            foreach (var game in SortGames(FilterGames(_allGames)))
                AddGameRow(game);

            if (selectExePath is not null)
            {
                foreach (ListViewItem item in _games.Items)
                {
                    if (item.Tag is GameCandidate game &&
                        game.ExePath.Equals(selectExePath, StringComparison.OrdinalIgnoreCase))
                    {
                        item.Selected = true;
                        item.Focused = true;
                        item.EnsureVisible();
                        break;
                    }
                }
            }
        }
        finally
        {
            _games.EndUpdate();
            ResizeGameColumns();
            UpdateButtons();
        }
    }

    IEnumerable<GameCandidate> FilterGames(IEnumerable<GameCandidate> games)
    {
        var result = _hideIncompatible.Checked
            ? games.Where(game => game.Route != InstallRoute.Unsupported)
            : games;

        var tokens = _search.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length == 0) return result;

        return result.Where(game => tokens.All(token => GameMatches(game, token)));
    }

    static bool GameMatches(GameCandidate game, string token)
    {
        return Contains(game.DisplayName, token) ||
               Contains(game.Name, token) ||
               Contains(game.ExePath, token) ||
               Contains(game.Root, token) ||
               Contains(game.Arch.ToString(), token) ||
               Contains(game.DisplayApi, token) ||
               Contains(game.DisplayPossibleApis, token) ||
               Contains(game.DisplayRoute, token) ||
               Contains(game.Detection, token);
    }

    static bool Contains(string value, string token)
    {
        return value.Contains(token, StringComparison.OrdinalIgnoreCase);
    }

    IEnumerable<GameCandidate> SortGames(IEnumerable<GameCandidate> games)
    {
        var sorted = games.Order(new GameCandidateComparer(_sortColumn, _sortAscending));
        return sorted.ThenBy(x => x.DisplayName, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(x => x.ExePath, StringComparer.OrdinalIgnoreCase);
    }

    void SortByColumn(int column)
    {
        if (column == _sortColumn)
            _sortAscending = !_sortAscending;
        else
        {
            _sortColumn = column;
            _sortAscending = true;
        }
        RenderGameRows();
    }

    void UpdateColumnHeaders()
    {
        for (var i = 0; i < _games.Columns.Count && i < GameColumnTitles.Length; i++)
            _games.Columns[i].Text = i == _sortColumn
                ? GameColumnTitles[i] + (_sortAscending ? " ^" : " v")
                : GameColumnTitles[i];
    }

    void AddGameRow(GameCandidate game)
    {
        var item = new ListViewItem(game.DisplayName);
        item.SubItems.Add(game.Name);
        item.SubItems.Add(game.ExePath);
        item.SubItems.Add(game.Arch.ToString());
        item.SubItems.Add(game.DisplayPossibleApis);
        item.SubItems.Add(game.DisplayRoute);
        item.SubItems.Add(game.Detection);
        item.ToolTipText = string.IsNullOrWhiteSpace(game.SuggestedArguments)
            ? game.ExePath
            : game.ExePath + " " + game.SuggestedArguments;
        item.Tag = game;
        _games.Items.Add(item);
    }

    GameCandidate? SelectedGame()
    {
        var game = SelectedBaseGame();
        if (game is null) return null;
        return _engineChoice.SelectedItem is EngineChoice choice
            ? game.WithApi(choice.Api)
            : game;
    }

    GameCandidate? SelectedBaseGame()
    {
        return _games.SelectedItems.Count == 0 ? null : _games.SelectedItems[0].Tag as GameCandidate;
    }

    void UpdateEngineChoices()
    {
        var game = SelectedBaseGame();
        _engineChoice.BeginUpdate();
        try
        {
            _engineChoice.Items.Clear();
            if (game is not null)
            {
                foreach (var api in game.AllApis)
                    _engineChoice.Items.Add(new EngineChoice(api, EngineChoiceLabel(game, api)));

                var selected = 0;
                for (var i = 0; i < _engineChoice.Items.Count; i++)
                {
                    if (_engineChoice.Items[i] is EngineChoice choice && choice.Api == game.Api)
                    {
                        selected = i;
                        break;
                    }
                }
                _engineChoice.SelectedIndex = selected;
            }
        }
        finally
        {
            _engineChoice.EndUpdate();
        }
        _engineChoice.Enabled = game is not null && game.AllApis.Count > 1;
        UpdateSelectedRouteDisplay();
        UpdateButtons();
    }

    static string EngineChoiceLabel(GameCandidate game, GraphicsApi api)
    {
        var choice = game.WithApi(api);
        var ready = choice.Route == InstallRoute.Unsupported ? "unsupported" : choice.DisplayRoute;
        return choice.DisplayApi + " - " + ready;
    }

    void UpdateSelectedRouteDisplay()
    {
        if (_games.SelectedItems.Count == 0) return;
        var game = SelectedGame();
        if (game is null) return;
        var item = _games.SelectedItems[0];
        if (item.SubItems.Count > 5)
            item.SubItems[5].Text = game.DisplayRoute;
        item.ToolTipText = string.IsNullOrWhiteSpace(game.SuggestedArguments)
            ? game.ExePath
            : game.ExePath + " " + game.SuggestedArguments;
    }

    void UpdateButtons()
    {
        var game = SelectedGame();
        var hasGame = game is not null;
        var installGame = game is null || _payload is null
            ? null
            : game.Route == InstallRoute.Unsupported
                ? BestSupportedFallback(game, _payload)
                : game;
        var payloadOk = installGame is not null && _payload?.IsReadyFor(installGame.Route) == true;
        _install.Enabled = hasGame && (payloadOk || game!.Route == InstallRoute.Unsupported);
        _restore.Enabled = hasGame;
        _runExe.Enabled = hasGame;
        _openFolder.Enabled = hasGame;
        _engineChoice.Enabled = hasGame && (SelectedBaseGame()?.AllApis.Count ?? 0) > 1;
    }

    static GameCandidate? BestSupportedFallback(GameCandidate selected, PayloadInfo? payload)
    {
        if (selected.Route != InstallRoute.Unsupported)
            return selected;

        foreach (var api in selected.AllApis)
        {
            if (api == selected.Api)
                continue;

            var candidate = selected.WithApi(api);
            if (candidate.Route == InstallRoute.Unsupported)
                continue;

            if (payload is null || payload.IsReadyFor(candidate.Route))
                return candidate;
        }

        return null;
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

    bool PickFolder(TextBox target)
    {
        using var dialog = new FolderBrowserDialog
        {
            SelectedPath = Directory.Exists(target.Text) ? target.Text : "",
            UseDescriptionForTitle = true,
            Description = "Choose folder"
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return false;
        target.Text = dialog.SelectedPath;
        return true;
    }

    void PickPayloadFolder()
    {
        using var dialog = new FolderBrowserDialog
        {
            SelectedPath = Directory.Exists(ResolvePayloadPath()) ? ResolvePayloadPath() : AppContext.BaseDirectory,
            UseDescriptionForTitle = true,
            Description = "Choose external DLSS/ReShade payload folder"
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        _payloadRoot.Text = DisplayPath(dialog.SelectedPath);
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

    static string? LoadLastGameRoot()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return null;
            foreach (var line in File.ReadLines(SettingsPath))
            {
                if (!line.StartsWith("LastGameRoot=", StringComparison.OrdinalIgnoreCase)) continue;
                var path = line["LastGameRoot=".Length..].Trim();
                return Directory.Exists(path) ? path : null;
            }
        }
        catch { }

        return null;
    }

    static void SaveLastGameRoot(string path)
    {
        try
        {
            if (!Directory.Exists(path)) return;
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllText(SettingsPath, "LastGameRoot=" + Path.GetFullPath(path));
        }
        catch { }
    }

    void ResizeGameColumns()
    {
        if (_games.Columns.Count < GameColumnTitles.Length || _games.ClientSize.Width <= 0) return;
        var width = _games.ClientSize.Width - SystemInformation.VerticalScrollBarWidth - 6;
        _games.Columns[0].Width = Math.Max(170, (int)(width * 0.18));
        _games.Columns[1].Width = Math.Max(170, (int)(width * 0.17));
        _games.Columns[3].Width = 70;
        _games.Columns[4].Width = 100;
        _games.Columns[6].Width = 120;
        _games.Columns[5].Width = Math.Max(260, (int)(width * 0.24));
        _games.Columns[2].Width = Math.Max(360, width - _games.Columns[0].Width - _games.Columns[1].Width - _games.Columns[3].Width - _games.Columns[4].Width - _games.Columns[5].Width - _games.Columns[6].Width);
    }

    string ResolvePayloadPath()
    {
        var text = _payloadRoot.Text.Trim();
        if (string.IsNullOrWhiteSpace(text)) return AppContext.BaseDirectory;
        return Path.IsPathRooted(text) ? text : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, text));
    }

    static string DisplayPath(string path)
    {
        var relative = Path.GetRelativePath(AppContext.BaseDirectory, path);
        return !relative.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(relative)
            ? @".\" + relative
            : path;
    }

    sealed class GameCandidateComparer(int column, bool ascending) : IComparer<GameCandidate>
    {
        public int Compare(GameCandidate? x, GameCandidate? y)
        {
            if (ReferenceEquals(x, y)) return 0;
            if (x is null) return ascending ? -1 : 1;
            if (y is null) return ascending ? 1 : -1;

            var result = column switch
            {
                0 => TextCompare(x.DisplayName, y.DisplayName),
                1 => TextCompare(x.Name, y.Name),
                2 => TextCompare(x.ExePath, y.ExePath),
                3 => x.Arch.CompareTo(y.Arch),
                4 => TextCompare(x.DisplayPossibleApis, y.DisplayPossibleApis),
                5 => TextCompare(x.DisplayRoute, y.DisplayRoute),
                6 => TextCompare(x.Detection, y.Detection),
                _ => TextCompare(x.DisplayName, y.DisplayName)
            };

            return ascending ? result : -result;
        }

        static int TextCompare(string left, string right)
        {
            return string.Compare(left, right, StringComparison.OrdinalIgnoreCase);
        }
    }

    sealed class EngineChoice(GraphicsApi api, string label)
    {
        public GraphicsApi Api { get; } = api;
        readonly string _label = label;
        public override string ToString() => _label;
    }

    void DrawEngineChoice(object? sender, DrawItemEventArgs e)
    {
        e.DrawBackground();
        if (e.Index < 0 || e.Index >= _engineChoice.Items.Count)
            return;

        if (_engineChoice.Items[e.Index] is not EngineChoice choice)
            return;

        var baseGame = SelectedBaseGame();
        var game = baseGame?.WithApi(choice.Api);
        var unsupported = game?.Route == InstallRoute.Unsupported;
        var selected = (e.State & DrawItemState.Selected) != 0;
        var color = unsupported == true && !selected ? Color.Firebrick : e.ForeColor;
        TextRenderer.DrawText(e.Graphics, choice.ToString(), e.Font ?? Font, e.Bounds, color,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        e.DrawFocusRectangle();
    }
}
