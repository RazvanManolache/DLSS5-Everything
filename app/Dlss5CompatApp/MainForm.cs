using System.Diagnostics;

namespace Dlss5CompatApp;

sealed partial class MainForm : Form
{
    static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Dlss5DxCompat",
        "settings.ini");
    static readonly string[] GameColumnTitles = ["Game", "EXE", "Path", "Arch", "APIs", "Route", "Detected by"];

    readonly TextBox _scanRoot = new() { Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top };
    readonly TextBox _payloadRoot = new() { Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top, ReadOnly = true, TabStop = false };
    readonly Label _payloadStatus = new() { AutoSize = false, Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top };
    readonly ThemedProgressBar _payloadProgress = new() { Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top, Minimum = 0, Maximum = 100 };
    readonly Panel _dropZone = new()
    {
        AllowDrop = true,
        BorderStyle = BorderStyle.FixedSingle,
        BackColor = Color.FromArgb(244, 248, 255),
        Dock = DockStyle.Fill,
        Margin = Padding.Empty,
        Padding = new Padding(12, 8, 12, 8)
    };
    readonly Button _dropZoneText = new()
    {
        AllowDrop = true,
        Dock = DockStyle.Fill,
        Text = "Drop a game EXE, DOS EXE/COM/BAT, or DOS folder here for a temporary DLSS5 run",
        TextAlign = ContentAlignment.MiddleCenter,
        FlatStyle = FlatStyle.Flat,
        ForeColor = Color.FromArgb(25, 55, 100)
    };
    readonly TextBox _search = new() { Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top, PlaceholderText = "Search game, EXE, API, route, path..." };
    readonly CheckBox _hideIncompatible = new() { Text = "Hide unsupported routes", AutoSize = true, Checked = true, Anchor = AnchorStyles.Left, TextAlign = ContentAlignment.MiddleLeft };
    readonly CheckBox _forceVrEyeSplit = new() { Text = "Force VR eye split", AutoSize = true, Anchor = AnchorStyles.Left, TextAlign = ContentAlignment.MiddleLeft };
    readonly NumericUpDown _frameIterations = new ThemedNumericUpDown() { Minimum = 1, Maximum = 10, Value = 1, Width = 58 };
    readonly ComboBox _engineChoice = new ThemedComboBox() { DropDownStyle = ComboBoxStyle.DropDownList, DrawMode = DrawMode.OwnerDrawFixed, Width = 380 };
    readonly ListView _games = new() { View = View.Details, FullRowSelect = true, MultiSelect = false, ShowItemToolTips = true, Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right };
    readonly TextBox _log = new() { Multiline = true, ScrollBars = ScrollBars.Vertical, ReadOnly = true, Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom };
    readonly Button _install = new() { Text = "Install selected" };
    readonly Button _verify = new() { Text = "Verify & fix", UseMnemonic = false, AccessibleName = "Verify and fix installation" };
    readonly Button _restore = new() { Text = "Restore selected" };
    readonly Button _runExe = new() { Text = "Run EXE" };
    readonly Button _openFolder = new() { Text = "Open folder" };
    readonly Button _updatePayload = new() { Text = "Update payload" };

    PayloadInfo? _payload;
    string? _payloadScanFailure;
    CancellationTokenSource? _scanCts;
    readonly List<GameCandidate> _allGames = [];
    int _sortColumn;
    bool _sortAscending = true;

    readonly bool _previewArtwork;

    public MainForm(bool preview = false, string previewPage = "Library", bool previewLight = false, bool previewSmall = false, string? payloadRoot = null, bool previewArtwork = false,
        string? initialPage = null, bool startStreamer = false, string? streamerPairingFile = null)
    {
        _preview = preview;
        _previewArtwork = preview && previewArtwork;
        Text = "DLSS5 Everything";
        Font = new Font("Segoe UI", 9.5f);
        AutoScaleMode = AutoScaleMode.Dpi;
        Width = previewSmall ? 1060 : 1400;
        Height = previewSmall ? 740 : 900;
        MinimumSize = new Size(1060, 740);
        StartPosition = FormStartPosition.CenterScreen;

        BuildShellUi();
        InitializeAccessibility();
        WireEvents();

        _scanRoot.Text = preview ? @"C:\Games" : LoadLastGameRoot() ?? DefaultGameRoot();
        _payloadRoot.Text = payloadRoot ?? @".\Payload";
        RefreshPayload();
        if (previewLight && preview) _libraryState.Theme = "light";
        InitializeLibrary();
        InitializeConvenience();
        InitializeCommandStates();
        InitializeUx();
        Shown += async (_, _) =>
        {
            if (_preview) { LoadPreviewLibrary(); if (_previewArtwork) SetConvenienceTiles(true); if (_pages.ContainsKey(previewPage)) ShowPage(previewPage); }
            else {
                if (initialPage is not null && (_pages.ContainsKey(initialPage) || initialPage == "LAN")) ShowPage(initialPage);
                if (startStreamer && _streamer.StartListening() && streamerPairingFile is not null)
                {
                    try { StreamerPairing.WriteNew(streamerPairingFile, _streamer.PairingData()); }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException) { SetOutcome("Pairing export failed: " + error.Message); }
                }
                await BootstrapPayloadAsync();
            }
        };
    }


    void WireEvents()
    {
        _install.Click += async (_, _) => await InstallSelectedAsync();
        _verify.Click += async (_, _) => await VerifySelectedAsync();
        _restore.Click += async (_, _) => await RestoreSelectedAsync();
        _runExe.Click += (_, _) => RunSelectedExe();
        _openFolder.Click += (_, _) => OpenSelectedFolder();
        _updatePayload.Click += async (_, _) => await BootstrapPayloadAsync();
        _games.SelectedIndexChanged += (_, _) => { if (!_usingMyGames) UpdateEngineChoices(); };
        _engineChoice.SelectedIndexChanged += (_, _) =>
        {
            UpdateSelectedRouteDisplay();
            UpdateButtons();
        };
        _engineChoice.DrawItem += DrawEngineChoice;
        _games.ColumnClick += (_, e) => SortByColumn(e.Column);
        _search.TextChanged += (_, _) => RenderGameRows();
        _hideIncompatible.CheckedChanged += (_, _) => RenderGameRows();
        WireDropZone(_dropZone);
        WireDropZone(_dropZoneText);
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

        SaveRoot();
        await ScanRootsAsync([_scanRoot.Text]);
    }

    async Task BootstrapPayloadAsync(bool replaceLocalDevelopment = false)
    {
        if (_downloadCts is not null || _busy || _preview) return;
        _downloadCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token);
        _updatePayload.Enabled = false;
        _cancelBackground.Enabled = true;
        UpdateButtons();
        _payloadProgress.Value = 0;
        var payloadPath = ResolvePayloadPath();
        var progress = new Progress<BootstrapProgress>(item =>
        {
            if (IsDisposed) return;
            _payloadProgress.Value = Math.Clamp(item.Percent, _payloadProgress.Minimum, _payloadProgress.Maximum);
            _componentMessage.Text = item.Message;
            if (!string.IsNullOrWhiteSpace(item.Message))
                Log(item.Message);
        });

        try
        {
            Log("Checking payload downloads in " + DisplayPath(payloadPath));
            SetOutcome("Checking components...");
            await PayloadBootstrapper.EnsureCurrentAsync(payloadPath, progress, _downloadCts.Token,
                channel: _releaseChannel.SelectedIndex == 1 ? PhoenixReleaseChannel.StableOnly : PhoenixReleaseChannel.LatestPublished,
                replaceLocalDevelopment: replaceLocalDevelopment);
            await OptionalModelLibrary.EnsureSelectedAsync(payloadPath, SelectedOptionalModels(), progress, _downloadCts.Token);
            SetOutcome("Component check completed.");
        }
        catch (OperationCanceledException)
        {
            Log("Component download cancelled.");
            SetOutcome("Component download cancelled. Existing components kept.");
        }
        catch (Exception ex)
        {
            Log("Payload update failed: " + ex.Message);
            SetOutcome("Component update failed: " + ex.Message);
        }
        finally
        {
            _downloadCts.Dispose();
            _downloadCts = null;
            if (!IsDisposed)
            {
                RefreshPayload();
                _updatePayload.Enabled = true;
                _cancelBackground.Enabled = _scanCts is not null;
            }
        }
    }

    void AddExe()
    {
        if (_busy) return;
        using var dialog = new OpenFileDialog
        {
            Filter = "Executable (*.exe)|*.exe",
            Title = "Choose game executable"
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        var game = GameScanner.ScanSingleExe(dialog.FileName);
        if (game is null)
        {
            MessageBox.Show(this, "Could not detect this executable. Try the renderer override for an added game.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        _scanRoot.Text = Path.GetDirectoryName(dialog.FileName) ?? _scanRoot.Text;
        SaveLastGameRoot(_scanRoot.Text);
        _allGames.RemoveAll(x => x.ExePath.Equals(game.ExePath, StringComparison.OrdinalIgnoreCase));
        _allGames.Add(game);
        if (game.Route == InstallRoute.Unsupported) _hideIncompatible.Checked = false;
        if (!_libraryState.ManualPaths.Contains(game.ExePath, StringComparer.OrdinalIgnoreCase)) _libraryState.ManualPaths.Add(game.ExePath);
        SaveLibrary();
        RenderGameRows(game.ExePath);
    }

    void WireDropZone(Control control)
    {
        control.DragEnter += (object? _, DragEventArgs e) =>
        {
            var hasPath = TryGetDroppedPath(e, out var ignoredPath);
            if (hasPath)
            {
                e.Effect = DragDropEffects.Copy;
                SetDropFeedback(true);
            }
            else
            {
                e.Effect = DragDropEffects.None;
            }
        };
        control.DragLeave += (_, _) => SetDropFeedback(false);
        control.DragDrop += async (object? _, DragEventArgs e) =>
        {
            SetDropFeedback(false);
            if (!TryGetDroppedPath(e, out var path))
                return;
            await HandleDroppedTargetAsync(path);
        };
    }

    static bool TryGetDroppedPath(DragEventArgs e, out string path)
    {
        path = "";
        if (e.Data is null || !e.Data.GetDataPresent(DataFormats.FileDrop))
            return false;

        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files || files.Length == 0)
            return false;

        path = files[0];
        return File.Exists(path) || Directory.Exists(path);
    }

    async Task HandleDroppedTargetAsync(string path)
    {
        if (_busy || _downloadCts is not null || _preview) return;
        try
        {
            var fullPath = Path.GetFullPath(path);
            var options = BuildDropOptions(fullPath);
            if (options.Count == 0)
            {
                MessageBox.Show(this,
                    "I could not detect a supported game route from that file or folder.",
                    Text,
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return;
            }

            using var dialog = new DropRouteDialog(fullPath, options);
            UiTheme.Apply(dialog, _palette);
            if (dialog.ShowDialog(this) != DialogResult.OK || dialog.SelectedOption is null)
                return;

            if (dialog.SelectedOption.DosTarget is not null)
                await RunTemporaryDosAsync(dialog.SelectedOption.DosTarget);
            else if (dialog.SelectedOption.Game is not null)
                await RunTemporaryWindowsGameAsync(dialog.SelectedOption.Game);
        }
        catch (Exception ex)
        {
            Log("Dropped launch failed: " + ex.Message);
            MessageBox.Show(this, ex.Message, "Dropped launch failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    static List<DropLaunchOption> BuildDropOptions(string path)
    {
        var options = new List<DropLaunchOption>();
        GameCandidate? game = null;
        if (File.Exists(path) && Path.GetExtension(path).Equals(".exe", StringComparison.OrdinalIgnoreCase))
            game = GameScanner.ScanSingleExe(path);

        if (game is not null)
        {
            foreach (var api in game.AllApis)
            {
                var choice = game.WithApi(api);
                options.Add(new DropLaunchOption(
                    choice.DisplayName,
                    choice.DisplayApi + " - " + choice.DisplayRoute,
                    choice.Route != InstallRoute.Unsupported,
                    choice,
                    null));
            }
        }

        if (game is null && DosGameLauncher.IsSupportedDosTargetPath(path))
        {
            try
            {
                var dosTarget = DosGameLauncher.ResolveDosTargetPath(path);
                options.Add(new DropLaunchOption(
                    Path.GetFileNameWithoutExtension(dosTarget),
                    "DOSBox Enhanced + native Phoenix DLSS5 RTX - isolated DOS session",
                    true,
                    null,
                    dosTarget));
            }
            catch
            {
                // Keep the drop parser permissive; the launch path reports the detailed DOS error.
            }
        }

        return options;
    }

    async Task RunTemporaryDosAsync(string target)
    {
        var payloadRoot = ResolvePayloadPath();
        DosGameLauncher.DosLaunchSession? session = null;
        SetBusy(true);
        try
        {
            var progress = new Progress<BootstrapProgress>(item =>
            {
                _payloadProgress.Value = Math.Clamp(item.Percent, _payloadProgress.Minimum, _payloadProgress.Maximum);
                if (!string.IsNullOrWhiteSpace(item.Message))
                    Log(item.Message);
            });
            Log("Preparing temporary DOS run for " + target);
            session = await Task.Run(() => DosGameLauncher.Launch(target, payloadRoot, allowUpdate: true, progress, CancellationToken.None, FrameIterations()));
            RefreshPayload();
            Log("Launched " + Path.GetFileName(session.TargetPath) + ". Cleanup will run after it exits.");
        }
        catch (Exception ex)
        {
            Log("DOS temporary launch failed: " + ex.Message);
            MessageBox.Show(this, ex.Message, "DOS temporary launch failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        finally
        {
            SetBusy(false);
        }

        await WaitThenCleanupDosAsync(session);
    }

    async Task WaitThenCleanupDosAsync(DosGameLauncher.DosLaunchSession session)
    {
        try
        {
            await session.Process.WaitForExitAsync();
        }
        catch (Exception ex)
        {
            Log("DOS wait failed: " + ex.Message);
        }
        finally
        {
            DosGameLauncher.CleanupSession(session);
            Log("Cleaned temporary DOS config for " + Path.GetFileName(session.TargetPath));
        }
    }

    async Task RunTemporaryWindowsGameAsync(GameCandidate game)
    {
        RefreshPayload();
        if (_payload is null)
            return;

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
                game.DisplayApi + "\n" + DisplayInstallRoute(game) + "\n\n" +
                "Use the closest supported route for this temporary run?\n\n" +
                fallback.DisplayApi + "\n" + DisplayInstallRoute(fallback),
                "Unsupported engine selected",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2);
            if (answer != DialogResult.Yes)
                return;

            game = fallback;
        }

        if (!_payload.IsReadyFor(game))
        {
            MessageBox.Show(this,
                "Payload is missing for this route:\n\n" + _payload.MissingFor(game),
                "Payload missing",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return;
        }

        var engine = new InstallerEngine(Log);
        Process? process = null;
        var installed = false;
        SetBusy(true);
        try
        {
            SetOutcome("Checking temporary installation safety...");
            var startup = GameStartupSettings();
            var checks = await Task.Run(() => InstallerEngine.CheckPreflight(game, _payload, _lifetimeCts.Token, startup), _lifetimeCts.Token);
            if (!ConfirmTemporaryInstall(checks)) return;
            Log("Temporary install for " + game.Name + " using " + DisplayInstallRoute(game));
            var result = await InstallGameFilesAsync(engine, game, _payload, startup);
            installed = true;
            if (!result.Complete)
                MessageBox.Show(this, result.Warning, "Installation incomplete", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            else
            {
                process = StartTemporaryLauncher(game);
                Log("Launched " + game.Name + ". Cleanup will run after it exits.");
            }
        }
        catch (Exception ex)
        {
            Log("Temporary launch failed: " + ex.Message);
            MessageBox.Show(this, ex.Message, "Temporary launch failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            SetBusy(false);
        }

        if (!installed)
            return;

        if (process is null)
        {
            Log("Could not track the launched process; restoring immediately.");
            await RestoreTemporaryInstallAsync(engine, game);
            return;
        }

        await WaitThenRestoreAsync(engine, game, process);
    }

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal Func<string, string, MessageBoxButtons, MessageBoxDefaultButton, DialogResult>? TemporaryInstallPrompt { get; set; }

    internal bool ConfirmTemporaryInstall(InstallPreflightResult checks)
    {
        DialogResult Prompt(string message, string title, MessageBoxButtons buttons, MessageBoxDefaultButton defaultButton) =>
            TemporaryInstallPrompt?.Invoke(message, title, buttons, defaultButton)
            ?? MessageBox.Show(this, message, title, buttons, MessageBoxIcon.Warning, defaultButton);

        if (!checks.CanInstall)
        {
            Prompt(checks.DisplayText, "Installation blocked", MessageBoxButtons.OK, MessageBoxDefaultButton.Button1);
            SetOutcome("Temporary installation blocked. No files changed.");
            return false;
        }
        if (checks.HasWarnings && Prompt(checks.DisplayText + "\n\nContinue with temporary installation and launch?",
                checks.RequiresAntiCheatAcknowledgement ? "Anti-cheat warning" : "Review installation warnings",
                MessageBoxButtons.YesNo, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
        {
            SetOutcome("Temporary run cancelled. No files changed.");
            return false;
        }
        return true;
    }

    Process? StartTemporaryLauncher(GameCandidate game)
    {
        var launchTarget = InstallerEngine.LaunchTargetFor(game);
        if (launchTarget.EndsWith(".bat", StringComparison.OrdinalIgnoreCase))
        {
            return Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = "/d /c call \"" + launchTarget + "\" --wait",
                WorkingDirectory = Path.GetDirectoryName(game.ExePath) ?? game.Root,
                UseShellExecute = false,
                CreateNoWindow = true
            });
        }

        var start = new ProcessStartInfo
        {
            FileName = launchTarget,
            WorkingDirectory = Path.GetDirectoryName(game.ExePath) ?? game.Root,
            UseShellExecute = false
        };
        if (launchTarget.Equals(game.ExePath, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(game.SuggestedArguments))
            start.Arguments = game.SuggestedArguments;
        return Process.Start(start);
    }

    async Task WaitThenRestoreAsync(InstallerEngine engine, GameCandidate game, Process process)
    {
        try
        {
            await process.WaitForExitAsync();
        }
        catch (Exception ex)
        {
            Log("Wait failed: " + ex.Message);
        }

        await RestoreTemporaryInstallAsync(engine, game);
    }

    async Task RestoreTemporaryInstallAsync(InstallerEngine engine, GameCandidate game)
    {
        SetBusy(true);
        try
        {
            Log("Restoring temporary install for " + game.Name);
            await engine.RestoreAsync(game.Root);
        }
        catch (Exception ex)
        {
            Log("Temporary restore failed: " + ex.Message);
            MessageBox.Show(this, ex.Message, "Temporary restore failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    async Task InstallSelectedAsync()
    {
        if (_preview || _busy || _downloadCts is not null) return;
        var game = SelectedInstallGame();
        if (game is null || _payload is null) return;
        game = ResolveInstallChoice(game);
        if (game is null) return;

        SetBusy(true);
        try
        {
            SetOutcome("Checking installation safety...");
            var startup = GameStartupSettings();
            var checks = await Task.Run(() => InstallerEngine.CheckPreflight(game, _payload, _lifetimeCts.Token, startup), _lifetimeCts.Token);
            if (!checks.CanInstall)
            {
                MessageBox.Show(this, checks.DisplayText, "Installation blocked", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                SetOutcome("Installation blocked. Review the safety check.");
                return;
            }
            if (checks.HasWarnings && MessageBox.Show(this, checks.DisplayText + "\n\nContinue with installation?",
                    checks.RequiresAntiCheatAcknowledgement ? "Anti-cheat warning" : "Review installation warnings",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
            Log("Installing " + game.Name + " using " + DisplayInstallRoute(game));
            SetOutcome("Installing Phoenix DLSS5 for " + game.DisplayName + "...");
            var result = await InstallGameFilesAsync(new InstallerEngine(Log), game, _payload, startup);
            SaveSelectedProfile();
            _runtimeChecks.Remove(game.ExePath);
            RecordHistory(game, HistoryAction.Install, result.Complete, result.Complete
                ? "Phoenix DLSS5 files installed. Runtime verification pending." : result.Warning);
            if (!result.Complete)
            {
                SetOutcome("Installation incomplete: " + result.Failures.Count + " file(s) could not be installed. Check the warning and install again.");
                MessageBox.Show(this, result.Warning, "Installation incomplete", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            else SetOutcome("Installed for " + game.DisplayName + ". Choose Verify & fix to check it, or Play.");
        }
        catch (Exception ex)
        {
            Log("Install failed: " + ex.Message);
            SetOutcome("Installation failed: " + ex.Message);
            RecordHistory(game, HistoryAction.Install, false, ex.Message);
            MessageBox.Show(this, ex.Message, "Install failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    Task<InstallResult> InstallGameFilesAsync(InstallerEngine engine, GameCandidate game, PayloadInfo payload, PhoenixSettings startup)
    {
        // Read controls on the UI thread; antivirus scanning/copying stays off it.
        var split = _forceVrEyeSplit.Checked;
        var iterations = FrameIterations();
        var model = GameInstallModelPath();
        var token = _lifetimeCts.Token;
        return Task.Run(() => engine.InstallAsync(game, payload, split, iterations, token, startup, model), token);
    }

    async Task RestoreSelectedAsync()
    {
        if (_preview || _busy || _downloadCts is not null) return;
        var game = SelectedGame();
        if (game is null) return;
        var recovering = InstallerEngine.HasPending(game.Root);
        var explanation = recovering ? "Undo the interrupted installation for " + game.DisplayName + "?\n\nPartial files will be removed and recorded originals restored. Then you can install again."
            : "Remove this install for " + game.DisplayName + "?\n\nFiles added by the installer will be removed and recorded originals restored. Other game files and saved settings profiles are kept.";
        if (MessageBox.Show(this, explanation, recovering ? "Undo interrupted installation" : "Remove Phoenix DLSS5",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;

        SetBusy(true);
        try
        {
            Log("Restoring " + game.Root);
            SetOutcome("Restoring recorded original files...");
            var engine = new InstallerEngine(Log);
            if (InstallerEngine.HasPending(game.Root)) await engine.RecoverAsync(game.Root, _lifetimeCts.Token);
            else await engine.RestoreAsync(game.Root, _lifetimeCts.Token);
            RecordHistory(game, HistoryAction.Restore, true, "Original files restored.");
            SetOutcome(recovering ? "Interrupted installation undone. Originals restored; you can install again." : "Removed install for " + game.DisplayName + ". Recorded originals restored.");
        }
        catch (Exception ex)
        {
            Log("Restore failed: " + ex.Message);
            SetOutcome("Restore stopped: " + ex.Message);
            RecordHistory(game, HistoryAction.Restore, false, ex.Message);
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
            StartExternalProcess(new ProcessStartInfo
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
            var start = GameLauncher.Create(game, _launchArguments.Text);
            StartExternalProcess(start);
            Log("Launched " + Path.GetFileName(game.ExePath));
        }
        catch (Exception ex)
        {
            Log("Launch failed: " + ex.Message);
            MessageBox.Show(this, ex.Message, "Launch failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    void RefreshPayload()
    {
        try
        {
            _payload = PayloadScanner.Scan(ResolvePayloadPath());
            RefreshOptionalModels();
            _payloadStatus.Text = _payload.Summary;
            _payloadStatus.ForeColor = _payload.HasPhoenixFeederApi && _payload.HasEmbeddedPhoenixRuntime && _payload.HasPhoenixModel && _payload.HasReShade64
                ? _palette.Success
                : _palette.Error;
            if (_payloadScanFailure is not null && _operationOutcome == _payloadScanFailure) SetOutcome("");
            _payloadScanFailure = null;
            RefreshComponentDetails();
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or NotSupportedException or System.Security.SecurityException)
        {
            _payload = null;
            _payloadStatus.Text = "Components could not be read - open Components";
            _payloadStatus.ForeColor = _palette.Error;
            _payloadScanFailure = "Component scan failed: " + ex.Message;
            _componentDetails.Text = _payloadScanFailure;
            SetOutcome(_payloadScanFailure);
            Log(_payloadScanFailure);
        }
        UpdateButtons();
        RefreshGameDetails(false);
    }

    void RenderGameRows(string? selectExePath = null)
    {
        selectExePath ??= SelectedGame()?.ExePath;
        UpdateColumnHeaders();
        _games.BeginUpdate();
        try
        {
            _games.Items.Clear();
            _games.Groups.Clear();
            foreach (var game in SortGames(FilterGames(_allGames).Where(MatchesInspectedComponents)))
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
            RenderMyGames();
            ResizeGameColumns();
            UpdateButtons();
            UpdateGameCount();
            var unsupportedHidden = _hideIncompatible.Checked ? _allGames.Count(g => _libraryState.ProfileFor(g.ExePath).Apply(g).Route == InstallRoute.Unsupported) : 0;
            if (!_usingMyGames && unsupportedHidden > 0) _gameCount.Text += $" / {unsupportedHidden} unsupported hidden";
            UpdateEmptyState();
            RefreshGameDetails(!_usingMyGames);
            RefreshConvenienceTiles();
            QueueArtwork();
        }
    }

    IEnumerable<GameCandidate> FilterGames(IEnumerable<GameCandidate> games)
    {
        var filter = _libraryFilter.SelectedItem?.ToString() ?? "All games";
        var result = games.Where(game =>
        {
            var hidden = _libraryState.HiddenPaths.Contains(game.ExePath, StringComparer.OrdinalIgnoreCase);
            if (filter == "Hidden" ? !hidden : hidden) return false;
            if (filter == "Installed" && !IsInstalled(game)) return false;
            if (filter == "Favorites" && !_libraryState.FavouritePaths.Contains(game.ExePath, StringComparer.OrdinalIgnoreCase)) return false;
            var profile = _libraryState.ProfileFor(game.ExePath);
            var effective = profile.Apply(game);
            if (_hideIncompatible.Checked && effective.Route == InstallRoute.Unsupported) return false;
            if (_apiFilter.SelectedItem is GraphicsApi api && (profile.ApiOverride is not null ? effective.Api != api : !game.AllApis.Contains(api))) return false;
            if (_storeFilter.SelectedIndex > 0 && StoreFor(game) != _storeFilter.SelectedItem?.ToString()) return false;
            return true;
        });

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
        if (_sortColumn == 7)
            return (_sortAscending ? games.OrderBy(IsInstalled) : games.OrderByDescending(IsInstalled)).ThenBy(x => x.DisplayName, StringComparer.OrdinalIgnoreCase);
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
        item.SubItems.Add(DisplayInstallRoute(game));
        item.SubItems.Add(game.Detection);
        item.SubItems.Add(game.Route == InstallRoute.Unsupported ? "Unsupported route" : GameInstallStatus(game));
        if (_groupByStore.Checked)
        {
            var store = StoreFor(game);
            var group = _games.Groups.Cast<ListViewGroup>().FirstOrDefault(g => g.Header == store);
            if (group is null) { group = new ListViewGroup(store); _games.Groups.Add(group); }
            item.Group = group;
        }
        item.ToolTipText = game.DisplayName + Environment.NewLine + (string.IsNullOrWhiteSpace(game.SuggestedArguments)
            ? game.ExePath
            : game.ExePath + " " + game.SuggestedArguments)
            + (game.CompatibilityWarnings.Count > 0 ? Environment.NewLine + string.Join(Environment.NewLine, game.CompatibilityWarnings) : "");
        item.Tag = game;
        _games.Items.Add(item);
    }

    GameCandidate? SelectedInstallGame()
    {
        var game = SelectedGame();
        return game is null ? null : game with { SuggestedArguments = _launchArguments.Text };
    }

    GameCandidate? SelectedGame()
    {
        var game = SelectedBaseGame();
        if (game is null) return null;
        var effective = _engineChoice.SelectedItem is EngineChoice choice ? game.WithApi(choice.Api) : game;
        var arguments = _libraryState.ProfileFor(game.ExePath).LaunchArguments;
        if (arguments is not null) effective = effective with { SuggestedArguments = arguments };
        return ApplyUiRouteChoices(effective, game);
    }

    GameCandidate? SelectedBaseGame()
    {
        var list = _usingMyGames ? _myGames : _games;
        return list.SelectedItems.Count == 0 ? null : list.SelectedItems[0].Tag as GameCandidate;
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
                _engineChoice.Items.Add(new EngineChoice(game.Api, "Automatic - " + game.DisplayApi));
                foreach (var api in Enum.GetValues<GraphicsApi>().Where(a => a != GraphicsApi.Unknown))
                    _engineChoice.Items.Add(new EngineChoice(api, EngineChoiceLabel(game, api)));

                var selected = 0;
                var apiOverride = _libraryState.ProfileFor(game.ExePath).ApiOverride;
                for (var i = 0; i < _engineChoice.Items.Count; i++)
                {
                    if (i > 0 && apiOverride is not null && _engineChoice.Items[i] is EngineChoice choice && choice.Api == apiOverride)
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
        _engineChoice.Enabled = game is not null && !_busy;
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
        if (_usingMyGames) return;
        if (_games.SelectedItems.Count == 0) return;
        var game = SelectedGame();
        if (game is null) return;
        var item = _games.SelectedItems[0];
        if (item.SubItems.Count > 5)
            item.SubItems[5].Text = DisplayInstallRoute(game);
        item.ToolTipText = game.DisplayName + Environment.NewLine + (string.IsNullOrWhiteSpace(game.SuggestedArguments)
            ? game.ExePath
            : game.ExePath + " " + game.SuggestedArguments);
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
        var needsChoice = game is not null && InstallationChoice.NeedsSelection(SelectedBaseGame() ?? game, game, _engineChoice.SelectedIndex > 0);
        var payloadOk = needsChoice ? InstallationChoice.Options(game!).Any(choice => _payload?.IsReadyFor(choice) == true)
            : installGame is not null && _payload?.IsReadyFor(installGame) == true;
        _install.Enabled = !_preview && !_busy && _downloadCts is null && hasGame && (payloadOk || game!.Route == InstallRoute.Unsupported);
        var pending = hasGame && File.Exists(Path.Combine(game!.Root, "_DLSS5_Compat_Backup", "pending.json"));
        _verify.Enabled = !_preview && !_busy && _downloadCts is null && hasGame && !pending && IsInstalled(game!);
        _restore.Text = pending ? "Undo interrupted" : "Remove install";
        _restore.AccessibleName = pending ? "Undo interrupted installation" : "Remove Phoenix DLSS5";
        _tips.SetToolTip(_restore, pending ? "Undo partial changes and restore recorded originals" : "Remove this Phoenix installation and restore its recorded original files");
        _install.Text = hasGame && IsInstalled(game!) ? "Reinstall" : "Install";
        _install.Tag = hasGame && !IsInstalled(game!) && payloadOk ? "primary" : null;
        _runExe.Tag = hasGame && IsInstalled(game!) ? "primary" : null;
        UiTheme.Apply(_install, _palette); UiTheme.Apply(_runExe, _palette);
        _restore.Enabled = !_preview && !_busy && hasGame && (pending || File.Exists(Path.Combine(game!.Root, "_DLSS5_Compat_Backup", "manifest.json")));
        _runExe.Enabled = !_preview && !_busy && hasGame;
        _openFolder.Enabled = !_preview && !_busy && hasGame;
        _engineChoice.Enabled = hasGame && !_busy;
        RefreshCommandStates();
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

            if (payload is null || payload.IsReadyFor(candidate))
                return candidate;
        }

        return null;
    }

    void SetBusy(bool busy)
    {
        _busy = busy;
        UseWaitCursor = busy;
        foreach (var button in _busyButtons) button.Enabled = !busy;
        foreach (var control in new Control[] { _games, _myGames, _myGamesSearch, _myGamesFavorites, _scanRoot, _search, _libraryFilter, _apiFilter, _storeFilter, _componentFilter, _hideIncompatible, _groupByStore, _detailedColumns }) control.Enabled = !busy;
        UpdateVerificationProgress();
        UpdateButtons();
        RefreshGameDetails(false);
    }

    void Log(string message)
    {
        if (IsDisposed) return;
        if (InvokeRequired)
        {
            BeginInvoke(new Action<string>(Log), message);
            return;
        }

        _log.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}");
        if (_log.TextLength > 128000) _log.Text = _log.Text[^64000..];
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

    int FrameIterations() => (int)_frameIterations.Value;

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
        var width = _games.ClientSize.Width;
        var executableWidth = width < 540 && !_detailedColumns.Checked ? 0 : Math.Clamp(width - 389, 70, 90);
        _games.Columns[0].Width = Math.Max(125, width - executableWidth - 286);
        _games.Columns[1].Width = executableWidth;
        _games.Columns[3].Width = 58;
        _games.Columns[4].Width = 100;
        _games.Columns[2].Width = _detailedColumns.Checked ? 360 : 0;
        _games.Columns[5].Width = _detailedColumns.Checked ? 310 : 0;
        _games.Columns[6].Width = _detailedColumns.Checked ? 150 : 0;
        if (_games.Columns.Count > 7) _games.Columns[7].Width = 128;
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

    sealed class DropLaunchOption(string title, string details, bool supported, GameCandidate? game, string? dosTarget)
    {
        public string Title { get; } = title;
        public string Details { get; } = details;
        public bool Supported { get; } = supported;
        public GameCandidate? Game { get; } = game;
        public string? DosTarget { get; } = dosTarget;
        public override string ToString() => Title + " - " + Details;
    }

    sealed class DropRouteDialog : Form
    {
        readonly ListBox _options = new()
        {
            Dock = DockStyle.Fill,
            DrawMode = DrawMode.OwnerDrawFixed,
            ItemHeight = 34
        };

        public DropLaunchOption? SelectedOption => _options.SelectedItem as DropLaunchOption;

        public DropRouteDialog(string droppedPath, IReadOnlyList<DropLaunchOption> options)
        {
            Text = "Choose temporary launch route";
            Width = 720;
            Height = 300;
            MinimizeBox = false;
            MaximizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;

            var pathLabel = new Label
            {
                Dock = DockStyle.Fill,
                AutoEllipsis = true,
                Text = droppedPath,
                TextAlign = ContentAlignment.MiddleLeft
            };
            var intro = new Label
            {
                Dock = DockStyle.Fill,
                Text = "Choose how to run this target. The app will restore files after the launched process exits.",
                TextAlign = ContentAlignment.MiddleLeft
            };
            var ok = new Button { Text = "Run", DialogResult = DialogResult.OK, Width = 92 };
            var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Width = 92 };
            var buttons = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.RightToLeft,
                WrapContents = false
            };
            buttons.Controls.Add(cancel);
            buttons.Controls.Add(ok);

            _options.DrawItem += DrawOption;
            foreach (var option in options)
                _options.Items.Add(option);
            if (_options.Items.Count > 0)
                _options.SelectedIndex = 0;
            _options.DoubleClick += (_, _) =>
            {
                if (SelectedOption is not null)
                    DialogResult = DialogResult.OK;
            };

            var main = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(12),
                ColumnCount = 1,
                RowCount = 4
            };
            main.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
            main.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
            main.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            main.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
            main.Controls.Add(intro, 0, 0);
            main.Controls.Add(pathLabel, 0, 1);
            main.Controls.Add(_options, 0, 2);
            main.Controls.Add(buttons, 0, 3);
            Controls.Add(main);

            AcceptButton = ok;
            CancelButton = cancel;
        }

        void DrawOption(object? sender, DrawItemEventArgs e)
        {
            e.DrawBackground();
            if (e.Index < 0 || e.Index >= _options.Items.Count)
                return;

            if (_options.Items[e.Index] is not DropLaunchOption option)
                return;

            var selected = (e.State & DrawItemState.Selected) != 0;
            var palette = BackColor == UiPalette.Dark.Background ? UiPalette.Dark : UiPalette.Light;
            var titleColor = !option.Supported && !selected ? palette.Error : palette.Text;
            var detailColor = !option.Supported && !selected ? palette.Error : palette.Muted;
            var titleBounds = new Rectangle(e.Bounds.Left + 6, e.Bounds.Top + 2, e.Bounds.Width - 12, 16);
            var detailBounds = new Rectangle(e.Bounds.Left + 6, e.Bounds.Top + 18, e.Bounds.Width - 12, 14);
            TextRenderer.DrawText(e.Graphics, option.Title, e.Font ?? SystemFonts.DefaultFont, titleBounds, titleColor,
                TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
            TextRenderer.DrawText(e.Graphics, option.Details, e.Font ?? SystemFonts.DefaultFont, detailBounds, detailColor,
                TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
            e.DrawFocusRectangle();
        }
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
        var color = unsupported == true && !selected ? _palette.Error : _palette.Text;
        TextRenderer.DrawText(e.Graphics, choice.ToString(), e.Font ?? Font, e.Bounds, color,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        e.DrawFocusRectangle();
    }
}
