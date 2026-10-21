namespace Dlss5CompatApp;

sealed partial class MainForm
{
    AppPreferences _appPreferences = new();
    UiLocalization _localization = new();
    bool _convenienceInitialized;
    bool _preferencesWritable = true;
    bool _applyingConvenience;
    bool _exitFromTray;
    bool _convenienceDisposed;
    string? _lastViewedGame;
    NotifyIcon? _convenienceTray;
    ContextMenuStrip? _trayCommands;
    FormWindowState _beforeTrayState = FormWindowState.Normal;
    readonly Dictionary<Control, string> _localizedControls = [];
    readonly Dictionary<ToolStripItem, string> _localizedMenu = [];
    readonly List<ComboBox> _localizedCombos = [];
    readonly System.Windows.Forms.Timer _convenienceTimer = new() { Interval = 400 };
    readonly ImageList _tileImages = new() { ImageSize = new Size(156, 256), ColorDepth = ColorDepth.Depth32Bit };
    readonly Dictionary<string, int> _tileImageIndexes = new(StringComparer.OrdinalIgnoreCase);
    ListViewItem? _tileFirstRow;
    int _tileRowCount = -1;
    string? _tileSelectedArtwork;
    int _tileArtworkRevision;
    int _tileRenderedRevision = -1;
    TableLayoutPanel? _convenienceSidebar;
    TableLayoutPanel? _convenienceShell;
    float _expandedSidebarWidth;
    Padding _expandedSidebarPadding;
    readonly Dictionary<Control, bool> _sidebarLabels = [];
    readonly Dictionary<Button, (bool AutoSize, Size MinimumSize)> _navigationSizes = [];
    Button? _tilesCommand;
    Button? _listCommand;

    // Parent calls once, immediately after InitializeLibrary(). No existing partial needs other hooks.
    void InitializeConvenience()
    {
        if (_convenienceInitialized) return;
        _convenienceInitialized = true;
        if (!_preview)
        {
            try { _appPreferences = AppPreferences.Load(); }
            catch (Exception ex)
            {
                _preferencesWritable = false;
                Log("Could not read preferences; the original file was kept: " + ex.Message);
            }
        }

        BindConvenienceLocalization(this);
        if (_games.ContextMenuStrip is { } menu)
            foreach (ToolStripItem item in menu.Items) BindConvenienceMenu(item);
        foreach (var combo in new[] { _libraryFilter, _apiFilter, _storeFilter, _themeChoice })
        {
            combo.DrawMode = DrawMode.OwnerDrawFixed;
            combo.DrawItem += DrawConvenienceCombo;
            _localizedCombos.Add(combo);
        }

        if (_pages.TryGetValue("Settings", out var settings))
        {
            if (settings.Controls.Count == 1 && settings.Controls[0] is TableLayoutPanel settingsLayout)
            {
                var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
                settings.Controls.Remove(settingsLayout);
                settingsLayout.Dock = DockStyle.Top;
                settingsLayout.Height = Math.Max(560, (int)settingsLayout.RowStyles.Cast<RowStyle>()
                    .Where(s => s.SizeType == SizeType.Absolute).Sum(s => s.Height) + 32);
                scroll.Controls.Add(settingsLayout);
                settings.Controls.Add(scroll);
                UiTheme.Apply(scroll, _palette);
            }
            var preferences = Command("Application behavior…", "\uE713", ShowConveniencePreferences);
            preferences.Name = "ConveniencePreferences";
            preferences.Dock = DockStyle.None;
            preferences.Height = 38;
            var application = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(0, 8, 0, 0) };
            application.Controls.Add(new Label { Text = "Application", AutoSize = true, Font = new Font(Font, FontStyle.Bold) });
            application.Controls.Add(new Label { Text = "Language, startup, tray behavior, recent games and navigation", AutoSize = true });
            application.Controls.Add(preferences);
            settings.Controls.Add(application);
            application.SendToBack();
            UiTheme.Apply(application, _palette);
            _tips.SetToolTip(preferences, "Application preferences: language, tray behavior, startup, recent games and navigation.");
        }
        if (_pages.TryGetValue("Library", out var library))
        {
            var toolbar = new ToolStrip { Name = "ConvenienceToolbar", Dock = DockStyle.Bottom, GripStyle = ToolStripGripStyle.Hidden, AutoSize = false, Height = 36, TabStop = true, RenderMode = ToolStripRenderMode.System, BackColor = _palette.Surface, ForeColor = _palette.Text };
            AddConvenienceTool(toolbar, "commands", "\uE721", ShowConvenienceCommands, "Ctrl+K / Ctrl+P");
            AddConvenienceTool(toolbar, "recentTitle", "\uE81C", ShowConvenienceRecents);
            _listCommand = Command("List", "\uE8FD", () => SetConvenienceTiles(false));
            _tilesCommand = Command("Previews", "\uE8A9", () => SetConvenienceTiles(true));
            foreach (var mode in new[] { _listCommand, _tilesCommand })
            {
                mode.AutoSize = false;
                mode.MinimumSize = Size.Empty;
                mode.Size = new Size(91, 30);
                mode.Margin = new Padding(0, 0, 4, 0);
                _libraryViewTools.Controls.Add(mode);
            }
            var sort = new ContextMenuStrip();
            foreach (var (title, column) in new[] { ("Game", 0), ("EXE", 1), ("Architecture", 3), ("Renderer", 4), ("Installation", 7) })
            {
                var item = sort.Items.Add(title, null, (_, _) => { if (!_busy) SortByColumn(column); });
                item.Tag = column;
            }
            sort.Opening += (_, _) =>
            {
                foreach (var item in sort.Items.OfType<ToolStripMenuItem>())
                {
                    item.Enabled = !_busy;
                    item.Checked = (int)item.Tag! == _sortColumn;
                }
            };
            var sortButton = LibraryMenuButton("Sort games", "\uE8CB", sort);
            _libraryViewTools.Controls.Add(sortButton);
            BuildArtworkMenu(_libraryViewTools);
            library.Controls.Add(toolbar);
            toolbar.SendToBack();
        }

