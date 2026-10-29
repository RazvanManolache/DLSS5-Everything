namespace Dlss5CompatApp;

sealed partial class MainForm
{
    readonly List<ComponentSetupPanel> _componentSetupPanels = [];
    // Allows preview-mode UI tests to exercise the real workflow without a
    // network request, an installed game or writes to personal settings.
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal Func<IProgress<BootstrapProgress>, CancellationToken, Task>? ComponentSetupOverride { get; set; }

    ComponentSetupPanel BuildInlineComponentSetup()
    {
        var panel = new ComponentSetupPanel(async () => await BootstrapPayloadAsync(downloadAll: true), CancelDownloads);
        _componentSetupPanels.Add(panel);
        _tips.SetToolTip(panel.DownloadButton, "Download runtimes and every supported model: Original, Lightweight and Tiny. Progress stays here.");
        return panel;
    }

    void ReportInlineComponentSetup(BootstrapProgress item)
    {
        foreach (var panel in _componentSetupPanels) panel.Report(item);
    }

    void UpdateInlineComponentSetupAvailability()
    {
        foreach (var panel in _componentSetupPanels)
            panel.SetAvailable(!_busy && _downloadCts is null && (!_preview || ComponentSetupOverride is not null));
    }

    void SelectAllDownloadModels()
    {
        foreach (var checkbox in _optionalModelChoices.Values) checkbox.Checked = true;
        _tinyModelDownload.Checked = true;
    }
}
