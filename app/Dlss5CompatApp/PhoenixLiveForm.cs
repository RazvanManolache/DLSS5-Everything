using System.Globalization;
using System.Text.Json;

namespace Dlss5CompatApp;

internal sealed partial class PhoenixLiveForm : Form
{
    readonly PhoenixControlClient client;
    readonly PhoenixOfflineControls offline;
    readonly CancellationTokenSource lifetime = new();
    readonly System.Windows.Forms.Timer poll = new() { Interval = 1000 };
    readonly ToolTip tips = new();
    readonly Dictionary<PhoenixSetting, Control> editors = new();
    readonly HashSet<PhoenixSetting> dirty = new();
    readonly List<Button> commands = new();
    readonly List<Image> icons = new();
    readonly Label connection = new() { AutoSize = true, MaximumSize = new Size(1000, 0) };
    readonly Label outcome = new() { AutoSize = true, MaximumSize = new Size(1000, 0) };
    readonly Label pending = new() { AutoSize = true };
    readonly Label routeGuidance = new() { AutoSize = true, MaximumSize = new Size(900, 0), Name = "RouteCapabilities" };
    PhoenixControlAvailability availability;
    readonly bool colorOnlyCapture;
    readonly TextBox runtime = Readout();
    readonly TextBox timing = Readout();
    readonly TextBox snapshot = Readout();
    readonly NumericUpDown captureFrames = new ThemedNumericUpDown() { Minimum = 1, Maximum = 8, Value = 1, Width = 55, AccessibleName = "Capture frames" };
    readonly Button apply;
    CancellationTokenSource? currentCommand;
    string? session;
    bool busy, connected, loading, offlineEditable, closingConfirmed, resourcesDisposed;

    // MainForm hookup: new PhoenixLiveForm(gameDirectory, palette).ShowDialog(this);
    public PhoenixLiveForm(string directory, UiPalette? palette = null, string initialTab = "Rendering", bool colorOnlyCapture = false, bool sessionOnly = false, PhoenixControlClient? controlClient = null)
    {
        this.colorOnlyCapture=colorOnlyCapture;
        this.sessionOnly=sessionOnly;
        SuspendLayout();
        client = controlClient ?? new PhoenixControlClient(directory);
        offline = new PhoenixOfflineControls(client.GameDirectory);
        Text = "Phoenix DLSS5 Live Controls";
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Segoe UI", 9F);
        StartPosition = FormStartPosition.CenterParent;
        Size = new Size(980, 840);
        MinimumSize = new Size(660, 560);
        var colors = palette ?? UiPalette.Light;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Padding = new Padding(12) };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        Controls.Add(layout);

        var header = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        header.Controls.Add(connection);
        header.Controls.Add(outcome);
        header.Controls.Add(routeGuidance);
        layout.Controls.Add(header, 0, 0);
        connection.Text = "Looking for the game's Phoenix session…";
        outcome.Text = "No command acknowledgement";

