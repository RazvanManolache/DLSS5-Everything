using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace Dlss5CompatApp;

sealed record UiPalette(Color Background, Color Surface, Color Sidebar, Color Text, Color Muted, Color Border, Color Accent, Color Selection)
{
    public Color Error => this == Dark ? Color.FromArgb(255, 153, 153) : this == HighContrast ? Text : Color.FromArgb(166, 34, 45);
    public Color Success => this == Dark ? Color.FromArgb(115, 219, 164) : this == HighContrast ? Text : Color.FromArgb(19, 111, 65);
    public Color ControlBoundary => this == HighContrast ? SystemColors.WindowText : this == Dark ? Color.FromArgb(123, 143, 153) : Color.FromArgb(113, 129, 138);
    public static UiPalette Light { get; } = new(Color.FromArgb(243, 245, 247), Color.White, Color.FromArgb(234, 239, 242), Color.FromArgb(29, 39, 45), Color.FromArgb(87, 103, 111), Color.FromArgb(214, 222, 226), Color.FromArgb(0, 111, 104), Color.FromArgb(220, 239, 234));
    public static UiPalette Dark { get; } = new(Color.FromArgb(25, 28, 31), Color.FromArgb(33, 37, 40), Color.FromArgb(21, 24, 26), Color.FromArgb(235, 239, 240), Color.FromArgb(163, 177, 185), Color.FromArgb(58, 67, 72), Color.FromArgb(76, 197, 172), Color.FromArgb(37, 72, 66));
    public static UiPalette HighContrast { get; } = new(SystemColors.Window, SystemColors.Window, SystemColors.Control, SystemColors.WindowText, SystemColors.WindowText, SystemColors.WindowText, SystemColors.Highlight, SystemColors.Control);
}

static class UiTheme
{
    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ListView, HeaderTheme> Headers = new();
    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Button, ButtonTheme> Buttons = new();
    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Control, NativeTheme> NativeThemes = new();
    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ComboBox, ComboTheme> Combos = new();
    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<TabControl, TabTheme> Tabs = new();
    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<CheckBox, CheckTheme> Checks = new();
    public static void Apply(Control root, UiPalette palette)
    {
        root.BackColor = palette.Background;
        root.ForeColor = palette.Text;
        if (root.Tag is "error") root.ForeColor = palette.Error;
        if (root.Tag is "muted") root.ForeColor = palette.Muted;
        NativeThemes.GetValue(root, control => new NativeTheme(control)).Set(palette);
        if (root.ContextMenuStrip is { } menu) Apply(menu, palette);
        foreach (Control child in root.Controls) Apply(child, palette);
        switch (root)
        {
            case Button button:
                button.FlatStyle = FlatStyle.Flat;
                button.FlatAppearance.BorderColor = palette.ControlBoundary;
                button.FlatAppearance.MouseOverBackColor = palette.Selection;
                button.FlatAppearance.MouseDownBackColor = palette.Selection;
                var buttonTheme = Buttons.GetValue(button, view => new ButtonTheme(view));
                buttonTheme.Palette = palette;
                buttonTheme.Refresh();
                button.Cursor = Cursors.Hand;
                break;
            case CheckBox check:
                Checks.GetValue(check, view => new CheckTheme(view)).Palette = palette;
                break;
            case TextBox box:
                box.BackColor = palette.Surface;
                box.ForeColor = palette.Text;
                box.BorderStyle = BorderStyle.FixedSingle;
                break;
            case ListView list:
                list.BackColor = palette.Surface;
                list.ForeColor = palette.Text;
                list.BorderStyle = BorderStyle.None;
                var header = Headers.GetValue(list, view => new HeaderTheme(view));
                header.Palette = palette;
                list.Invalidate();
                break;
            case ComboBox combo:
                combo.BackColor = palette.Surface;
                combo.ForeColor = palette.Text;
                combo.FlatStyle = FlatStyle.Flat;
                if (combo.DrawMode == DrawMode.Normal || Combos.TryGetValue(combo, out _))
                {
                    combo.DrawMode = DrawMode.OwnerDrawFixed;
                    Combos.GetValue(combo, view => new ComboTheme(view)).Palette = palette;
                }
                if (combo is ThemedComboBox themed) themed.ApplyPalette(palette);
                break;
            case ThemedProgressBar progress:
                progress.ApplyPalette(palette);
                break;
            case LinkLabel link:
                link.LinkColor = palette.Accent;
                link.ActiveLinkColor = palette.Text;
                link.VisitedLinkColor = palette.Accent;
                link.DisabledLinkColor = palette.Muted;
                break;
            case ListBox list:
                list.BackColor = palette.Surface;
                list.BorderStyle = BorderStyle.FixedSingle;
                break;
            case ToolStrip strip:
                strip.BackColor = palette.Surface;
                strip.ForeColor = palette.Text;
                strip.Renderer = new MenuRenderer(palette);
                foreach (ToolStripItem item in strip.Items) ApplyMenuItem(item, palette);
                break;
            case TabControl tabs:
                tabs.DrawMode = TabDrawMode.OwnerDrawFixed;
                Tabs.GetValue(tabs, view => new TabTheme(view)).Palette = palette;
                if (tabs is ThemedTabControl themedTabs) themedTabs.ApplyPalette(palette);
                break;
            case TabPage page:
                page.UseVisualStyleBackColor = false;
                page.BackColor = palette.Background;
                break;
            case NumericUpDown number:
                number.BackColor = palette.Surface;
                number.ForeColor = palette.Text;
                if (number is ThemedNumericUpDown themedNumber) themedNumber.ApplyPalette(palette);
                break;
            case PropertyGrid grid:
                grid.ViewBackColor = palette.Surface;
                grid.ViewForeColor = palette.Text;
                grid.HelpBackColor = palette.Background;
                grid.HelpForeColor = palette.Text;
                grid.LineColor = palette.Border;
                grid.CategoryForeColor = palette.Text;
                grid.CategorySplitterColor = palette.Border;
                grid.CommandsBackColor = palette.Surface;
                grid.CommandsForeColor = palette.Text;
                grid.SelectedItemWithFocusBackColor = palette.Selection;
                grid.SelectedItemWithFocusForeColor = palette.Text;
                break;
        }
    }

