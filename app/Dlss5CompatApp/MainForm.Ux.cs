using System.Diagnostics;

namespace Dlss5CompatApp;

sealed partial class MainForm
{
    readonly Label _installSummary = new() { AutoSize = true, Dock = DockStyle.Top, MaximumSize = new Size(330, 0) };
    readonly Label _verificationProgress = new() { AutoSize = true, Dock = DockStyle.Top, MaximumSize = new Size(330, 0) };
    readonly Button _stopVerification = new() { Text = "Stop check", AutoSize = true, AccessibleName = "Stop verification and close its test game", Visible = false };
    readonly Button _verificationReport = new() { Text = "Verification details", AutoSize = true, Visible = false };
    readonly Stopwatch _verificationElapsed = new();
    readonly System.Windows.Forms.Timer _verificationTimer = new() { Interval = 1000 };
    readonly Label _runtimeRequirement = new() { AutoSize = true, Dock = DockStyle.Fill };
    readonly Label _modelRequirement = new() { AutoSize = true, Dock = DockStyle.Fill };
    readonly Label _optionalRequirement = new() { AutoSize = true, Dock = DockStyle.Fill };
    readonly Dictionary<string, GameVerificationReceipt?> _verificationReceipts = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, string> _verificationReceiptStamps = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, (bool Passed, string Stamp)> _sessionVerification = new(StringComparer.OrdinalIgnoreCase);
    TableLayoutPanel? _shellFrame;
    Panel? _inspectorScroll;
    bool _verificationStopped;
    string? _lastVerificationReport;
    string _verificationPhase = "Preparing the check";

    void InitializeUx()
    {
        _stopVerification.Click += (_, _) => StopVerification();
        _verificationReport.Click += (_, _) =>
        {
            if (SelectedBaseGame() is { } game && VerificationReceipt(game) is { } receipt && File.Exists(receipt.ReportPath))
            {
                using var dialog = new Form { Text = "Verification result", Size = new Size(680, 360), StartPosition = FormStartPosition.CenterParent, Font = Font };
                var summary = new TextBox { Dock = DockStyle.Fill, ReadOnly = true, Multiline = true, ScrollBars = ScrollBars.Vertical,
                    Text = $"{game.DisplayName}\r\n{(receipt.Passed ? "Phoenix output verified" : "Needs attention")} · {receipt.CheckedAt.LocalDateTime:g}\r\n\r\n{receipt.Message}\r\n\r\nThis confirms processing and presentation for the checked session. It does not certify gameplay or visual quality. Changes to the executable or installation invalidate this result." };
                var open = new Button { Text = "Open report folder", Dock = DockStyle.Bottom, Height = 36 };
                open.Click += (_, _) => OpenDirectory(Path.GetDirectoryName(receipt.ReportPath)!);
                dialog.Controls.Add(summary); dialog.Controls.Add(open); UiTheme.Apply(dialog, _palette); dialog.ShowDialog(this);
            }
        };
        _verificationTimer.Tick += (_, _) => UpdateVerificationProgress();
        SizeChanged += (_, _) => ReflowWorkspace();
        DpiChanged += (_, _) => ReflowWorkspace();
        FormClosed += (_, _) => _verificationTimer.Dispose();
        AllowDrop = true;
        WireDropZone(this);
        ReflowWorkspace();
    }

    void ReflowWorkspace()
    {
        if (IsDisposed) return;
        if (_convenienceInitialized) ApplyConvenienceNavigation();
        var compact = ClientSize.Width < 1180 * DeviceDpi / 96;
        var scale = DeviceDpi / 96f;
        if (_discoveryBody is not null) _discoveryBody.ColumnStyles[1].Width = (compact ? 304 : 360) * scale;
        _installSummary.MaximumSize = _verificationProgress.MaximumSize = new Size((int)((compact ? 270 : 326) * scale), 0);
        ResizeGameColumns();
    }

    void StopVerification()
    {
        if (_verificationCts is null) return;
        _verificationStopped = true;
        _stopVerification.Enabled = false;
        _verificationCts.Cancel();
        SetOutcome("Stopping the check and closing its test game. Keeping the installed route.");
        UpdateVerificationProgress();
    }

