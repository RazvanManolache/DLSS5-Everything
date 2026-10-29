using System.Runtime.InteropServices;

namespace Dlss5CompatApp;

sealed partial class MainForm
{
    GameArtwork? _artworkService;
    readonly Dictionary<string, string> _automaticArtwork = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> _artworkAttempted = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, string> _fallbackArtwork = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> _fallbackArtworkAttempted = new(StringComparer.OrdinalIgnoreCase);
    bool _fallbackArtworkLoading;
    bool _artworkLoading;
    bool _retryMissingArtwork;
    CancellationTokenSource? _artworkCts;

    string? PrimaryArtworkFor(GameCandidate game) => new[]
    {
        _libraryState.ProfileFor(game.ExePath).ArtworkPath,
        _automaticArtwork.GetValueOrDefault(game.ExePath)
    }.FirstOrDefault(path => path is not null && File.Exists(path));

    string? ArtworkFor(GameCandidate game) => PrimaryArtworkFor(game) ?? _fallbackArtwork.GetValueOrDefault(game.ExePath);

    IEnumerable<GameCandidate> VisibleArtworkGames() => new[] { SelectedBaseGame() }.OfType<GameCandidate>()
        .Concat(_appPreferences.UseTiles
            ? (_usingMyGames ? _myGames : _games).Items.Cast<ListViewItem>().Select(row => row.Tag).OfType<GameCandidate>()
            : []).DistinctBy(game => game.ExePath, StringComparer.OrdinalIgnoreCase);