        // The parent sidebar is a local variable. Locate it through its known navigation controls.
        if (_navigation.TryGetValue("Library", out var libraryNav) && libraryNav.Parent is TableLayoutPanel sidebar
            && sidebar.Parent is TableLayoutPanel shell && shell.ColumnStyles.Count > 0)
        {
            _convenienceSidebar = sidebar;
            _convenienceShell = shell;
            _expandedSidebarWidth = shell.ColumnStyles[0].Width;
            _expandedSidebarPadding = sidebar.Padding;
            foreach (Control label in sidebar.Controls.OfType<Label>()) _sidebarLabels[label] = true;
            foreach (var button in _navigation.Values) _navigationSizes[button] = (button.AutoSize, button.MinimumSize);
        }
        _games.ItemSelectionChanged += (_, e) =>
        {
            if (e.IsSelected && e.Item?.Tag is GameCandidate game && !_applyingConvenience)
                RecordConvenienceView(game);
        };
        _games.ColumnClick += (_, _) => QueueConvenienceColumnRefresh();
        _games.HandleCreated += (_, _) => SetPosterSpacing();
        _games.DpiChangedAfterParent += (_, _) => SetPosterSpacing();
        FormClosing += ConvenienceFormClosing;
        FormClosed += (_, _) => DisposeConvenience();
        Disposed += (_, _) => DisposeConvenience();
        Shown += async (_, _) =>
        {
            if (!_preview && _appPreferences.ScanOnStartup && _libraryState.ScanRoots.Count > 0)
                await ScanSavedRootsAsync();
        };
        _convenienceTimer.Tick += (_, _) => { RefreshConvenienceColumns(); RefreshConvenienceTiles(); };
        ApplyConveniencePreferences();
        _convenienceTimer.Start();
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (_convenienceInitialized && (keyData == (Keys.Control | Keys.K) || keyData == (Keys.Control | Keys.P)))
        {
            ShowConvenienceCommands();
            return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    ToolStripButton AddConvenienceTool(ToolStrip toolbar, string key, string glyph, Action action, string? shortcut = null)
    {
        var image = UiTheme.Icon(glyph, _palette.Accent);
        var button = new ToolStripButton { Name = "Convenience_" + key, Image = image, DisplayStyle = ToolStripItemDisplayStyle.Image, AutoToolTip = false };
        button.Click += (_, _) => action();
        button.Disposed += (_, _) => image.Dispose();
        _localizedMenu[button] = key;
        button.Tag = shortcut;
        toolbar.Items.Add(button);
        return button;
    }

    void RecordConvenienceView(GameCandidate game)
    {
        if (string.Equals(_lastViewedGame, game.ExePath, StringComparison.OrdinalIgnoreCase)) return;
        _lastViewedGame = game.ExePath;
        try
        {
            if (_appPreferences.RecordViewed(game.ExePath, game.DisplayName)) SaveConveniencePreferences();
        }
        catch (Exception ex) { Log("Could not record recent game: " + ex.Message); }
    }

    bool SaveConveniencePreferences()
    {
        if (_preview) return true;
        if (!_preferencesWritable) return false;
        try { _appPreferences.Save(); return true; }
        catch (Exception ex) { Log("Could not save preferences: " + ex.Message); return false; }
    }

    void SetConvenienceTiles(bool tiles)
    {
        var next = _appPreferences.Copy();
        next.UseTiles = tiles;
        if (!TryApplyConveniencePreferences(next, out var error))
        {
            _appPreferences.UseTiles = tiles;
            ApplyConveniencePreferences();
            Log(error);
            SetOutcome("View changed for this session. Could not save the preference: " + error);
        }
    }

    bool TryApplyConveniencePreferences(AppPreferences next, out string error)
    {
        try
        {
            next.Validate();
            if (!_preview)
            {
                if (!_preferencesWritable) throw new IOException("Preferences could not be loaded. The saved file is unchanged.");
                next.Save();
            }
            _appPreferences = next;
            ApplyConveniencePreferences();
            error = "";
            return true;
        }
        catch (Exception ex) { error = ex.Message; return false; }
    }

    void ApplyConveniencePreferences()
    {
        _applyingConvenience = true;
        SuspendLayout();
        try
        {
            _localization = new(_appPreferences.Language);
            foreach (var (control, key) in _localizedControls) SetConvenienceText(control, key);
            foreach (var (item, key) in _localizedMenu)
            {
                item.Text = _localization[key];
                item.AccessibleName = item.Text;
                item.ToolTipText = item.Text + (item.Tag is string shortcut ? " (" + shortcut + ")" : "");
            }
            if (_tilesCommand is not null) _tilesCommand.Text = _tilesCommand.AccessibleName = _localization["previewView"];
            if (_listCommand is not null) _listCommand.Text = _listCommand.AccessibleName = _localization["listView"];
            foreach (var combo in _localizedCombos) combo.Invalidate();
            _search.PlaceholderText = _localization["searchGamesHint"];
            _launchArguments.PlaceholderText = _localization["launchArguments"];
            ApplyConvenienceNavigation();
            // Changing View keeps the native rows, their Tag, selection, groups and parent sort state.
            _games.View = _appPreferences.UseTiles ? View.LargeIcon : View.Details;
            ApplyMyGamesView();
            _games.HideSelection = false;
            SetPosterSpacing();
            if (!_appPreferences.AutomaticArtwork) _artworkCts?.Cancel();
            if (_listCommand is not null) { _listCommand.Tag = !_appPreferences.UseTiles ? "primary" : null; UiTheme.Apply(_listCommand, _palette); }
            if (_tilesCommand is not null) { _tilesCommand.Tag = _appPreferences.UseTiles ? "primary" : null; UiTheme.Apply(_tilesCommand, _palette); }
            _tileRowCount = -1;
            RefreshConvenienceColumns();
            RefreshConvenienceTiles();
            QueueArtwork();
            if (!_appPreferences.RememberRecents) _lastViewedGame = null;
        }
        finally { ResumeLayout(true); _applyingConvenience = false; }
    }

    void ApplyConvenienceNavigation()
    {
        if (_convenienceSidebar is null || _convenienceShell is null) return;
        var compact = _appPreferences.CompactNavigation || ClientSize.Width < 1180 * DeviceDpi / 96;
        var dpiScale = DeviceDpi / 96f;
        _convenienceShell.ColumnStyles[0].Width = compact ? 72 * dpiScale : _expandedSidebarWidth;
        _convenienceSidebar.Padding = compact ? new Padding((int)(8 * dpiScale), (int)(22 * dpiScale), (int)(8 * dpiScale), (int)(12 * dpiScale)) : _expandedSidebarPadding;
        foreach (var (label, visible) in _sidebarLabels) label.Visible = !compact && visible;
        foreach (var (key, button) in _navigation)
        {
            var title = _localization[UiLocalization.CommonActions[key]];
            var original = _navigationSizes.GetValueOrDefault(button);
            button.AutoSize = compact ? false : original.AutoSize;
            button.MinimumSize = compact ? new Size(32, 32) : original.MinimumSize;
            button.Text = compact ? "" : title;
            button.ImageAlign = compact ? ContentAlignment.MiddleCenter : ContentAlignment.MiddleLeft;
            button.AccessibleName = title;
            _tips.SetToolTip(button, title);
        }
    }

    void BindConvenienceLocalization(Control root)
    {
        if (root != _detailName && root != _detailRoute && root != _detailStatus && root != _gameCount
            && root is Button or CheckBox or Label && UiLocalization.CommonActions.TryGetValue(root.Text, out var key))
            BindConvenienceControl(root, key);
        foreach (Control child in root.Controls) BindConvenienceLocalization(child);
    }

    void BindConvenienceControl(Control control, string key)
    {
        _localizedControls[control] = key;
        control.TextChanged += (_, _) =>
        {
            if (_applyingConvenience) return;
            if (!UiLocalization.CommonActions.TryGetValue(control.Text, out var next)) return;
            _localizedControls[control] = next;
            _applyingConvenience = true;
            try { SetConvenienceText(control, next); }
            finally { _applyingConvenience = false; }
        };
    }

    void SetConvenienceText(Control control, string key)
    {
        control.Text = _localization[key];
        control.AccessibleName = control.Text;
        control.RightToLeft = _localization.RightToLeft ? RightToLeft.Yes : RightToLeft.No;
        if (control is Button) _tips.SetToolTip(control, control.Text);
    }

    void BindConvenienceMenu(ToolStripItem item)
    {
        if (UiLocalization.CommonActions.TryGetValue(item.Text ?? "", out var key)) _localizedMenu[item] = key;
    }

    void DrawConvenienceCombo(object? sender, DrawItemEventArgs e)
    {
        if (sender is not ComboBox combo || e.Index < 0) return;
        using var brush = new SolidBrush((e.State & DrawItemState.Selected) != 0 ? _palette.Selection : _palette.Surface);
        e.Graphics.FillRectangle(brush, e.Bounds);
        var original = combo.Items[e.Index]?.ToString() ?? "";
        var key = UiLocalization.CommonActions.GetValueOrDefault(original) ?? original switch
        {
            "All games" => "allGames", "Installed" => "installed", "Favorites" => "favorites", "Hidden" => "hidden",
            "All renderers" => "allApis", "All stores" => "allStores", _ => ""
        };
        TextRenderer.DrawText(e.Graphics, key.Length == 0 ? original : _localization[key], e.Font, e.Bounds, combo.Enabled ? _palette.Text : _palette.Muted,
            TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | (_localization.RightToLeft ? TextFormatFlags.RightToLeft | TextFormatFlags.Right : TextFormatFlags.Left));
        e.DrawFocusRectangle();
    }

    void QueueConvenienceColumnRefresh()
    {
        if (IsHandleCreated && !IsDisposed) BeginInvoke(new Action(RefreshConvenienceColumns));
    }

    void RefreshConvenienceColumns()
    {
        string[] keys = ["columnGame", "fExe", "Path", "Arch", "API", "fRoute", "Detected by", "columnStatus"];
        for (var i = 0; i < Math.Min(keys.Length, _games.Columns.Count); i++)
        {
            var title = _localization[keys[i]] + (i == _sortColumn ? (_sortAscending ? " ^" : " v") : "");
            if (_games.Columns[i].Text != title) _games.Columns[i].Text = title;
        }
    }

    void RefreshConvenienceTiles()
    {
        if (!_appPreferences.UseTiles || IsDisposed) return;
        var first = _games.Items.Count > 0 ? _games.Items[0] : null;
        var artwork = SelectedBaseGame() is { } selected ? ArtworkFor(selected) : null;
        if (_tileRowCount == _games.Items.Count && ReferenceEquals(first, _tileFirstRow) && artwork == _tileSelectedArtwork && _tileRenderedRevision == _tileArtworkRevision) return;
        _tileFirstRow = first;
        _tileRowCount = _games.Items.Count;
        _tileSelectedArtwork = artwork;
        _tileRenderedRevision = _tileArtworkRevision;
        _games.BeginUpdate();
        try
        {
            _games.LargeImageList = _tileImages;
            _ = _tileImages.Handle;
            if (_tileImages.Images.Count == 0)
            {
                using var neutral = new Bitmap(156, 256);
                using (var graphics = Graphics.FromImage(neutral)) graphics.Clear(_palette.Background);
                _tileImages.Images.Add(neutral);
            }
            foreach (ListViewItem row in _games.Items)
            {
                if (row.Tag is not GameCandidate game) continue;
                var path = ArtworkFor(game);
                var key = string.Join('|', game.DisplayName, game.Arch, game.DisplayApi, IsInstalled(game), path);
                if (!_tileImageIndexes.TryGetValue(key, out var index))
                {
                    if (_tileImages.Images.Count >= 512) { row.ImageIndex = 0; continue; }
                    try
                    {
                        using var tile = MakeConvenienceTile(path, game, IsInstalled(game));
                        index = _tileImages.Images.Count;
                        _tileImages.Images.Add(tile);
                    }
                    catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException or OutOfMemoryException or System.Runtime.InteropServices.ExternalException)
                    {
                        using var tile = MakeConvenienceTile(null, game, IsInstalled(game));
                        index = _tileImages.Images.Count;
                        _tileImages.Images.Add(tile);
                    }
                    _tileImageIndexes[key] = index;
                }
                row.ImageIndex = index;
            }
        }
        finally { _games.EndUpdate(); }
    }