    void UpdateVerificationProgress()
    {
        if (IsDisposed) return;
        _verificationProgress.Text = _verificationCts is not null
            ? (_verificationStopped ? "Stopping check; waiting for the owned game to close…" :
                $"{_verificationPhase} · {_verificationElapsed.Elapsed.TotalSeconds:0}s elapsed\nThe test game closes when the check finishes.")
            : "Verify & fix runs and closes the game (usually 20–70 seconds per route). It asks before installing a replacement.";
        _stopVerification.Visible = _verificationCts is not null;
        _stopVerification.Enabled = _verificationCts is not null && !_verificationStopped;
        _cancelBackground.Text = _verificationCts is not null ? "Stop check" : "Cancel";
        _cancelBackground.Enabled = _verificationCts is not null || _scanCts is not null || _downloadCts is not null;
    }

    GameVerificationReceipt? VerificationReceipt(GameCandidate game)
    {
        if (_preview) return null;
        var executable = new FileInfo(game.ExePath);
        var manifest = new FileInfo(Path.Combine(game.Root, "_DLSS5_Compat_Backup", "manifest.json"));
        var stamp = executable.Exists && manifest.Exists ? $"{executable.Length}/{executable.LastWriteTimeUtc.Ticks}/{manifest.Length}/{manifest.LastWriteTimeUtc.Ticks}" : "missing";
        if (!_verificationReceipts.TryGetValue(game.ExePath, out var receipt) || _verificationReceiptStamps.GetValueOrDefault(game.ExePath) != stamp)
        {
            receipt = GameVerificationReceipt.ReadCurrent(game);
            _verificationReceipts[game.ExePath] = receipt;
            _verificationReceiptStamps[game.ExePath] = stamp;
        }
        return receipt;
    }

    string GameInstallStatus(GameCandidate game)
    {
        if (!IsInstalled(game)) return "Not installed";
        if (_sessionVerification.TryGetValue(game.ExePath, out var current))
        {
            if (current.Stamp == InstallationStamp(game)) return current.Passed ? "Output verified" : "Needs attention";
            _sessionVerification.Remove(game.ExePath); _runtimeChecks.Remove(game.ExePath);
        }
        return VerificationReceipt(game) is { } receipt ? receipt.Passed ? "Output verified" : "Needs attention" : "Installed · unchecked";
    }

    static string InstallationStamp(GameCandidate game)
    {
        var executable = new FileInfo(game.ExePath); var manifest = new FileInfo(Path.Combine(game.Root, "_DLSS5_Compat_Backup", "manifest.json"));
        return executable.Exists && manifest.Exists ? $"{executable.Length}/{executable.LastWriteTimeUtc.Ticks}/{manifest.Length}/{manifest.LastWriteTimeUtc.Ticks}" : "missing";
    }

    string RememberVerification(GameCandidate game, bool passed, string message, string? report)
    {
        _sessionVerification[game.ExePath] = (passed, InstallationStamp(game));
        if (!_preview && report is not null && File.Exists(report))
            try { GameVerificationReceipt.Save(game, passed, message, report); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
            { Log("Could not remember verification: " + error.Message); message += " Result could not be saved for the next app launch."; }
        _verificationReceipts.Remove(game.ExePath); _verificationReceiptStamps.Remove(game.ExePath);
        _runtimeChecks[game.ExePath] = message;
        return message;
    }

    string EffectiveInstallSummary() =>
        "Future install: " + (_defaultRenderer.SelectedIndex == 1 ? "NVIDIA DLSS5" : "Phoenix DLSS5") +
        " · " + (_defaultNeuralModel.SelectedIndex == 1 ? "Lightweight FP8" : "Original model") + "\n" +
        (_copyNvidiaDll.Checked ? "Model DLL copied into the game." : "Shared model in Components; moving or deleting it can break this installation.");

    void RefreshRequirementCards()
    {
        _runtimeRequirement.Text = _payload?.HasEmbeddedPhoenixRuntime == true && _payload.HasPhoenixFeederApi
            ? "Ready · Phoenix runtime and bundled execution backends" : "Needed · Download Phoenix or import a compatible addon build";
        var model = !string.IsNullOrWhiteSpace(_modelPath) ? _modelPath : _payload?.NvidiaDlls.FirstOrDefault(path => Path.GetFileName(path).Equals("nvngx_dlssnr.dll", StringComparison.OrdinalIgnoreCase));
        _modelRequirement.Text = model is not null && File.Exists(model) ? "Selected · model source (compatibility is checked when loaded)" :
            _payload?.HasPhoenixModel == true ? "Ready · native models installed in the shared library" : "Needed · Import a model DLL or download a native export below";
        _optionalRequirement.Text = "Optional · vendor upscalers, legacy translators, DOS and Vulkan setup depend on the game. They are not required for every installation.";
    }
}
