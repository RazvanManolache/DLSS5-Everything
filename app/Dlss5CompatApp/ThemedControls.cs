namespace Dlss5CompatApp;

sealed class ThemedComboBox : ComboBox
{
    UiPalette palette = UiPalette.Light;
    public ThemedComboBox()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        DrawMode = DrawMode.OwnerDrawFixed;
    }
    public void ApplyPalette(UiPalette value) { palette = value; Invalidate(); }
    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(palette.Surface);
        using var border = new Pen(Focused ? palette.Accent : Enabled ? palette.ControlBoundary : palette.Border, Focused ? 2 : 1);
        e.Graphics.DrawRectangle(border, 0, 0, Width - 1, Height - 1);
        var text = new Rectangle(5, 1, Math.Max(0, Width - 28), Height - 2);
        OnDrawItem(new DrawItemEventArgs(e.Graphics, Font, text, SelectedIndex, Enabled ? DrawItemState.ComboBoxEdit : DrawItemState.Disabled, palette.Text, palette.Surface));
        using var arrow = UiTheme.Icon("\uE70D", Enabled ? palette.Text : palette.Muted, 14);
        e.Graphics.DrawImageUnscaled(arrow, Width - 20, (Height - 14) / 2);
        if (Focused) ControlPaint.DrawFocusRectangle(e.Graphics, text, palette.Text, palette.Surface);
    }
    protected override void OnSelectedIndexChanged(EventArgs e) { base.OnSelectedIndexChanged(e); Invalidate(); }
    protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Invalidate(); }
    protected override void OnDrawItem(DrawItemEventArgs e)
    {
        using var brush = new SolidBrush((e.State & DrawItemState.Selected) != 0 ? palette.Selection : palette.Surface);
        e.Graphics.FillRectangle(brush, e.Bounds);
        if (e.Index >= 0 && e.Index < Items.Count) TextRenderer.DrawText(e.Graphics, GetItemText(Items[e.Index]), e.Font, e.Bounds,
            Enabled ? palette.Text : palette.Muted, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
        base.OnDrawItem(e);
    }
}

sealed class ThemedProgressBar : Control
{
    UiPalette palette = UiPalette.Light;
    int minimum, maximum = 100, value;
    [System.ComponentModel.DefaultValue(0)]
    public int Minimum { get => minimum; set { if (value > maximum) throw new ArgumentOutOfRangeException(nameof(value)); minimum = value; this.value = Math.Clamp(this.value, minimum, maximum); Invalidate(); } }
    [System.ComponentModel.DefaultValue(100)]
    public int Maximum { get => maximum; set { if (value < minimum) throw new ArgumentOutOfRangeException(nameof(value)); maximum = value; this.value = Math.Clamp(this.value, minimum, maximum); Invalidate(); } }
    [System.ComponentModel.DefaultValue(0)]
    public int Value
    {
        get => value;
        set
        {
            if (value < minimum || value > maximum) throw new ArgumentOutOfRangeException(nameof(value));
            this.value = value;
            Invalidate();
            if (IsHandleCreated) AccessibilityNotifyClients(AccessibleEvents.ValueChange, -1);
        }
    }
    public ThemedProgressBar()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw, true);
        TabStop = false;
        AccessibleRole = AccessibleRole.ProgressBar;
        AccessibleName = "Component download progress";
    }
    public void ApplyPalette(UiPalette value) { palette = value; Invalidate(); }
    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(palette.Surface);
        using var border = new Pen(palette.Border);
        e.Graphics.DrawRectangle(border, 0, 0, Width - 1, Height - 1);
        var fraction = Maximum == Minimum ? 0 : (double)(Value - Minimum) / (Maximum - Minimum);
        using var fill = new SolidBrush(palette.Accent);
        e.Graphics.FillRectangle(fill, 2, 2, (int)((Width - 4) * fraction), Math.Max(0, Height - 4));
    }
    protected override AccessibleObject CreateAccessibilityInstance() => new ProgressAccessibility(this);
    sealed class ProgressAccessibility(ThemedProgressBar owner) : ControlAccessibleObject(owner)
    {
        public override string? Value { get => owner.Maximum == owner.Minimum ? "0%" : ((owner.Value - owner.Minimum) * 100 / (owner.Maximum - owner.Minimum)) + "%"; set { } }
    }
}

sealed class ThemedTabControl : TabControl
{
    UiPalette palette = UiPalette.Light;
    public ThemedTabControl() => SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
    public void ApplyPalette(UiPalette value) { palette = value; Invalidate(); }
    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(palette.Background);
        using var border = new Pen(palette.Border);
        var page = DisplayRectangle;
        e.Graphics.DrawRectangle(border, page.Left - 1, page.Top - 1, page.Width + 1, page.Height + 1);
        for (var i = 0; i < TabCount; i++) OnDrawItem(new DrawItemEventArgs(e.Graphics, Font, GetTabRect(i), i, i == SelectedIndex ? DrawItemState.Selected : DrawItemState.None));
        if (Focused && SelectedIndex >= 0) ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(GetTabRect(SelectedIndex), -3, -3), palette.Text, palette.Surface);
    }
    protected override void OnSelectedIndexChanged(EventArgs e) { base.OnSelectedIndexChanged(e); Invalidate(); }
}

sealed class ThemedNumericUpDown : NumericUpDown
{
    UiPalette palette = UiPalette.Light;
    readonly HashSet<Control> themedSpinners = [];
    public ThemedNumericUpDown() => BorderStyle = BorderStyle.None;
    public void ApplyPalette(UiPalette value)
    {
        palette = value;
        foreach (Control child in Controls)
        {
            if (child is TextBoxBase || !themedSpinners.Add(child)) continue;
            child.Paint += (_, e) =>
            {
                e.Graphics.Clear(palette.Surface);
                using var pen = new Pen(palette.Border);
                e.Graphics.DrawLine(pen, 0, 0, 0, child.Height);
                var half = child.Height / 2;
                using var up = UiTheme.Icon("\uE70E", Enabled ? palette.Text : palette.Muted, 10);
                using var down = UiTheme.Icon("\uE70D", Enabled ? palette.Text : palette.Muted, 10);
                e.Graphics.DrawImageUnscaled(up, (child.Width - 10) / 2, (half - 10) / 2);
                e.Graphics.DrawImageUnscaled(down, (child.Width - 10) / 2, half + (half - 10) / 2);
            };
            child.Invalidate();
        }
        Invalidate();
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var pen = new Pen(ContainsFocus ? palette.Accent : Enabled ? palette.ControlBoundary : palette.Border);
        e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
    }
}
