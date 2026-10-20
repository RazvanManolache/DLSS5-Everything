using System.Reflection;

namespace Dlss5CompatApp.Tests;

public sealed class ConvenienceTests
{
    [Fact]
    public void DefaultsAreLocalAndStartupActionsAreOptIn()
    {
        using var workspace = new TestWorkspace();
        var file = workspace.FilePath("preferences.json");
        var preferences = AppPreferences.Load(file);
        Assert.False(preferences.CloseToTray);
        Assert.False(preferences.ScanOnStartup);
        Assert.False(preferences.UseTiles);
        Assert.False(preferences.CompactNavigation);
        Assert.Equal("en", preferences.Language);
        Assert.Empty(preferences.Recents);
        Assert.Equal("", preferences.RecentQuery);
        Assert.False(File.Exists(file));
        Assert.Equal("preferences.json", Path.GetFileName(AppPreferences.DefaultPath));
    }

    [Fact]
    public void PreferencesRoundTripWithBoundedCaseInsensitiveRecentsAndFilter()
    {
        using var workspace = new TestWorkspace();
        var file = workspace.FilePath("preferences.json");
        var preferences = new AppPreferences
        {
            CloseToTray = true, ScanOnStartup = true, CompactNavigation = true, UseTiles = true,
            RecentLimit = 3, Language = "ro", RecentQuery = "example", OnlyAvailableRecents = true
        };
        var start = DateTimeOffset.UtcNow.AddMinutes(-10);
        for (var i = 0; i < 8; i++) preferences.RecordViewed(workspace.FilePath($"game{i}.exe"), "Game " + i, start.AddMinutes(i));
        preferences.RecordViewed(workspace.FilePath("GAME7.EXE"), "New name", start.AddMinutes(9));
        preferences.Save(file);
        var loaded = AppPreferences.Load(file);
        Assert.Equal(1, loaded.Revision);
        Assert.True(loaded.CloseToTray && loaded.ScanOnStartup && loaded.CompactNavigation && loaded.UseTiles);
        Assert.True(loaded.OnlyAvailableRecents);
        Assert.Equal("example", loaded.RecentQuery);
        Assert.Equal("ro", loaded.Language);
        Assert.Equal(3, loaded.Recents.Count);
        Assert.Equal("New name", loaded.Recents[0].Name);
        Assert.Equal(3, loaded.Recents.Select(r => r.ExePath).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.InRange(new FileInfo(file).Length, 1, AppPreferences.MaximumFileBytes);
        loaded.RememberRecents = false;
        loaded.Save(file);
        Assert.Empty(AppPreferences.Load(file).Recents);
        Assert.False(loaded.RecordViewed(workspace.FilePath("ignored.exe"), "Ignored"));
    }

    [Theory]
    [InlineData("{bad")]
    [InlineData("null")]
    [InlineData("{\"SchemaVersion\":2}")]
    [InlineData("{\"Language\":\"unknown\"}")]
    [InlineData("{\"RecentLimit\":101}")]
    [InlineData("{\"RecentLimit\":0}")]
    [InlineData("{\"Recents\":null}")]
    [InlineData("{\"RecentQuery\":null}")]
    public void CorruptOrUnsupportedPreferencesCannotBeOverwritten(string original)
    {
        using var workspace = new TestWorkspace();
        var file = workspace.FilePath("preferences.json");
        File.WriteAllText(file, original);
        Assert.ThrowsAny<Exception>(() => AppPreferences.Load(file));
        Assert.ThrowsAny<Exception>(() => new AppPreferences().Save(file));
        Assert.Equal(original, File.ReadAllText(file));
    }

    [Fact]
    public void StaleSaveAndOversizedFilesPreserveSavedData()
    {
        using var workspace = new TestWorkspace();
        var file = workspace.FilePath("preferences.json");
        var first = AppPreferences.Load(file);
        var stale = AppPreferences.Load(file);
        first.Language = "de";
        first.Save(file);
        stale.CloseToTray = true;
        Assert.Throws<InvalidOperationException>(() => stale.Save(file));
        Assert.Equal("de", AppPreferences.Load(file).Language);
        var large = new string(' ', AppPreferences.MaximumFileBytes + 1);
        File.WriteAllText(file, large);
        Assert.Throws<InvalidDataException>(() => AppPreferences.Load(file));
        Assert.Throws<InvalidDataException>(() => first.Save(file));
        Assert.Equal(large, File.ReadAllText(file));
    }

    [Theory]
    [InlineData("relative.exe")]
    [InlineData("C:relative.exe")]
    [InlineData("\\\\server\\games\\game.exe")]
    [InlineData("C:\\games\\game.exe:stream")]
    [InlineData("C:\\games\\file.txt")]
    public void RecentsRejectNonLocalOrNonExecutablePathsWithoutChangingList(string path)
    {
        var preferences = new AppPreferences();
        Assert.ThrowsAny<Exception>(() => preferences.RecordViewed(path, "Example"));
        Assert.Empty(preferences.Recents);
    }

    [Fact]
    public void MalformedEntriesAndUnboundedQueriesCannotPersist()
    {
        using var workspace = new TestWorkspace();
        var preferences = new AppPreferences { RecentQuery = new string('x', 257) };
        Assert.Throws<InvalidDataException>(() => preferences.Save(workspace.FilePath("preferences.json")));
        preferences.RecentQuery = "";
        preferences.Recents = [new(workspace.FilePath("example.exe"), "Example", default)];
        Assert.Throws<InvalidDataException>(() => preferences.Validate());
        preferences.Recents = Enumerable.Repeat(new RecentGame(workspace.FilePath("example.exe"), "Example", DateTimeOffset.UtcNow), 101).ToList();
        Assert.Throws<InvalidDataException>(() => preferences.Validate());
    }

    [Fact]
    public void All38ReferenceLanguagesHaveRealCommonActionTranslationsAndExplicitFallback()
    {
        var expected = "en ar zh es pt ru de fr ja ko it tr pl zh-TW uk nl cs hu ro el sv da nb fi th vi id ms fil hi bn fa ur bg sr hr sk ca".Split(' ');
        Assert.Equal(expected, UiLocalization.Languages.Select(l => l.Code));
        foreach (var language in UiLocalization.Languages)
        {
            var local = new UiLocalization(language.Code);
            Assert.True(UiLocalization.Supports(language.Code));
            Assert.False(local.UsesFallback("navSettings"));
            Assert.False(local.UsesFallback("restore"));
            Assert.False(string.IsNullOrWhiteSpace(local["navSettings"]));
            Assert.False(string.IsNullOrWhiteSpace(local["restore"]));
            Assert.Equal(language.Code != "en", local.IsPartial);
            if (language.Code != "en") Assert.Contains("English", local.CoverageText);
        }
        Assert.True(new UiLocalization("ar").RightToLeft);
        Assert.True(new UiLocalization("fa").RightToLeft);
        Assert.True(new UiLocalization("ur").RightToLeft);
        Assert.False(new UiLocalization("ro").RightToLeft);
        Assert.True(new UiLocalization("ro").UsesFallback("startupScan"));
        Assert.Equal(new UiLocalization()["startupScan"], new UiLocalization("ro")["startupScan"]);
        Assert.Equal("en", new UiLocalization("unknown").Code);
        Assert.Contains("Copyright (c) 2026 Rakan Alkhaldi", UiLocalization.TranslationLicense);
        Assert.Contains("Permission is hereby granted", UiLocalization.TranslationLicense);
    }

    [Theory]
    [InlineData(1060, 740)]
    [InlineData(1400, 900)]
    public void NativeViewsPreserveRowsTagsSelectionAndSortAndCompactNavigationIsAccessible(int width, int height)
    {
        OnSta(() =>
        {
            using var form = Preview(width, height);
            var games = Field<ListView>(form, "_games");
            var selected = games.Items[1];
            selected.Selected = true;
            selected.Focused = true;
            var rows = games.Items.Cast<ListViewItem>().ToArray();
            var tags = rows.Select(r => r.Tag).ToArray();
            SetField(form, "_sortColumn", 1);
            SetField(form, "_sortAscending", false);
            var preferences = Field<AppPreferences>(form, "_appPreferences");
            preferences.UseTiles = true;
            preferences.CompactNavigation = true;
            preferences.Language = "ro";
            Call(form, "ApplyConveniencePreferences");
            Application.DoEvents();
            Assert.Equal(View.LargeIcon, games.View);
            Assert.NotNull(games.LargeImageList);
            Assert.True(games.LargeImageList.Images.Count > 0);
            Assert.All(rows, row => Assert.InRange(row.ImageIndex, 0, games.LargeImageList.Images.Count - 1));
            Assert.Equal(rows, games.Items.Cast<ListViewItem>());
            Assert.Equal(tags, games.Items.Cast<ListViewItem>().Select(r => r.Tag));
            Assert.Same(selected, Assert.Single(games.SelectedItems.Cast<ListViewItem>()));
            Assert.Equal(1, Field<int>(form, "_sortColumn"));
            Assert.False(Field<bool>(form, "_sortAscending"));
            var navigation = Field<Dictionary<string, Button>>(form, "_navigation");
            Assert.All(navigation.Values, button =>
            {
                Assert.Equal("", button.Text);
                Assert.False(string.IsNullOrWhiteSpace(button.AccessibleName));
                Assert.True(button.Width >= 32);
                Assert.True(button.TabStop);
            });
            AssertPrimaryActionsInClient(form);
            Capture(form, $"library-tiles-ro-{width}");
            preferences.UseTiles = false;
            preferences.CompactNavigation = false;
            Call(form, "ApplyConveniencePreferences");
            Assert.Equal(View.Details, games.View);
            Assert.Same(selected, Assert.Single(games.SelectedItems.Cast<ListViewItem>()));
            Assert.All(navigation.Values, button =>
            {
                Assert.False(string.IsNullOrWhiteSpace(button.AccessibleName));
                Assert.Equal(width < 1180, string.IsNullOrWhiteSpace(button.Text));
            });
            Call(form, "ShowPage", "Settings");
            Application.DoEvents();
            var button = form.Controls.Find("ConveniencePreferences", true).Single();
            Assert.True(button.Visible);
            Assert.True(form.ClientRectangle.Contains(form.RectangleToClient(button.RectangleToScreen(button.ClientRectangle))));
            var settings = Field<Dictionary<string, Panel>>(form, "_pages")["Settings"];
            var scroll = Assert.Single(settings.Controls.OfType<Panel>(), p => p.AutoScroll);
            Assert.True(scroll.Controls[0].Height >= 560);
            var contentBounds = form.RectangleToClient(scroll.RectangleToScreen(scroll.ClientRectangle));
            var buttonBounds = form.RectangleToClient(button.RectangleToScreen(button.ClientRectangle));
            Assert.True(contentBounds.Bottom <= buttonBounds.Top, "Settings content must not overlap application preferences.");
            Capture(form, $"settings-ro-{width}");
            form.Close();
        });
    }

    [Fact]
    public void LocalizedParentFiltersKeepWorkingAndInitializeIsIdempotent()
    {
        OnSta(() =>
        {
            using var form = Preview();
            Call(form, "InitializeConvenience");
            Assert.Single(form.Controls.Find("ConvenienceToolbar", true));
            Field<AppPreferences>(form, "_appPreferences").Language = "fr";
            Call(form, "ApplyConveniencePreferences");
            Field<TextBox>(form, "_search").Text = "Spider-Man";
            var games = Field<ListView>(form, "_games");
            Assert.Single(games.Items.Cast<ListViewItem>());
            games.Items[0].Selected = true;
            Field<Button>(form, "_favorite").PerformClick();
            Field<ComboBox>(form, "_libraryFilter").SelectedItem = "Favorites";
            Assert.Single(games.Items.Cast<ListViewItem>());
            Assert.Equal("Favorites", Field<ComboBox>(form, "_libraryFilter").SelectedItem);
            Call(form, "ShowPage", "History");
            Assert.Equal(new UiLocalization("fr")["navHistory"], Field<Label>(form, "_pageTitle").Text);
            form.Close();
        });
    }

    [Fact]
    public void PaletteListsExpectedCommandsAndHonorsPreviewAndBusyGates()
    {
        OnSta(() =>
        {
            using var form = Preview();
            Field<AppPreferences>(form, "_appPreferences").Language = "ar";
            Call(form, "ApplyConveniencePreferences");
            var commands = (IReadOnlyList<ConvenienceCommand>)Call(form, "BuildConvenienceCommands")!;
            foreach (var id in new[] { "page:Library", "page:Components", "scan", "play", "open-folder", "recent", "preferences", "list", "tiles" })
                Assert.Contains(commands, c => c.Id == id);
            Assert.False(commands.Single(c => c.Id == "play").CanExecute());
            Assert.False(commands.Single(c => c.Id == "scan").CanExecute());
            Assert.False(commands.Single(c => c.Id == "open-folder").CanExecute());
            Assert.True(commands.Single(c => c.Id == "page:Library").CanExecute());
            Assert.True(commands.Single(c => c.Id == "page:Components").Matches("components"));
            using var dialog = new ConvenienceCommandDialog(commands, new UiLocalization("ar"), form.Font);
            dialog.Show();
            Application.DoEvents();
            Assert.True(dialog.RightToLeftLayout);
            Capture(dialog, "palette-ar");
            dialog.Close();
            form.Close();
        });
    }

    [Fact]
    public void RecentlyViewedGamesSurviveRerenderWithoutDuplicatesAndRespectPrivacyToggle()
    {
        OnSta(() =>
        {
            using var form = Preview();
            var games = Field<ListView>(form, "_games");
            games.Items[1].Selected = true;
            var path = ((GameCandidate)games.Items[1].Tag!).ExePath;
            var preferences = Field<AppPreferences>(form, "_appPreferences");
            var viewed = preferences.Recents.Single(r => r.ExePath == path).ViewedAt;
            Call(form, "RenderGameRows", path);
            Assert.Equal(viewed, preferences.Recents.Single(r => r.ExePath == path).ViewedAt);
            preferences.RememberRecents = false;
            preferences.Validate();
            games.Items[2].Selected = true;
            Assert.Empty(preferences.Recents);
            form.Close();
        });
    }

    [Fact]
    public void RecentsFilterKeepsHiddenEntriesOutAndStaleEntriesCannotBeOpened()
    {
        OnSta(() =>
        {
            using var workspace = new TestWorkspace();
            var preferences = new AppPreferences();
            var known = workspace.FilePath("known.exe");
            var stale = workspace.FilePath("stale.exe");
            var hidden = workspace.FilePath("hidden.exe");
            preferences.RecordViewed(known, "Example known");
            preferences.RecordViewed(stale, "Example stale");
            preferences.RecordViewed(hidden, "Example hidden");
            using var dialog = new ConvenienceRecentsDialog(preferences, new(StringComparer.OrdinalIgnoreCase) { known }, new(StringComparer.OrdinalIgnoreCase) { hidden }, new UiLocalization(), SystemFonts.MessageBoxFont!);
            dialog.Show();
            Application.DoEvents();
            var list = Field<ListView>(dialog, "_recentList");
            Assert.Equal(2, list.Items.Count);
            list.Items.Cast<ListViewItem>().Single(r => ((RecentGame)r.Tag!).ExePath == stale).Selected = true;
            Assert.False(Field<Button>(dialog, "_view").Enabled);
            Field<TextBox>(dialog, "_query").Text = "EXAMPLE known";
            Assert.Single(list.Items.Cast<ListViewItem>());
            list.Items[0].Selected = true;
            Assert.True(Field<Button>(dialog, "_view").Enabled);
            Capture(dialog, "recents-filtered");
            dialog.Close();
        });
    }

    [Fact]
    public void PreferencesDialogKeepsCancelIsolatedAndDisplaysSaveFailures()
    {
        OnSta(() =>
        {
            var original = new AppPreferences();
            using var dialog = new ConveniencePreferencesDialog(original.Copy(), new UiLocalization("ro"), SystemFonts.MessageBoxFont!, true)
            {
                Size = new Size(540, 530), SaveRequested = _ => "Simulated write failure"
            };
            dialog.Show();
            Application.DoEvents();
            ((Button)dialog.AcceptButton!).PerformClick();
            Assert.NotEqual(DialogResult.OK, dialog.DialogResult);
            Assert.Contains(Descendants(dialog).OfType<Label>(), label => label.Text.Contains("Simulated write failure"));
            var save = (Button)dialog.AcceptButton;
            Assert.True(dialog.ClientRectangle.Contains(dialog.RectangleToClient(save.RectangleToScreen(save.ClientRectangle))));
            Capture(dialog, "preferences-ro-minimum");
            Assert.False(original.CloseToTray);
            Assert.Equal("en", original.Language);
            dialog.Close();
        });
    }

    [Theory]
    [InlineData(Keys.K)]
    [InlineData(Keys.P)]
    public void BothPaletteShortcutsOpenAndNavigateWithoutLaunchingGames(Keys key)
    {
        OnSta(() =>
        {
            using var form = Preview();
            using var timer = new System.Windows.Forms.Timer { Interval = 100 };
            var opened = false;
            timer.Tick += (_, _) =>
            {
                if (Application.OpenForms.OfType<ConvenienceCommandDialog>().FirstOrDefault() is not { } palette) return;
                opened = true;
                timer.Stop();
                Field<TextBox>(palette, "_query").Text = "Components";
                Call(palette, "ProcessCmdKey", new Message(), Keys.Enter);
            };
            timer.Start();
            Assert.True((bool)Call(form, "ProcessCmdKey", new Message(), Keys.Control | key)!);
            Assert.True(opened);
            Assert.Equal("Components", Field<string>(form, "_activePage"));
            form.Close();
        });
    }

    [Fact]
    public void TrayCloseRestoreAndExplicitExitPreserveLifetimeUntilExit()
    {
        OnSta(() =>
        {
            using var form = Preview();
            // Switch only after preview Shown has completed; no startup scan/download runs in this fixture.
            SetField(form, "_preview", false);
            Field<AppPreferences>(form, "_appPreferences").CloseToTray = true;
            var lifetime = Field<CancellationTokenSource>(form, "_lifetimeCts");
            form.Close();
            Assert.False(form.IsDisposed);
            Assert.False(form.Visible);
            Assert.False(lifetime.IsCancellationRequested);
            var tray = Field<NotifyIcon>(form, "_convenienceTray");
            Assert.True(tray.Visible);
            Call(form, "RestoreConvenienceWindow");
            Assert.True(form.Visible);
            Assert.False(tray.Visible);
            SetField(form, "_exitFromTray", true);
            form.Close();
            Assert.True(form.IsDisposed);
            Assert.True(lifetime.IsCancellationRequested);
        });
    }

    static MainForm Preview(int width = 1060, int height = 740)
    {
        var form = new MainForm(preview: true) { Size = new Size(width, height) };
        Call(form, "InitializeConvenience");
        form.Show();
        Application.DoEvents();
        return form;
    }

    static void AssertPrimaryActionsInClient(MainForm form)
    {
        foreach (var name in new[] { "_install", "_runExe", "_restore", "_openFolder" })
        {
            var button = Field<Button>(form, name);
            Assert.True(button.Visible);
            Assert.True(form.ClientRectangle.Contains(form.RectangleToClient(button.RectangleToScreen(button.ClientRectangle))));
        }
    }

    static IEnumerable<Control> Descendants(Control root) => root.Controls.Cast<Control>().SelectMany(c => new[] { c }.Concat(Descendants(c)));
    static T Field<T>(object instance, string name) => (T)instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(instance)!;
    static void SetField(object instance, string name, object value) => instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(instance, value);
    static object? Call(object instance, string name, params object?[] args) => instance.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(instance, args);
    static void Capture(Form form, string name)
    {
        if (Environment.GetEnvironmentVariable("DLSS5_CONVENIENCE_CAPTURE_DIR") is not { Length: > 0 } root) return;
        Directory.CreateDirectory(root);
        using var image = new Bitmap(form.Width, form.Height);
        form.DrawToBitmap(image, new Rectangle(Point.Empty, image.Size));
        image.Save(Path.Combine(root, name + ".png"), System.Drawing.Imaging.ImageFormat.Png);
    }
    static void OnSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() => { try { action(); } catch (Exception ex) { failure = ex; } });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "Convenience UI test did not finish.");
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
