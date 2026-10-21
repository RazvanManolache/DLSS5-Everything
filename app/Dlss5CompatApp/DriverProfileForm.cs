namespace Dlss5CompatApp;

internal sealed class DriverProfileForm : Form
{
    readonly string executable;
    readonly Dictionary<uint, ComboBox> controls = [];
    readonly Dictionary<uint, uint?> original = [];
    readonly Label status = new() { AutoSize = true, MaximumSize = new Size(620, 0) };
    sealed record Choice(string Label, uint? Value) { public override string ToString() => Label; }
    internal DriverProfileForm(string executable)
    {
        this.executable = Path.GetFullPath(executable);
        Text = "NVIDIA driver settings — " + Path.GetFileName(executable);
        Width = 680; Height = 560; MinimumSize = new Size(620, 500); StartPosition = FormStartPosition.CenterParent;
        var body = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown,
            AutoScroll = true, WrapContents = false, Padding = new Padding(18) };
        body.Controls.Add(new Label { Text = "Driver profile for this game", AutoSize = true, Font = new Font("Segoe UI", 14, FontStyle.Bold) });
        body.Controls.Add(new Label { Text = this.executable, AutoSize = true, MaximumSize = new Size(620, 0) });
        Add(body, 0x10835002, "Driver frame limit", [new("Inherit", null), new("Off", 0), ..new uint[] { 30, 60, 90, 120, 144, 165, 240 }.Select(v => new Choice(v + " FPS", v))]);
        Add(body, 0xB0D384C0, "Smooth Motion", [new("Inherit", null), new("Off", 0), new("On", 1)]);
        Add(body, 0x00DD48FB, "RTX HDR", [new("Inherit", null), new("Off", 0), new("On", 1)]);
        Add(body, 0x1057EB71, "Power management", [new("Inherit", null), new("Adaptive", 0), new("Prefer maximum performance", 1), new("Driver controlled", 5)]);
        Add(body, 0x10E41DF3, "DLSS SR preset", [new("Inherit", null), new("Driver default", 0), ..new[] { 'E', 'F', 'J', 'K', 'L' }.Select(v => new Choice("Preset " + v, (uint)(v - 'A' + 1)))]);
        var buttons = new FlowLayoutPanel { AutoSize = true };
        var apply = new Button { Text = "Apply changes", AutoSize = true };
        var restore = new Button { Text = "Restore previous values", AutoSize = true };
        apply.Click += (_, _) => Apply(); restore.Click += (_, _) => Restore();
        buttons.Controls.Add(apply); buttons.Controls.Add(restore); body.Controls.Add(buttons);
        body.Controls.Add(status); Controls.Add(body);
        Shown += (_, _) => RefreshValues();
    }
    void Add(FlowLayoutPanel body, uint key, string label, Choice[] choices)
    {
        body.Controls.Add(new Label { Text = label, AutoSize = true });
        var control = new ComboBox { Width = 360, DropDownStyle = ComboBoxStyle.DropDownList };
        control.Items.AddRange(choices.Cast<object>().ToArray()); control.SelectedIndex = 0;
        controls[key] = control; body.Controls.Add(control);
    }
    void RefreshValues()
    {
        try
        {
            using var profile = new NvidiaGameProfile(executable);
            foreach (var (key, control) in controls)
            {
                var value = profile.Read(key); original[key] = value;
                var index = control.Items.Cast<Choice>().ToList().FindIndex(c => c.Value == value);
                if (index < 0) { control.Items.Add(new Choice("Current value: " + value, value)); index = control.Items.Count - 1; }
                control.SelectedIndex = index;
            }
            var details = profile.Details;
            status.Text = $"Driver profile: {details.Name} ({details.ApplicationCount} associated executables). " +
                "Changes apply to the whole driver profile, including matching executable names in other folders. " +
                "Only changed values are applied. Original local values are recorded for restoration. Driver feature support varies.";
        }
        catch (Exception error) { status.Text = error.Message; }
    }
    void Apply()
    {
        try
        {
            var changes = controls.Where(p => p.Value.SelectedItem is Choice c && original.TryGetValue(p.Key, out var prior) && prior != c.Value)
                .ToDictionary(p => p.Key, p => ((Choice)p.Value.SelectedItem!).Value);
            if (changes.TryGetValue(0x10E41DF3, out var preset)) changes[0x10E41E01] = preset is null ? null : 1;
            DriverProfileService.Apply(executable, changes); RefreshValues(); status.Text = "Driver profile changes saved.";
        }
        catch (Exception error) { status.Text = error.Message; }
    }
    void Restore()
    {
        try
        {
            var conflicts = DriverProfileService.Restore(executable); RefreshValues();
            status.Text = conflicts.Count == 0 ? "Previous values restored." : "Preserved values changed by another application: " + string.Join(", ", conflicts);
        }
        catch (Exception error) { status.Text = error.Message; }
    }
}