    Bitmap MakeConvenienceTile(string? path, GameCandidate game, bool installed)
    {
        var tile = new Bitmap(156, 256);
        try
        {
            using var graphics = Graphics.FromImage(tile);
            graphics.Clear(_palette.Background);
            if (path is null)
            {
                using var markFont = new Font("Segoe UI Semibold", 26);
                var initials = string.Concat(game.DisplayName.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(2).Select(word => word[0]));
                TextRenderer.DrawText(graphics, initials, markFont, new Rectangle(8, 65, 140, 64), _palette.Accent, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                TextRenderer.DrawText(graphics, game.DisplayName, Font, new Rectangle(12, 133, 132, 72), _palette.Muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis);
            }
            else
            {
                LocalPaths.RequireNoLinks(path);
                if (new FileInfo(path).Length > GameArtwork.MaximumImageBytes) throw new InvalidDataException("Artwork is too large.");
                using var source = Image.FromFile(path);
                if (source.Width > 8192 || source.Height > 8192) throw new InvalidDataException("Artwork is too large.");
                var scale = Math.Min(156f / source.Width, 234f / source.Height);
                var width = source.Width * scale;
                var height = source.Height * scale;
                graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                graphics.DrawImage(source, (156 - width) / 2, (234 - height) / 2, width, height);
            }
            using var metaFont = new Font("Segoe UI", 8);
            var renderer = game.DisplayApi.Replace("DirectX ", "DX").Replace("DirectX", "DX");
            TextRenderer.DrawText(graphics, renderer + " / " + game.Arch, metaFont, new Rectangle(3, 235, 150, 21), _palette.Muted, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            if (installed)
            {
                using var accent = new SolidBrush(_palette.Accent);
                graphics.FillRectangle(accent, 0, 0, 156, 3);
            }
            return tile;
        }
        catch { tile.Dispose(); throw; }
    }

    void ConvenienceFormClosing(object? sender, FormClosingEventArgs e)
    {
        if (_preview || _exitFromTray || !_appPreferences.CloseToTray || e.CloseReason != CloseReason.UserClosing || e.Cancel) return;
        try
        {
            if (_convenienceTray is null)
            {
                _trayCommands = new ContextMenuStrip();
                _trayCommands.Items.Add(_localization["show"], null, (_, _) => RestoreConvenienceWindow());
                _trayCommands.Items.Add(_localization["preferences"], null, (_, _) => { RestoreConvenienceWindow(); ShowConveniencePreferences(); });
                _trayCommands.Items.Add(new ToolStripSeparator());
                _trayCommands.Items.Add(_localization["exitTray"], null, (_, _) => { _exitFromTray = true; Close(); });
                _localizedMenu[_trayCommands.Items[0]] = "show";
                _localizedMenu[_trayCommands.Items[1]] = "preferences";
                _localizedMenu[_trayCommands.Items[3]] = "exitTray";
                _convenienceTray = new NotifyIcon { Text = "DLSS5 Everything", Icon = SystemIcons.Application, ContextMenuStrip = _trayCommands };
                _convenienceTray.DoubleClick += (_, _) => RestoreConvenienceWindow();
            }
            _beforeTrayState = WindowState == FormWindowState.Minimized ? FormWindowState.Normal : WindowState;
            _convenienceTray.Visible = true;
            e.Cancel = true;
            Hide();
        }
        catch (Exception ex) { Log("Notification area unavailable: " + ex.Message); }
    }

    void RestoreConvenienceWindow()
    {
        Show();
        WindowState = _beforeTrayState;
        Activate();
        if (_convenienceTray is not null) _convenienceTray.Visible = false;
    }

    void DisposeConvenience()
    {
        if (_convenienceDisposed) return;
        _convenienceDisposed = true;
        _convenienceTimer.Stop();
        _convenienceTimer.Dispose();
        _convenienceTray?.Dispose();
        _trayCommands?.Dispose();
        _games.LargeImageList = null;
        _tileImages.Dispose();
        _artworkCts?.Cancel();
        _artworkService?.Dispose();
    }

    IReadOnlyList<ConvenienceCommand> BuildConvenienceCommands()
    {
        var commands = new List<ConvenienceCommand>();
        foreach (var page in new[] { "My Games", "Library", "Screen Studio", "Camera", "Media", "Streamer", "Components", "Add-ons", "History", "Settings", "Resources" })
        {
            var target = page;
            commands.Add(new("page:" + page, _localization[UiLocalization.CommonActions[page]], page, () => true, () => ShowPage(target)));
        }
        commands.AddRange([
            new("scan", _localization["scan"], "Scan folder", () => !_busy && !_preview && Directory.Exists(_scanRoot.Text), async () => await ScanAsync()),
            new("scan-saved", _localization["scanAllFolders"], "Scan saved folders", () => !_busy && !_preview && _libraryState.ScanRoots.Count > 0, async () => await ScanSavedRootsAsync()),
            new("discover", _localization["discover"], "Discover stores", () => !_busy && !_preview, async () => await DiscoverLibraryAsync()),
            new("add-exe", _localization["addExe"], "Add EXE game", () => !_busy && !_preview, AddExe),
            new("temporary-play", "Temporary play", "Drop or choose game; restore files after exit", () => !_busy && !_preview, ChooseTemporaryGame),
            new("play", _localization["play"], "Play launch run", () => _runExe.Enabled, () => _runExe.PerformClick()),
            new("open-folder", _localization["menuOpen"], "Open folder explorer", () => _openFolder.Enabled, () => _openFolder.PerformClick()),
            new("diagnostics", _localization["checkRuntime"], "Check runtime diagnostics", () => _diagnostics.Enabled && !_preview, () => _diagnostics.PerformClick()),
            new("recent", _localization["recentTitle"], "Recent viewed games", () => true, ShowConvenienceRecents),
            new("preferences", _localization["preferences"], "Preferences language tray startup", () => true, ShowConveniencePreferences),
            new("list", _localization["listView"], "List view", () => true, () => SetConvenienceTiles(false)),
            new("tiles", _localization["useTiles"], "Tile cover view", () => true, () => SetConvenienceTiles(true))
        ]);
        return commands;
    }

    void ShowConvenienceCommands()
    {
        using var dialog = new ConvenienceCommandDialog(BuildConvenienceCommands(), _localization, Font);
        UiTheme.Apply(dialog, _palette);
        if (dialog.ShowDialog(this) != DialogResult.OK || dialog.SelectedCommand is not { } command) return;
        if (!command.CanExecute()) { Log(_localization["commandUnavailable"]); return; }
        command.Execute();
    }

    void ShowConveniencePreferences()
    {
        using var dialog = new ConveniencePreferencesDialog(_appPreferences.Copy(), _localization, Font, _preferencesWritable || _preview);
        dialog.SaveRequested = next => TryApplyConveniencePreferences(next, out var error) ? null : error;
        UiTheme.Apply(dialog, _palette);
        dialog.ShowDialog(this);
    }

    void ShowConvenienceRecents()
    {
        var available = _allGames.DistinctBy(g => g.ExePath, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.ExePath, g => g, StringComparer.OrdinalIgnoreCase);
        var hidden = _libraryState.HiddenPaths.ToHashSet(StringComparer.OrdinalIgnoreCase);
        using var dialog = new ConvenienceRecentsDialog(_appPreferences, available.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase), hidden, _localization, Font);
        UiTheme.Apply(dialog, _palette);
        var result = dialog.ShowDialog(this);
        if (_appPreferences.RecentQuery != dialog.Query || _appPreferences.OnlyAvailableRecents != dialog.OnlyAvailable)
        {
            _appPreferences.RecentQuery = dialog.Query;
            _appPreferences.OnlyAvailableRecents = dialog.OnlyAvailable;
            SaveConveniencePreferences();
        }
        if (result != DialogResult.OK || dialog.SelectedPath is not { } path) return;
        if (!available.TryGetValue(path, out var game)) { Log(_localization["recentUnavailable"]); return; }
        ShowPage("Library");
        _search.Clear();
        _libraryFilter.SelectedIndex = 0;
        _apiFilter.SelectedIndex = 0;
        _storeFilter.SelectedIndex = 0;
        if (_libraryState.ProfileFor(game.ExePath).Apply(game).Route == InstallRoute.Unsupported) _hideIncompatible.Checked = false;
        RenderGameRows(path);
        _games.Focus();
    }
}

sealed record ConvenienceCommand(string Id, string Label, string SearchTerms, Func<bool> CanExecute, Action Execute)
{
    public bool Matches(string query) => query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .All(token => Label.Contains(token, StringComparison.OrdinalIgnoreCase) || SearchTerms.Contains(token, StringComparison.OrdinalIgnoreCase));
}

sealed class ConvenienceCommandDialog : Form
{
    readonly TextBox _query;
    readonly ListBox _commands;
    readonly IReadOnlyList<ConvenienceCommand> _source;
    readonly UiLocalization _text;
    public ConvenienceCommand? SelectedCommand { get; private set; }

