namespace Dlss5CompatApp;

sealed partial class MainForm
{
    readonly Label _phoenixUpdateNotice = new() { Dock = DockStyle.Fill, AutoEllipsis = true, Text = "Phoenix version not checked yet." };
    readonly Label _installedPhoenixNotice = new() { AutoSize = true, Text = "", AccessibleName = "Installed Phoenix version" };
    readonly Button _phoenixPaths = new() { Text = "Phoenix version and binary paths", AutoSize = true, Enabled = false };
    PhoenixInstallationReport? _phoenixInstallation;
    readonly Button _checkPhoenixUpdate = new() { Text = "Check Phoenix", Dock = DockStyle.Fill, AutoSize = true };
    readonly Button _applyPhoenixUpdate = new() { Text = "Update Phoenix", Dock = DockStyle.Fill, AutoSize = true };
    PhoenixUpdateResult? _phoenixUpdate;
    string? _phoenixCheckKey;
    DateTimeOffset _phoenixCheckedAt;
    int _phoenixCheckGeneration, _installedPhoenixGeneration;
    bool _checkingPhoenix;
    CancellationTokenSource? _installedPhoenixCts;
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal Func<string, PhoenixReleaseChannel, CancellationToken, Task<PhoenixUpdateResult>>? PhoenixUpdateCheckOverride { get; set; }

    Control BuildPhoenixUpdateBar()
    {
        var bar = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, Margin = Padding.Empty };
        bar.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bar.Controls.Add(_phoenixUpdateNotice, 0, 0);
        bar.Controls.Add(_checkPhoenixUpdate, 1, 0);
        bar.Controls.Add(_applyPhoenixUpdate, 2, 0);
        _checkPhoenixUpdate.Click += async (_, _) => await RefreshPhoenixUpdateAsync(force: true);
        _applyPhoenixUpdate.Click += async (_, _) => await UsePublishedPhoenixAsync();
        return bar;
    }

    async Task RefreshPhoenixUpdateAsync(bool force = false)
    {
        if (IsDisposed || !IsHandleCreated || (_preview && PhoenixUpdateCheckOverride is null)) return;
        var root = ResolvePayloadPath();
        var channel = _releaseChannel.SelectedIndex == 1 ? PhoenixReleaseChannel.StableOnly : PhoenixReleaseChannel.LatestPublished;
        var key = root + "|" + channel;
        if (_checkingPhoenix && key == _phoenixCheckKey) return;
        if (!force && key == _phoenixCheckKey && DateTimeOffset.UtcNow - _phoenixCheckedAt < TimeSpan.FromMinutes(10)) return;
        var generation = ++_phoenixCheckGeneration;
        _checkingPhoenix = true; _phoenixCheckKey = key;
        _phoenixUpdateNotice.Text = "Checking latest Phoenix release...";
        _checkPhoenixUpdate.Enabled = false;
        try
        {
            var result = await Task.Run(() => PhoenixUpdateCheckOverride is { } check ? check(root, channel, _lifetimeCts.Token) :
                PhoenixUpdateService.CheckAsync(root, channel, _lifetimeCts.Token), _lifetimeCts.Token);
            if (IsDisposed || generation != _phoenixCheckGeneration) return;
            _phoenixUpdate = result; _phoenixCheckedAt = DateTimeOffset.UtcNow;
            UpdateReleaseSummary();
            _phoenixUpdateNotice.Text = result.Message;
            _phoenixUpdateNotice.ForeColor = result.NeedsAttention ? _palette.Error : _palette.Success;
            _tips.SetToolTip(_phoenixUpdateNotice, result.Message);
            Log(result.Message);
            RefreshComponentDetails();
            await RefreshInstalledPhoenixAsync();
        }
        catch (OperationCanceledException) { }
        finally
        {
            if (!IsDisposed && generation == _phoenixCheckGeneration)
            { _checkingPhoenix = false; _checkPhoenixUpdate.Enabled = true; }
        }
    }

    async Task RefreshInstalledPhoenixAsync()
    {
        var generation = ++_installedPhoenixGeneration;
        _installedPhoenixCts?.Cancel();
        var game = SelectedGame();
        _installedPhoenixNotice.Text = "";
        _phoenixInstallation = null; _phoenixPaths.Enabled = false;
        if (game is null || (_preview && PhoenixUpdateCheckOverride is null)) return;
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token);
        _installedPhoenixCts = cancel;
        _installedPhoenixNotice.Text = "Checking installed Phoenix...";
        try
        {
            var payload = _payload; var latest = _phoenixUpdate;
            var report = await Task.Run(() => PhoenixInstallationInfo.InspectAsync(game, payload, latest, cancel.Token), cancel.Token);
            if (IsDisposed || generation != _installedPhoenixGeneration) return;
            _phoenixInstallation = report; _phoenixPaths.Enabled = true;
            _installedPhoenixNotice.Text = report.Summary;
            _tips.SetToolTip(_installedPhoenixNotice, report.Details);
        }
        catch (OperationCanceledException) { }
        finally { if (ReferenceEquals(_installedPhoenixCts, cancel)) _installedPhoenixCts = null; }
    }

    void ShowPhoenixBinaryPaths()
    {
        if (_phoenixInstallation is not { } report) return;
        using var dialog = new Form { Text = "Phoenix version and binary paths", Size = new Size(1000, 650),
            MinimumSize = new Size(650, 400), StartPosition = FormStartPosition.CenterParent, Font = Font };
        dialog.Controls.Add(new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both,
            WordWrap = false, Text = report.Details, AccessibleName = "Installed Phoenix versions, file links and configured dependency paths" });
        UiTheme.Apply(dialog, _palette); dialog.ShowDialog(this);
    }
}
