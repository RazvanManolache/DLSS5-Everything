namespace Dlss5CompatApp;

internal sealed partial class ScreenStudioView
{
    readonly Panel everyday = new() { Dock = DockStyle.Top, AutoSize = true, Name = "EverydayEffectControls" };
    Control BuildEverydayControls()
    {
        var fields = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2 };
        fields.ColumnStyles.Add(new(SizeType.Absolute, 180)); fields.ColumnStyles.Add(new(SizeType.Percent, 100));
        TrackBar Mirror(TrackBar source, string name)
        {
            var copy = new TrackBar { Minimum = source.Minimum, Maximum = source.Maximum, Value = source.Value, Enabled = source.Enabled, TickStyle = TickStyle.None, Dock = DockStyle.Fill, AccessibleName = name };
            source.EnabledChanged += (_, _) => copy.Enabled = source.Enabled;
            copy.ValueChanged += (_, _) => { if (source.Value != copy.Value) source.Value = copy.Value; };
            source.ValueChanged += (_, _) => { if (copy.Value != source.Value) copy.Value = source.Value; };
            return copy;
        }
        Field(fields, "Overall strength", ScaleEditor(Mirror(strength, "Overall strength"), Decimal));
        Field(fields, "Before / after", ComparisonMirror());
        Field(fields, "Local tone", ScaleEditor(Mirror(tone, "Basic local tone"), Decimal));
        Field(fields, "Local structure", ScaleEditor(Mirror(structure, "Basic local structure"), Decimal));
        Field(fields, "Skin (Auto = -1)", ScaleEditor(Mirror(skin, "Basic skin structure"), value => value == -100 ? "Auto" : Decimal(value)));
        if (cameraOnly) Field(fields, "Stream output", CameraOutputs());
        everyday.Controls.Add(fields); return everyday;
    }

    void ExplainDependencies()
    {
        var editable = !busy && !starting && !stopping;
        fov.Enabled = vignette.Enabled = aberration.Enabled = editable && lens.Checked;
        paperWhite.Enabled = editable && hdr.Checked;
        spoutName.Enabled = editable && spout.Checked;
        strength.Enabled = editable && residual.Checked;
        tips.SetToolTip(strength, residual.Checked ? "Overall residual effect strength" : "Enable Preserve full-resolution detail to adjust residual composition strength.");
        tips.SetToolTip(fov, lens.Checked ? "Lens field of view" : "Enable Lens distortion to edit camera controls.");
        tips.SetToolTip(paperWhite, hdr.Checked ? "HDR paper white in nits" : "Enable HDR transport to set paper white.");
        tips.SetToolTip(spoutName, spout.Checked ? "Spout sender name" : "Enable Spout GPU sender to set its name.");
    }

    void RefreshPendingState()
    {
        var staged = ReadSettings();
        pending.Text = session?.Running == true
            ? staged != active ? "Unapplied changes" : staged == preferences.Settings ? "Applied and saved" : "Applied to capture; save for next time"
            : staged == preferences.Settings ? "Saved for next capture" : "Unapplied changes";
    }
}
