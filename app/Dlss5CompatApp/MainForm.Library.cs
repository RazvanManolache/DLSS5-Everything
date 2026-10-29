using System.Diagnostics;
using System.Text.Json;

namespace Dlss5CompatApp;

sealed partial class MainForm
{
    LibraryState _libraryState = new();
    readonly Dictionary<string, string> _stores = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, bool> _installed = new(StringComparer.OrdinalIgnoreCase);
    CancellationTokenSource? _downloadCts;
    string _modelPath = "";

    void InitializeLibrary()
    {
        if (!_preview)
        {
            try { _libraryState = LibraryState.Load(); }
            catch (Exception ex) { Log("Could not read library settings: " + ex.Message); }
        }
        _groupByStore.Checked = _libraryState.GroupByStore;
        foreach (var (path, store) in _libraryState.GameStores) _stores[path] = store;
        _releaseChannel.SelectedIndex = _libraryState.PhoenixChannel == "StableOnly" ? 1 : 0;
        _themeChoice.SelectedItem = _libraryState.Theme == "system" ? "System" : _libraryState.Theme == "dark" ? "Dark" : "Light";
        RefreshLibrarySettings();
        InitializeInstallationDefaults();
        _groupByStore.CheckedChanged += (_, _) => { _libraryState.GroupByStore = _groupByStore.Checked; SaveLibrary(); };
        _releaseChannel.SelectedIndexChanged += (_, _) => { _libraryState.PhoenixChannel = _releaseChannel.SelectedIndex == 1 ? "StableOnly" : "LatestPublished"; SaveLibrary(); _ = RefreshPhoenixUpdateAsync(force: true); };
        if (!_preview) Shown += async (_, _) =>
        {
            try { await ReloadSavedLibraryAsync(); }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Log("Could not reload library: " + ex.Message); }
        };
    }

    void SaveLibrary()
    {
        if (_preview) return;
        try { _libraryState.Save(); }
        catch (Exception ex) { Log("Could not save library settings: " + ex.Message); }
    }

    async Task ReloadSavedLibraryAsync()
    {
        var paths = _libraryState.CachedGamePaths.Concat(_libraryState.ManualPaths).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (paths.Length == 0) return;
        var games = await Task.Run(() => paths.Select(GameScanner.ScanSingleExe).OfType<GameCandidate>().ToArray(), _lifetimeCts.Token);
        if (IsDisposed) return;
        foreach (var game in games)
            if (!_allGames.Any(g => g.ExePath.Equals(game.ExePath, StringComparison.OrdinalIgnoreCase))) _allGames.Add(game);
        RenderGameRows();
    }

    void SaveRoot()
    {
        if (!Directory.Exists(_scanRoot.Text)) return;
        SaveLastGameRoot(_scanRoot.Text);
        var root = Path.GetFullPath(_scanRoot.Text);
        if (!_libraryState.ScanRoots.Contains(root, StringComparer.OrdinalIgnoreCase)) _libraryState.ScanRoots.Add(root);
        SaveLibrary();
        RefreshLibrarySettings();
    }

    async Task ScanRootsAsync(IEnumerable<string> roots)
    {
        if (_busy) return;
        _scanCts?.Cancel();
        _scanCts?.Dispose();
        _scanCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token);
        var selected = SelectedBaseGame()?.ExePath;
        SetBusy(true);
        try
        {
            var found = new List<GameCandidate>();
            foreach (var root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!Directory.Exists(root)) { Log("Scan folder unavailable: " + root); continue; }
                Log("Scanning " + root);
                SetOutcome("Scanning game folders...");
                found.AddRange(await GameScanner.ScanAsync(root, new Progress<string>(Log), _scanCts.Token));
            }
            foreach (var game in found)
            {
                _allGames.RemoveAll(g => g.ExePath.Equals(game.ExePath, StringComparison.OrdinalIgnoreCase));
                _allGames.Add(game);
            }
            _libraryState.CachedGamePaths = _allGames.Select(g => g.ExePath).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            SaveLibrary();
            RenderGameRows(selected);
            Log($"Scan complete: {found.Count} candidates.");
            SetOutcome($"Scan completed: {found.Count} games found.");
        }
        catch (OperationCanceledException) { Log("Scan cancelled."); SetOutcome("Scan cancelled. Previously found games kept."); }
        catch (Exception ex) { Log("Scan failed: " + ex.Message); SetOutcome("Scan failed: " + ex.Message); }
        finally { _scanCts?.Dispose(); _scanCts = null; if (!IsDisposed) SetBusy(false); }
    }

    async Task ScanSavedRootsAsync() => await ScanRootsAsync(_libraryState.ScanRoots.ToArray());

    void AddScanRoot()
    {
        using var dialog = new FolderBrowserDialog { Description = "Add game folder", UseDescriptionForTitle = true };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        _scanRoot.Text = dialog.SelectedPath;
        SaveRoot();
    }

    void RemoveScanRoot()
    {
        if (_rootsList.SelectedItem is not string root) return;
        _libraryState.ScanRoots.RemoveAll(p => p.Equals(root, StringComparison.OrdinalIgnoreCase));
        SaveLibrary();
        RefreshLibrarySettings();
    }

    void RefreshLibrarySettings()
    {
        _rootsList.Items.Clear();
        _rootsList.Items.AddRange(_libraryState.ScanRoots.Cast<object>().ToArray());
        _hiddenList.Items.Clear();
        _hiddenList.Items.AddRange(_libraryState.HiddenPaths.Cast<object>().ToArray());
        RefreshCommandStates();
    }

    void ToggleFavorite()
    {
        var game = SelectedBaseGame();
        if (game is null) return;
        TogglePath(_libraryState.FavouritePaths, game.ExePath);
        SaveLibrary();
        RenderGameRows(game.ExePath);
        RefreshGameDetails();
    }

    void ToggleHidden()
    {
        var game = SelectedBaseGame();
        if (game is null) return;
        TogglePath(_libraryState.HiddenPaths, game.ExePath);
        SaveLibrary();
        RefreshLibrarySettings();
        RenderGameRows();
    }

    void UnhideSelected()
    {
        if (_hiddenList.SelectedItem is not string path) return;
        _libraryState.HiddenPaths.RemoveAll(p => p.Equals(path, StringComparison.OrdinalIgnoreCase));
        SaveLibrary();
        RefreshLibrarySettings();
        RenderGameRows();
    }

    static void TogglePath(List<string> paths, string path)
    {
        if (paths.Contains(path, StringComparer.OrdinalIgnoreCase)) paths.RemoveAll(p => p.Equals(path, StringComparison.OrdinalIgnoreCase));
        else paths.Add(path);
    }

    string StoreFor(GameCandidate game)
    {
        if (_stores.TryGetValue(game.ExePath, out var store)) return store;
        if (game.ExePath.Contains("steamapps", StringComparison.OrdinalIgnoreCase)) return "Steam";
        if (game.ExePath.Contains("GOG", StringComparison.OrdinalIgnoreCase)) return "GOG";
        if (game.ExePath.Contains("XboxGames", StringComparison.OrdinalIgnoreCase)) return "Xbox";
        if (game.ExePath.Contains("Epic Games", StringComparison.OrdinalIgnoreCase)) return "Epic";
        if (game.ExePath.Contains("Ubisoft", StringComparison.OrdinalIgnoreCase)) return "Ubisoft";
        return "Manual";
    }

    bool IsInstalled(GameCandidate game)
    {
        if (!_installed.TryGetValue(game.ExePath, out var installed))
        {
            installed = InstallEvidence?.Invoke(game) ?? (!_preview && RuntimeDiagnostics.HasInstallManifest(game));
            _installed[game.ExePath] = installed;
        }
        return installed;
    }

    void RefreshGameDetails(bool loadProfile = true)
    {
        if (loadProfile) LoadRouteChoices(SelectedBaseGame());
        var game = SelectedGame();
        if (game is not null) _ = GameInstallStatus(game);
        _detailName.Text = game?.DisplayName ?? "Select a game";
        _detailPath.Text = game?.ExePath ?? "";
        _detailRoute.Text = game is null ? "" : PackagedPhoenixPlan.For(game, _payload)?.Description ?? game.DisplayRoute;
        var receipt = game is null ? null : VerificationReceipt(game);
        var readiness = game is null ? "" : IsInstalled(game) ? _runtimeChecks.GetValueOrDefault(game.ExePath, receipt is null ? "Installed · output not checked" : $"{GameInstallStatus(game)} · {receipt.CheckedAt.LocalDateTime:g}") :
            game.Route == InstallRoute.Unsupported ? "Unsupported renderer - choose an override in Advanced" :
            _payload?.IsReadyFor(game) == true ? "Ready to install" : SummarizeMissingComponents(_payload?.MissingFor(game));
        if (game is not null && game.CompatibilityWarnings.Count > 0)
            readiness += Environment.NewLine + "Renderer warning - runtime verification required";
        _detailStatus.Text = game is null ? "" : game.DisplayApi + " | " + game.Arch + Environment.NewLine + readiness;
        _ = RefreshInstalledPhoenixAsync();
        _installSummary.Text = game is null ? "" : $"Next install: {(_defaultRenderer.SelectedIndex == 1 ? "NVIDIA" : "Phoenix")} · {(_defaultNeuralModel.SelectedIndex == 1 ? "FP8 model" : "Original model")}\nModel: {(_copyNvidiaDll.Checked ? "copy DLL to game" : "shared — keep Components in place")}";
        _tips.SetToolTip(_installSummary, EffectiveInstallSummary());
        _verificationReport.Visible = receipt is not null && File.Exists(receipt.ReportPath);
        _setupComponents.Tag = game is not null && !IsInstalled(game) && _payload?.IsReadyFor(game) != true ? "primary" : null;
        UiTheme.Apply(_setupComponents, _palette);
        _tips.SetToolTip(_detailStatus, game is null ? "" : string.Join(Environment.NewLine,
            game.CompatibilityWarnings.Append(_payload?.MissingFor(game) ?? "Choose components first.")));
        if (game is not null && EmulatorProfiles.ProfileFor(game.ExePath) is { } emulator)
            _tips.SetToolTip(_engineChoice, emulator.Name + ": " + emulator.Hint + ". Renderer metadata is not runtime verification.");
        else _tips.SetToolTip(_engineChoice, "Choose a detected renderer or an explicit override.");
        foreach (var control in new Control[] { _favorite, _hide, _saveProfile, _diagnostics, _launchArguments, _frameIterations, _forceVrEyeSplit, _routeChoice, _proxyChoice }) control.Enabled = game is not null && !_busy;
        if (game is null) return;
        _favorite.Text = _libraryState.FavouritePaths.Contains(game.ExePath, StringComparer.OrdinalIgnoreCase) ? "Favorited" : "Favorite";
        _hide.Text = _libraryState.HiddenPaths.Contains(game.ExePath, StringComparer.OrdinalIgnoreCase) ? "Show again" : "Hide";
        if (!loadProfile) return;
        _loadingProfile = true;
        try
        {
            var profile = _libraryState.ProfileFor(game.ExePath);
            _launchArguments.Text = profile.LaunchArguments ?? game.SuggestedArguments;
            _frameIterations.Value = Math.Clamp(profile.FrameIterations, 1, 10);
            _forceVrEyeSplit.Checked = profile.ForceVrEyeSplit;
            ShowArtwork(ArtworkFor(game));
            QueueArtwork();
        }
        finally { _loadingProfile = false; }
    }

    internal static string SummarizeMissingComponents(string? missing)
    {
        if (missing?.Contains(PayloadScanner.FeederApiRemediation, StringComparison.Ordinal) == true)
            return "Missing: direct feeder API";
        var names = System.Text.RegularExpressions.Regex.Replace(missing ?? "components", @"\s*\([^)]*\)", "");
        names = names.Replace("Phoenix.addon64", "Phoenix DLSS5", StringComparison.OrdinalIgnoreCase)
            .Replace("nvngx_dlssnr.dll", "model DLL", StringComparison.OrdinalIgnoreCase)
            .Replace("nvngx_dlss.dll", "NGX SR DLL", StringComparison.OrdinalIgnoreCase);
        return "Missing: " + names;
    }

    void SaveSelectedProfile()
    {
        var detected = SelectedBaseGame();
        var effective = SelectedGame();
        if (detected is null || effective is null || _loadingProfile) return;
        var profile = _libraryState.ProfileFor(detected.ExePath) with
        {
            ApiOverride = _engineChoice.SelectedIndex > 0 && _engineChoice.SelectedItem is EngineChoice choice ? choice.Api : null,
            PreferredRoute = ChosenRoute,
            ProxyName = ChosenProxy,
            LaunchArguments = _launchArguments.Text,
            FrameIterations = FrameIterations(),
            ForceVrEyeSplit = _forceVrEyeSplit.Checked
        };
        _libraryState.Profiles[detected.ExePath] = profile;
        SaveLibrary();
        Log("Saved profile for " + detected.DisplayName);
    }

    void ChangeTheme()
    {
        var choice = (_themeChoice.SelectedItem?.ToString() ?? "Light").ToLowerInvariant();
        _libraryState.Theme = choice;
        var dark = choice == "dark";
        if (choice == "system")
        {
            try { dark = (int?)Microsoft.Win32.Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 1) == 0; }
            catch { }
        }
        _palette = SystemInformation.HighContrast ? UiPalette.HighContrast : dark ? UiPalette.Dark : UiPalette.Light;
        SaveLibrary();
        ApplyTheme();
    }

    void WireGameMenu()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Play", null, (_, _) => RunSelectedExe());
        menu.Items.Add("Open folder", null, (_, _) => OpenSelectedFolder());
        menu.Items.Add("Copy path", null, (_, _) => { if (SelectedGame() is { } game) CopyText(game.ExePath); });
        menu.Items.Add("Rescan", null, (_, _) => RescanSelected());
        menu.Items.Add("Favorite / unfavorite", null, (_, _) => ToggleFavorite());
        menu.Items.Add("Hide / show", null, (_, _) => ToggleHidden());
        menu.Items.Add("Change artwork", null, (_, _) => ChangeArtwork());
        menu.Items.Add("Restore originals", null, async (_, _) => await RestoreSelectedAsync());
        menu.Items.Add("NVIDIA driver settings…", null, (_, _) =>
        {
            if (_preview || SelectedBaseGame() is not { } game) return;
            using var dialog = new DriverProfileForm(game.ExePath);
            UiTheme.Apply(dialog, _palette);
            dialog.ShowDialog(this);
        });
        menu.Items.Add("Phoenix runtime recovery…", null, (_, _) =>
        {
            if (_preview || SelectedBaseGame() is not { } game) return;
            try { using var dialog = new PhoenixRecoveryForm(game.ExePath, _payload?.Root); UiTheme.Apply(dialog, _palette); dialog.ShowDialog(this); }
            catch (Exception error) { Log("Phoenix recovery unavailable: " + error.Message); }
        });
        menu.Items.Add("Existing mods and loading plan…", null, (_, _) =>
        {
            if (_preview || SelectedGame() is not { } game) return;
            try
            {
                using var dialog = new ModLoadingPlanForm(ModCoexistencePlanner.Inspect(game));
                UiTheme.Apply(dialog, _palette);
                if (dialog.ShowDialog(this) == DialogResult.OK && dialog.SelectedProxy is { } proxy)
                {
                    _proxyChoice.SelectedIndex = proxy == "dxgi.dll" ? 1 : 2;
                    SaveSelectedProfile();
                }
            }
            catch (Exception error) { Log("Loading plan unavailable: " + error.Message); }
        });
        _games.ContextMenuStrip = menu;
        _myGames.ContextMenuStrip = menu;
        menu.Opening += (_, e) =>
        {
            e.Cancel = SelectedBaseGame() is null || _busy;
            menu.Items[0].Enabled = _runExe.Enabled;
            menu.Items[1].Enabled = _openFolder.Enabled;
            menu.Items[3].Enabled = !_preview;
            menu.Items[6].Enabled = CanRunCommand("Change artwork");
            menu.Items[7].Enabled = _restore.Enabled;
        };
        _games.MouseDown += (_, e) => { if (e.Button == MouseButtons.Right && _games.GetItemAt(e.X, e.Y) is { } item) { item.Selected = true; item.Focused = true; } };
        _myGames.MouseDown += (_, e) => { if (e.Button == MouseButtons.Right && _myGames.GetItemAt(e.X, e.Y) is { } item) { item.Selected = true; item.Focused = true; } };
    }

    void RescanSelected()
    {
        var game = SelectedBaseGame();
        if (game is null) return;
        var detected = GameScanner.ScanSingleExe(game.ExePath);
        if (detected is null) { Log("The executable could not be detected. Existing entry kept."); return; }
        _allGames.RemoveAll(x => x.ExePath.Equals(game.ExePath, StringComparison.OrdinalIgnoreCase));
        _allGames.Add(detected);
        _installed.Remove(game.ExePath);
        _verificationReceipts.Remove(game.ExePath);
        RenderGameRows(game.ExePath);
    }

    void ChangeArtwork()
    {
        if (_preview || _busy) return;
        var game = SelectedBaseGame();
        if (game is null) return;
        using var dialog = new OpenFileDialog { Title = "Game artwork", Filter = "Images|*.png;*.jpg;*.jpeg;*.bmp" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            using var original = Image.FromFile(dialog.FileName);
            if (original.Width > 8192 || original.Height > 8192) throw new InvalidDataException("Artwork is too large.");
            var root = Path.Combine(LocalData.DirectoryPath, "artwork");
            Directory.CreateDirectory(root);
            var path = Path.Combine(root, Guid.NewGuid().ToString("N") + ".png");
            using var resized = new Bitmap(original, new Size(320, Math.Max(1, 320 * original.Height / original.Width)));
            resized.Save(path, System.Drawing.Imaging.ImageFormat.Png);
            _libraryState.Profiles[game.ExePath] = _libraryState.ProfileFor(game.ExePath) with { ArtworkPath = path };
            SaveLibrary();
            ShowArtwork(path);
            InvalidateArtworkTiles();
        }
        catch (Exception ex) { Log("Artwork could not be loaded: " + ex.Message); }
    }

    void ShowArtwork(string? path)
    {
        _cover.Image?.Dispose();
        _cover.Image = null;
        try { if (path is not null && File.Exists(path)) { using var image = Image.FromFile(path); _cover.Image = new Bitmap(image); } }
        catch (Exception ex) { Log("Artwork unavailable: " + ex.Message); }
        _cover.Visible = _cover.Image is not null;
    }

    void RefreshHistory()
    {
        _historyView.Items.Clear();
        if (_preview) return;
        try
        {
            foreach (var entry in InstallHistory.Load(warning: Log))
            {
                var item = new ListViewItem(entry.Timestamp.LocalDateTime.ToString("g")) { Tag = entry, ToolTipText = entry.Message };
                item.SubItems.AddRange([entry.GameName, entry.Action.ToString(), entry.Outcome.ToString(), entry.Message]);
                _historyView.Items.Add(item);
            }
        }
        catch (Exception ex) { Log("History unavailable: " + ex.Message); }
    }

    void CopyHistory()
    {
        if (_historyView.SelectedItems.Count == 0) return;
        if (_historyView.SelectedItems[0].Tag is InstallHistoryEntry entry) CopyText(entry.Summary);
    }

    void RecordHistory(GameCandidate game, HistoryAction action, bool success, string message)
    {
        _verificationReceipts.Remove(game.ExePath); _verificationReceiptStamps.Remove(game.ExePath);
        if (action != HistoryAction.Verify) _sessionVerification.Remove(game.ExePath);
        if (_preview) return;
        try { InstallHistory.Append(new InstallHistoryEntry { GameName = game.DisplayName, GamePath = game.ExePath, Action = action, Outcome = success ? HistoryOutcome.Success : HistoryOutcome.Failure, Message = message, Route = game.Route.ToString() }); }
        catch (Exception ex) { Log("Could not record history: " + ex.Message); }
        _installed.Remove(game.ExePath);
        RefreshGameDetails(false);
        RenderGameRows(game.ExePath);
    }

    async void CheckSelectedRuntime()
    {
        if (SelectedGame() is not { } game) return;
        try
        {
            var report = RuntimeDiagnostics.Collect(game);
            var summary = report.HasLogs ? "Logs found; activation unverified" : "No session evidence";
            var gameDir = Path.GetDirectoryName(game.ExePath)!;
            var directory = File.Exists(Path.Combine(gameDir, PhoenixConfiguration.FileName)) ? gameDir :
                Path.Combine(gameDir, game.Api == GraphicsApi.OpenXr ? "_DLSS5_OpenXR/host64" : "host64");
            var client = new PhoenixControlClient(directory);
            if (!client.CanEditOffline())
            {
                try
                {
                    var response = await client.SendAsync(PhoenixOperation.Status, timeout: TimeSpan.FromSeconds(2), cancellationToken: _lifetimeCts.Token);
                    summary = response.Ok ? "Live Phoenix DLSS5 session connected; displayed output unverified" : "Phoenix DLSS5 session reported: " + response.Error;
                }
                catch (Exception ex) when (ex is IOException or InvalidOperationException or TimeoutException) { summary = "Session check failed: " + ex.Message; }
            }
            if (IsDisposed) return;
            _runtimeChecks[game.ExePath] = summary + " (" + DateTime.Now.ToString("t") + ")";
            RefreshGameDetails(false);
            using var form = new Form { Text = "Runtime diagnostics - " + game.DisplayName, Size = new Size(960, 620), StartPosition = FormStartPosition.CenterParent, Font = Font };
            var text = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, Text = report.Text };
            var footer = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 42, FlowDirection = FlowDirection.RightToLeft };
            footer.Controls.Add(Command("Export redacted report", "\uE74E", () =>
            {
                var redacted = SupportReport.Redact(report.Text, game.Root);
                using var review = new SupportReportForm(redacted, _palette);
                review.ShowDialog(form);
            }));
            form.Controls.Add(text);
            form.Controls.Add(footer);
            form.Controls.Add(new Label { Text = summary + "\nChecked: " + DateTime.Now.ToString("g") + " | Logs and connection alone do not prove image enhancement.", Dock = DockStyle.Top, Height = 58, Padding = new Padding(10, 6, 10, 6) });
            UiTheme.Apply(form, _palette);
            form.ShowDialog(this);
        }
        catch (Exception ex)
        {
            Log("Diagnostics failed: " + ex.Message);
            _runtimeChecks[game.ExePath] = "Check failed - see Activity";
            SetOutcome("Diagnostics failed: " + ex.Message);
            RefreshGameDetails(false);
        }
    }

    async Task CheckAppUpdateAsync()
    {
        if (_preview) { SetOutcome("Preview - app update checks are disabled"); return; }
        var version = typeof(MainForm).Assembly.GetName().Version?.ToString() ?? "0.0.0";
        _updateStatus.Text = "Checking...";
        try
        {
            var result = await AppUpdateService.CheckAsync(version, cancellationToken: _lifetimeCts.Token);
            _updateStatus.Text = result.Newer ? "Available: " + result.LatestVersion : result.CheckSucceeded ? "Up to date: " + version : result.Error;
        }
        catch (OperationCanceledException) { }
    }

    void LoadPreviewLibrary()
    {
        foreach (var (name, exe, arch, api, native) in new[] {
            ("Call of Duty 4: Modern Warfare", "iw3sp.exe", CpuArch.X86, GraphicsApi.DirectX9, false),
            ("Spider-Man 3", "Game.exe", CpuArch.X86, GraphicsApi.DirectX9, false),
            ("Example modern game", "Game64.exe", CpuArch.X64, GraphicsApi.DirectX12, true),
            ("Example OpenGL game", "OpenGL.exe", CpuArch.X64, GraphicsApi.OpenGl, false),
            ("Example Vulkan game", "Vulkan.exe", CpuArch.X64, GraphicsApi.Vulkan, false) })
        {
            var folderName = string.Concat(name.Select(character => Path.GetInvalidFileNameChars().Contains(character) ? '_' : character));
            var root = Path.Combine(@"C:\Games", folderName);
            _allGames.Add(new(root, Path.Combine(root, exe), name, exe, arch, api, "Preview fixture", 200000, [api], HasNativeDlssSignal: native));
        }
        RenderGameRows();
        if (_games.Items.Count > 0) _games.Items[0].Selected = true;
        _componentMessage.Text = "Preview - no game files are changed";
    }
}
