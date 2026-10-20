using System.Diagnostics;

namespace Dlss5CompatApp;

sealed partial class MainForm
{
    readonly List<(Button Button, string Command)> _commandButtons = [];
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal Func<ProcessStartInfo, Process?> StartExternalProcess { get; set; } = Process.Start;
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal Action<string> CopyText { get; set; } = Clipboard.SetText;
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal Func<GameCandidate, bool>? InstallEvidence { get; set; }

    void InitializeCommandStates()
    {
        _addonList.SelectedIndexChanged += (_, _) => RefreshCommandStates();
        _historyView.SelectedIndexChanged += (_, _) => RefreshCommandStates();
        _rootsList.SelectedIndexChanged += (_, _) => RefreshCommandStates();
        _hiddenList.SelectedIndexChanged += (_, _) => RefreshCommandStates();
        RefreshCommandStates();
    }

    bool CanRunCommand(string command)
    {
        var idle = !_busy && _downloadCts is null;
        var selected = SelectedGame() is not null;
        return command switch
        {
            "Scan" or "Discover" or "Add EXE" or "Browse game folder" or "Add folder" => idle && !_preview,
            "Scan all folders" => idle && !_preview && _libraryState.ScanRoots.Count > 0,
            "Refresh My Games" or "Clear My Games search" => idle,
            "Remove" => idle && _rootsList.SelectedItem is not null,
            "Show again" => idle && _hiddenList.SelectedItem is not null,
            "Inspect component files" or "Change artwork" => idle && selected && !_preview,
            "Controls" or "Compare" => idle && selected && !_preview && PhoenixSettingsDirectory() is not null,
            "Import Phoenix DLSS5 build" or "Download Phoenix DLSS5" or "Import model / NGX DLL" or "Import add-on" or
                "Set up Vulkan" or "Remove Vulkan reference" or "Download selected native models" => idle && !_preview,
            "Enable / disable" or "Remove from library" => idle && !_preview && _addonList.SelectedItems.Count > 0,
            "Cancel download" => _downloadCts is not null,
            "Copy selected" => _historyView.SelectedItems.Count > 0,
            "Open components" => Directory.Exists(ResolvePayloadPath()),
            _ => true
        };
    }

    string? PhoenixSettingsDirectory()
    {
        if (SelectedGame() is not { } game) return null;
        var root = Path.GetDirectoryName(game.ExePath)!;
        foreach (var directory in new[] { root, Path.Combine(root, game.Api == GraphicsApi.OpenXr ? "_DLSS5_OpenXR/host64" : "host64") })
            if (File.Exists(Path.Combine(directory, PhoenixConfiguration.FileName))) return directory;
        return null;
    }

    void RefreshCommandStates()
    {
        _commandButtons.RemoveAll(entry => entry.Button.IsDisposed);
        foreach (var (button, command) in _commandButtons) button.Enabled = CanRunCommand(command);
    }
}
