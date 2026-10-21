namespace Dlss5CompatApp;

internal sealed partial class PhoenixLiveForm
{
    readonly Dictionary<PhoenixSetting, Label> dependencyLabels = [];
    readonly Dictionary<PhoenixSetting, Control> fieldRows = [];
    readonly CheckBox showAdvanced = new() { Text = "Advanced controls and diagnostics", AutoSize = true };
    readonly Label persistenceScope = new() { AutoSize = true, MaximumSize = new Size(900, 0) };
    bool sessionOnly, lastCommandSucceeded;
    string lastCommandPhase = "";
    Button? saveButton;
    TableLayoutPanel[] FreezeFields()
    {
        var parents = fieldRows.Values.Select(row => row.Parent).OfType<TableLayoutPanel>().Distinct().ToArray();
        foreach (var parent in parents) parent.SuspendLayout();
        return parents;
    }
    static void ThawFields(IEnumerable<TableLayoutPanel> parents)
    {
        foreach (var parent in parents) parent.ResumeLayout(true);
    }

    Control ValueHost(PhoenixSettingDefinition definition, Control editor)
    {
        var host = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1, Margin = Padding.Empty };
        host.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        host.Controls.Add(editor, 0, 0);
        if (definition.Key is PhoenixSetting.ModelFolder or PhoenixSetting.ModelDll)
        {
            var browse = new Button { Text = "Browse…", AutoSize = true, AccessibleName = "Browse " + definition.Label };
            browse.Click += (_, _) =>
            {
                if (definition.Key == PhoenixSetting.ModelFolder)
                {
                    using var dialog = new FolderBrowserDialog { Description = "Select a model folder", UseDescriptionForTitle = true };
                    if (dialog.ShowDialog(this) == DialogResult.OK) ((TextBox)editor).Text = dialog.SelectedPath;
                }
                else
                {
                    using var dialog = new OpenFileDialog { Title = "Select a model DLL", Filter = "Model DLL|*.dll" };
                    if (dialog.ShowDialog(this) == DialogResult.OK) ((TextBox)editor).Text = dialog.FileName;
                }
            };
            host.Controls.Add(browse, 0, 1);
            var inspect = new Button { Text = "Check path", AutoSize = true, AccessibleName = "Check " + definition.Label + " path" };
            inspect.Click += (_, _) =>
            {
                var path = ((TextBox)editor).Text;
                try
                {
                    LocalPaths.RequireNoLinks(path);
                    var valid = definition.Key == PhoenixSetting.ModelFolder ? Directory.Exists(path) : PayloadScanner.IsX64Dll(path);
                    outcome.Text = valid ? "Path is readable. Use Validate model in a running game to check model compatibility." : "Choose a readable model folder or an x64 model DLL, as appropriate.";
                }
                catch (Exception error) when (error is IOException or ArgumentException or UnauthorizedAccessException) { outcome.Text = "Path check failed: " + error.Message; }
            };
            host.Controls.Add(inspect, 0, 2);
        }
        var note = new Label { AutoSize = true, MaximumSize = new Size(460, 0), Dock = DockStyle.Top, Tag = "muted" };
        dependencyLabels[definition.Key] = note; host.Controls.Add(note, 0, host.RowCount);
        return host;
    }

    void ConfigureUx(TabControl tabs, FlowLayoutPanel toolbar, FlowLayoutPanel header)
    {
        header.Controls.Add(persistenceScope);
        persistenceScope.Text = sessionOnly
            ? "Capture session only. Advanced changes are temporary; basic strengths return to the capture controls. Save basic settings there for the next capture."
            : "Apply & save includes pending edits and asks the game to remember them. Offline edits are saved for the next launch.";
        toolbar.Controls.Add(showAdvanced);
        var pages = tabs.TabPages.Cast<TabPage>().ToArray();
        void RefreshAdvanced()
        {
            var frozen = FreezeFields();
            try
            {
            foreach (var pair in fieldRows)
            {
                var visible = showAdvanced.Checked || !IsTechnical(pair.Key);
                pair.Value.Visible = visible;
                if (pair.Value.Tag is Control caption) caption.Visible = visible;
            }
            foreach (Control tool in toolbar.Controls)
                if (tool == captureFrames || tool is Label || tool is Button button && button.Text is "Reset history" or "Validate model" or "Capture")
                    tool.Visible = showAdvanced.Checked;
            UpdateGuidance();
            foreach (var page in pages.Where(page => page.Text is "NVIDIA DLSS5" or "Runtime" or "Timing" or "Snapshot"))
                if (showAdvanced.Checked && !tabs.TabPages.Contains(page)) tabs.TabPages.Add(page);
                else if (!showAdvanced.Checked && tabs.TabPages.Contains(page)) tabs.TabPages.Remove(page);
            }
            finally { ThawFields(frozen); }
        }
        showAdvanced.CheckedChanged += (_, _) => RefreshAdvanced(); RefreshAdvanced();
    }

    void UpdateGuidance() => routeGuidance.Text = !availability.ColorOnlyDirectFeed ? "" : showAdvanced.Checked
        ? "Color-only capture: spatial upscaling, pair comparison and GPU-time budgets. Temporal upscalers need engine depth and motion guides."
        : "Color capture · spatial upscaling only. Advanced explains the route limits.";

    static bool IsTechnical(PhoenixSetting key) => key is PhoenixSetting.WorkerEngine or PhoenixSetting.ModelSource or PhoenixSetting.ModelFolder or PhoenixSetting.ModelDll
        or PhoenixSetting.OffloadAgeFrames or PhoenixSetting.OffloadAgeMs or PhoenixSetting.MotionX or PhoenixSetting.MotionY or PhoenixSetting.Depth or PhoenixSetting.HostUnjitter
        or PhoenixSetting.ResetAfterSwitch or PhoenixSetting.ResetEvery or PhoenixSetting.ForceReset or PhoenixSetting.TransformerBlocks or PhoenixSetting.RefinementPasses
        or PhoenixSetting.ExternalReload or PhoenixSetting.LogLevel || key >= PhoenixSetting.Block1Repeats && key <= PhoenixSetting.Block8Repeats;

    static string SettingHelp(PhoenixSetting key) => key switch
    {
        PhoenixSetting.Backend => "Choose the enhancement engine. This is separate from the game's graphics API.",
        PhoenixSetting.ExecutionDevice => "Choose where Phoenix computes the effect. Native uses the optimized NVIDIA rendering GPU path.",
        PhoenixSetting.WorkerEngine => "Advanced implementation choice for an alternate execution device. Automatic is recommended.",
        PhoenixSetting.OverrideStrengths => "Use custom effect strengths instead of the model's default control values.",
        PhoenixSetting.Tone => "Controls the model's local tone strength. 1 is the normal value.",
        PhoenixSetting.Structure => "Controls local structure strength. Compare the result on the same image before saving.",
        PhoenixSetting.CpuThreads => "0 chooses automatically; a positive value limits CPU processing threads.",
        PhoenixSetting.ModelFolder => "A model bundle on disk. Check the path here, then validate loadability in a running Phoenix session.",
        PhoenixSetting.ModelDll => "A separately supplied NVIDIA model DLL. It is read as a model source; it is not bundled with this application.",
        PhoenixSetting.Experimental => "Enables network experiments. Fewer blocks or repeats can reduce cost and alter quality.",
        PhoenixSetting.TransformerBlocks => "Number of enabled central transformer blocks; this is not the number of complete network passes.",
        PhoenixSetting.RefinementPasses => "Whole-network passes. Additional passes increase processing cost.",
        PhoenixSetting.BudgetMs => "Target GPU time for Phoenix processing. The game and transport have additional costs.",
        _ => PhoenixControlContract.Definition(key).Label
    };

    object? Current(PhoenixSetting key)
    {
        try { return EditorValue(key); } catch (ArgumentException) { return null; }
    }
    bool Flag(PhoenixSetting key) => Current(key) is true;
    int Number(PhoenixSetting key) => Current(key) is int value ? value : -1;
    string DependencyReason(PhoenixSetting key)
    {
        var device = Current(PhoenixSetting.ExecutionDevice) as string ?? "native";
        if(key is PhoenixSetting.MotionProvider or PhoenixSetting.FlowAnalysisHeight or PhoenixSetting.MotionDiagnosticView or PhoenixSetting.FlowConsistencyPixels or PhoenixSetting.FlowCostThreshold && !Flag(PhoenixSetting.MotionEstimation))
            return "Enable motion estimation first.";
        if(key is PhoenixSetting.PipelinePriority or PhoenixSetting.MonitoringSampleInterval or PhoenixSetting.MaximumFrameAgeMs or PhoenixSetting.AdjustMotionResolution or PhoenixSetting.AdjustNeuralResolution or PhoenixSetting.AdjustCentralBlocks or PhoenixSetting.AdjustNetworkPasses && Number(PhoenixSetting.PipelineMonitoring)==0)
            return "Enable pipeline monitoring first. Monitoring off performs no adaptive adjustments.";
        if(key==PhoenixSetting.ScalerSharpness&&Number(PhoenixSetting.AdditionalScaler)!=1)
            return "Sharpness belongs to NVIDIA Image Scaling.";
        if (PhoenixControlContract.Definition(key).Group == "NVIDIA DLSS5")
        {
            if (Number(PhoenixSetting.Backend) is not (1 or 2)) return "Select the NVIDIA enhancement engine to use these overrides.";
            if (key != PhoenixSetting.NativeOptions && !Flag(PhoenixSetting.NativeOptions)) return "Enable NVIDIA-only overrides first.";
        }
        if (key is PhoenixSetting.Tone or PhoenixSetting.Structure && !Flag(PhoenixSetting.OverrideStrengths)) return "Enable Override strengths to edit this value.";
        if (key is PhoenixSetting.AutoMask or PhoenixSetting.Skin && !Flag(PhoenixSetting.Advanced)) return "Enable Customize skin / guides first.";
        if (availability.ColorOnlyDirectFeed && key is PhoenixSetting.MotionX or PhoenixSetting.MotionY or PhoenixSetting.Depth or PhoenixSetting.HostUnjitter) return "This capture supplies color only; engine motion/depth guides are unavailable.";
        if (key is PhoenixSetting.ExecutionDevice or PhoenixSetting.WorkerEngine && Number(PhoenixSetting.Backend) is 0 or 1) return "Used by Phoenix enhancement; the selected engine bypasses it.";
        if (key == PhoenixSetting.WorkerEngine && device == "native") return "The optimized native device selects its own execution backend.";
        if (key == PhoenixSetting.CpuThreads && device != "cpu" && device != "sycl:cpu") return "Select a CPU device to set its thread count.";
        if (key is PhoenixSetting.OffloadAgeFrames or PhoenixSetting.OffloadAgeMs && device == "native") return "Freshness limits apply to alternate/offloaded devices.";
        if (key is PhoenixSetting.Upscaler or PhoenixSetting.ResolutionQuality or PhoenixSetting.ResolutionAspect && Number(PhoenixSetting.ResolutionMode) != 1) return "Select Super resolution to use this option.";
        if (key is PhoenixSetting.ResolutionRatioWidth or PhoenixSetting.ResolutionRatioHeight && (Number(PhoenixSetting.ResolutionMode) != 1 || Number(PhoenixSetting.ResolutionAspect) != 5)) return "Select Super resolution and Custom aspect ratio.";
        if (key == PhoenixSetting.ModelFolder && Current(PhoenixSetting.ModelSource) as string != "model_folder") return "Choose Model folder as the model source.";
        if (key == PhoenixSetting.ModelDll && Current(PhoenixSetting.ModelSource) as string != "nvidia_dll") return "Choose NVIDIA model DLL as the model source.";
        if (key is PhoenixSetting.BudgetMs or PhoenixSetting.BudgetMode or PhoenixSetting.MinimumBlocks && !Flag(PhoenixSetting.AutomaticBudget)) return "Enable Automatic budget first.";
        if (key == PhoenixSetting.DesiredFPS && (availability.ColorOnlyDirectFeed || !Flag(PhoenixSetting.AutomaticBudget) || Number(PhoenixSetting.BudgetMode) != 1)) return availability.ColorOnlyDirectFeed ? "Color-only capture uses GPU time, not the game's presentation FPS." : "Select an automatic presentation-FPS budget.";
        if ((key is PhoenixSetting.TransformerBlocks or PhoenixSetting.RefinementPasses || key >= PhoenixSetting.Block1Repeats && key <= PhoenixSetting.Block8Repeats) && !Flag(PhoenixSetting.Experimental)) return "Enable experimental network controls. These can affect speed and quality.";
        return "";
    }

    async Task ApplyAndSaveAsync()
    {
        var hadChanges = dirty.Count != 0;
        if (hadChanges) await ApplyAsync();
        if (sessionOnly || !connected || hadChanges && (dirty.Count != 0 || !lastCommandSucceeded)) return;
        await RunAsync(PhoenixOperation.Save);
        if (lastCommandSucceeded) persistenceScope.Text = lastCommandPhase == "saved"
            ? "Saved for the next game launch." : "Save requested; runtime confirmation is pending.";
    }
}
