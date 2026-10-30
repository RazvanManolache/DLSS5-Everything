using System.ComponentModel;
using System.Diagnostics;

namespace Dlss5CompatApp;

sealed partial class MainForm
{
    readonly LinkLabel _componentUpdateNotice = new()
    {
        AutoSize = true, Visible = false, MaximumSize = new Size(310, 0),
        Margin = new Padding(12, 6, 0, 0), AccessibleName = "Component updates available; open Components"
    };
    readonly LinkLabel _everythingReleaseDetail = new() { AutoSize = true, MaximumSize = new Size(850, 0), Text = "Everything: not checked yet." };
    readonly LinkLabel _phoenixReleaseDetail = new() { AutoSize = true, MaximumSize = new Size(850, 0), Text = "Phoenix: not checked yet." };
    readonly Dictionary<CompanionProject, LinkLabel> _companionReleaseDetails = [];
    readonly Dictionary<CompanionProject, CompanionUpdateResult> _companionUpdates = [];
    readonly Button _checkAllReleases = new() { Text = "Check all releases", AutoSize = true };
    readonly TextBox _componentBinaryPaths = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both,
        WordWrap = false, Height = 110, Width = 850, Visible = false, AccessibleName = "Component binary locations" };
    string? _companionCheckKey;
    DateTimeOffset _companionsCheckedAt;
    int _companionCheckGeneration;
    bool _checkingCompanions;
    CancellationTokenSource? _companionCheckCts;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal Func<CompanionProject, string?, PhoenixReleaseChannel, CancellationToken, Task<CompanionUpdateResult>>? CompanionUpdateCheckOverride { get; set; }

    Control BuildReleaseChecks()
    {
        var panel = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1 };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        void Row(Control control)
        {
            panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            control.Margin = new Padding(3, 4, 3, 4);
            panel.Controls.Add(control, 0, panel.RowCount++);
        }
        Row(_everythingReleaseDetail); Row(_phoenixReleaseDetail);
        _everythingReleaseDetail.LinkClicked += (_, _) => OpenReleaseLink(AppUpdateService.ReleasesUrl);
        _phoenixReleaseDetail.LinkClicked += (_, _) => OpenReleaseLink(new Uri("https://github.com/" + PhoenixReleaseDownloader.Repository + "/releases"));
        foreach (var project in Enum.GetValues<CompanionProject>())
        {
            var label = new LinkLabel { AutoSize = true, MaximumSize = new Size(850, 0),
                Text = CompanionUpdateService.Name(project) + ": not checked yet. Open releases ↗",
                AccessibleName = CompanionUpdateService.Name(project) + " version and GitHub releases" };
            _companionReleaseDetails[project] = label;
            label.LinkClicked += (_, _) =>
            {
                OpenReleaseLink(CompanionUpdateService.ReleasesUrl(project));
            };
            Row(label);
        }
        _checkAllReleases.Click += async (_, _) => await CheckAllReleaseUpdatesAsync();
        _componentUpdateNotice.LinkClicked += (_, _) => ShowPage("Components");
        _tips.SetToolTip(_checkAllReleases, "Read GitHub release versions for Everything, Phoenix, DOSBox Enhanced and Video Player. No downloads.");
        Row(_checkAllReleases);
        var paths = new CheckBox { AutoSize = true, Text = "Show component binary paths" };
        paths.CheckedChanged += (_, _) => _componentBinaryPaths.Visible = paths.Checked;
        Row(paths); Row(_componentBinaryPaths);
        Row(new Label { AutoSize = true, MaximumSize = new Size(850, 0),
            Text = "Checks read release metadata only. Open release links for notes/downloads. Updating Phoenix components does not update existing game installations." });
        return panel;
    }

    void OpenReleaseLink(Uri url)
    {
        try { StartExternalProcess(new ProcessStartInfo(url.AbsoluteUri) { UseShellExecute = true }); }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException) { SetOutcome("Couldn't open GitHub: " + error.Message); }
    }

    async Task CheckAllReleaseUpdatesAsync()
    {
        if (IsDisposed || Disposing) return;
        _checkAllReleases.Enabled = false;
        try
        {
            await Task.WhenAll(RefreshAppUpdateAsync(force: true), RefreshPhoenixUpdateAsync(force: true),
                RefreshCompanionUpdatesAsync(force: true));
        }
        finally { if (!IsDisposed) _checkAllReleases.Enabled = true; }
    }

    async Task RefreshCompanionUpdatesAsync(bool force = false)
    {
        if (IsDisposed || Disposing || !IsHandleCreated || (_preview && CompanionUpdateCheckOverride is null)) return;
        var root = ResolvePayloadPath();
        var dos = _payload?.DosBoxEnhancedExe;
        var video = PhoenixMediaPlayer.Find(root);
        var channel = _releaseChannel.SelectedIndex == 1 ? PhoenixReleaseChannel.StableOnly : PhoenixReleaseChannel.LatestPublished;
        var key = root + "|" + dos + "|" + video + "|" + channel + "|" +
            (dos is null ? "" : File.GetLastWriteTimeUtc(dos).Ticks) + "|" + (video is null ? "" : File.GetLastWriteTimeUtc(video).Ticks);
        if (_checkingCompanions && key == _companionCheckKey) return;
        if (!force && key == _companionCheckKey && DateTimeOffset.UtcNow - _companionsCheckedAt < TimeSpan.FromHours(1)) return;
        var changed = key != _companionCheckKey;
        _companionCheckCts?.Cancel();
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token);
        _companionCheckCts = cancel;
        _companionCheckKey = key; _checkingCompanions = true;
        int generation = ++_companionCheckGeneration;
        if (changed) _companionUpdates.Clear();
        var check = CompanionUpdateCheckOverride;
        foreach (var project in Enum.GetValues<CompanionProject>())
            _companionReleaseDetails[project].Text = CompanionUpdateService.Name(project) + ": checking releases...";
        try
        {
            var results = await Task.WhenAll(Enum.GetValues<CompanionProject>().Select(project => Task.Run(() =>
                check is null ? CompanionUpdateService.CheckAsync(project, project == CompanionProject.DosBoxEnhanced ? dos : video,
                    channel, cancel.Token) : check(project, project == CompanionProject.DosBoxEnhanced ? dos : video, channel, cancel.Token), cancel.Token)));
            if (IsDisposed || Disposing || generation != _companionCheckGeneration) return;
            _companionsCheckedAt = DateTimeOffset.UtcNow;
            foreach (var result in results)
            {
                // Preserve an already discovered update when a repeat check is offline.
                if (result.State != CompanionUpdateState.Unavailable || !_companionUpdates.ContainsKey(result.Project))
                    _companionUpdates[result.Project] = result;
                var label = _companionReleaseDetails[result.Project];
                label.Text = result.Message + (result.State == CompanionUpdateState.Unavailable && _companionUpdates[result.Project].NeedsUpdate
                    ? " Last known: " + _companionUpdates[result.Project].Message : "") + " Open releases ↗";
                _tips.SetToolTip(label, result.Message + Environment.NewLine + "Binary: " + (result.BinaryPath ?? "Not installed"));
            }
            UpdateReleaseSummary();
        }
        catch (OperationCanceledException) { }
        finally
        {
            if (generation == _companionCheckGeneration) _checkingCompanions = false;
            if (ReferenceEquals(_companionCheckCts, cancel)) _companionCheckCts = null;
        }
    }

    void UpdateReleaseSummary()
    {
        _everythingReleaseDetail.Text = "Everything " + AppUpdateService.CurrentVersion + ": " +
            (_appUpdate is null ? "not checked yet." : _appUpdate.Newer ? "update available: " + _appUpdate.LatestVersion :
            _appUpdate.CheckSucceeded ? "published files verified." : _appUpdate.Error ?? "couldn't check GitHub.");
        _phoenixReleaseDetail.Text = PhoenixReleaseStatusText();
        _componentBinaryPaths.Text = string.Join(Environment.NewLine, new[] {
            "Everything: " + typeof(MainForm).Assembly.Location,
            "Component library: " + (_payload?.Root ?? "Not selected"),
            "Phoenix x64: " + (_payload?.PhoenixAddon ?? "Not installed"),
            "Phoenix x86: " + (_payload?.PhoenixAddon32 ?? "Not installed") }.Concat(
            Enum.GetValues<CompanionProject>().Select(project => CompanionUpdateService.Name(project) + ": " +
                (_companionUpdates.GetValueOrDefault(project)?.BinaryPath ?? "Not checked / not installed"))));
        int updates = _companionUpdates.Values.Count(result => result.NeedsUpdate)
            + (_phoenixUpdate?.State is PhoenixUpdateState.UpdateAvailable or PhoenixUpdateState.NeedsRepair ? 1 : 0);
        _componentUpdateNotice.Text = updates > 0 ? $"Component updates available ({updates})  ↗" : "";
        _componentUpdateNotice.Visible = updates > 0;
        if (_downloadCts is not null)
        { _componentUpdateNotice.Text = $"Updating components: {_payloadProgress.Value}%  ↗"; _componentUpdateNotice.Visible = true; }
        _componentUpdateNotice.LinkColor = _palette.Accent;
        _componentUpdateNotice.ActiveLinkColor = _palette.Accent;
        _componentUpdateNotice.VisitedLinkColor = _palette.Accent;
        foreach (var label in _companionReleaseDetails.Values.Concat([_everythingReleaseDetail, _phoenixReleaseDetail]))
        { label.LinkColor = _palette.Accent; label.ActiveLinkColor = _palette.Accent; label.VisitedLinkColor = _palette.Accent; }
        UpdateComponentVersions();
    }
}
