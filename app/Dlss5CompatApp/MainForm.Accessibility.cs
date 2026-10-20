namespace Dlss5CompatApp;

sealed partial class MainForm
{
    string _operationOutcome = "";
    readonly Dictionary<string, string> _runtimeChecks = new(StringComparer.OrdinalIgnoreCase);
    readonly FlowLayoutPanel _emptyState = new() { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(20, 40, 20, 20), Visible = false };
    readonly Label _emptyTitle = new() { AutoSize = true, Font = new Font("Segoe UI Semibold", 13) };

    void InitializeAccessibility()
    {
        foreach (var (control, name) in new (Control, string)[] {
            (_scanRoot, "Game scan folder"), (_payloadRoot, "Component folder"),
            (_search, "Search games"), (_libraryFilter, "Library filter"), (_apiFilter, "Game graphics API filter"),
            (_storeFilter, "Store filter"), (_engineChoice, "Game graphics API override"),
            (_launchArguments, "Game launch arguments"), (_frameIterations, "Frame iterations"),
            (_forceVrEyeSplit, "Force VR eye split"), (_releaseChannel, "Phoenix DLSS5 release channel"),
            (_detailStatus, "Installation readiness and runtime evidence"), (_dropZone, "Temporary game launch") })
            control.AccessibleName = name;
        _dropZoneText.Text = "Drop game for temporary play";
        _dropZone.DoubleClick += (_, _) => ChooseTemporaryGame();
        _dropZoneText.Click += (_, _) => ChooseTemporaryGame();
        _dropZoneText.TabStop = true;
        _tips.SetToolTip(_dropZoneText, "Drop a game, or click to choose one. The app restores its changes after the launched game exits.");
    }

    async void ChooseTemporaryGame()
    {
        if (_preview || _busy || _downloadCts is not null) return;
        using var dialog = new OpenFileDialog { Title = "Temporary game launch", Filter = "Games|*.exe;*.com;*.bat" };
        if (dialog.ShowDialog(this) == DialogResult.OK) await HandleDroppedTargetAsync(dialog.FileName);
    }

    void SetOutcome(string text)
    {
        _operationOutcome = text;
        _componentMessage.Text = text;
        _componentMessage.AccessibleName = "Operation result: " + text;
        _tips.SetToolTip(_componentMessage, text);
    }

    void BuildEmptyState(Panel host)
    {
        _emptyState.Controls.Add(_emptyTitle);
        _emptyState.Controls.Add(Command("Find installed games", "\uE721", async () => await DiscoverLibraryAsync()));
        _emptyState.Controls.Add(Command("Choose game", "\uE710", AddExe));
        _emptyState.Controls.Add(Command("Clear filters / show all", "\uE711", ClearLibraryFilters));
        host.Controls.Add(_emptyState);
        _emptyState.BringToFront();
    }

    void ClearLibraryFilters()
    {
        _search.Clear();
        _hideIncompatible.Checked = false;
        _libraryFilter.SelectedIndex = _apiFilter.SelectedIndex = _storeFilter.SelectedIndex = 0;
        _componentFilter.SelectedIndex = 0;
        RenderGameRows();
    }

    void UpdateEmptyState()
    {
        _emptyState.Visible = _games.Items.Count == 0;
        _emptyTitle.Text = _allGames.Count == 0 ? "No games added" : "No matching games";
        if (_emptyState.Visible) _emptyState.BringToFront();
    }
}
