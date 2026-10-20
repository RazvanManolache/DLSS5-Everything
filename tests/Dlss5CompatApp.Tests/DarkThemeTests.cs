using System.Reflection;

namespace Dlss5CompatApp.Tests;

public class DarkThemeTests
{
    [Theory]
    [InlineData(1060, 740)]
    [InlineData(1400, 900)]
    [InlineData(1920, 1080)]
    public void RenderEveryPageOffscreen(int width, int height) => OnSta(() =>
    {
        using var form = new MainForm(preview: true) { Size = new(width, height), StartPosition = FormStartPosition.Manual, Location = new(-20000, -20000), ShowInTaskbar = false };
        form.Show(); Application.DoEvents();
        Field<ComboBox>(form, "_themeChoice").SelectedItem = "Dark";
        foreach (var page in Field<Dictionary<string, Panel>>(form, "_pages").Keys)
        {
            Call(form, "ShowPage", page); Application.DoEvents();
            Capture(form, page + "-" + width);
        }
    });

    [Fact]
    public void DisabledButtonsAndStatusColorsRemainReadableInBothThemes() => OnSta(() =>
    {
        foreach (var palette in new[] { UiPalette.Dark, UiPalette.Light })
        {
            using var button = new Button { Text = "Unavailable", Size = new(140, 36), Enabled = false };
            UiTheme.Apply(button, palette);
            using var bitmap = new Bitmap(button.Width, button.Height);
            button.DrawToBitmap(bitmap, button.ClientRectangle);
            Assert.True(ContainsColor(bitmap, palette.Muted), "Disabled text did not use the theme color.");
            foreach (var color in new[] { palette.Text, palette.Muted, palette.Error, palette.Success })
                Assert.True(Contrast(color, palette.Surface) >= 4.5, $"{color} has poor contrast against the surface.");
        }
    });