    sealed class ButtonTheme
    {
        readonly Button button;
        public UiPalette Palette { get; set; } = UiPalette.Light;
        public ButtonTheme(Button button)
        {
            this.button = button;
            button.EnabledChanged += (_, _) => Refresh();
            var hover = false;
            button.MouseEnter += (_, _) => { hover = true; button.Invalidate(); };
            button.MouseLeave += (_, _) => { hover = false; button.Invalidate(); };
            button.Paint += (_, e) =>
            {
                // Native FlatStyle ignores ForeColor for disabled text. Paint it consistently.
                var primary = button.Enabled && Equals(button.Tag, "primary");
                e.Graphics.Clear(button.Enabled && hover && !primary ? Palette.Selection : button.BackColor);
                using var border = new Pen(button.Focused ? Palette.Accent : Palette.ControlBoundary, button.Focused ? 2 : 1);
                e.Graphics.DrawRectangle(border, 0, 0, button.Width - 1, button.Height - 1);
                var bounds = Rectangle.Inflate(button.ClientRectangle, -4, -3);
                var icon = button.Image;
                if (icon is not null)
                {
                    var x = button.Text.Length == 0 ? (button.Width - icon.Width) / 2 : bounds.Left;
                    using var tint = new System.Drawing.Imaging.ImageAttributes();
                    var foreground = button.ForeColor;
                    tint.SetColorMatrix(new System.Drawing.Imaging.ColorMatrix
                    {
                        Matrix00 = 0, Matrix11 = 0, Matrix22 = 0, Matrix33 = 1, Matrix44 = 1,
                        Matrix40 = foreground.R / 255f, Matrix41 = foreground.G / 255f, Matrix42 = foreground.B / 255f
                    });
                    e.Graphics.DrawImage(icon, new Rectangle(x, (button.Height - icon.Height) / 2, icon.Width, icon.Height), 0, 0, icon.Width, icon.Height, GraphicsUnit.Pixel, tint);
                    if (button.Text.Length > 0) { bounds.X += icon.Width + 4; bounds.Width -= icon.Width + 4; }
                }
                TextRenderer.DrawText(e.Graphics, button.Text, button.Font, bounds, button.ForeColor,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding |
                    (button.TextAlign is ContentAlignment.MiddleLeft or ContentAlignment.TopLeft or ContentAlignment.BottomLeft ? TextFormatFlags.Left : TextFormatFlags.HorizontalCenter) |
                    (button.UseMnemonic ? 0 : TextFormatFlags.NoPrefix));
                if (button.Focused) ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(button.ClientRectangle, -4, -4), button.ForeColor, button.BackColor);
            };
        }
        public void Refresh()
        {
            var primary = button.Enabled && Equals(button.Tag, "primary");
            button.BackColor = primary ? Palette.Accent : Palette.Surface;
            button.ForeColor = !button.Enabled ? Palette.Muted : primary ? (Palette == UiPalette.Dark ? Palette.Sidebar : SystemInformation.HighContrast ? SystemColors.HighlightText : Color.White) : Palette.Text;
            button.Invalidate();
        }
    }

    sealed class HeaderTheme
    {
        public UiPalette Palette { get; set; } = UiPalette.Light;
        public HeaderTheme(ListView list)
        {
            list.OwnerDraw = true;
            list.DrawItem += (_, e) =>
            {
                if (list.View == View.LargeIcon && e.Item is { } item)
                {
                    using var background = new SolidBrush(Palette.Surface);
                    e.Graphics.FillRectangle(background, e.Bounds);
                    var icon = item.GetBounds(ItemBoundsPortion.Icon);
                    if (list.LargeImageList is { } images && item.ImageIndex >= 0 && item.ImageIndex < images.Images.Count)
                        images.Draw(e.Graphics, icon.Left, icon.Top, item.ImageIndex);
                    var nativeLabel = item.GetBounds(ItemBoundsPortion.Label);
                    var label = new Rectangle(icon.Left, nativeLabel.Top, icon.Width, Math.Max(nativeLabel.Height, list.Font.Height * 2 + 2));
                    using var caption = new SolidBrush(item.Selected ? Palette.Selection : Palette.Surface);
                    e.Graphics.FillRectangle(caption, label);
                    TextRenderer.DrawText(e.Graphics, item.Text, list.Font, label, Palette.Text,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
                    if (item.Selected)
                    {
                        using var outline = new Pen(Palette.Accent, 2);
                        e.Graphics.DrawRectangle(outline, icon.Left, icon.Top, Math.Max(0, icon.Width - 1), Math.Max(0, icon.Height - 1));
                    }
                    return;
                }
                if (list.View != View.Details) { e.DrawDefault = true; return; }
                using var brush = new SolidBrush(e.Item?.Selected == true ? Palette.Selection : Palette.Surface);
                e.Graphics.FillRectangle(brush, e.Bounds);
            };
            list.DrawSubItem += (_, e) =>
            {
                var selected = e.Item?.Selected == true;
                using var brush = new SolidBrush(selected ? Palette.Selection : Palette.Surface);
                e.Graphics.FillRectangle(brush, e.Bounds);
                TextRenderer.DrawText(e.Graphics, e.SubItem?.Text ?? "", list.Font, Rectangle.Inflate(e.Bounds, -7, 0), Palette.Text,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            };
            list.DrawColumnHeader += (_, e) =>
            {
                using var brush = new SolidBrush(Palette.Sidebar);
                using var pen = new Pen(Palette.Border);
                e.Graphics.FillRectangle(brush, e.Bounds);
                if (e.ColumnIndex == list.Columns.Cast<ColumnHeader>().LastOrDefault(column => column.Width > 0)?.Index)
                    e.Graphics.FillRectangle(brush, e.Bounds.Right, e.Bounds.Top, Math.Max(0, list.ClientSize.Width - e.Bounds.Right), e.Bounds.Height);
                e.Graphics.DrawLine(pen, e.Bounds.Right - 1, e.Bounds.Top + 5, e.Bounds.Right - 1, e.Bounds.Bottom - 5);
                TextRenderer.DrawText(e.Graphics, e.Header?.Text ?? "", list.Font,
                    Rectangle.Inflate(e.Bounds, -7, -2), Palette.Text,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
            };
        }
    }

    sealed class CheckTheme
    {
        public UiPalette Palette { get; set; } = UiPalette.Light;
        public CheckTheme(CheckBox check)
        {
            check.Paint += (_, e) =>
            {
                e.Graphics.Clear(check.BackColor);
                var size = (int)(14 * check.DeviceDpi / 96f);
                var square = new Rectangle(1, (check.Height - size) / 2, size, size);
                using var fill = new SolidBrush(check.Checked ? Palette.Accent : Palette.Surface);
                using var border = new Pen(check.Enabled ? Palette.Muted : Palette.Border);
                e.Graphics.FillRectangle(fill, square);
                e.Graphics.DrawRectangle(border, square);
                if (check.Checked)
                {
                    using var glyph = Icon("\uE73E", Palette == UiPalette.HighContrast ? SystemColors.HighlightText : Palette == UiPalette.Dark ? Palette.Sidebar : Color.White, size);
                    e.Graphics.DrawImageUnscaled(glyph, square.Location);
                }
                var text = new Rectangle(size + 5, 0, Math.Max(0, check.Width - size - 5), check.Height);
                TextRenderer.DrawText(e.Graphics, check.Text, check.Font, text, check.Enabled ? Palette.Text : Palette.Muted,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                if (check.Focused) ControlPaint.DrawFocusRectangle(e.Graphics, text, Palette.Text, check.BackColor);
            };
            check.CheckedChanged += (_, _) => check.Invalidate();
            check.EnabledChanged += (_, _) => check.Invalidate();
        }
    }

    static void ApplyMenuItem(ToolStripItem item, UiPalette palette)
    {
        item.BackColor = palette.Surface;
        item.ForeColor = palette.Text;
        if (item is ToolStripDropDownItem dropdown)
        {
            dropdown.DropDown.Renderer = new MenuRenderer(palette);
            dropdown.DropDown.BackColor = palette.Surface;
            foreach (ToolStripItem child in dropdown.DropDownItems) ApplyMenuItem(child, palette);
        }
    }

    sealed class MenuRenderer(UiPalette palette) : ToolStripProfessionalRenderer(new MenuColors(palette))
    {
        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        { e.TextColor = e.Item.Enabled ? palette.Text : palette.Muted; base.OnRenderItemText(e); }
    }

    sealed class MenuColors(UiPalette palette) : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => palette.Surface;
        public override Color ImageMarginGradientBegin => palette.Surface;
        public override Color ImageMarginGradientMiddle => palette.Surface;
        public override Color ImageMarginGradientEnd => palette.Surface;
        public override Color ToolStripGradientBegin => palette.Surface;
        public override Color ToolStripGradientMiddle => palette.Surface;
        public override Color ToolStripGradientEnd => palette.Surface;
        public override Color MenuItemSelected => palette.Selection;
        public override Color MenuItemSelectedGradientBegin => palette.Selection;
        public override Color MenuItemSelectedGradientEnd => palette.Selection;
        public override Color MenuItemBorder => palette.Border;
        public override Color MenuBorder => palette.Border;
        public override Color ToolStripBorder => palette.Border;
        public override Color SeparatorDark => palette.Border;
        public override Color SeparatorLight => palette.Surface;
        public override Color CheckBackground => palette.Selection;
        public override Color CheckSelectedBackground => palette.Selection;
    }

    sealed class ComboTheme
    {
        public UiPalette Palette { get; set; } = UiPalette.Light;
        public ComboTheme(ComboBox combo) => combo.DrawItem += (_, e) =>
        {
            using var brush = new SolidBrush((e.State & DrawItemState.Selected) != 0 ? Palette.Selection : Palette.Surface);
            e.Graphics.FillRectangle(brush, e.Bounds);
            if (e.Index >= 0) TextRenderer.DrawText(e.Graphics, combo.GetItemText(combo.Items[e.Index]), e.Font, e.Bounds, combo.Enabled ? Palette.Text : Palette.Muted,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        };
    }

    sealed class TabTheme
    {
        public UiPalette Palette { get; set; } = UiPalette.Light;
        public TabTheme(TabControl tabs) => tabs.DrawItem += (_, e) =>
        {
            using var brush = new SolidBrush(e.Index == tabs.SelectedIndex ? Palette.Selection : Palette.Surface);
            e.Graphics.FillRectangle(brush, e.Bounds);
            TextRenderer.DrawText(e.Graphics, tabs.TabPages[e.Index].Text, tabs.Font, e.Bounds, Palette.Text,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        };
    }

    sealed class NativeTheme
    {
        readonly Control control;
        UiPalette palette = UiPalette.Light;
        public NativeTheme(Control control)
        { this.control = control; control.HandleCreated += (_, _) => Update(); }
        public void Set(UiPalette next) { palette = next; if (control.IsHandleCreated) Update(); }
        void Update()
        {
            if (control is Form)
            {
                var dark = palette == UiPalette.Dark ? 1 : 0;
                DwmSetWindowAttribute(control.Handle, 20, ref dark, sizeof(int));
            }
            else if (control is ListView or TextBoxBase or ComboBox or ListBox or NumericUpDown or TabControl or ScrollableControl)
                SetWindowTheme(control.Handle, palette == UiPalette.Dark ? "DarkMode_Explorer" : "Explorer", null);
        }
    }
    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
    static extern int SetWindowTheme(IntPtr window, string theme, string? classes);
    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);

    public static Bitmap Icon(string glyph, Color color, int size = 18)
    {
        var bitmap = new Bitmap(size, size);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var font = new Font("Segoe MDL2 Assets", size - 3, GraphicsUnit.Pixel);
        using var brush = new SolidBrush(color);
        graphics.DrawString(glyph, font, brush, new RectangleF(0, 0, size, size), new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center });
        return bitmap;
    }
}