    public ConvenienceCommandDialog(IReadOnlyList<ConvenienceCommand> commands, UiLocalization text, Font font)
    {
        _source = commands;
        _text = text;
        Text = text["commands"];
        Font = font;
        Size = new Size(580, 460);
        MinimumSize = new Size(450, 320);
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        MinimizeBox = MaximizeBox = false;
        RightToLeft = text.RightToLeft ? RightToLeft.Yes : RightToLeft.No;
        RightToLeftLayout = text.RightToLeft;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(16), ColumnCount = 1, RowCount = 2 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        _query = new TextBox { Dock = DockStyle.Fill, PlaceholderText = text["chooseCommand"], AccessibleName = text["chooseCommand"], MaxLength = 256, Margin = new Padding(0, 0, 0, 12) };
        _commands = new ListBox { Dock = DockStyle.Fill, IntegralHeight = false, DrawMode = DrawMode.OwnerDrawFixed, ItemHeight = 32, AccessibleName = text["commands"] };
        _commands.DrawItem += DrawCommand;
        _query.TextChanged += (_, _) => RefreshCommands();
        _query.KeyDown += (_, e) =>
        {
            if (e.KeyCode is Keys.Down or Keys.Up)
            {
                if (_commands.Items.Count > 0) _commands.SelectedIndex = Math.Clamp(_commands.SelectedIndex + (e.KeyCode == Keys.Down ? 1 : -1), 0, _commands.Items.Count - 1);
                e.SuppressKeyPress = true;
            }
        };
        _commands.DoubleClick += (_, _) => ChooseCommand();
        layout.Controls.Add(_query, 0, 0);
        layout.Controls.Add(_commands, 0, 1);
        Controls.Add(layout);
        Shown += (_, _) => _query.Focus();
        RefreshCommands();
    }