    [Fact]
    public void DropdownAndMenuRoundTripsDoNotKeepLightChrome() => OnSta(() =>
    {
        using var form = new Form { Size = new(320, 180), StartPosition = FormStartPosition.Manual, Location = new(-20000, -20000), ShowInTaskbar = false };
        using var menu = new ContextMenuStrip();
        menu.Items.Add("Example command");
        var combo = new ThemedComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = new(15, 15), Size = new(220, 28), ContextMenuStrip = menu };
        combo.Items.AddRange(["One", "Two"]); combo.SelectedIndex = 0;
        form.Controls.Add(combo); form.Show(); Application.DoEvents();
        foreach (var palette in new[] { UiPalette.Dark, UiPalette.Light, UiPalette.Dark })
        {
            UiTheme.Apply(form, palette);
            using var bitmap = new Bitmap(combo.Width, combo.Height);
            combo.DrawToBitmap(bitmap, combo.ClientRectangle);
            Assert.Equal(palette.Surface.ToArgb(), bitmap.GetPixel(combo.Width / 2, 3).ToArgb());
            Assert.Equal(palette.Surface, menu.BackColor);
            Assert.Equal(palette.Text, menu.Items[0].ForeColor);
            combo.SelectedIndex = 1 - combo.SelectedIndex;
            Assert.Equal(DrawMode.OwnerDrawFixed, combo.DrawMode);
        }
    });

    [Fact]
    public void ProgressAndNumericControlsKeepBehaviorAndDarkBorders() => OnSta(() =>
    {
        using var progress = new ThemedProgressBar { Size = new(200, 20), Maximum = 10, Value = 5 };
        UiTheme.Apply(progress, UiPalette.Dark);
        using var bitmap = new Bitmap(progress.Width, progress.Height);
        progress.DrawToBitmap(bitmap, progress.ClientRectangle);
        Assert.Equal(UiPalette.Dark.Border.ToArgb(), bitmap.GetPixel(0, 0).ToArgb());
        Assert.Equal(UiPalette.Dark.Accent.ToArgb(), bitmap.GetPixel(20, 10).ToArgb());
        Assert.Equal(UiPalette.Dark.Surface.ToArgb(), bitmap.GetPixel(160, 10).ToArgb());
        Assert.Throws<ArgumentOutOfRangeException>(() => progress.Value = 11);
        using var number = new ThemedNumericUpDown { Minimum = 1, Maximum = 10, Value = 1 };
        UiTheme.Apply(number, UiPalette.Dark);
        number.UpButton(); Assert.Equal(2, number.Value);
        number.DownButton(); Assert.Equal(1, number.Value);
        number.DownButton(); Assert.Equal(1, number.Value);
    });

    [Fact]
    public void DialogsAndEveryPhoenixTabRenderInDarkMode() => OnSta(() =>
    {
        using var workspace = new TestWorkspace();
        File.WriteAllText(workspace.FilePath("Phoenix.json"), "{}");
        using var phoenix = new PhoenixLiveForm(workspace.Root, UiPalette.Dark) { StartPosition = FormStartPosition.Manual, Location = new(-20000, -20000), ShowInTaskbar = false };
        phoenix.Show(); Application.DoEvents();
        foreach (var tabs in Descendants(phoenix).OfType<TabControl>())
            foreach (TabPage page in tabs.TabPages)
            {
                tabs.SelectedTab = page; Application.DoEvents();
                Assert.Equal(UiPalette.Dark.Background, page.BackColor);
                Capture(phoenix, "Phoenix-" + page.Text);
            }
        phoenix.Size = phoenix.MinimumSize;
        foreach (var tabs in Descendants(phoenix).OfType<TabControl>())
        {
            tabs.SelectedIndex = 0;
            Application.DoEvents();
            Capture(phoenix, "Phoenix-minimum");
        }
        phoenix.Close();
        var font = new Font("Segoe UI", 9.5f);
        using var preferences = new ConveniencePreferencesDialog(new(), new(), font, true);
        using var recents = new ConvenienceRecentsDialog(new(), [], [], new(), font);
        using var commands = new ConvenienceCommandDialog([new("test", "Example command", "", () => false, () => { })], new(), font);
        using var report = new SupportReportForm("Example redacted report", UiPalette.Dark);
        foreach (var dialog in new Form[] { preferences, recents, commands, report })
        {
            dialog.StartPosition = FormStartPosition.Manual; dialog.Location = new(-20000, -20000); dialog.ShowInTaskbar = false;
            UiTheme.Apply(dialog, UiPalette.Dark);
            dialog.Show(); Application.DoEvents();
            Capture(dialog, dialog.GetType().Name);
            foreach (var box in Descendants(dialog).OfType<TextBox>()) Assert.Equal(UiPalette.Dark.Surface, box.BackColor);
            dialog.Close();
        }
        font.Dispose();
    });

    static IEnumerable<Control> Descendants(Control control) => control.Controls.Cast<Control>().SelectMany(child => new[] { child }.Concat(Descendants(child)));
    static bool ContainsColor(Bitmap bitmap, Color color)
    {
        for (var y = 0; y < bitmap.Height; y++)
            for (var x = 0; x < bitmap.Width; x++) if (bitmap.GetPixel(x, y).ToArgb() == color.ToArgb()) return true;
        return false;
    }
    static double Contrast(Color first, Color second)
    {
        static double Channel(byte value) { var n = value / 255.0; return n <= .04045 ? n / 12.92 : Math.Pow((n + .055) / 1.055, 2.4); }
        static double Luminance(Color value) => .2126 * Channel(value.R) + .7152 * Channel(value.G) + .0722 * Channel(value.B);
        var a = Luminance(first); var b = Luminance(second);
        return (Math.Max(a, b) + .05) / (Math.Min(a, b) + .05);
    }

    static void Capture(Form form, string name)
    {
        if (Environment.GetEnvironmentVariable("DLSS5_DARK_CAPTURE_DIR") is not { Length: > 0 } directory) return;
        Directory.CreateDirectory(directory);
        using var bitmap = new Bitmap(form.Width, form.Height);
        form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
        var safeName = string.Concat(name.Select(character => Path.GetInvalidFileNameChars().Contains(character) ? '_' : character));
        bitmap.Save(Path.Combine(directory, safeName + ".png"));
    }
    static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;
    static void Call(object target, string name, params object[] arguments) => target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, arguments);
    static void OnSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() => { try { action(); } catch (Exception ex) { failure = ex; } });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(45)), "Theme test did not finish.");
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
