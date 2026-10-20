namespace Dlss5CompatApp;

sealed partial class MainForm
{
    readonly ListView _myGames = new() { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, MultiSelect = false, HideSelection = false, ShowItemToolTips = true };
    readonly TextBox _myGamesSearch = new() { Dock = DockStyle.Fill, PlaceholderText = "Search My Games", AccessibleName = "Search My Games" };
    readonly CheckBox _myGamesFavorites = new() { Text = "Favorites", AutoSize = true };
    readonly FlowLayoutPanel _myGamesEmpty = new() { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(20, 40, 20, 20) };
    readonly Label _myGamesEmptyTitle = new() { AutoSize = true, Font = new Font("Segoe UI Semibold", 13) };
    readonly ImageList _myGameImages = new() { ImageSize = new Size(156, 256), ColorDepth = ColorDepth.Depth32Bit };
    readonly Dictionary<string, int> _myGameImageIndexes = new(StringComparer.OrdinalIgnoreCase);
    TableLayoutPanel _myGamesBody = null!;
    Button _myGamesList = null!, _myGamesPreviews = null!;
    bool _usingMyGames;
    bool _renderingMyGames;
    int _myGamesSort;
    bool _myGamesAscending = true;

    Panel BuildMyGamesPage()
    {
        var page = Page();
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var toolbar = Row(100, 94, 91, 96, 42, 42, 140);
        toolbar.Controls.Add(_myGamesSearch, 0, 0);
        toolbar.Controls.Add(_myGamesFavorites, 1, 0);
        _myGamesList = Command("List", "\uE8FD", () => SetConvenienceTiles(false));
        _myGamesPreviews = Command("Previews", "\uE8A9", () => SetConvenienceTiles(true));
        foreach (var button in new[] { _myGamesList, _myGamesPreviews }) { button.Dock = DockStyle.Fill; button.MinimumSize = Size.Empty; button.AutoSize = false; }
        toolbar.Controls.Add(_myGamesList, 2, 0);
        toolbar.Controls.Add(_myGamesPreviews, 3, 0);
        var refresh = Command("Refresh My Games", "\uE72C", () => { _installed.Clear(); RenderGameRows(); });
        refresh.Text = ""; refresh.MinimumSize = Size.Empty; refresh.Dock = DockStyle.Fill; refresh.AutoSize = false;
        toolbar.Controls.Add(refresh, 4, 0);
        var sort = new ContextMenuStrip();
        foreach (var (title, column) in new[] { ("Game", 0), ("Renderer", 1), ("Architecture", 2) })
        {
            var item = sort.Items.Add(title, null, (_, _) => SortMyGames(column));
            item.Tag = column;
        }
        sort.Opening += (_, _) =>
        {
            foreach (var item in sort.Items.OfType<ToolStripMenuItem>()) { item.Checked = (int)item.Tag! == _myGamesSort; item.Enabled = !_busy; }
        };
        toolbar.Controls.Add(LibraryMenuButton("Sort My Games", "\uE8CB", sort), 5, 0);
        toolbar.Controls.Add(Command("Add games", "\uE710", () => ShowPage("Library")), 6, 0);
        layout.Controls.Add(toolbar, 0, 0);
        _myGamesBody = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
        _myGamesBody.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _myGamesBody.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 304));
        var host = new Panel { Dock = DockStyle.Fill };
        host.Controls.Add(_myGames);
        _myGamesEmpty.Controls.Add(_myGamesEmptyTitle);
        _myGamesEmpty.Controls.Add(new Label { Text = "1. Prepare Components   2. Install a game   3. Verify & fix, then Play", AutoSize = true, MaximumSize = new Size(600, 0), Margin = new Padding(3, 12, 3, 12) });
        _myGamesEmpty.Controls.Add(Command("Prepare components", "\uE896", () => ShowPage("Components")));
        _myGamesEmpty.Controls.Add(Command("Temporary play", "\uE768", ChooseTemporaryGame));
        _myGamesEmpty.Controls.Add(Command("Discover games", "\uE721", () => ShowPage("Library")));
        _myGamesEmpty.Controls.Add(Command("Clear My Games search", "\uE711", () => { _myGamesSearch.Clear(); _myGamesFavorites.Checked = false; }));
        host.Controls.Add(_myGamesEmpty);
        _myGamesEmpty.BringToFront();
        _myGamesBody.Controls.Add(host, 0, 0);
        layout.Controls.Add(_myGamesBody, 0, 1);
        page.Controls.Add(layout);
        foreach (var (title, width) in new[] { ("Game", 240), ("Graphics API", 110), ("Arch", 60), ("Status", 136) }) _myGames.Columns.Add(title, width);
        _myGames.Font = _games.Font;
        _myGamesSearch.TextChanged += (_, _) => RenderMyGames();
        _myGamesFavorites.CheckedChanged += (_, _) => RenderMyGames();
        _myGames.SelectedIndexChanged += (_, _) => { if (_usingMyGames && !_renderingMyGames) { UpdateEngineChoices(); RefreshGameDetails(); UpdateButtons(); } };
        _myGames.ItemSelectionChanged += (_, e) => { if (e.IsSelected && _usingMyGames && !_renderingMyGames && e.Item?.Tag is GameCandidate game && _convenienceInitialized) RecordConvenienceView(game); };
        _myGames.DoubleClick += (_, _) => { if (_runExe.Enabled && _usingMyGames) _runExe.PerformClick(); };
        _myGames.ColumnClick += (_, e) => SortMyGames(e.Column);
        _myGames.Resize += (_, _) => { if (_myGames.Columns.Count > 0) _myGames.Columns[0].Width = Math.Max(120, _myGames.ClientSize.Width - 312); };
        _myGames.HandleCreated += (_, _) => SetMyGamesPosterSpacing();
        _myGames.DpiChangedAfterParent += (_, _) => SetMyGamesPosterSpacing();
        FormClosed += (_, _) => { _myGames.LargeImageList = null; _myGameImages.Dispose(); };
        return page;
    }

    void SortMyGames(int column)
    {
        if (_busy) return;
        _myGamesAscending = column != _myGamesSort || !_myGamesAscending;
        _myGamesSort = column;
        RenderMyGames();
    }

    void RenderMyGames()
    {
        var selected = _myGames.SelectedItems.Count > 0 ? (_myGames.SelectedItems[0].Tag as GameCandidate)?.ExePath : null;
        var tokens = _myGamesSearch.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var installed = _allGames.Where(IsInstalled).Where(game => !_libraryState.HiddenPaths.Contains(game.ExePath, StringComparer.OrdinalIgnoreCase));
        var result = installed.Where(game => (!_myGamesFavorites.Checked || _libraryState.FavouritePaths.Contains(game.ExePath, StringComparer.OrdinalIgnoreCase)) && tokens.All(token => GameMatches(game, token)));
        string Key(GameCandidate game) => _myGamesSort switch { 1 => game.DisplayApi, 2 => game.Arch.ToString(), _ => game.DisplayName };
        result = _myGamesAscending ? result.OrderBy(Key, StringComparer.OrdinalIgnoreCase) : result.OrderByDescending(Key, StringComparer.OrdinalIgnoreCase);
        _myGames.BeginUpdate();
        _renderingMyGames = true;
        try
        {
            _myGames.Items.Clear();
            foreach (var game in result)
            {
                var row = new ListViewItem(game.DisplayName) { Tag = game, ToolTipText = game.ExePath };
                row.SubItems.AddRange([game.DisplayApi, game.Arch.ToString(), GameInstallStatus(game)]);
                _myGames.Items.Add(row);
                row.Selected = game.ExePath.Equals(selected, StringComparison.OrdinalIgnoreCase);
            }
            if (_myGames.SelectedItems.Count == 0 && _myGames.Items.Count > 0) _myGames.Items[0].Selected = true;
        }
        finally { _myGames.EndUpdate(); _renderingMyGames = false; }
        _myGamesEmpty.Visible = _myGames.Items.Count == 0;
        _myGames.Visible = _myGames.Items.Count > 0;
        _myGamesBody.ColumnStyles[1].Width = _myGames.Items.Count > 0 ? 304 : 0;
        if (_usingMyGames) _gameInspector.Visible = _myGames.Items.Count > 0;
        _myGamesEmptyTitle.Text = installed.Any() ? "No matching games" : "No DLSS games installed yet";
        _myGamesEmptyTitle.MaximumSize = new Size(Math.Max(100, _myGamesEmpty.Width - 40), 0);
        if (_myGamesEmpty.Visible) _myGamesEmpty.BringToFront();
        ApplyMyGamesView();
        UpdateGameCount();
        if (_usingMyGames)
        {
            var next = SelectedBaseGame()?.ExePath;
            var changed = !string.Equals(selected, next, StringComparison.OrdinalIgnoreCase);
            if (changed) UpdateEngineChoices();
            RefreshGameDetails(changed);
            UpdateButtons();
        }
        QueueArtwork();
    }

    void UpdateGameCount()
    {
        _gameCount.Text = _activePage == "My Games" ? $"{_myGames.Items.Count} installed {(_myGames.Items.Count == 1 ? "game" : "games")}" : $"{_games.Items.Count} shown / {_allGames.Count} discovered";
    }

    void ApplyMyGamesView()
    {
        if (_myGamesList is null) return;
        _myGames.View = _appPreferences.UseTiles ? View.LargeIcon : View.Details;
        _myGamesList.Tag = _appPreferences.UseTiles ? null : "primary";
        _myGamesPreviews.Tag = _appPreferences.UseTiles ? "primary" : null;
        UiTheme.Apply(_myGamesList, _palette); UiTheme.Apply(_myGamesPreviews, _palette);
        SetMyGamesPosterSpacing();
        if (!_appPreferences.UseTiles) return;
        _myGames.LargeImageList = _myGameImages;
        foreach (ListViewItem row in _myGames.Items)
        {
            var game = (GameCandidate)row.Tag!;
            var path = ArtworkFor(game);
            var key = string.Join('|', game.DisplayName, game.Arch, game.DisplayApi, path);
            if (!_myGameImageIndexes.TryGetValue(key, out var index))
            {
                if (_myGameImages.Images.Count >= 512) { row.ImageIndex = -1; continue; }
                index = _myGameImages.Images.Count;
                try { using var tile = MakeConvenienceTile(path, game, true); _myGameImages.Images.Add(tile); }
                catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException or OutOfMemoryException or System.Runtime.InteropServices.ExternalException)
                { using var tile = MakeConvenienceTile(null, game, true); _myGameImages.Images.Add(tile); }
                _myGameImageIndexes[key] = index;
            }
            row.ImageIndex = index;
        }
    }

    void SetMyGamesPosterSpacing()
    {
        if (!_myGames.IsHandleCreated || _myGames.View != View.LargeIcon) return;
        var scale = _myGames.DeviceDpi / 96f;
        SendMessage(_myGames.Handle, 0x1035, IntPtr.Zero, (IntPtr)(((int)(310 * scale) << 16) | (int)(184 * scale)));
    }
}