    void RefreshCommands()
    {
        _commands.Items.Clear();
        foreach (var command in _source.Where(c => c.Matches(_query.Text)))
            _commands.Items.Add(new CommandChoice(command, command.Label + (command.CanExecute() ? "" : " (" + _text["unavailable"] + ")")));
        if (_commands.Items.Count > 0) _commands.SelectedIndex = 0;
    }

    void DrawCommand(object? sender, DrawItemEventArgs e)
    {
        if (e.Index < 0 || _commands.Items[e.Index] is not CommandChoice choice) return;
        var palette = BackColor == UiPalette.Dark.Background ? UiPalette.Dark : UiPalette.Light;
        using var brush = new SolidBrush((e.State & DrawItemState.Selected) != 0 ? palette.Selection : palette.Surface);
        e.Graphics.FillRectangle(brush, e.Bounds);
        TextRenderer.DrawText(e.Graphics, choice.Text, e.Font, e.Bounds, choice.Command.CanExecute() ? palette.Text : palette.Muted,
            TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | (_text.RightToLeft ? TextFormatFlags.RightToLeft | TextFormatFlags.Right : TextFormatFlags.Left));
        e.DrawFocusRectangle();
    }

    void ChooseCommand()
    {
        if (_commands.SelectedItem is not CommandChoice choice || !choice.Command.CanExecute()) return;
        SelectedCommand = choice.Command;
        DialogResult = DialogResult.OK;
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.Escape) { DialogResult = DialogResult.Cancel; return true; }
        if (keyData == Keys.Enter) { ChooseCommand(); return true; }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    sealed record CommandChoice(ConvenienceCommand Command, string Text)
    {
        public override string ToString() => Text;
    }
}

sealed class ConveniencePreferencesDialog : Form
{
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal Func<AppPreferences, string?>? SaveRequested { get; set; }

