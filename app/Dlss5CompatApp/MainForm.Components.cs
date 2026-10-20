namespace Dlss5CompatApp;

sealed partial class MainForm
{
    async Task UsePublishedPhoenixAsync()
    {
        if (_preview || _busy || _downloadCts is not null) return;
        if (MessageBox.Show(this, "Replace the imported Phoenix DLSS5 build with the latest release in the selected channel? This changes the component library, not installed games.", "Use published Phoenix DLSS5", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        await BootstrapPayloadAsync(replaceLocalDevelopment: true);
    }

    async Task DiscoverLibraryAsync()
    {
        if (_busy) return;
        _scanCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token);
        SetBusy(true);
        SetOutcome("Finding installed games...");
        try
        {
            var result = await LibraryDiscovery.DiscoverAsync(_libraryState, progress: new Progress<string>(Log), cancellationToken: _scanCts.Token);
            foreach (var entry in result.Games)
            {
                var game = entry.DetectedGame;
                _allGames.RemoveAll(g => g.ExePath.Equals(game.ExePath, StringComparison.OrdinalIgnoreCase));
                _allGames.Add(game);
                _stores[game.ExePath] = entry.Store;
                _libraryState.GameStores[game.ExePath] = entry.Store;
            }
            foreach (var warning in result.Warnings) Log(warning);
            _libraryState.CachedGamePaths = _allGames.Select(g => g.ExePath).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            SaveLibrary();
            RenderGameRows();
            Log($"Discovery complete: {result.Games.Count} games.");
            SetOutcome($"Discovery completed: {result.Games.Count} games found.");
        }
        catch (OperationCanceledException) { Log("Discovery cancelled."); SetOutcome("Discovery cancelled. Existing library kept."); }
        catch (Exception ex) { Log("Discovery failed: " + ex.Message); SetOutcome("Discovery failed: " + ex.Message); }
        finally { _scanCts?.Dispose(); _scanCts = null; if (!IsDisposed) SetBusy(false); }
    }

    async void ImportPhoenix()
    {
        if (_busy || _downloadCts is not null || _preview) return;
        using var dialog = new OpenFileDialog { Title = "Select local Phoenix DLSS5 build", Filter = "Phoenix DLSS5 add-on|phoenix.addon64|64-bit add-ons|*.addon64" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        SetBusy(true);
        try
        {
            var imported = await PhoenixReleaseDownloader.ImportLocalAsync(dialog.FileName, ResolvePayloadPath(), _lifetimeCts.Token);
            Log("Imported local Phoenix DLSS5 development build: " + imported.Sha256);
            RefreshPayload();
            SetOutcome("Phoenix DLSS5 build imported. Installed games are updated when you choose Install or Reinstall.");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Phoenix DLSS5 import failed", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        finally { if (!IsDisposed) SetBusy(false); }
    }

    async void ImportModel()
    {
        if (_busy || _downloadCts is not null || _preview) return;
        using var dialog = new OpenFileDialog { Title = "Import model or NGX DLL", Filter = "NVIDIA DLSS5 model / NGX DLLs|nvngx_dlssnr.dll;nvngx_dlss.dll;nvngx_dlssg.dll", Multiselect = true };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        SetBusy(true);
        try
        {
            foreach (var file in dialog.FileNames) await PhoenixReleaseDownloader.ImportLocalModelAsync(file, ResolvePayloadPath(), _lifetimeCts.Token);
            _modelPath = Path.Combine(ResolvePayloadPath(), "nvngx_dlssnr.dll");
            RefreshPayload();
            Log("Model DLL imported. Phoenix DLSS5 validates model compatibility when loaded.");
            SetOutcome("Model DLL imported. Compatibility is checked when Phoenix loads it.");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Model import failed", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        finally { if (!IsDisposed) SetBusy(false); }
    }

    void CancelDownloads() => _downloadCts?.Cancel();

    void RefreshComponentDetails()
    {
        RefreshRequirementCards();
        if (_payload is null) return;
        var buildWarning = PhoenixBuildWarning(_payload.PhoenixAddon);
        var lines = new List<string>
        {
            "PHOENIX DLSS5",
            "Source: https://github.com/RazvanManolache/dlss5-phoenix-addon/releases",
            "Add-on: " + (_payload.PhoenixAddon is null ? "Missing" : "Available"),
            "Model weights: " + (_payload.HasCoreDlss ? "NVIDIA DLL available (runtime validation required)" : _payload.HasPhoenixModel ? "Shared native model export available" : "Import a model DLL or download a native model export"),
            "Direct feeder API: " + (_payload.HasPhoenixFeederApi ? "Exports present (host checks ABI 1)" : "Unavailable - import a direct-feeder-compatible addon"),
            "NGX SR (nvngx_dlss.dll): Not required by direct feeder enhancement",
            "Native NR comparison: Optional; enabled only by the NVIDIA DLSS5 backend",
            "Internal runtime companions: " + RuntimeCompanionSummary(),
            "",
            "COMPATIBILITY",
            "ReShade x86: " + (_payload.HasReShade32 ? "Available" : "Missing"),
            "ReShade x64: " + (_payload.HasReShade64 ? "Available" : "Missing"),
            "DOSBox Enhanced: " + (_payload.DosBoxEnhancedExe is null ? "Missing" : "Available"),
            "d3d8to9: " + (_payload.D3D8To9D3D8 is null ? "Missing" : "Available"),
            "dgVoodoo2: " + (_payload.DgVoodooD3D9 is null ? "Missing" : "Available")
        };
        var manifest = Path.Combine(ResolvePayloadPath(), "phoenix-release.json");
        if (File.Exists(manifest))
        {
            try
            {
                if (new FileInfo(manifest).Length > 1024 * 1024) throw new IOException("Component provenance is too large.");
                using var json = System.Text.Json.JsonDocument.Parse(File.ReadAllText(manifest));
                lines.Add("");
                foreach (var property in json.RootElement.EnumerateObject())
                {
                    if (property.Name.Equals("Version", StringComparison.OrdinalIgnoreCase) || property.Name.Equals("Sha256", StringComparison.OrdinalIgnoreCase))
                        lines.Add(property.Name + ": " + property.Value.ToString());
                }
            }
            catch (IOException ex) { Log(ex.Message); }
            catch (System.Text.Json.JsonException ex) { Log("Component provenance invalid: " + ex.Message); }
        }
        if (buildWarning is not null) lines.InsertRange(0, [buildWarning, ""]);
        _componentDetails.Text = string.Join(Environment.NewLine, lines);
        if (string.IsNullOrEmpty(_operationOutcome))
        {
            _componentMessage.Text = buildWarning ?? (_payload.PhoenixAddon is null ? "Phoenix DLSS5 missing - open Components" : "Phoenix DLSS5 component available");
            _tips.SetToolTip(_componentMessage, _componentMessage.Text);
        }
    }

    string RuntimeCompanionSummary()
    {
        try
        {
            var files = PhoenixRuntimePackage.For(_payload!, CpuArch.X86, verify: false)
                .Concat(PhoenixRuntimePackage.For(_payload!, CpuArch.X64, verify: false)).ToArray();
            var available = new[] { "win-x86", "win-x64", "wow64" }
                .Where(section => files.Any(file => file.RelativePath.StartsWith(section + "/", StringComparison.Ordinal)))
                .Select(section => section == "wow64" ? "WOW64 native (profile-dependent)" : section +
                    (files.Any(file => file.RelativePath == section + "/vulkan/phoenix_vulkan.dll") ? " (CPU/Vulkan)" : " (CPU-only)"))
                .ToArray();
            return available.Length == 0 ? "Not supplied by this addon package; helper remains available"
                : string.Join(", ", available) + " (verified during installation; WOW64 also requires a matching Windows/driver profile)";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or System.Text.Json.JsonException)
        { return "Needs repair: " + error.Message; }
    }

    internal static string? PhoenixBuildWarning(string? addonPath) => PayloadScanner.IsPhoenixAddon(addonPath) &&
        !PayloadScanner.HasPhoenixFeederApi(addonPath)
        ? PayloadScanner.FeederApiRemediation
        : null;

    string CustomAddonRoot => Path.Combine(ResolvePayloadPath(), "CustomAddons");

    void ImportAddon()
    {
        if (_preview || _busy || _downloadCts is not null) return;
        using var dialog = new OpenFileDialog { Title = "Import custom ReShade add-on", Filter = "ReShade add-ons|*.addon32;*.addon64" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            var name = Path.GetFileName(dialog.FileName);
            if (name.StartsWith("renodx", StringComparison.OrdinalIgnoreCase) || name.StartsWith("phoenix", StringComparison.OrdinalIgnoreCase) || name.StartsWith("dlss5-feed", StringComparison.OrdinalIgnoreCase) || name.StartsWith("dlss5-bridge", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Use Components for managed rendering add-ons.");
            var arch = PeReader.GetArchitecture(dialog.FileName);
            var expected = name.EndsWith(".addon64", StringComparison.OrdinalIgnoreCase) ? CpuArch.X64 : CpuArch.X86;
            if (arch != expected) throw new InvalidDataException("The add-on architecture does not match its extension.");
            Directory.CreateDirectory(CustomAddonRoot);
            var destination = Path.Combine(CustomAddonRoot, name);
            if (!Path.GetFullPath(dialog.FileName).Equals(destination, StringComparison.OrdinalIgnoreCase)) File.Copy(dialog.FileName, destination, true);
            RefreshAddons();
            RefreshPayload();
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Add-on import failed", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }

    void RefreshAddons()
    {
        _addonList.Items.Clear();
        if (!Directory.Exists(CustomAddonRoot)) return;
        foreach (var path in Directory.EnumerateFiles(CustomAddonRoot))
        {
            var disabled = path.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase);
            var name = disabled ? Path.GetFileName(path)[..^9] : Path.GetFileName(path);
            if (!name.EndsWith(".addon32", StringComparison.OrdinalIgnoreCase) && !name.EndsWith(".addon64", StringComparison.OrdinalIgnoreCase)) continue;
            var item = new ListViewItem(name) { Tag = path };
            item.SubItems.Add(PeReader.GetArchitecture(path).ToString());
            item.SubItems.Add(disabled ? "Disabled" : "Enabled");
            _addonList.Items.Add(item);
        }
    }

    void ToggleAddon()
    {
        if (_preview || _busy || _downloadCts is not null) return;
        if (_addonList.SelectedItems.Count == 0 || _addonList.SelectedItems[0].Tag is not string path) return;
        try
        {
            File.Move(path, path.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase) ? path[..^9] : path + ".disabled");
            RefreshAddons();
            RefreshPayload();
        }
        catch (Exception ex) { Log("Could not change add-on state: " + ex.Message); }
    }

    void RemoveAddon()
    {
        if (_preview || _busy || _downloadCts is not null) return;
        if (_addonList.SelectedItems.Count == 0 || _addonList.SelectedItems[0].Tag is not string path) return;
        if (MessageBox.Show(this, "Remove this custom add-on from the component library?", "Remove add-on", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        try { File.Delete(path); RefreshAddons(); RefreshPayload(); }
        catch (Exception ex) { Log("Could not remove add-on: " + ex.Message); }
    }

    void EditPhoenixSettings(string tab = "Rendering")
    {
        if (SelectedGame() is not { } game || _preview) return;
        var gameDir = Path.GetDirectoryName(game.ExePath)!;
        var hostDir = game.Api == GraphicsApi.OpenXr
            ? Path.Combine(gameDir, "_DLSS5_OpenXR", "host64") : Path.Combine(gameDir, "host64");
        var directory = File.Exists(Path.Combine(gameDir, PhoenixConfiguration.FileName)) ? gameDir : hostDir;
        if (!File.Exists(Path.Combine(directory, PhoenixConfiguration.FileName)))
        {
            MessageBox.Show(this, "Install Phoenix DLSS5 for this game first.", "Phoenix DLSS5 controls", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        try { using var editor = new PhoenixLiveForm(directory, _palette, tab); editor.ShowDialog(this); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Phoenix DLSS5 controls", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }
}