        var toolbar = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true, Padding = new Padding(0, 8, 0, 8) };
        refreshButton = Command("Refresh", "\uE72C", () => RunAsync(PhoenixOperation.Status), colors, requiresConnection: false);
        toolbar.Controls.Add(refreshButton);
        toolbar.Controls.Add(Command("Reset history", "\uE777", () => RunAsync(PhoenixOperation.ResetHistory), colors));
        toolbar.Controls.Add(Command("Validate model", "\uE73E", () => RunAsync(PhoenixOperation.ValidateModel), colors));
        toolbar.Controls.Add(Command("Screenshot", "\uE722", () => RunAsync(PhoenixOperation.Screenshot), colors));
        toolbar.Controls.Add(new Label { Text = "Frames", AutoSize = true, Margin = new Padding(8, 9, 3, 0) });
        toolbar.Controls.Add(captureFrames);
        toolbar.Controls.Add(Command("Capture", "\uE714", () => RunAsync(PhoenixOperation.Capture), colors));
        saveButton = Command("Apply & save", "\uE74E", ApplyAndSaveAsync, colors, requiresConnection: false);
        saveButton.Tag = "primary";
        saveButton.UseMnemonic = false; saveButton.Visible = !sessionOnly; toolbar.Controls.Add(saveButton);
        comparisonPreset = Command("Before / after", "\uE8FF", () =>
        {
            SelectChoice(PhoenixSetting.Backend, 3);
            SelectChoice(PhoenixSetting.Comparison, 1);
            SelectChoice(PhoenixSetting.SourceA, 0);
            SelectChoice(PhoenixSetting.SourceB, 2);
            if (editors.GetValueOrDefault(PhoenixSetting.Divider) is NumericUpDown divider) divider.Value = 0.5m;
            return Task.CompletedTask;
        }, colors, requiresConnection: false);
        toolbar.Controls.Add(comparisonPreset);
        var cancel = new Button { Text = "Cancel", AutoSize = true, MinimumSize = new Size(80, 32), Enabled = false, Visible = false };
        cancel.Click += (_, _) => currentCommand?.Cancel();
        toolbar.Controls.Add(cancel);
        tips.SetToolTip(cancel, "Stop waiting for the current command");
        layout.Controls.Add(toolbar, 0, 1);

        var tabs = new ThemedTabControl { Dock = DockStyle.Fill, Multiline = true };
        foreach (var group in PhoenixControlContract.Settings.GroupBy(item => item.Group))
        {
            var page = new TabPage(group.Key) { AutoScroll = true, Padding = new Padding(8) };
            var fields = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, GrowStyle = TableLayoutPanelGrowStyle.AddRows };
            fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 45));
            fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 55));
            fields.SuspendLayout();
            foreach (var definition in group)
            {
                var editor = CreateEditor(definition);
                editor.Name = definition.WireKey;
                editor.AccessibleName = definition.Label;
                editor.Dock = DockStyle.Fill;
                editor.Margin = new Padding(5);
                editor.Enabled = false;
                editors.Add(definition.Key, editor);
                var label = new Label { Text = definition.Label, AutoSize = true, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Margin = new Padding(5) };
                var row = fields.RowCount++;
                fields.Controls.Add(label, 0, row);
                var valueHost = ValueHost(definition, editor);
                valueHost.Tag = label;
                fieldRows[definition.Key] = valueHost;
                fields.Controls.Add(valueHost, 1, row);
                tips.SetToolTip(editor, SettingHelp(definition.Key));
            }
            page.Controls.Add(fields);
            fields.ResumeLayout(false);
            tabs.TabPages.Add(page);
        }
        tabs.TabPages.Add(ReadoutPage("Runtime", runtime));
        tabs.TabPages.Add(ReadoutPage("Timing", timing));
        tabs.TabPages.Add(ReadoutPage("Snapshot", snapshot));
        layout.Controls.Add(tabs, 0, 2);
        ConfigureUx(tabs, toolbar, header);
        SizeChanged += (_, _) => connection.MaximumSize = outcome.MaximumSize = new Size(Math.Max(200, ClientSize.Width - 40), 0);

        var footer = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true, Padding = new Padding(0, 8, 0, 0) };
        apply = Command("Apply changes", "\uE73E", ApplyAsync, colors);
        apply.Tag = null;
        footer.Controls.Add(apply);
        footer.Controls.Add(pending);
        layout.Controls.Add(footer, 0, 3);
        UiTheme.Apply(this, colors);
        offlineEditable = client.CanEditOffline();
        LoadSnapshot(offline.Read());
        connection.Text = offlineEditable ? "Game is not running · settings are available for the next launch" : "Waiting for a live Phoenix session";
        UpdateCommands();
        Shown += async (_, _) => { await RunAsync(PhoenixOperation.Status); if (!IsDisposed) poll.Start(); };
        poll.Tick += async (_, _) =>
        {
            if (!busy && (connected || File.Exists(Path.Combine(client.BridgeDirectory, "endpoint.json"))))
                await RunAsync(PhoenixOperation.Status, background: true);
        };
        if (initialTab is "Runtime" or "Timing" or "Snapshot" or "NVIDIA DLSS5") showAdvanced.Checked = true;
        foreach (TabPage page in tabs.TabPages) if (page.Text == initialTab) tabs.SelectedTab = page;
        FormClosing += async (_, e) =>
        {
            if (!closingConfirmed && dirty.Count > 0)
            {
                var answer = MessageBox.Show(this, "Apply pending changes before closing?", "Pending Phoenix DLSS5 changes", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
                if (answer == DialogResult.Cancel) { e.Cancel = true; return; }
                if (answer == DialogResult.Yes)
                {
                    e.Cancel = true;
                    if (busy) return;
                    if (sessionOnly) await ApplyAsync(); else await ApplyAndSaveAsync();
                    if (dirty.Count == 0) { closingConfirmed = true; Close(); }
                    return;
                }
            }
            poll.Stop(); lifetime.Cancel();
        };
        void BusyChanged() { cancel.Enabled = busy; cancel.Visible = busy || showAdvanced.Checked; }
        busyChanged = BusyChanged;
        ResumeLayout(true);
    }

    readonly Action busyChanged;
    void SelectChoice(PhoenixSetting key, int value)
    {
        if (editors.GetValueOrDefault(key) is not ComboBox combo) return;
        for (var index = 0; index < combo.Items.Count; index++)
            if (combo.Items[index] is PhoenixChoice choice && Equals(choice.Value, value)) { combo.SelectedIndex = index; break; }
    }
    static TextBox Readout() => new() { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false };
    static TabPage ReadoutPage(string title, TextBox text)
    {
        var page = new TabPage(title) { Padding = new Padding(8) };
        page.Controls.Add(text);
        return page;
    }
    Button Command(string title, string glyph, Func<Task> action, UiPalette palette, bool requiresConnection = true)
    {
        var icon = UiTheme.Icon(glyph, palette.Text);
        icons.Add(icon);
        var button = new Button { Text = title, Image = icon, TextImageRelation = TextImageRelation.ImageBeforeText,
            AutoSize = true, MinimumSize = new Size(90, 32), AccessibleName = title };
        button.Click += async (_, _) => await action();
        tips.SetToolTip(button, title);
        if (requiresConnection) commands.Add(button);
        return button;
    }
    Button? refreshButton;
    Button? comparisonPreset;

    Control CreateEditor(PhoenixSettingDefinition definition)
    {
        void Changed()
        {
            if (loading) return;
            dirty.Add(definition.Key);
            UpdateCommands();
        }
        if (definition.ValueType == typeof(bool))
        {
            var check = new CheckBox { AutoSize = true };
            check.CheckedChanged += (_, _) => Changed();
            return check;
        }
        if (definition.Choices is not null || definition.Key == PhoenixSetting.ExecutionDevice)
        {
            var combo = new ThemedComboBox { DropDownStyle = ComboBoxStyle.DropDownList, DropDownWidth = 380 };
            if (definition.Choices is not null) combo.Items.AddRange(definition.Choices.Cast<object>().ToArray());
            combo.SelectedIndexChanged += (_, _) => Changed();
            return combo;
        }
        if (definition.ValueType == typeof(string))
        {
            var text = new TextBox();
            text.TextChanged += (_, _) => Changed();
            return text;
        }
        var number = new ThemedNumericUpDown { Minimum = definition.Minimum, Maximum = definition.Maximum,
            DecimalPlaces = definition.ValueType == typeof(int) ? 0 : 3,
            Increment = definition.ValueType == typeof(int) ? 1 : .01m, ThousandsSeparator = false };
        number.ValueChanged += (_, _) => Changed();
        return number;
    }

    Task ApplyAsync()
    {
        try
        {
            var settings = new PhoenixControlSettings();
            foreach (var key in dirty)
            {
                var value = EditorValue(key);
                switch (value)
                {
                    case bool flag: settings.Set(key, flag); break;
                    case int number: settings.Set(key, number); break;
                    case float number: settings.Set(key, number); break;
                    case string text: settings.Set(key, text); break;
                }
            }
            if (connected)
            {
                availability.Validate(settings);
                return RunAsync(PhoenixOperation.Set, settings);
            }
            offline.Apply(settings, client.CanEditOffline);
            lastCommandSucceeded = true;
            dirty.Clear();
            LoadSnapshot(offline.Read());
            outcome.Text = "Offline settings saved - restart the game to load them";
            outcome.Visible = true;
            persistenceScope.Text = "Saved for the next game launch.";
            UpdateCommands();
            return Task.CompletedTask;
        }
        catch (Exception exception) { outcome.Text = "Settings error: " + exception.Message; return Task.CompletedTask; }
    }

    object EditorValue(PhoenixSetting key) => editors[key] switch
    {
        CheckBox check => check.Checked,
        ComboBox combo => (combo.SelectedItem as PhoenixChoice)?.Value ?? throw new ArgumentException("Select " + key),
        NumericUpDown number when PhoenixControlContract.Definition(key).ValueType == typeof(int) => (int)number.Value,
        NumericUpDown number => (float)number.Value,
        TextBox text => text.Text,
        _ => throw new InvalidOperationException("Unsupported editor.")
    };

    async Task RunAsync(PhoenixOperation operation, PhoenixControlSettings? settings = null, bool background = false)
    {
        if (busy || lifetime.IsCancellationRequested) return;
        busy = true;
        currentCommand = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        UpdateCommands();
        busyChanged();
        try
        {
            var response = await client.SendAsync(operation, settings,
                frames: operation == PhoenixOperation.Capture ? (int)captureFrames.Value : null,
                cancellationToken: currentCommand.Token, expectedSession: operation == PhoenixOperation.Status ? null : session);
            if (IsDisposed || lifetime.IsCancellationRequested) return;
            lastCommandSucceeded = response.Ok;
            lastCommandPhase = response.Phase;
            bool changed = session is not null && session != response.Session || !connected && dirty.Count > 0;
            connected = true;
            offlineEditable = false;
            if (changed || operation == PhoenixOperation.Set && response.Ok) dirty.Clear();
            session = response.Session;
            connection.Text = "Connected · changes can be applied to the running game";
            if (operation == PhoenixOperation.Set && response.Ok) persistenceScope.Text = sessionOnly ? "Applied to this capture session only." : "Changes acknowledged by the game; requesting Save next.";
            if (!background || !response.Ok || changed)
            {
                outcome.Visible = true;
                outcome.Text = !response.Ok ? $"{response.Phase}: {response.Error}" : changed ? "New Phoenix DLSS5 session connected; pending edits cleared" :
                    $"{PhoenixControlClient.WireOperation(operation)}: {response.Phase} (acknowledged)";
            }
            LoadSnapshot(response.Snapshot);
        }
        catch (OperationCanceledException)
        {
            lastCommandSucceeded = false;
            if (!IsDisposed) outcome.Text = "Wait cancelled; a published command may still execute before expiry";
        }
        catch (Exception exception)
        {
            lastCommandSucceeded = false;
            var wasConnected = connected;
            connected = false;
            offlineEditable = client.CanEditOffline();
            if (!IsDisposed)
            {
                connection.Text = offlineEditable ? "Game is not running · edit settings for the next launch" : "Phoenix session unavailable · see Runtime diagnostics";
                if (!background || wasConnected) outcome.Text = offlineEditable && exception is IOException ? "Offline settings ready" : exception.Message;
                outcome.Visible = outcome.Text != "Offline settings ready";
                tips.SetToolTip(connection, exception.Message);
                if (offlineEditable && (wasConnected || !background && dirty.Count == 0))
                    LoadSnapshot(offline.Read());
            }
        }
        finally
        {
            currentCommand.Dispose();
            currentCommand = null;
            busy = false;
            if (!IsDisposed) { UpdateCommands(); busyChanged(); }
            else lifetime.Dispose();
        }
    }

    void LoadSnapshot(JsonElement root)
    {
        loading = true;
        try
        {
            availability = PhoenixControlAvailability.FromSnapshot(root);
            if (colorOnlyCapture) availability=availability with { ColorOnlyDirectFeed=true };
            UpdateGuidance();
            snapshot.Text = JsonSerializer.Serialize(root, new JsonSerializerOptions { WriteIndented = true });
            runtime.Text = string.Join(Environment.NewLine + Environment.NewLine,
                new[] { "phoenix", "nvidia", "execution", "model_source", "model_validation", "input", "capture", "offline_warnings", "scope" }.Select(key => Section(root, key)));
            timing.Text = string.Join(Environment.NewLine + Environment.NewLine,
                new[] { "tick_ms", "input_tick_ms", "present_fps", "gpu_timing", "worker_timing" }.Select(key => Section(root, key)));
            if (!root.TryGetProperty("settings", out var settings) || settings.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("Snapshot has no settings object.");
            foreach (var definition in PhoenixControlContract.Settings)
            {
                var editor = editors[definition.Key];
                if (!settings.TryGetProperty(definition.WireKey, out var raw)) { editor.Tag = false; editor.Enabled = false; continue; }
                object value = definition.ValueType == typeof(bool) ? raw.GetBoolean() :
                    definition.ValueType == typeof(int) ? (object)raw.GetInt32() :
                    definition.ValueType == typeof(float) ? raw.GetSingle() : raw.GetString()!;
                definition.Validate(value);
                editor.Tag = true;
                if (dirty.Contains(definition.Key)) continue;
                switch (editor)
                {
                    case CheckBox check: check.Checked = (bool)value; break;
                    case NumericUpDown number: number.Value = Convert.ToDecimal(value, CultureInfo.InvariantCulture); break;
                    case TextBox text: text.Text = (string)value; break;
                    case ComboBox combo:
                        if (definition.Choices is not null)
                        {
                            combo.Items.Clear();
                            combo.Items.AddRange(definition.Choices.Where(choice => availability.Allows(definition.Key, choice.Value)).Cast<object>().ToArray());
                            // Preserve the identity of an older saved request while
                            // explaining that it is inactive. Choosing Spatial is
                            // an explicit edit, rather than a silent config rewrite.
                            if (!availability.Allows(definition.Key, value))
                            {
                                var saved = definition.Choices.First(choice => Equals(choice.Value, value));
                                combo.Items.Add(new PhoenixChoice(value, saved.Label + " (inactive on this route)"));
                            }
                            tips.SetToolTip(combo, availability.ColorOnlyDirectFeed && definition.Key is PhoenixSetting.Comparison or PhoenixSetting.BudgetMode or PhoenixSetting.Upscaler ? availability.Reason(definition.Key) : SettingHelp(definition.Key));
                        }
                        if (definition.Key == PhoenixSetting.ExecutionDevice)
                        {
                            combo.Items.Clear();
                            if (availability.Devices is { } devices)
                                foreach (var device in devices) combo.Items.Add(new PhoenixChoice(device.Key,device.Value));
                            if (!combo.Items.Cast<PhoenixChoice>().Any(item => Equals(item.Value, value)))
                                combo.Items.Add(new PhoenixChoice(value, (string)value + (availability.Allows(definition.Key,value) ? "" : " (unavailable)")));
                        }
                        combo.SelectedItem = combo.Items.Cast<PhoenixChoice>().FirstOrDefault(item => Equals(item.Value, value));
                        break;
                }
            }
        }
        finally { loading = false; }
    }

    static string Section(JsonElement root, string key) => key + ": " +
        (root.TryGetProperty(key, out var value) ? JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }) : "unavailable");
    void UpdateCommands()
    {
        var frozen = FreezeFields();
        try
        {
        foreach (var command in commands) command.Enabled = connected && !busy;
        apply.Text = sessionOnly ? "Apply to this capture" : "Apply changes";
        apply.Visible = connected && sessionOnly;
        apply.Enabled = (connected || offlineEditable) && !busy && dirty.Count != 0;
        if (saveButton is not null) saveButton.Enabled = !sessionOnly && (connected || offlineEditable) && !busy && (connected || dirty.Count != 0);
        if (refreshButton is not null) refreshButton.Enabled = !busy;
        if (comparisonPreset is not null) comparisonPreset.Enabled = (connected || offlineEditable) && !busy;
        captureFrames.Enabled = connected && !busy;
        foreach (var pair in editors)
        {
            var reason = DependencyReason(pair.Key);
            pair.Value.Enabled = (connected || offlineEditable) && !busy && pair.Value.Tag is true && reason.Length == 0;
            if (fieldRows.TryGetValue(pair.Key, out var row))
                foreach (var button in row.Controls.OfType<Button>()) button.Enabled = pair.Value.Enabled;
            if (dependencyLabels.TryGetValue(pair.Key, out var note)) { note.Text = reason; note.Visible = reason.Length > 0; }
        }
        pending.Text = dirty.Count == 0 ? "No pending changes" : $"{dirty.Count} pending changes";
        }
        finally { ThawFields(frozen); }
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing && !resourcesDisposed)
        {
            resourcesDisposed = true;
            lifetime.Cancel();
            poll.Dispose();
            tips.Dispose();
            foreach (var icon in icons) icon.Dispose();
            // The pending worker owns its linked token until it has released the mailbox mutex.
            if (currentCommand is null) lifetime.Dispose();
        }
        base.Dispose(disposing);
    }
}