    public ConveniencePreferencesDialog(AppPreferences preferences, UiLocalization text, Font font, bool canSave)
    {
        Text = text["preferences"];
        Font = font;
        Size = new Size(640, 610);
        MinimumSize = new Size(540, 530);
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = MaximizeBox = false;
        RightToLeft = text.RightToLeft ? RightToLeft.Yes : RightToLeft.No;
        RightToLeftLayout = text.RightToLeft;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(18), ColumnCount = 2, RowCount = 13 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 45));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 55));
        for (var i = 0; i < 12; i++) layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var language = new ThemedComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill, DropDownWidth = 480, AccessibleName = text["language"] };
        language.Items.AddRange(UiLocalization.Languages.Cast<object>().ToArray());
        language.SelectedItem = UiLocalization.Languages.First(l => l.Code == preferences.Language);
        layout.Controls.Add(new Label { Text = text["language"], AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
        layout.Controls.Add(language, 1, 0);
        var coverage = new Label { Text = text.CoverageText, Dock = DockStyle.Fill, AutoSize = true, Margin = new Padding(3, 8, 3, 12) };
        layout.Controls.Add(coverage, 0, 1);
        layout.SetColumnSpan(coverage, 2);
        language.SelectedIndexChanged += (_, _) => coverage.Text = new UiLocalization(((UiLanguage)language.SelectedItem!).Code).CoverageText;
        CheckBox Option(string key, bool value, int row)
        {
            var box = new CheckBox { Text = text[key], Checked = value, AutoSize = true, Dock = DockStyle.Fill, AccessibleName = text[key], Margin = new Padding(3, 8, 3, 8) };
            layout.Controls.Add(box, 0, row);
            layout.SetColumnSpan(box, 2);
            return box;
        }
        var closeToTray = Option("closeToTray", preferences.CloseToTray, 2);
        var startup = Option("startupScan", preferences.ScanOnStartup, 3);
        var compact = Option("compactNavigation", preferences.CompactNavigation, 4);
        var tiles = Option("useTiles", preferences.UseTiles, 5);
        var remember = Option("rememberRecents", preferences.RememberRecents, 6);
        var limit = new ThemedNumericUpDown { Minimum = 1, Maximum = AppPreferences.MaximumRecents, Value = preferences.RecentLimit, Dock = DockStyle.Fill, AccessibleName = text["recentLimit"] };
        layout.Controls.Add(new Label { Text = text["recentLimit"], AutoSize = true, Anchor = AnchorStyles.Left }, 0, 7);
        layout.Controls.Add(limit, 1, 7);
        var clear = new Button { Text = text["clearRecents"], AutoSize = true, MinimumSize = new Size(120, 34), Margin = new Padding(3, 12, 3, 8) };
        clear.Click += (_, _) => { preferences.Recents.Clear(); preferences.RecentQuery = ""; clear.Enabled = false; };
        layout.Controls.Add(clear, 0, 8);
        layout.SetColumnSpan(clear, 2);
        var license = new LinkLabel { Text = text["license"], AutoSize = true, Margin = new Padding(3, 8, 3, 8) };
        license.LinkClicked += (_, _) =>
        {
            using var credits = new Form { Text = text["licenseTitle"], Size = new Size(740, 540), StartPosition = FormStartPosition.CenterParent, Font = Font, ShowInTaskbar = false };
            credits.Controls.Add(new TextBox { Text = UiLocalization.TranslationLicense, Multiline = true, ReadOnly = true, Dock = DockStyle.Fill, ScrollBars = ScrollBars.Vertical, RightToLeft = RightToLeft.No });
            UiTheme.Apply(credits, BackColor == UiPalette.Dark.Background ? UiPalette.Dark : UiPalette.Light);
            credits.ShowDialog(this);
        };
        layout.Controls.Add(license, 0, 9);
        layout.SetColumnSpan(license, 2);
        var error = new Label { AutoSize = true, Dock = DockStyle.Fill, ForeColor = Color.Firebrick, Tag = "error" };
        layout.Controls.Add(error, 0, 10);
        layout.SetColumnSpan(error, 2);
        var actions = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = true, Padding = new Padding(16, 8, 16, 12) };
        var save = new Button { Text = text["save"], AutoSize = true, MinimumSize = new Size(92, 34), Enabled = canSave };
        var cancel = new Button { Text = text["cancel"], AutoSize = true, MinimumSize = new Size(92, 34), DialogResult = DialogResult.Cancel };
        save.Click += (_, _) =>
        {
            preferences.Language = ((UiLanguage)language.SelectedItem!).Code;
            preferences.CloseToTray = closeToTray.Checked;
            preferences.ScanOnStartup = startup.Checked;
            preferences.CompactNavigation = compact.Checked;
            preferences.UseTiles = tiles.Checked;
            preferences.RememberRecents = remember.Checked;
            preferences.RecentLimit = (int)limit.Value;
            var message = SaveRequested?.Invoke(preferences);
            if (message is not null) { error.Text = text["preferencesFailed"] + ": " + message; return; }
            DialogResult = DialogResult.OK;
        };
        actions.Controls.AddRange([save, cancel]);
        var frame = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
        frame.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        frame.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        frame.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        frame.Controls.Add(layout, 0, 0);
        frame.Controls.Add(actions, 0, 1);
        Controls.Add(frame);
        AcceptButton = save;
        CancelButton = cancel;
    }
}

