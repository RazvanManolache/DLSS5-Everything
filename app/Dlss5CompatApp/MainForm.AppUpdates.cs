using System.ComponentModel;
using System.Diagnostics;

namespace Dlss5CompatApp;

sealed partial class MainForm
{
    readonly LinkLabel _appUpdateNotice = new()
    {
        AutoSize = true, MaximumSize = new Size(310, 0), Visible = false,
        Anchor = AnchorStyles.Top | AnchorStyles.Right, Margin = new Padding(12, 10, 0, 0),
        TextAlign = ContentAlignment.MiddleRight, LinkBehavior = LinkBehavior.HoverUnderline,
        AccessibleName = "DLSS5 Everything update available on GitHub", TabStop = true
    };
    readonly System.Windows.Forms.Timer _appUpdateTimer = new() { Interval = 24 * 60 * 60 * 1000 };
    readonly CancellationTokenSource _appUpdateCts = new();
    AppUpdateResult? _appUpdate;
    DateTimeOffset _appCheckedAt;
    bool _checkingAppUpdate;
    bool _appUpdatesDisposed;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal Func<string, CancellationToken, Task<AppUpdateResult>>? AppUpdateCheckOverride { get; set; }

    Control BuildAppUpdateHeader(Control heading)
    {
        var header = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        header.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        header.Controls.Add(heading, 0, 0);
        var notices = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false,
            Dock = DockStyle.Top, Margin = Padding.Empty };
        notices.Controls.Add(_appUpdateNotice); notices.Controls.Add(_componentUpdateNotice);
        header.Controls.Add(notices, 1, 0);
        _appUpdateNotice.LinkClicked += (_, _) => OpenAppUpdatePage();
        _tips.SetToolTip(_appUpdateNotice, "Open release notes and downloads on GitHub. Nothing is installed automatically.");
        return header;
    }

    void InitializeAppUpdates()
    {
        Shown += async (_, _) =>
        {
            if (_preview && AppUpdateCheckOverride is null && CompanionUpdateCheckOverride is null) return;
            _appUpdateTimer.Start();
            await Task.WhenAll(RefreshAppUpdateAsync(), RefreshCompanionUpdatesAsync());
        };
        _appUpdateTimer.Tick += async (_, _) => await Task.WhenAll(RefreshAppUpdateAsync(), RefreshPhoenixUpdateAsync(), RefreshCompanionUpdatesAsync());
        Disposed += (_, _) =>
        {
            if (_appUpdatesDisposed) return;
            _appUpdatesDisposed = true;
            _appUpdateTimer.Stop();
            _appUpdateTimer.Dispose();
            _appUpdateCts.Cancel();
            _lifetimeCts.Cancel();
            // The request owns its token until its asynchronous continuation finishes.
            if (!_checkingAppUpdate) _appUpdateCts.Dispose();
        };
    }

    Task CheckAppUpdateAsync() => RefreshAppUpdateAsync(force: true);

    async Task RefreshAppUpdateAsync(bool force = false)
    {
        if (IsDisposed || Disposing || !IsHandleCreated || _checkingAppUpdate
            || (_preview && AppUpdateCheckOverride is null)) return;
        if (!force && DateTimeOffset.UtcNow - _appCheckedAt < TimeSpan.FromDays(1)) return;
        _checkingAppUpdate = true;
        _updateStatus.Text = "Checking Everything updates...";
        try
        {
            var check = AppUpdateCheckOverride;
            var token = _appUpdateCts.Token;
            var result = await Task.Run(() => check is null
                ? AppUpdateService.CheckInstalledAppAsync(AppUpdateService.CurrentVersion, token)
                : check(AppUpdateService.CurrentVersion, token), token);
            if (IsDisposed || Disposing || token.IsCancellationRequested) return;
            _appCheckedAt = DateTimeOffset.UtcNow;
            _updateStatus.Text = result.Newer ? $"Everything {result.LatestVersion} is available."
                : result.CheckSucceeded ? $"Everything {AppUpdateService.CurrentVersion} is up to date."
                : result.Status is AppUpdateStatus.Unverified or AppUpdateStatus.NeedsRepair ? "Everything: " + result.Error
                : "Couldn't check Everything updates. Try again later.";
            // An offline retry must not erase an update that was already discovered.
            if (result.CheckSucceeded || result.Status is AppUpdateStatus.Unverified or AppUpdateStatus.NeedsRepair || _appUpdate is null) _appUpdate = result;
            _appUpdateNotice.Visible = _appUpdate?.Newer == true || _appUpdate?.Status == AppUpdateStatus.NeedsRepair;
            _appUpdateNotice.Text = _appUpdateNotice.Visible ? (_appUpdate!.Status == AppUpdateStatus.NeedsRepair
                ? "Everything files differ — update / repair  ↗" : $"Everything update available: {_appUpdate.LatestVersion}  ↗") : "";
            StyleAppUpdateNotice();
            UpdateReleaseSummary();
        }
        catch (OperationCanceledException) { }
        finally
        {
            _checkingAppUpdate = false;
            if (IsDisposed) _appUpdateCts.Dispose();
        }
    }

    void StyleAppUpdateNotice()
    {
        _appUpdateNotice.LinkColor = _palette.Accent;
        _appUpdateNotice.ActiveLinkColor = _palette.Accent;
        _appUpdateNotice.VisitedLinkColor = _palette.Accent;
    }

    void OpenAppUpdatePage()
    {
        if ((_appUpdate?.Newer != true && _appUpdate?.Status != AppUpdateStatus.NeedsRepair) || IsDisposed) return;
        try
        {
            StartExternalProcess(new ProcessStartInfo(AppUpdateService.ReleasesUrl.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException)
        { SetOutcome("Couldn't open GitHub: " + error.Message); }
    }
}
