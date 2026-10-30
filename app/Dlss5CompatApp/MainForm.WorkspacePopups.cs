namespace Dlss5CompatApp;

sealed partial class MainForm
{
    readonly LinkLabel _componentVersions = new() { Dock = DockStyle.Fill, TextAlign = ContentAlignment.BottomLeft,
        Font = new Font("Segoe UI", 8), AccessibleName = "Installed component versions; open component manager",
        LinkBehavior = LinkBehavior.HoverUnderline, Text = "Component versions\nChecking…" };
    readonly Label _gameCustomSummary = new() { AutoSize = true, MaximumSize = new Size(320, 0),
        AccessibleName = "Custom settings for this game", Visible = false };
    readonly Label _componentDownloadStatus = new() { AutoSize = true, MaximumSize = new Size(850, 0),
        AccessibleName = "Current component update progress" };
    Control _advancedGameOptions = null!;
    ComponentSetupPanel _inspectorSetupPanel = null!;
    Form? _componentManager, _gameOptionsDialog;

    void ShowComponentManager()
    {
        if (IsDisposed || Disposing || !_pages.TryGetValue("Components", out var content)) return;
        if (_componentManager is null || _componentManager.IsDisposed)
        {
            _componentManager = new Form { Text = "Components and updates", Size = new Size(1000, 800),
                MinimumSize = new Size(650, 460), StartPosition = FormStartPosition.CenterParent,
                Font = Font, AutoScaleMode = AutoScaleMode.Dpi, ShowInTaskbar = false };
            _componentManager.Controls.Add(content); content.Visible = true;
            // Hide rather than dispose: all existing controls and download state are shared.
            _componentManager.FormClosing += (_, e) =>
            { if (!IsDisposed && !Disposing && e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; _componentManager.Hide(); } };
            UiTheme.Apply(_componentManager, _palette); UpdateReleaseSummary();
        }
        if (!_componentManager.Visible) _componentManager.Show(this);
        content.PerformLayout(); _componentManager.PerformLayout();
        _componentManager.Activate();
    }

