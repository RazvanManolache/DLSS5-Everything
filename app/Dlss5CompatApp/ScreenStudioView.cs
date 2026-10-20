using System.Globalization;
using System.Runtime.InteropServices;

namespace Dlss5CompatApp;

internal sealed partial class ScreenStudioView : UserControl
{
    readonly Func<PayloadInfo?> payload;
    readonly Func<string> model;
    readonly Func<UiPalette> palette;
    readonly bool preview;
    readonly bool persistPreferences;
    readonly bool cameraOnly;
    readonly string? preferencesPath;
    readonly RadioButton wholeDisplay = new() { Text = "Entire display", AutoSize = true, Checked = true, AccessibleName = "Enhance entire display", Margin = new(0, 8, 24, 0) };
    readonly RadioButton specificWindow = new() { Text = "Window", AutoSize = true, AccessibleName = "Enhance one window", Margin = new(0, 8, 0, 0) };
    readonly Label sourceLabel = Label("Display");
    readonly Dictionary<string, ScreenCaptureSource> selectedTargets = [];
    readonly Panel settingsPanel = new() { Dock = DockStyle.Fill, Visible = false, Name = "ScreenStudioSettings", Margin = Padding.Empty };
    readonly ComboBox source = Choice("Capture source");
    readonly ComboBox comparison = Choice("Comparison mode", "Enhanced", "Input", "Input / enhanced wipe", "Absolute difference");
    readonly NumericUpDown iterations = Number("Frame iterations", 1, 10, 1);
    readonly NumericUpDown fps = Number("Frame limit", 0, 240, 60);
    readonly CheckBox enabled = Toggle("Neural enhancement", true);
    readonly CheckBox residual = Toggle("Preserve full-resolution detail", true);
    readonly CheckBox cursor = Toggle("Capture cursor", true);
    readonly CheckBox mask = Toggle("Automatic skin mask", false);
    readonly CheckBox lens = Toggle("Lens distortion", false);
    readonly CheckBox hdr = Toggle("HDR scRGB transport (restart required)", false);
    readonly NumericUpDown paperWhite = Number("HDR paper white (nits)", 80, 400, 203);
    readonly CheckBox spout = Toggle("Spout GPU sender", false);
    readonly TextBox spoutName = new() { Dock = DockStyle.Fill, MaxLength = 100, AccessibleName = "Spout sender name" };
    readonly Button convert;
    readonly TrackBar scale = Slider("Processing scale", 25, 100, 100);
    readonly TrackBar divider = Slider("Wipe position", 0, 100, 50);
    readonly TrackBar gain = Slider("Difference gain", 1, 64, 4);
    readonly TrackBar strength = Slider("Residual strength", 0, 200, 100);
    readonly TrackBar tone = Slider("Local tone", 0, 200, 100);
    readonly TrackBar structure = Slider("Local structure", 0, 200, 100);
    readonly TrackBar skin = Slider("Skin structure", -100, 200, -100);
    readonly TrackBar fov = Slider("Lens field of view", 30, 160, 110);
    readonly TrackBar vignette = Slider("Vignette", 0, 100, 0);
    readonly TrackBar aberration = Slider("Chromatic aberration", 0, 100, 0);
    readonly TrackBar noise = Slider("Camera noise", 0, 100, 0);
    readonly ComboBox format = Choice("Screenshot format", "PNG", "JPEG");
    readonly TextBox output = new() { Dock = DockStyle.Fill, ReadOnly = true, AccessibleName = "Screenshot folder" };
    readonly Label state = new() { Dock = DockStyle.Fill, Text = "Phoenix DLSS5 is off", AutoEllipsis = true, AccessibleName = "Phoenix DLSS5 status" };
    readonly Label telemetry = new() { Dock = DockStyle.Fill, AutoEllipsis = true, Tag = "muted", AccessibleName = "Capture telemetry" };
    readonly Label notice = new() { Dock = DockStyle.Fill, AutoEllipsis = true, Tag = "muted", Text = "" };
    readonly Label pending = new() { Dock = DockStyle.Fill, AutoEllipsis = true, Tag = "muted" };
    readonly TextBox presetName = new() { Dock = DockStyle.Fill, MaxLength = 60, AccessibleName = "Preset name" };
    readonly ComboBox presets = Choice("Saved presets");
    readonly Dictionary<string, ComboBox> hotkeyEditors = [];
    readonly ToolTip tips = new();
    readonly System.Windows.Forms.Timer timer = new() { Interval = 500 };
    readonly CancellationTokenSource lifetime = new();
    readonly Button start, stop, apply, screenshot, advanced, refresh, settingsButton;
    readonly TabControl tabs;
    ScreenStudioPreferences preferences = new();
    ScreenStudioSession? session;
    ScreenCaptureSource? activeSource;
    ScreenStudioHotkeys? hotkeys;
    ScreenStudioSettings active = new();
    CancellationTokenSource? startupCancellation;
    TaskCompletionSource? startupCompletion;
    long operationId;
    bool loading, busy, starting, stopping, polling, resourcesDisposed;