    async void QueueLocalArtwork()
    {
        if ((_preview && !_previewArtwork) || _fallbackArtworkLoading || !_convenienceInitialized || IsDisposed || _lifetimeCts.IsCancellationRequested) return;
        _fallbackArtworkLoading = true;
        var changed = false;
        try
        {
            _artworkService ??= new GameArtwork(cache: _preview ? Path.Combine(Path.GetTempPath(), "DLSS5-artwork-preview") : null);
            while (!IsDisposed && !_lifetimeCts.IsCancellationRequested)
            {
                var game = VisibleArtworkGames().FirstOrDefault(candidate => PrimaryArtworkFor(candidate) is null &&
                    !_fallbackArtworkAttempted.Contains(candidate.ExePath));
                if (game is null) break;
                _fallbackArtworkAttempted.Add(game.ExePath);
                var result = await _artworkService.GetLocalFallbackAsync(game, _lifetimeCts.Token);
                if (IsDisposed || _lifetimeCts.IsCancellationRequested) break;
                if (result is null) continue;
                _fallbackArtwork[game.ExePath] = result.Path;
                changed = true;
                if (SelectedBaseGame()?.ExePath == game.ExePath) ShowArtwork(ArtworkFor(game));
                // Paint batches rather than rebuilding every poster for each icon.
                if (_fallbackArtworkAttempted.Count % 8 == 0) { InvalidateArtworkTiles(); changed = false; }
            }
        }
        catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested) { }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or ExternalException)
        { Log("Local game artwork unavailable: " + error.Message); }
        finally
        {
            _fallbackArtworkLoading = false;
            if (changed && !IsDisposed && !_lifetimeCts.IsCancellationRequested) InvalidateArtworkTiles();
        }
    }

    Button LibraryMenuButton(string title, string glyph, ContextMenuStrip menu)
    {
        Button? button = null;
        button = Command(title, glyph, () => menu.Show(button!, new Point(0, button!.Height)));
        button.Text = "";
        button.AutoSize = false;
        button.MinimumSize = Size.Empty;
        button.Size = new Size(30, 30);
        button.Margin = new Padding(0, 0, 4, 0);
        button.ImageAlign = ContentAlignment.MiddleCenter;
        button.ContextMenuStrip = menu;
        button.Disposed += (_, _) => menu.Dispose();
        return button;
    }

    void BuildArtworkMenu(FlowLayoutPanel toolbar)
    {
        var menu = new ContextMenuStrip();
        var automatic = new ToolStripMenuItem("Automatic covers from Steam") { CheckOnClick = true };
        automatic.ToolTipText = "Uses Steam's local cache and public artwork service. Only game titles are sent, never executable paths.";
        menu.Opening += (_, _) => automatic.Checked = _appPreferences.AutomaticArtwork;
        automatic.Click += (_, _) =>
        {
            var next = _appPreferences.Copy();
            next.AutomaticArtwork = automatic.Checked;
            if (!TryApplyConveniencePreferences(next, out var error)) { Log(error); SetOutcome("Artwork preference could not be saved: " + error); }
        };
        menu.Items.Add(automatic);
        menu.Items.Add("Retry missing covers", null, (_, _) => { _artworkAttempted.Clear(); _fallbackArtworkAttempted.Clear(); _retryMissingArtwork = true; _artworkCts?.Cancel(); QueueArtwork(); });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Choose cover for selected game", null, (_, _) => ChangeArtwork());
        menu.Items.Add("Use automatic cover for selected game", null, (_, _) =>
        {
            if (SelectedBaseGame() is not { } game || _busy || _preview) return;
            _libraryState.Profiles[game.ExePath] = _libraryState.ProfileFor(game.ExePath) with { ArtworkPath = null };
            SaveLibrary();
            InvalidateArtworkTiles();
            ShowArtwork(ArtworkFor(game));
            QueueArtwork();
        });
        menu.Opening += (_, _) =>
        {
            menu.Items[1].Enabled = (!_preview || _previewArtwork) && _games.Items.Count > 0;
            menu.Items[3].Enabled = CanRunCommand("Change artwork");
            menu.Items[4].Enabled = CanRunCommand("Change artwork") && SelectedBaseGame() is { } game && _libraryState.ProfileFor(game.ExePath).ArtworkPath is not null;
        };
        toolbar.Controls.Add(LibraryMenuButton("Game artwork", "\uE91B", menu));
    }

    async void QueueArtwork()
    {
        QueueLocalArtwork();
        if ((_preview && !_previewArtwork) || _artworkLoading || !_appPreferences.AutomaticArtwork || !_convenienceInitialized || IsDisposed || _lifetimeCts.IsCancellationRequested) return;
        _artworkLoading = true;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token);
        _artworkCts = cancellation;
        var retryMissing = _retryMissingArtwork;
        _retryMissingArtwork = false;
        try
        {
            _artworkService ??= new GameArtwork(cache: _preview ? Path.Combine(Path.GetTempPath(), "DLSS5-artwork-preview") : null);
            while (!IsDisposed && !cancellation.IsCancellationRequested && _appPreferences.AutomaticArtwork)
            {
                // Read the current filtered rows each time: changing filters or selection takes priority.
                var game = VisibleArtworkGames()
                    .FirstOrDefault(candidate => PrimaryArtworkFor(candidate) is null && !_artworkAttempted.Contains(candidate.ExePath));
                if (game is null) break;
                _artworkAttempted.Add(game.ExePath);
                try
                {
                    var result = await _artworkService.GetAsync(game, cancellation.Token, retryMissing);
                    if (IsDisposed || cancellation.IsCancellationRequested) break;
                    if (result is null) continue;
                    _automaticArtwork[game.ExePath] = result.Path;
                    InvalidateArtworkTiles();
                    if (SelectedBaseGame()?.ExePath == game.ExePath) ShowArtwork(ArtworkFor(game));
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { _artworkAttempted.Remove(game.ExePath); break; }
                catch (Exception ex) when (ex is IOException or HttpRequestException or OperationCanceledException or ArgumentException or UnauthorizedAccessException or System.Text.Json.JsonException or OutOfMemoryException or ExternalException)
                { Log("Artwork unavailable for " + game.DisplayName + ": " + ex.Message); }
            }
        }
        finally
        {
            _artworkLoading = false;
            _artworkCts = null;
            if (_retryMissingArtwork && !IsDisposed && !_lifetimeCts.IsCancellationRequested) QueueArtwork();
        }
    }

    void InvalidateArtworkTiles()
    {
        _tileArtworkRevision++;
        RefreshConvenienceTiles();
        ApplyMyGamesView();
    }

    void SetPosterSpacing()
    {
        if (!_games.IsHandleCreated || _games.View != View.LargeIcon) return;
        var scale = _games.DeviceDpi / 96f;
        var width = (int)(184 * scale);
        var height = (int)(310 * scale);
        SendMessage(_games.Handle, 0x1035, IntPtr.Zero, (IntPtr)((height << 16) | width)); // LVM_SETICONSPACING
    }

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);
}
