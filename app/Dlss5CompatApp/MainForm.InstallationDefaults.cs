namespace Dlss5CompatApp;

sealed partial class MainForm
{
    readonly CheckBox _copyNvidiaDll = new() { AutoSize = true, Text = "Copy NVIDIA DLL into each game",
        AccessibleName = "Copy NVIDIA DLL into each game", Checked = true };
    readonly ComboBox _defaultRenderer = new ThemedComboBox { DropDownStyle = ComboBoxStyle.DropDownList,
        Dock = DockStyle.Fill, AccessibleName = "Default renderer" };
    readonly ComboBox _defaultNeuralModel = new ThemedComboBox { DropDownStyle = ComboBoxStyle.DropDownList,
        Dock = DockStyle.Fill, AccessibleName = "Default neural model" };
    readonly ComboBox _defaultExecutionMode = new ThemedComboBox { DropDownStyle = ComboBoxStyle.DropDownList,
        Dock = DockStyle.Fill, AccessibleName = "Default execution mechanism" };

    void InitializeInstallationDefaults()
    {
        var defaults = _libraryState.InstallationDefaults;
        _copyNvidiaDll.Checked = defaults.CopyNvidiaDllToGame;
        _defaultRenderer.SelectedIndex = defaults.Renderer == "nvidia" ? 1 : 0;
        _defaultNeuralModel.SelectedIndex = defaults.ModelId == "small-fp8" ? 1 : 0;
        _defaultExecutionMode.SelectedIndex = PhoenixConfiguration.ExecutionModes.ToList().IndexOf(defaults.ExecutionMode);
        _tips.SetToolTip(_copyNvidiaDll, "Future installs only. Shared DLL references break if the library file is moved or deleted.");
        _tips.SetToolTip(_defaultNeuralModel, "Lightweight FP8 requires a compatible NVIDIA DLL containing that model. Defaults are saved when installing or reinstalling a game.");
        _tips.SetToolTip(_defaultExecutionMode, "Automatic keeps the compatible helper available. Explicit internal mechanisms require matching runtime packages; unsupported selections stop installation before files change.");
        _copyNvidiaDll.CheckedChanged += (_, _) => SaveInstallationDefaults();
        _defaultRenderer.SelectedIndexChanged += (_, _) => SaveInstallationDefaults();
        _defaultNeuralModel.SelectedIndexChanged += (_, _) => SaveInstallationDefaults();
        _defaultExecutionMode.SelectedIndexChanged += (_, _) => SaveInstallationDefaults();
    }

    void SaveInstallationDefaults()
    {
        _libraryState.InstallationDefaults = new() { CopyNvidiaDllToGame = _copyNvidiaDll.Checked,
            Renderer = _defaultRenderer.SelectedIndex == 1 ? "nvidia" : "native",
            ModelId = _defaultNeuralModel.SelectedIndex == 1 ? "small-fp8" : "original",
            ExecutionMode = PhoenixConfiguration.ExecutionModes[Math.Max(0, _defaultExecutionMode.SelectedIndex)] };
        SaveLibrary();
        if (_convenienceInitialized) RefreshGameDetails(false);
    }

    PhoenixSettings GameStartupSettings() => _libraryState.InstallationDefaults.Settings(FrameIterations());
    string? GameInstallModelPath() => string.IsNullOrWhiteSpace(_modelPath) ? null : _modelPath;

    Control DefaultChoiceRow(string label, ComboBox choice)
    {
        var row = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Margin = Padding.Empty };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 170));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row.Controls.Add(Caption(label), 0, 0);
        row.Controls.Add(choice, 1, 0);
        return row;
    }
}