sealed class ConvenienceRecentsDialog : Form
{
    readonly TextBox _query;
    readonly CheckBox _onlyAvailable;
    readonly ListView _recentList;
    readonly Button _view;
    public string Query => _query.Text;
    public bool OnlyAvailable => _onlyAvailable.Checked;
    public string? SelectedPath => _recentList.SelectedItems.Count > 0 ? (_recentList.SelectedItems[0].Tag as RecentGame)?.ExePath : null;

    public ConvenienceRecentsDialog(AppPreferences preferences, HashSet<string> available, HashSet<string> hidden, UiLocalization text, Font font)
    {
        Text = text["recentsViewed"];
        Font = font;
        Size = new Size(820, 520);
        MinimumSize = new Size(580, 360);
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        RightToLeft = text.RightToLeft ? RightToLeft.Yes : RightToLeft.No;
        RightToLeftLayout = text.RightToLeft;
        var frame = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(16), ColumnCount = 1, RowCount = 4 };
        frame.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        frame.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        frame.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        frame.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        frame.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _query = new TextBox { Text = preferences.RecentQuery, PlaceholderText = text["recentSearch"], AccessibleName = text["recentSearch"], MaxLength = 256, Dock = DockStyle.Fill };
        _onlyAvailable = new CheckBox { Text = text["availableRecent"], Checked = preferences.OnlyAvailableRecents, AutoSize = true, Margin = new Padding(3, 8, 3, 8) };
        _recentList = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, MultiSelect = false, HideSelection = false, AccessibleName = text["recentTitle"], ShowItemToolTips = true };
        _recentList.Columns.Add(text["columnGame"], 240);
        _recentList.Columns.Add(text["viewedAt"], 160);
        _recentList.Columns.Add("Path", 350);
        var actions = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = true, Margin = new Padding(0, 12, 0, 0) };
        _view = new Button { Text = text["openRecent"], AutoSize = true, MinimumSize = new Size(120, 34), Enabled = false };
        var cancel = new Button { Text = text["cancel"], AutoSize = true, MinimumSize = new Size(92, 34), DialogResult = DialogResult.Cancel };
        void RefreshRows()
        {
            _recentList.BeginUpdate();
            try
            {
                _recentList.Items.Clear();
                foreach (var recent in preferences.Recents.Where(r => !hidden.Contains(r.ExePath) && (!OnlyAvailable || available.Contains(r.ExePath)) && Matches(r, Query)))
                {
                    var row = new ListViewItem(recent.Name) { Tag = recent, ToolTipText = available.Contains(recent.ExePath) ? recent.ExePath : text["recentUnavailable"], ForeColor = available.Contains(recent.ExePath) ? ForeColor : SystemColors.GrayText };
                    row.SubItems.Add(recent.ViewedAt.LocalDateTime.ToString("g"));
                    row.SubItems.Add(recent.ExePath);
                    _recentList.Items.Add(row);
                }
                _view.Enabled = false;
            }
            finally { _recentList.EndUpdate(); }
        }
        void ChooseRecent() { if (SelectedPath is { } path && available.Contains(path)) DialogResult = DialogResult.OK; }
        _query.TextChanged += (_, _) => RefreshRows();
        _onlyAvailable.CheckedChanged += (_, _) => RefreshRows();
        _recentList.SelectedIndexChanged += (_, _) => _view.Enabled = SelectedPath is { } path && available.Contains(path);
        _recentList.DoubleClick += (_, _) => ChooseRecent();
        _view.Click += (_, _) => ChooseRecent();
        actions.Controls.AddRange([_view, cancel]);
        frame.Controls.Add(_query, 0, 0);
        frame.Controls.Add(_onlyAvailable, 0, 1);
        frame.Controls.Add(_recentList, 0, 2);
        frame.Controls.Add(actions, 0, 3);
        Controls.Add(frame);
        AcceptButton = _view;
        CancelButton = cancel;
        RefreshRows();
    }

    public static bool Matches(RecentGame game, string query) => query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .All(token => game.Name.Contains(token, StringComparison.OrdinalIgnoreCase) || game.ExePath.Contains(token, StringComparison.OrdinalIgnoreCase));
}
