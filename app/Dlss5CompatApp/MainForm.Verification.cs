namespace Dlss5CompatApp;

partial class MainForm
{
    CancellationTokenSource? _verificationCts;
    bool _verificationCloseRequested;

    async Task VerifySelectedAsync()
    {
        if (_preview || _busy || _downloadCts is not null || SelectedBaseGame() is not { } selected) return;
        _verificationCloseRequested = false;
        _verificationStopped = false;
        SetBusy(true);
        try
        {
            var game = InstallerEngine.InstalledGame(selected);
            var payload = _payload ?? PayloadScanner.Scan(ResolvePayloadPath());
            var attempted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var attempt = 0; attempt < 12; attempt++)
            {
                attempted.Add(InstallationChoice.RouteKey(game));
                var checks = InstallerEngine.CheckPreflight(game, payload);
                if (!checks.CanInstall) { SetOutcome("Verification blocked: " + checks.DisplayText); return; }
                if (checks.RequiresAntiCheatAcknowledgement && MessageBox.Show(this, checks.DisplayText + "\n\nLaunch this game to verify?",
                    "Review verification warnings", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
                var result = await VerifyInstalledGameAsync(game, checks.RequiresAntiCheatAcknowledgement);
                if (_verificationStopped)
                {
                    SetOutcome("Check stopped. The test game is closed; the installed route and previous result are kept.");
                    return;
                }
                var message = result.Passed ? "Phoenix DLSS5 processed and presented output; image quality unverified."
                    : "Runtime verification failed: " + result.Error;
                message = RememberVerification(game, result.Passed, message, _lastVerificationReport);
                RecordHistory(game, HistoryAction.Verify, result.Passed, message);
                SetOutcome(message);
                if (result.Passed || _verificationCloseRequested || _verificationStopped || _lifetimeCts.IsCancellationRequested || IsDisposed) return;
                var choices = InstallationChoice.RepairOptions(game, attempted);
                if (choices.Count == 0 || attempt == 11)
                {
                    SetOutcome(message + " No further untried routes are available; review Diagnostics or the game's launch settings.");
                    return;
                }
                var replacement = ChooseInstallPlan(game, choices, true, result.Error);
                if (replacement is null) { SetOutcome(message + " No replacement installed."); return; }
                await SmokeTestRunner.WaitForPostExitFileReleaseAsync(() => InstallerEngine.CheckPreflight(replacement, payload), Log, _lifetimeCts.Token);
                var startup = GameStartupSettings();
                var replacementChecks = InstallerEngine.CheckPreflight(replacement, payload, phoenixSettings: startup);
                if (!replacementChecks.CanInstall) { SetOutcome("Replacement blocked: " + replacementChecks.DisplayText); return; }
                if (replacementChecks.HasWarnings && MessageBox.Show(this, replacementChecks.DisplayText + "\n\nInstall and verify this route?",
                    "Review installation warnings", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
                SetOutcome("Installing the selected replacement route...");
                var install = await InstallGameFilesAsync(new InstallerEngine(Log), replacement, payload, startup);
                if (!install.Complete)
                {
                    RecordHistory(replacement, HistoryAction.Install, false, install.Warning);
                    SetOutcome("Replacement installation incomplete. Review the blocked files and install again.");
                    MessageBox.Show(this, install.Warning, "Installation incomplete", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                SaveSelectedProfile();
                _runtimeChecks.Remove(game.ExePath);
                RecordHistory(replacement, HistoryAction.Install, true, "User-selected replacement installed; verifying next.");
                game = InstallerEngine.InstalledGame(replacement);
            }
        }
        catch (OperationCanceledException)
        {
            SetOutcome("Check stopped. No replacement route was installed.");
        }
        catch (Exception error)
        {
            SetOutcome("Verification failed: " + error.Message);
            Log("Verification failed: " + error.Message);
        }
        finally { SetBusy(false); }
    }

    // Delay a real exit until verification has closed its owned game and helper.
    // Close-to-tray is handled earlier and does not interrupt the check.
    void VerificationFormClosing(object? sender, FormClosingEventArgs args)
    {
        if (args.Cancel || _verificationCts is null) return;
        args.Cancel = true;
        _verificationCloseRequested = true;
        _verificationCts.Cancel();
    }

    async Task<SmokeTestResult> VerifyInstalledGameAsync(GameCandidate game, bool antiCheatAcknowledged)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token);
        _verificationCts = cancellation;
        _verificationPhase = "Preparing the check";
        _verificationElapsed.Restart(); _verificationTimer.Start(); UpdateVerificationProgress();
        _inspectorScroll?.ScrollControlIntoView(_verificationProgress);
        FormClosing += VerificationFormClosing;
        try
        {
            SetOutcome("Verifying Phoenix DLSS5; the game will close when the check finishes...");
            var reports = Path.Combine(LocalData.DirectoryPath, "verification", Guid.NewGuid().ToString("N"));
            LocalPaths.RequireNoLinks(reports);
            Directory.CreateDirectory(reports);
            var config = new SmokeTestConfig
            {
                PayloadRoot = ResolvePayloadPath(), UpdatePayload = false,
                RestoreBeforeInstall = false, RestoreAfterRun = false, ClearLogsBeforeRun = false,
                AcknowledgeAntiCheatRisk = antiCheatAcknowledged, CaptureScreenshot = true, AutoResolve = false,
                VerifyExistingInstall = true,
                RunSeconds = 70, MinRunSeconds = 20, ReportPath = Path.Combine(reports, "report.json"), Progress = line =>
                {
                    Log(line);
                    if (line.Contains("Started process") || line.Contains("Monitoring process")) _verificationPhase = "Running game; checking output";
                    if (line.Contains("Closing") || line.Contains("Stopping process")) _verificationPhase = "Closing the test game";
                    UpdateVerificationProgress();
                },
                CancellationToken = cancellation.Token,
                Tests = [new SmokeTestTarget
                {
                    Exe = game.ExePath,
                    Arguments = game.SuggestedArguments
                }]
            };
            _lastVerificationReport = config.ReportPath;
            var report = await SmokeTestRunner.RunAsync(config, reports);
            var result = report.Tests.FirstOrDefault() ?? new SmokeTestResult { Error = report.Error ?? "No runtime result." };
            Log("Verification report: " + config.ReportPath);
            if (GameScanner.ScanSingleExe(game.ExePath) is { } refreshed)
            {
                _allGames.RemoveAll(item => item.ExePath.Equals(game.ExePath, StringComparison.OrdinalIgnoreCase));
                _allGames.Add(refreshed);
            }
            return result;
        }
        finally
        {
            FormClosing -= VerificationFormClosing;
            _verificationCts = null;
            _verificationElapsed.Stop(); _verificationTimer.Stop(); UpdateVerificationProgress();
            if (_verificationCloseRequested && !IsDisposed && IsHandleCreated)
                BeginInvoke(new Action(Close));
        }
    }
}
