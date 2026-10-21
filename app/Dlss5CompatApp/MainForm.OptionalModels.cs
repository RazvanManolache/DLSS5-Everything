namespace Dlss5CompatApp;

sealed partial class MainForm
{
    readonly Dictionary<string, CheckBox> _optionalModelChoices = [];
    readonly CheckBox _tinyModelDownload = new() { AutoSize = true, Text = "Experimental tiny native 2x upscaler — 0.4 MB download" };
    readonly Label _sharedModelStatus = new() { AutoSize = true, MaximumSize = new Size(900, 0) };

    Control BuildOptionalModelChoices()
    {
        var panel = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        panel.Controls.Add(new Label { AutoSize = true, MaximumSize = new Size(900, 0),
            Text = "Optional native models are stored once in the component library. Phoenix lists installed models automatically. These exports use the same Original and Lightweight networks, with full-resolution native processing." });
        foreach (var package in OptionalModelLibrary.Catalog)
        {
            var check = new CheckBox { AutoSize = true, AccessibleName = "Download " + package.Label,
                Text = $"{package.Label} — {package.Files[0].MaxBytes / 1000000.0:F0} MB download",
                Checked = _libraryState.OptionalModelDownloads.Contains(package.Id) };
            _optionalModelChoices[package.Id] = check; panel.Controls.Add(check);
            check.CheckedChanged += (_, _) =>
            {
                if (_preview) return;
                _libraryState.OptionalModelDownloads = SelectedOptionalModels().ToList(); SaveLibrary();
            };
        }
        _tinyModelDownload.Checked = _libraryState.DownloadTinyUpscaler;
        _tinyModelDownload.CheckedChanged += (_, _) => { if (!_preview) { _libraryState.DownloadTinyUpscaler = _tinyModelDownload.Checked; SaveLibrary(); } };
        panel.Controls.Add(_tinyModelDownload);
        panel.Controls.Add(Command("Download selected native models", "\uE896", async () => await DownloadOptionalModelsAsync()));
        panel.Controls.Add(_sharedModelStatus);
        return panel;
    }

    string[] SelectedOptionalModels() => _optionalModelChoices.Where(p => p.Value.Checked).Select(p => p.Key).ToArray();

    void RefreshOptionalModels()
    {
        var installed = OptionalModelLibrary.Installed(OptionalModelLibrary.Root(ResolvePayloadPath()));
        var names = installed.Select(p => p.label).ToList();
        if (TinyModelLibrary.Find(ResolvePayloadPath()) is not null) names.Add("Tiny native 2x upscaler");
        _sharedModelStatus.Text = names.Count == 0 ? "No optional native models installed." : "Installed centrally: " + string.Join(", ", names);
    }

    async Task DownloadOptionalModelsAsync()
    {
        if (_preview || _busy || _downloadCts is not null) return;
        var selected = SelectedOptionalModels();
        if (selected.Length == 0 && !_tinyModelDownload.Checked) { SetOutcome("Select at least one optional native model."); return; }
        _downloadCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token);
        UpdateButtons(); _cancelBackground.Enabled = true;
        try
        {
            var progress = new Progress<BootstrapProgress>(item =>
            {
                if (IsDisposed) return;
                _payloadProgress.Value = Math.Clamp(item.Percent, 0, 100);
                _componentMessage.Text = item.Message;
            });
            await OptionalModelLibrary.EnsureSelectedAsync(ResolvePayloadPath(), selected, progress, _downloadCts.Token);
            if (_tinyModelDownload.Checked) await TinyModelLibrary.EnsureAsync(ResolvePayloadPath(), progress, _downloadCts.Token);
            SetOutcome("Selected native models are ready. Phoenix discovers them through the shared library.");
        }
        catch (OperationCanceledException) { SetOutcome("Model download cancelled. Existing models kept."); }
        catch (Exception ex) { Log("Optional model download failed: " + ex.Message); SetOutcome("Model download failed: " + ex.Message); }
        finally
        {
            _downloadCts.Dispose(); _downloadCts = null;
            if (!IsDisposed) { RefreshPayload(); UpdateButtons(); _cancelBackground.Enabled = _scanCts is not null; }
        }
    }
}