    void ShowAdvancedGameOptions()
    {
        if (SelectedBaseGame() is not { } game || IsDisposed) return;
        if (_gameOptionsDialog is null || _gameOptionsDialog.IsDisposed)
        {
            _gameOptionsDialog = new Form { Text = "Advanced game settings", Size = new Size(760, 760),
                MinimumSize = new Size(600, 460), StartPosition = FormStartPosition.CenterParent,
                Font = Font, AutoScaleMode = AutoScaleMode.Dpi, ShowInTaskbar = false };
            var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(18) };
            _advancedGameOptions.Visible = true; scroll.Controls.Add(_advancedGameOptions);
            _gameOptionsDialog.Controls.Add(scroll);
            _gameOptionsDialog.Controls.Add(new Label { Dock = DockStyle.Bottom, Height = 42, Padding = new Padding(18, 8, 8, 8),
                Text = "Save profile keeps these choices for the selected game. The game library stays available." });
            _gameOptionsDialog.FormClosing += (_, e) =>
            { if (!IsDisposed && !Disposing && e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; _gameOptionsDialog.Hide(); } };
            UiTheme.Apply(_gameOptionsDialog, _palette);
        }
        _gameOptionsDialog.Text = "Advanced settings — " + game.DisplayName;
        if (!_gameOptionsDialog.Visible) _gameOptionsDialog.Show(this);
        _gameOptionsDialog.Activate();
    }

    void InitializeWorkspacePopups()
    {
        _componentVersions.LinkClicked += (_, _) => ShowComponentManager();
        _launchArguments.TextChanged += (_, _) => UpdateGameCustomSummary();
        _frameIterations.ValueChanged += (_, _) => UpdateGameCustomSummary();
        _forceVrEyeSplit.CheckedChanged += (_, _) => UpdateGameCustomSummary();
        _engineChoice.SelectedIndexChanged += (_, _) => UpdateGameCustomSummary();
        _routeChoice.SelectedIndexChanged += (_, _) => UpdateGameCustomSummary();
        _proxyChoice.SelectedIndexChanged += (_, _) => UpdateGameCustomSummary();
        Disposed += (_, _) =>
        {
            _componentManager?.Dispose(); _gameOptionsDialog?.Dispose();
            if (_advancedGameOptions.Parent is null) _advancedGameOptions.Dispose();
            if (_pages.TryGetValue("Components", out var content) && content.Parent is null) content.Dispose();
        };
        UpdateComponentVersions();
    }

    void UpdateGameCustomSummary()
    {
        var game = SelectedBaseGame();
        var items = new List<string>();
        if (game is not null)
        {
            if (_engineChoice.SelectedIndex > 0) items.Add("Custom API: " + _engineChoice.Text);
            if (ChosenRoute is { } route) items.Add("Input route: " + route);
            if (!string.IsNullOrEmpty(ChosenProxy)) items.Add("Proxy: " + ChosenProxy);
            if (!string.IsNullOrWhiteSpace(_launchArguments.Text) && _launchArguments.Text != game.SuggestedArguments)
                items.Add("Launch: " + _launchArguments.Text);
            if (FrameIterations() > 1) items.Add("Frame iterations: " + FrameIterations());
            if (_forceVrEyeSplit.Checked) items.Add("VR eye split enabled");
            if (!string.IsNullOrWhiteSpace(_libraryState.ProfileFor(game.ExePath).ArtworkPath)) items.Add("Custom artwork");
        }
        var full = string.Join(Environment.NewLine, items);
        _gameCustomSummary.Text = string.Join(Environment.NewLine, items.Select(s => s.Length > 85 ? s[..82] + "…" : s));
        _gameCustomSummary.Visible = items.Count > 0;
        _gameCustomSummary.ForeColor = _palette.Accent;
        _tips.SetToolTip(_gameCustomSummary, full);
        if (_gameOptionsDialog is { IsDisposed: false })
        {
            _gameOptionsDialog.Text = "Advanced settings — " + (game?.DisplayName ?? "Select a game");
            _advancedGameOptions.Enabled = game is not null;
        }
    }

    void UpdateComponentVersions(bool? compact = null)
    {
        compact ??= (_convenienceInitialized && _appPreferences.CompactNavigation) || ClientSize.Width < 1180 * DeviceDpi / 96;
        string Short(string? value) => value?.Split(':')[0] is { Length: > 0 } text ? text : "—";
        var phoenix = Short(_phoenixUpdate?.CachedVersion ?? (_phoenixUpdate?.State == PhoenixUpdateState.Current ? _phoenixUpdate.LatestVersion : null));
        var dos = Short(_companionUpdates.GetValueOrDefault(CompanionProject.DosBoxEnhanced)?.InstalledVersion);
        var video = Short(_companionUpdates.GetValueOrDefault(CompanionProject.VideoPlayer)?.InstalledVersion);
        var full = $"Everything {AppUpdateService.CurrentVersion}\nPhoenix {phoenix}\nDOSBox {dos}\nPlayer {video}";
        _componentVersions.Text = compact.Value ? $"E {AppUpdateService.CurrentVersion.Split('-')[0]}\nP {phoenix}\nD {dos}\nV {video}" : full;
        _componentVersions.LinkColor = _componentVersions.ActiveLinkColor = _componentVersions.VisitedLinkColor = _palette.Muted;
        _tips.SetToolTip(_componentVersions, full + "\nOpen components and updates");
    }

    void ThemeWorkspacePopups()
    {
        if (_componentManager is { IsDisposed: false }) UiTheme.Apply(_componentManager, _palette);
        if (_gameOptionsDialog is { IsDisposed: false }) UiTheme.Apply(_gameOptionsDialog, _palette);
        UpdateGameCustomSummary();
    }

    bool NeedsInitialComponentSetup() => _payload is null || !_payload.HasPhoenixAddon || !_payload.HasPhoenixModel || !_payload.HasReShade64;
}