    internal ScreenStudioView(Func<PayloadInfo?> payload, Func<string> model, Func<UiPalette> palette, bool preview = false, bool persistPreferences = true, bool cameraOnly = false)
    {
        this.payload = payload; this.model = model; this.palette = palette; this.preview = preview; this.persistPreferences = persistPreferences;
        this.cameraOnly = cameraOnly;
        preferencesPath = cameraOnly ? Path.Combine(LocalData.DirectoryPath, "camera-studio.json") : null;
        Dock = DockStyle.Fill; Name = cameraOnly ? "CameraStudio" : "ScreenStudio";
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 9, Margin = Padding.Empty };
        layout.ColumnStyles.Add(new(SizeType.Percent, 100));
        foreach (var height in new[] { 36, 40 }) layout.RowStyles.Add(new(SizeType.Absolute, height));
        layout.RowStyles.Add(new(SizeType.AutoSize));
        foreach (var height in new[] { 30, 28, 30 }) layout.RowStyles.Add(new(SizeType.Absolute, height));
        layout.RowStyles.Add(new(SizeType.AutoSize));
        layout.RowStyles.Add(new(SizeType.Percent, 100));
        layout.RowStyles.Add(new(SizeType.Absolute, 28));
        Controls.Add(layout);
        var scope = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Margin = Padding.Empty };
        if (cameraOnly) BuildCameraScope(scope); else scope.Controls.AddRange([wholeDisplay, specificWindow]); layout.Controls.Add(scope, 0, 0);
        if (cameraOnly) layout.RowStyles[0] = new RowStyle(SizeType.AutoSize);
        var selection = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, Margin = Padding.Empty };
        selection.ColumnStyles.Add(new(SizeType.Absolute, 110)); selection.ColumnStyles.Add(new(SizeType.Percent, 100)); selection.ColumnStyles.Add(new(SizeType.Absolute, 44));
        selection.Controls.Add(sourceLabel, 0, 0); selection.Controls.Add(source, 1, 0);
        refresh = Command("Refresh sources", "\uE72C", () => { RefreshSources(); return Task.CompletedTask; });
        refresh.Text = ""; refresh.AutoSize = false; refresh.MinimumSize = new(32, 32); refresh.Dock = DockStyle.Fill;
        selection.Controls.Add(refresh, 2, 0);
        layout.Controls.Add(selection, 0, 1);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = true, AutoScroll = false, Margin = Padding.Empty };
        start = Command("Enable Phoenix DLSS5", "\uE768", StartViewingAsync); start.Tag = "primary";
        stop = Command("Turn off", "\uE71A", StopAsync);
        settingsButton = Command("Advanced settings", "\uE713", () => { ToggleSettings(); return Task.CompletedTask; });
        apply = Command("Apply & save", "\uE73E", ApplyAsync);
        screenshot = Command("Screenshot", "\uE722", ScreenshotAsync);
        advanced = Command("Phoenix DLSS5 controls", "\uE9E9", AdvancedAsync);
        actions.Controls.AddRange([start, stop, screenshot, apply, settingsButton]); layout.Controls.Add(actions, 0, 2);
        layout.Controls.Add(state, 0, 3);
        layout.Controls.Add(telemetry, 0, 4);
        layout.Controls.Add(notice, 0, 5);
        layout.Controls.Add(BuildEverydayControls(), 0, 6);
        layout.Controls.Add(settingsPanel, 0, 7);
        layout.Controls.Add(pending, 0, 8);
        var settingsLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Margin = Padding.Empty };
        settingsLayout.ColumnStyles.Add(new(SizeType.Percent, 100));
        settingsLayout.RowStyles.Add(new(SizeType.Absolute, 40));
        settingsLayout.RowStyles.Add(new(SizeType.Absolute, 28));
        settingsLayout.RowStyles.Add(new(SizeType.Percent, 100));
        settingsLayout.RowStyles.Add(new(SizeType.Absolute, 28));
        settingsPanel.Controls.Add(settingsLayout);
        var tools = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, AutoScroll = true, Margin = Padding.Empty };
        tools.Controls.Add(advanced); settingsLayout.Controls.Add(tools, 0, 0);
        settingsLayout.Controls.Add(new Label { Text = "Basic settings are saved for the next capture. Advanced Phoenix controls are session-only.", AutoSize = true, Dock = DockStyle.Fill }, 0, 1);

        tabs = new ThemedTabControl { Dock = DockStyle.Fill, Name = "ScreenStudioTabs" };
        var rendering = Fields(tabs, "Rendering");
        Field(rendering, "Enhancement", enabled); Field(rendering, "View", comparison);
        Field(rendering, "Processing scale", ScaleEditor(scale, v => v + "%"));
        Field(rendering, "Frame iterations", iterations); Field(rendering, "Frame cap (0 = unlimited)", fps);
        Field(rendering, "Local tone", ScaleEditor(tone, Decimal)); Field(rendering, "Local structure", ScaleEditor(structure, Decimal));
        Field(rendering, "Skin structure", ScaleEditor(skin, v => v == -100 ? "Auto" : Decimal(v))); Field(rendering, "Mask", mask);
        Field(rendering, "Detail composition", residual); Field(rendering, "Residual strength", ScaleEditor(strength, Decimal));
        var compare = Fields(tabs, "Comparison"); Field(compare, "View", ComparisonMirror());
        Field(compare, "Wipe position", ScaleEditor(divider, v => v + "%")); Field(compare, "Difference gain", ScaleEditor(gain, v => v + "x"));
        var camera = Fields(tabs, "Camera"); Field(camera, "Lens", lens); Field(camera, "Field of view", ScaleEditor(fov, v => v + " deg"));
        Field(camera, "Vignette", ScaleEditor(vignette, Decimal)); Field(camera, "Chromatic aberration", ScaleEditor(aberration, Decimal)); Field(camera, "Noise", ScaleEditor(noise, Decimal));
        var media = Fields(tabs, "Capture"); Field(media, "Cursor (restart required)", cursor); Field(media, "Screenshot format", format);
        Field(media, "Screenshot folder", FolderEditor(output));
        var outputs = Fields(tabs, "Outputs");
        Field(outputs, "HDR transport", hdr); Field(outputs, "Paper white (nits)", paperWhite);
        Field(outputs, "GPU sharing", spout); Field(outputs, "Sender name", spoutName);
        if (cameraOnly) Field(outputs, "Webcam apps", virtualCamera);
        var mediaFiles = Fields(tabs, "Media");
        convert = Command("Convert image / video", "\uE8D4", ConvertAsync); Field(mediaFiles, "Offline conversion", convert);
        foreach (var (name, key) in new[] { ("Stop", Keys.End), ("Toggle enhancement", Keys.F8), ("Cycle comparison", Keys.F9), ("Screenshot", Keys.F10) })
        {
            var choice = Choice(name + " hotkey"); foreach (var item in ScreenStudioSettings.AllowedHotkeys) choice.Items.Add(item);
            choice.SelectedItem = key; hotkeyEditors[name] = choice; Field(media, "Ctrl + Alt + " + name, choice);
        }
        var saved = Fields(tabs, "Presets"); Field(saved, "Preset", presets); Field(saved, "Name", presetName);
        Field(saved, "", Command("Save preset", "\uE74E", () => { SavePreset(); return Task.CompletedTask; }));
        Field(saved, "", Command("Load preset", "\uE8F1", () => { LoadPreset(); return Task.CompletedTask; }));
        Field(saved, "", Command("Delete preset", "\uE74D", () => { DeletePreset(); return Task.CompletedTask; }));
        Field(saved, "", Command("Reset settings", "\uE777", () => { LoadSettings(new()); return Task.CompletedTask; }));
        settingsLayout.Controls.Add(tabs, 0, 2);
        tips.SetToolTip(scale, "100% is native 1:1. Lower values process a smaller image and compose its changes onto the full-resolution capture; no DLSS SR hook is used.");
        tips.SetToolTip(residual, "Adds the processed-minus-input residual to the full-resolution original, preserving small text and edges at reduced processing sizes.");
        tips.SetToolTip(screenshot, "Save normal, processing-resolution enhanced, and full-size displayed view. This is not a screen grab of the menu.");
        tips.SetToolTip(start, "Show Phoenix DLSS5 enhancement on the selected display or window. No game installation is required.");
        tips.SetToolTip(stop, "Remove the enhanced overlay and stop processing.");
        tips.SetToolTip(advanced, "Advanced Phoenix DLSS5 controls for this capture session. Basic strengths are synchronized on return; other advanced settings are session-only.");
        foreach (var control in Descendants(this))
        {
            if (control is TrackBar bar) bar.ValueChanged += (_, _) => Changed();
            else if (control is NumericUpDown num) num.ValueChanged += (_, _) => Changed();
            else if (control is CheckBox box) box.CheckedChanged += (_, _) => Changed();
            else if (control is ComboBox combo && combo != source && combo != presets) combo.SelectedIndexChanged += (_, _) => Changed();
        }
        output.TextChanged += (_, _) => Changed(); source.SelectedIndexChanged += (_, _) => {
            if (source.SelectedItem is ScreenCaptureSource selected) selectedTargets[selected.Kind] = selected;
            if (cameraOnly) RefreshCameraModeChoices();
            UpdateActions();
        };
        wholeDisplay.CheckedChanged += (_, _) => { if (wholeDisplay.Checked) RefreshSources(); };
        specificWindow.CheckedChanged += (_, _) => { if (specificWindow.Checked) RefreshSources(); };
        spoutName.TextChanged += (_, _) => Changed();
        notice.TextChanged += (_, _) => { notice.Visible = notice.Text.Length > 0; tips.SetToolTip(notice, notice.Text); };
        notice.Visible = false;
        try { if (!preview && persistPreferences) preferences = ScreenStudioPreferences.Load(preferencesPath); }
        catch (Exception error) { SetNotice("Preferences not loaded: " + error.Message, true); }
        if (cameraOnly && (preview || !persistPreferences || !File.Exists(preferencesPath)))
            preferences.Settings = CameraDefaults();
        LoadSettings(preferences.Settings); RefreshSources(); RefreshPresets();
        pending.Text = "Saved for next capture"; telemetry.Text = "Not running · processing and presentation metrics appear here";
        timer.Tick += async (_, _) => await PollAsync(); timer.Start(); InitializeCamera(); UpdateActions();
    }
    void ToggleSettings()
    {
        settingsPanel.Visible = !settingsPanel.Visible;
        everyday.Visible = !settingsPanel.Visible;
        settingsButton.Text = settingsPanel.Visible ? "Basic controls" : "Advanced settings";
        tips.SetToolTip(settingsButton, settingsPanel.Visible ? "Hide advanced settings" : "Show advanced settings");
    }

    async Task StartViewingAsync()
    {
        if (resourcesDisposed || preview || busy || starting || stopping) return;
        var selected = SelectedSource();
        ScreenStudioSession? previousSession = null;
        if (session is { Running: true } && selected is not null && !SameTarget(selected, activeSource))
        {
            previousSession = session;
            await StopAsync();
            if (resourcesDisposed || session is not null) return;
        }
        if (session is { Running: true } current)
        {
            var operation = BeginOperation();
            try
            {
                var next = active with { Enabled = true, Comparison = 0 };
                await current.ApplyAsync(next, lifetime.Token);
                if (!OwnsOperation(current, operation) || !current.Running) return;
                active = next;
                loading = true;
                try { enabled.Checked = true; comparison.SelectedIndex = 0; }
                finally { loading = false; }
                ActivateSelectedWindow(selected);
            }
            catch when (!OwnsOperation(current, operation)) { }
            finally { EndOperation(operation); }
            return;
        }
        // Viewing always starts enhanced; a saved bypass/comparison preset must not look like a failed start.
        loading = true;
        try { enabled.Checked = true; comparison.SelectedIndex = 0; }
        finally { loading = false; }
        await StartAsync();
        if (previousSession?.ShutdownWarning is { } warning) SetNotice("Previous capture: " + warning, true);
        ActivateSelectedWindow(selected);
    }
    void ActivateSelectedWindow(ScreenCaptureSource? selected)
    {
        // Only the explicit Enable gesture can activate the target, and never after the user switched away.
        if (!resourcesDisposed && session?.Running == true && FindForm()?.ContainsFocus == true &&
            selected is { Kind: "window" } && Equals(selected, source.SelectedItem)) ScreenCaptureSources.TryActivate(selected);
    }
    internal static bool SameTarget(ScreenCaptureSource? a, ScreenCaptureSource? b) => a is not null && b is not null &&
        a.Kind == b.Kind && a.Handle == b.Handle && a.ProcessId == b.ProcessId &&
        (a.Kind != "camera" || a.DeviceId == b.DeviceId && a.CaptureWidth == b.CaptureWidth && a.CaptureHeight == b.CaptureHeight && a.CaptureFps == b.CaptureFps);
    Control ComparisonMirror()
    {
        var mirror = Choice("Comparison view", "Enhanced", "Input", "Input / enhanced wipe", "Absolute difference"); mirror.SelectedIndex = 0;
        comparison.SelectedIndexChanged += (_, _) => mirror.SelectedIndex = comparison.SelectedIndex;
        mirror.SelectedIndexChanged += (_, _) => comparison.SelectedIndex = mirror.SelectedIndex; return mirror;
    }
    Control FolderEditor(TextBox path)
    {
        var row = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
        row.ColumnStyles.Add(new(SizeType.Percent, 100)); row.ColumnStyles.Add(new(SizeType.Absolute, 44));
        row.Controls.Add(path, 0, 0);
        var browse = Command("Choose " + path.AccessibleName!.ToLowerInvariant(), "\uE8B7", () => { using var dialog = new FolderBrowserDialog { InitialDirectory = path.Text }; if (dialog.ShowDialog(this) == DialogResult.OK) path.Text = dialog.SelectedPath; return Task.CompletedTask; });
        browse.Text = ""; browse.AutoSize = false; browse.MinimumSize = new(32, 32); browse.Dock = DockStyle.Fill;
        browse.ImageAlign = ContentAlignment.MiddleCenter; browse.Margin = new(4, 0, 0, 0);
        row.Controls.Add(browse, 1, 0);
        return row;
    }
    Task ConvertAsync()
    {
        if (session?.Running == true || starting || stopping) throw new InvalidOperationException("Stop capture before converting files.");
        var components = payload() ?? throw new InvalidOperationException("Import the Phoenix DLSS5 components first.");
        using var dialog = new ScreenMediaConversionForm(ReadSettings(), components, model(), palette());
        dialog.ShowDialog(this); return Task.CompletedTask;
    }
    static string Decimal(int value) => (value / 100f).ToString("0.00", CultureInfo.InvariantCulture);
    static ComboBox Choice(string name, params object[] choices) { var c = new ThemedComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList, AccessibleName = name, Name = name.Replace(" ", "") }; c.Items.AddRange(choices); if (choices.Length > 0) c.SelectedIndex = 0; return c; }
    static NumericUpDown Number(string name, int min, int max, int value) => new ThemedNumericUpDown { Dock = DockStyle.Fill, Minimum = min, Maximum = max, Value = value, AccessibleName = name };
    static CheckBox Toggle(string name, bool value) => new() { Dock = DockStyle.Fill, Text = name, Checked = value, AutoSize = true, AccessibleName = name };
    static TrackBar Slider(string name, int min, int max, int value) => new() { Dock = DockStyle.Fill, Minimum = min, Maximum = max, Value = value, TickStyle = TickStyle.None, AutoSize = false, Height = 30, AccessibleName = name, SmallChange = 1, LargeChange = 10 };
    static Label Label(string text) => new() { Dock = DockStyle.Fill, Text = text, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true };
    static TableLayoutPanel Fields(TabControl tabs, string title)
    {
        var page = new TabPage(title) { AutoScroll = true, Padding = new(12) }; tabs.TabPages.Add(page);
        var fields = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, Margin = Padding.Empty };
        fields.ColumnStyles.Add(new(SizeType.Absolute, 220)); fields.ColumnStyles.Add(new(SizeType.Percent, 100)); page.Controls.Add(fields); return fields;
    }
    static void Field(TableLayoutPanel fields, string name, Control editor)
    {
        var index = fields.RowCount++; fields.RowStyles.Add(new(SizeType.Absolute, 40)); editor.Margin = new(4, 5, 4, 5);
        fields.Controls.Add(Label(name), 0, index); fields.Controls.Add(editor, 1, index);
    }
    static Control ScaleEditor(TrackBar slider, Func<int, string> text)
    {
        var row = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
        row.ColumnStyles.Add(new(SizeType.Percent, 100)); row.ColumnStyles.Add(new(SizeType.Absolute, 65));
        var label = Label(text(slider.Value)); label.TextAlign = ContentAlignment.MiddleRight;
        slider.ValueChanged += (_, _) => label.Text = text(slider.Value); row.Controls.Add(slider, 0, 0); row.Controls.Add(label, 1, 0); return row;
    }
    Button Command(string text, string glyph, Func<Task> action)
    {
        var button = new Button
        {
            Text = text,
            UseMnemonic = false,
            AccessibleName = text,
            AutoSize = true,
            MinimumSize = new(85, 32),
            Height = 34,
            Margin = new(0, 0, 8, 4),
            Image = UiTheme.Icon(glyph, Color.Teal),
            TextImageRelation = TextImageRelation.ImageBeforeText,
            Padding = new(3, 0, 3, 0)
        };
        button.Click += async (_, _) => { try { await action(); } catch (OperationCanceledException) { } catch (Exception error) { ShowError(error); } finally { UpdateActions(); } };
        button.Disposed += (_, _) => button.Image?.Dispose(); tips.SetToolTip(button, text); return button;
    }
    static IEnumerable<Control> Descendants(Control control) => control.Controls.Cast<Control>().SelectMany(child => new[] { child }.Concat(Descendants(child)));
    void Changed() { if (!loading) { RefreshPendingState(); ExplainDependencies(); QueueCameraApply(); } }
    internal ScreenStudioSettings ReadSettings() => new()
    {
        Enabled = enabled.Checked,
        Iterations = (int)iterations.Value,
        Fps = (int)fps.Value,
        Scale = scale.Value / 100f,
        Comparison = comparison.SelectedIndex,
        Divider = divider.Value / 100f,
        DifferenceGain = gain.Value,
        Residual = residual.Checked,
        ResidualStrength = strength.Value / 100f,
        Cursor = cursor.Checked,
        Tone = tone.Value / 100f,
        Structure = structure.Value / 100f,
        Skin = skin.Value / 100f,
        AutoMask = mask.Checked,
        Lens = lens.Checked,
        Fov = fov.Value,
        Vignette = vignette.Value / 100f,
        Aberration = aberration.Value / 100f,
        Noise = noise.Value / 100f,
        Hdr = hdr.Checked, PaperWhite = (float)paperWhite.Value, Spout = spout.Checked, SpoutName = spoutName.Text, VirtualCamera = virtualCamera.Checked,
        ScreenshotDirectory = output.Text,
        ScreenshotFormat = format.SelectedIndex == 0 ? "png" : "jpeg",
        StopKey = (Keys)hotkeyEditors["Stop"].SelectedItem!,
        ToggleKey = (Keys)hotkeyEditors["Toggle enhancement"].SelectedItem!,
        CompareKey = (Keys)hotkeyEditors["Cycle comparison"].SelectedItem!,
        ScreenshotKey = (Keys)hotkeyEditors["Screenshot"].SelectedItem!
    };
    internal void LoadSettings(ScreenStudioSettings value)
    {
        value.Validate(); loading = true;
        try
        {
            enabled.Checked = value.Enabled; iterations.Value = value.Iterations; fps.Value = value.Fps; scale.Value = (int)Math.Round(value.Scale * 100);
            comparison.SelectedIndex = value.Comparison; divider.Value = (int)Math.Round(value.Divider * 100); gain.Value = (int)value.DifferenceGain;
            residual.Checked = value.Residual; strength.Value = (int)Math.Round(value.ResidualStrength * 100); cursor.Checked = value.Cursor;
            tone.Value = (int)Math.Round(value.Tone * 100); structure.Value = (int)Math.Round(value.Structure * 100); skin.Value = (int)Math.Round(value.Skin * 100);
            mask.Checked = value.AutoMask; lens.Checked = value.Lens; fov.Value = (int)value.Fov; vignette.Value = (int)Math.Round(value.Vignette * 100);
            aberration.Value = (int)Math.Round(value.Aberration * 100); noise.Value = (int)Math.Round(value.Noise * 100); output.Text = value.ScreenshotDirectory;
            format.SelectedIndex = value.ScreenshotFormat == "png" ? 0 : 1; hotkeyEditors["Stop"].SelectedItem = value.StopKey;
            hdr.Checked = value.Hdr; paperWhite.Value = (decimal)value.PaperWhite; spout.Checked = value.Spout; spoutName.Text = value.SpoutName; virtualCamera.Checked = value.VirtualCamera;
            hotkeyEditors["Toggle enhancement"].SelectedItem = value.ToggleKey; hotkeyEditors["Cycle comparison"].SelectedItem = value.CompareKey; hotkeyEditors["Screenshot"].SelectedItem = value.ScreenshotKey;
        }
        finally { loading = false; }
        RefreshPendingState();
    }
    void RefreshSources()
    {
        if (cameraOnly) { RefreshCameraSources(); return; }
        var kind = wholeDisplay.Checked ? "monitor" : "window";
        sourceLabel.Text = wholeDisplay.Checked ? "Display" : "Window";
        source.AccessibleName = sourceLabel.Text;
        source.Items.Clear();
        var all = preview ? new ScreenCaptureSource[] { new("monitor", 1, 0, "Display 1 - 2560 x 1440 (primary)"), new("window", 2, 1, "Example window") } : ScreenCaptureSources.Enumerate();
        var entries = all.Where(item => item.Kind == kind).ToArray();
        source.Items.AddRange(entries.Cast<object>().ToArray());
        selectedTargets.TryGetValue(kind, out var previous);
        source.SelectedItem = entries.FirstOrDefault(item => item.Handle == previous?.Handle && item.ProcessId == previous.ProcessId)
            ?? entries.FirstOrDefault(item => item.Name.EndsWith("(primary)", StringComparison.Ordinal)) ?? entries.FirstOrDefault();
        if (session is null && state.Tag is not "error") state.Text = entries.Length == 0
            ? kind == "window" ? "No visible windows" : "No displays available"
            : "Phoenix DLSS5 is off";
        UpdateActions();
    }
    void RefreshPresets() { presets.Items.Clear(); foreach (var name in preferences.Presets.Keys.Order(StringComparer.CurrentCultureIgnoreCase)) presets.Items.Add(name); if (presets.Items.Count > 0) presets.SelectedIndex = 0; }
    void SavePreset()
    {
        var name = presetName.Text.Trim(); if (!ScreenStudioPreferences.ValidName(name)) throw new InvalidDataException("Enter a preset name (1..60 characters).");
        if (!preferences.Presets.ContainsKey(name) && preferences.Presets.Count >= 40) throw new InvalidDataException("Delete a preset before adding another (40 maximum).");
        var value = ReadSettings(); value.Validate();
        var next = new ScreenStudioPreferences { Settings = preferences.Settings, Camera = preferences.Camera, Presets = new(preferences.Presets, StringComparer.OrdinalIgnoreCase) { [name] = value } };
        next.Validate(); if (!preview && persistPreferences) next.Save(preferencesPath); preferences = next; RefreshPresets(); presets.SelectedItem = name; SetNotice("Preset saved. It contains basic capture settings; advanced Phoenix controls are session-only.");
    }
    void LoadPreset() { if (presets.SelectedItem is string name) { LoadSettings(preferences.Presets[name]); presetName.Text = name; } }
    void DeletePreset() { if (presets.SelectedItem is string name) { preferences.Presets.Remove(name); if (!preview && persistPreferences) preferences.Save(preferencesPath); RefreshPresets(); SetNotice("Preset deleted; current settings and On/Off state were kept."); } }
    void SavePreferences(ScreenStudioSettings value)
    {
        preferences.Settings = value;
        if (cameraOnly && SelectedSource() is { } selected)
            preferences.Camera = new(selected.DeviceId, selected.CaptureWidth, selected.CaptureHeight, selected.CaptureFps, DeviceReported: true);
        if (!preview && persistPreferences) preferences.Save(preferencesPath);
    }

    bool OwnsSession(ScreenStudioSession current) => !resourcesDisposed && ReferenceEquals(session, current);
    bool OwnsOperation(ScreenStudioSession current, long operation) => OwnsSession(current) && operationId == operation;
    long BeginOperation()
    {
        busy = true;
        var operation = ++operationId;
        UpdateActions();
        return operation;
    }
    void EndOperation(long operation)
    {
        if (resourcesDisposed || operationId != operation) return;
        busy = false;
        UpdateActions();
    }
    void ShowError(Exception error)
    {
        if (resourcesDisposed) return;
        state.Text = error.Message; state.Tag = "error"; UiTheme.Apply(state, palette());
        tips.SetToolTip(state, error.Message);
    }
    void SetNotice(string text, bool error = false)
    {
        notice.Tag = error ? "error" : "muted"; notice.Text = text;
        UiTheme.Apply(notice, palette());
    }
    void ShowScreenshotRecovery(ScreenStudioSession current)
    {
        if (resourcesDisposed || current.PendingScreenshotRecoveryDirectory is not { } directory || !Directory.Exists(directory)) return;
        if (notice.Tag is "error")
        {
            if (!notice.Text.Contains(directory, StringComparison.OrdinalIgnoreCase)) SetNotice(notice.Text + $" | Recovery: {directory}", true);
        }
        else SetNotice($"Screenshots retained for recovery: {directory}");
        tips.SetToolTip(notice, notice.Text);
    }

    async Task AdvancedAsync()
    {
        if (resourcesDisposed || busy || starting || stopping || session is not { Running: true } current) return;
        var operation = BeginOperation();
        try
        {
            using (var form = new PhoenixLiveForm(current.HostDirectory, palette(), colorOnlyCapture:true, sessionOnly:true)) form.ShowDialog(this);
            if (!OwnsOperation(current, operation) || !current.Running) return;
            var strengths = await current.SynchronizeStrengthsAsync(lifetime.Token);
            if (!OwnsOperation(current, operation) || !current.Running) return;
            var staged = ReadSettings() with { Tone = strengths.Tone, Structure = strengths.Structure, Skin = strengths.Skin, AutoMask = strengths.AutoMask };
            active = active with { Tone = strengths.Tone, Structure = strengths.Structure, Skin = strengths.Skin, AutoMask = strengths.AutoMask };
            LoadSettings(staged);
        }
        catch when (!OwnsOperation(current, operation)) { }
        finally { EndOperation(operation); }
    }

    async Task StartAsync()
    {
        if (resourcesDisposed || preview || busy || starting || stopping || session?.Running == true) return;
        if (SelectedSource() is not ScreenCaptureSource selected) throw new InvalidOperationException("Choose a capture source.");
        var components = payload() ?? throw new InvalidOperationException("Set up Components first.");
        var value = ReadSettings(); value.Validate();
        if (cameraOnly) CameraDevices.Validate(selected, value);
        var modelPath = model();
        session?.Dispose();
        var current = new ScreenStudioSession(diagnosticRoot: persistPreferences ? Path.Combine(LocalData.DirectoryPath, "ScreenDiagnostics") : null)
        {
            ReportShutdownWarning = warning => {
                if (!resourcesDisposed) SetNotice(warning, true);
                else MessageBox.Show(warning, "Capture shutdown warning", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        };
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ScreenStudioHotkeys? registeredKeys = null;
        session = current; startupCancellation = cancellation; startupCompletion = completion; starting = true;
        var operation = BeginOperation();
        try
        {
            active = value; state.Tag = null; state.Text = "Starting Phoenix DLSS5..."; SetNotice("");
            hotkeys?.Dispose(); hotkeys = null;
            hotkeys = registeredKeys = new(value, action => HotkeyAsync(current, action));
            await current.StartAsync(selected, value, components, modelPath, cancellationToken: cancellation.Token);
            if (!OwnsOperation(current, operation)) return;
            activeSource = selected;
            SavePreferences(value); pending.Text = "";
        }
        catch
        {
            if (!OwnsOperation(current, operation)) return;
            registeredKeys?.Dispose();
            if (ReferenceEquals(hotkeys, registeredKeys)) hotkeys = null;
            current.Dispose(); session = null; activeSource = null;
            throw;
        }
        finally
        {
            if (ReferenceEquals(startupCompletion, completion))
            {
                starting = false; startupCompletion = null; startupCancellation = null;
            }
            cancellation.Dispose();
            completion.TrySetResult();
            EndOperation(operation);
        }
    }
    async Task ApplyAsync()
    {
        if (resourcesDisposed || busy || starting || stopping) return; var value = ReadSettings(); value.Validate();
        if (cameraOnly && SelectedSource() is { } cameraSource) CameraDevices.Validate(cameraSource, value);
        if (session is not { Running: true } current) { SavePreferences(value); pending.Text = "Saved for next capture"; return; }
        var operation = BeginOperation();
        try
        {
            if (value.StopKey != active.StopKey || value.ToggleKey != active.ToggleKey || value.CompareKey != active.CompareKey || value.ScreenshotKey != active.ScreenshotKey)
                throw new InvalidOperationException("Stop and restart capture to change hotkeys.");
            await current.ApplyAsync(value, lifetime.Token);
            if (!OwnsOperation(current, operation) || !current.Running) return;
            active = value; SavePreferences(value); pending.Text = "Waiting for worker acknowledgement...";
        }
        catch when (!OwnsOperation(current, operation)) { }
        finally { EndOperation(operation); }
    }
    async Task StopAsync()
    {
        if (resourcesDisposed || stopping || session is not { } current) return;
        var preparation = startupCompletion?.Task;
        stopping = true;
        var operation = BeginOperation();
        hotkeys?.Dispose(); hotkeys = null;
        startupCancellation?.Cancel();
        state.Text = "Turning Phoenix DLSS5 off..."; state.Tag = null;
        try
        {
            // Invalidate older UI work immediately, but drain startup before allowing a replacement.
            try { await current.StopAsync(); }
            catch (IOException error) when (current.WorkerExited) { SetNotice(error.Message, true); }
            finally { if (preparation is not null) await preparation; }
            if (!OwnsOperation(current, operation)) return;
            var status = current.ReadStatus();
            if (status is not null)
            {
                var exported = await Task.Run(() => current.ExportScreenshots(status), lifetime.Token);
                if (!OwnsOperation(current, operation)) return;
                if (exported.Count > 0) SetNotice($"Saved {exported.Count} screenshots to {Path.GetDirectoryName(exported[0])}");
            }
            current.Dispose(); session = null; activeSource = null;
            state.Text = "Phoenix DLSS5 is off"; state.Tag = null; telemetry.Text = "Not running · processing and presentation metrics appear here";
        }
        catch when (!OwnsOperation(current, operation)) { }
        finally
        {
            if (!resourcesDisposed && operationId == operation)
            {
                ShowScreenshotRecovery(current);
                stopping = false;
            }
            EndOperation(operation);
        }
    }
    async Task ScreenshotAsync()
    {
        if (resourcesDisposed || busy || starting || stopping || session is not { Running: true } current) return;
        var operation = BeginOperation();
        try
        {
            await current.RequestScreenshotAsync(lifetime.Token);
            if (OwnsOperation(current, operation) && current.Running) SetNotice("Screenshot requested");
        }
        catch when (!OwnsOperation(current, operation)) { }
        finally { EndOperation(operation); }
    }
    async Task HotkeyAsync(ScreenStudioSession current, int action)
    {
        if (!OwnsSession(current)) return;
        try
        {
            if (action == 0) { await StopAsync(); return; }
            if (busy || starting || stopping || !current.Running) return;
            if (action == 3) { await ScreenshotAsync(); return; }
            if (action is not (1 or 2)) return;
            var operation = BeginOperation();
            try
            {
                // Hotkeys change only active settings, never apply unrelated staged sliders.
                var next = action == 1 ? active with { Enabled = !active.Enabled } : active with { Comparison = (active.Comparison + 1) % 4 };
                await current.ApplyAsync(next, lifetime.Token);
                if (!OwnsOperation(current, operation) || !current.Running) return;
                active = next;
                loading = true; try { if (action == 1) enabled.Checked = next.Enabled; else comparison.SelectedIndex = next.Comparison; } finally { loading = false; }
            }
            catch when (!OwnsOperation(current, operation)) { }
            finally { EndOperation(operation); }
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { if (OwnsSession(current)) ShowError(error); }
    }
    async Task PollAsync()
    {
        if (polling || starting || stopping || session is null || resourcesDisposed) return; polling = true;
        var current = session;
        var operation = operationId;
        try
        {
            var status = current.ReadStatus();
            if (status is not null)
            {
                var background = activeSource is { Kind: "window" } selected && !ScreenCaptureSources.IsForeground(selected);
                state.Text = StatusText(status, active.Enabled, background);
                if (status.State == "running" && active.Enabled && !background && !status.OverlayVisible)
                    state.Text = "Phoenix DLSS5 processed - output is not visible";
                state.Tag = status.State == "failed" ? "error" : null; UiTheme.Apply(state, palette());
                tips.SetToolTip(state, state.Text);
                telemetry.Text = $"{status.Width} x {status.Height}  /  processing {status.ProcessingWidth} x {status.ProcessingHeight}  /  {status.Frames:N0} processed / {status.PresentedFrames:N0} presented  /  {status.AverageFps:0.0} average FPS";
                if (cameraOnly) telemetry.Text += $" / camera {status.CameraFps:0.##} FPS / Spout {(status.Spout ? "on" : "off")} / webcam {(status.VirtualCamera ? "on" : "off")}";
                if (pending.Text == "Waiting for worker acknowledgement..." && status.State == "running" && status.AppliedRevision == current.WorkerRevision) pending.Text = "Applied and saved";
                var exported = await Task.Run(() => current.ExportScreenshots(status), lifetime.Token);
                if (!OwnsOperation(current, operation)) return;
                if (exported.Count > 0) SetNotice($"Saved {exported.Count} screenshots to {Path.GetDirectoryName(exported[0])}");
            }
            if (OwnsOperation(current, operation) && !starting && !stopping && !current.Running) { hotkeys?.Dispose(); hotkeys = null; if (status?.State != "failed") state.Text = "Phoenix DLSS5 is off"; }
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            if (OwnsOperation(current, operation))
            {
                SetNotice(error.Message, true);
                ShowScreenshotRecovery(current);
            }
        }
        finally { polling = false; UpdateActions(); }
    }
    internal static string StatusText(ScreenStudioStatus status, bool enabled, bool background)
    {
        var text = status.State switch {
            "failed" => status.Error,
            "running" => enabled ? "Phoenix DLSS5 on" : "Phoenix DLSS5 bypassed",
            "starting" => "Starting Phoenix DLSS5...",
            "waiting-for-frame" => "Waiting for the first frame...",
            "paused" => "Phoenix DLSS5 paused",
            "stopped" => "Phoenix DLSS5 is off",
            _ => status.State
        };
        if (status.State == "failed") return text;
        if (status.State == "running" && background) text += " - window in background";
        return text;
    }
    void UpdateActions()
    {
        if (resourcesDisposed) return;
        var running = session?.Running == true;
        var idle = !busy && !starting && !stopping;
        var differentTarget = running && SelectedSource() is ScreenCaptureSource selected && !SameTarget(selected, activeSource);
        start.AutoSize = false; start.Width = 215; start.MinimumSize = new(215, 32);
        start.Text = differentTarget ? "Switch target" : cameraOnly ? "Start camera" : "Enable Phoenix DLSS5";
        stop.Text = cameraOnly ? "Stop camera" : "Turn off";
        start.AccessibleName = start.Text;
        start.Enabled = !preview && idle && (!running || differentTarget || !active.Enabled || active.Comparison != 0) && source.SelectedItem is not null;
        stop.Enabled = !stopping && session is not null && (starting || running);
        apply.Enabled = idle; screenshot.Enabled = running && idle; advanced.Enabled = running && idle; refresh.Enabled = idle;
        source.Enabled = wholeDisplay.Enabled = specificWindow.Enabled = idle;
        cursor.Enabled = !cameraOnly && !running && idle;
        cameraMode.Enabled = !running && idle;
        if (checkCameraModes is not null) checkCameraModes.Enabled = !preview && !running && idle && source.SelectedItem is not null;
        tabs.Enabled = idle; everyday.Enabled = idle;
        hdr.Enabled = !cameraOnly && !running && idle;
        convert.Enabled = !cameraOnly && !preview && !running && idle;
        foreach (var editor in hotkeyEditors.Values) editor.Enabled = !running && idle;
        ExplainDependencies();
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing && !resourcesDisposed) { resourcesDisposed = true; ++operationId; cameraApplyTimer.Dispose(); timer.Stop(); timer.Dispose(); lifetime.Cancel(); hotkeys?.Dispose(); session?.Dispose(); tips.Dispose(); lifetime.Dispose(); }
        base.Dispose(disposing);
    }
}

internal sealed class ScreenStudioHotkeys : NativeWindow, IDisposable
{
    readonly Func<int, Task> action;
    readonly List<int> registered = [];
    public ScreenStudioHotkeys(ScreenStudioSettings settings, Func<int, Task> action)
    {
        this.action = action; settings.Validate(); CreateHandle(new CreateParams { Parent = new(-3) });
        try
        {
            var keys = new[] { settings.StopKey, settings.ToggleKey, settings.CompareKey, settings.ScreenshotKey };
            for (var index = 0; index < keys.Length; ++index)
            {
                if (!RegisterHotKey(Handle, index + 1, 0x4003, (uint)keys[index])) throw new InvalidOperationException("Screen Studio hotkey is already in use: Ctrl+Alt+" + keys[index]);
                registered.Add(index + 1);
            }
        }
        catch { Dispose(); throw; }
    }
    protected override void WndProc(ref Message message)
    {
        if (message.Msg == 0x312) { var index = (int)message.WParam - 1; if (index is >= 0 and <= 4) _ = action(index); return; }
        base.WndProc(ref message);
    }
    public void Dispose() { foreach (var id in registered) UnregisterHotKey(Handle, id); registered.Clear(); DestroyHandle(); }
    [DllImport("user32.dll", SetLastError = true)] static extern bool RegisterHotKey(nint window, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] static extern bool UnregisterHotKey(nint window, int id);
}
