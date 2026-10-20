using System.Reflection;

namespace Dlss5CompatApp.Tests;

public class LayoutTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FeederCompatibilityWarningUsesExportsInsteadOfAReleaseHash(bool feeder)
    {
        using var workspace = new TestWorkspace();
        var addon = workspace.WritePe("Phoenix.addon64", CpuArch.X64);
        var bytes = File.ReadAllBytes(addon);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(0x96), 0x2022);
        File.WriteAllBytes(addon, bytes);
        if (feeder) TestWorkspace.SetPhoenixFeederExports(addon);
        else TestWorkspace.SetPhoenixExports(addon, "CnrPHOENIXSummary", "CnrPHOENIXRegister");
        Assert.Equal(!feeder, MainForm.PhoenixBuildWarning(addon) is not null);
        Assert.Null(MainForm.PhoenixBuildWarning(null));
        Assert.Equal("Missing: direct feeder API", MainForm.SummarizeMissingComponents(PayloadScanner.FeederApiRemediation));
    }

    [Fact]
    public void CompactColumnHeadersFitAndComponentPickerKeepsItsAccessibleName()
    {
        OnSta(() =>
        {
            using var form = new MainForm(preview: true, previewSmall: true);
            form.Show(); Application.DoEvents();
            var games = Field<ListView>(form, "_games");
            foreach (var index in new[] { 3, 4 })
            {
                var header = games.Columns[index];
                Assert.True(TextRenderer.MeasureText(header.Text, games.Font).Width + 8 <= header.Width, header.Text);
            }
            var picker = Assert.Single(Descendants(form).OfType<Button>(), button => button.AccessibleName == "Browse component folder");
            Assert.Equal("", picker.Text);
            Assert.NotNull(picker.Image);
            Assert.Equal(ContentAlignment.MiddleCenter, picker.ImageAlign);
            foreach (var label in new[] { "Find games", "Choose game" })
            {
                var button = Descendants(form).OfType<Button>().First(button => button.Text == label && button.Visible);
                var required = TextRenderer.MeasureText(button.Text, button.Font).Width + 36;
                Assert.True(required <= button.Width, $"{label}: needs {required}, has {button.Width}");
            }
            form.Close();
        });
    }

    [Fact]
    public void BusyInstallKeepsSelectionAndRouteInputsStableButAllowsNavigation()
    {
        OnSta(() =>
        {
            using var form = new MainForm(preview: true);
            form.Show(); Application.DoEvents();
            var busy = typeof(MainForm).GetMethod("SetBusy", BindingFlags.Instance | BindingFlags.NonPublic)!;
            busy.Invoke(form, [true]);
            foreach (var name in new[] { "_games", "_search", "_scanRoot", "_componentFilter", "_routeChoice", "_proxyChoice", "_engineChoice", "_frameIterations" })
                Assert.False(Field<Control>(form, name).Enabled, name);
            Assert.All(Field<Dictionary<string, Button>>(form, "_navigation").Values, button => Assert.True(button.Enabled));
            busy.Invoke(form, [false]);
            Assert.True(Field<Control>(form, "_games").Enabled);
            Assert.True(Field<Control>(form, "_routeChoice").Enabled);
            form.Close();
        });
    }

    static IEnumerable<Control> Descendants(Control control) => control.Controls.Cast<Control>().SelectMany(child => new[] { child }.Concat(Descendants(child)));

    [Theory]
    [InlineData("Phoenix.addon64 (valid x64 PE DLL with Phoenix DLSS5 exports; no Python imports), nvngx_dlss.dll (valid x64 PE DLL), nvngx_dlssnr.dll (valid x64 PE DLL)", "Missing: Phoenix DLSS5, NGX SR DLL, model DLL")]
    [InlineData(null, "Missing: components")]
    public void ReadinessUsesShortComponentNames(string? missing, string expected) => Assert.Equal(expected, MainForm.SummarizeMissingComponents(missing));

    [Fact]
    public void FirstRunShowsSetupAndKeyboardAccessibleInputs()
    {
        OnSta(() =>
        {
            using var form = new MainForm(preview: true, previewSmall: true);
            form.Show(); Application.DoEvents();
            foreach (var name in new[] { "_scanRoot", "_search", "_payloadRoot", "_engineChoice", "_launchArguments", "_frameIterations", "_releaseChannel" })
                Assert.False(string.IsNullOrWhiteSpace(Field<Control>(form, name).AccessibleName), name);
            Assert.IsType<Button>(Field<Control>(form, "_dropZoneText"));
            Assert.True(Field<Control>(form, "_dropZoneText").TabStop);
            var setup = Assert.Single(Descendants(form), control => control.Text == "Set up components");
            Assert.True(setup.Visible);
            var bounds = form.RectangleToClient(setup.RectangleToScreen(setup.ClientRectangle));
            Assert.True(form.ClientRectangle.Contains(bounds));
            Assert.False(Field<Control>(form, "_launchArguments").Visible);
            var advanced = Assert.Single(Descendants(form).OfType<CheckBox>(), control => control.AccessibleName == "Show advanced game options");
            advanced.Checked = true;
            Assert.True(Field<Control>(form, "_launchArguments").Visible);
            form.Close();
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ComponentActionsFitMinimumWindow(bool light)
    {
        OnSta(() =>
        {
            using var form = new MainForm(preview: true, previewPage: "Components", previewSmall: true, previewLight: light);
            form.Show(); Application.DoEvents();
            var pages = Field<Dictionary<string, Panel>>(form, "_pages");
            foreach (var button in Descendants(pages["Components"]).OfType<Button>().Where(button => button.Visible))
            {
                pages["Components"].ScrollControlIntoView(button);
                Application.DoEvents();
                Assert.True(button.Visible, button.Text);
                var bounds = pages["Components"].RectangleToClient(button.RectangleToScreen(button.ClientRectangle));
                Assert.True(pages["Components"].ClientRectangle.Contains(bounds), $"{button.Text}: {bounds}");
            }
            form.Close();
        });
    }

    [Fact]
    public void EmptySearchOffersRecoveryAndOperationFailureRemainsVisible()
    {
        OnSta(() =>
        {
            using var form = new MainForm(preview: true);
            form.Show(); Application.DoEvents();
            Field<TextBox>(form, "_search").Text = "no matches here";
            Assert.True(Field<Control>(form, "_emptyState").Visible);
            Assert.Equal("No matching games", Field<Label>(form, "_emptyTitle").Text);
            Assert.Single(Descendants(form).OfType<Button>(), button => button.Text == "Clear filters / show all").PerformClick();
            Assert.False(Field<Control>(form, "_emptyState").Visible);
            typeof(MainForm).GetMethod("SetOutcome", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(form, ["Install failed: locked file"]);
            typeof(MainForm).GetMethod("RefreshPayload", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(form, null);
            Assert.Equal("Install failed: locked file", Field<Label>(form, "_componentMessage").Text);
            form.Close();
        });
    }

    [Fact]
    public void DisabledPrimaryButtonIsNotStyledAsAvailable()
    {
        OnSta(() =>
        {
            using var button = new Button { Tag = "primary" };
            UiTheme.Apply(button, UiPalette.Dark);
            Assert.Equal(UiPalette.Dark.Accent, button.BackColor);
            button.Enabled = false;
            Assert.Equal(UiPalette.Dark.Surface, button.BackColor);
            button.Enabled = true;
            Assert.Equal(UiPalette.Dark.Accent, button.BackColor);
        });
    }

    [Fact]
    public void RouteAndProxyChoicesAreBoundToSavedProfilesAndInstallCandidate()
    {
        var phase = "construct form";
        OnSta(() =>
        {
            using var form = new MainForm(preview: true);
            phase = "show form";
            form.Show(); Application.DoEvents();
            var games = Field<ListView>(form, "_games");
            phase = "clear selection";
            foreach (ListViewItem item in games.Items) item.Selected = false;
            var modern = games.Items.Cast<ListViewItem>().Single(item => ((GameCandidate)item.Tag!).Api == GraphicsApi.DirectX12);
            phase = "select modern game";
            modern.Selected = true;
            phase = "choose native route";
            Field<ComboBox>(form, "_routeChoice").SelectedIndex = 1;
            GameCandidate Selected() => (GameCandidate)typeof(MainForm).GetMethod("SelectedGame", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(form, null)!;
            Assert.Equal(InstallRoute.X64DirectPhoenix, Selected().Route);
            phase = "save native profile";
            typeof(MainForm).GetMethod("SaveSelectedProfile", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(form, null);
            Assert.Equal(GameRoutePreference.Native, Field<LibraryState>(form, "_libraryState").ProfileFor(Selected().ExePath).PreferredRoute);
            phase = "choose feeder route";
            Field<ComboBox>(form, "_routeChoice").SelectedIndex = 2;
            Assert.Equal(InstallRoute.X64DxgiFeeder, Selected().Route);
            phase = "deselect modern game";
            modern.Selected = false;
            phase = "select first game";
            games.Items[0].Selected = true;
            phase = "deselect first game";
            games.Items[0].Selected = false;
            phase = "reselect modern game";
            modern.Selected = true;
            Assert.Equal(1, Field<ComboBox>(form, "_routeChoice").SelectedIndex);
            Assert.Equal(InstallRoute.X64DirectPhoenix, Selected().Route);
            phase = "close form";
            form.Close();
        }, () => phase);
    }

    [Fact]
    public void ComparisonPresetStagesChangesWithoutWritingConfiguration()
    {
        OnSta(() =>
        {
            using var directory = new TestWorkspace();
            var path = directory.FilePath("ReShade.ini");
            File.WriteAllText(path, "[Phoenix]\nBackend=0\nComparison=0\nSourceA=1\nSourceB=0\nDivider=0.2\n");
            using var form = new PhoenixLiveForm(directory.Root, initialTab: "Comparison");
            form.Show(); Application.DoEvents();
            var preset = Assert.Single(Descendants(form).OfType<Button>(), button => button.Text == "Before / after");
            preset.PerformClick();
            Assert.Equal("[Phoenix]\nBackend=0\nComparison=0\nSourceA=1\nSourceB=0\nDivider=0.2\n", File.ReadAllText(path));
            var apply = Assert.Single(Descendants(form).OfType<Button>(), button => button.Text == "Apply & save");
            Assert.True(apply.Enabled);
            apply.PerformClick();
            Assert.Contains("Backend=3", File.ReadAllText(path));
            Assert.Contains("SourceB=2", File.ReadAllText(path));
            Assert.False(apply.Enabled);
            form.Close();
        });
    }

    [Theory]
    [InlineData(1060, 740)]
    [InlineData(1400, 900)]
    [InlineData(1920, 1080)]
    public void PrimaryActionsStayVisibleWhenWindowResizes(int width, int height)
    {
        OnSta(() =>
        {
            using var form = new MainForm(preview: true) { Size = new Size(width, height) };
            form.Show();
            form.PerformLayout();
            Application.DoEvents();
            foreach (var name in new[] { "_install", "_runExe", "_restore", "_openFolder" })
            {
                var button = Field<Button>(form, name);
                Assert.True(button.Visible, name);
                var bounds = form.RectangleToClient(button.RectangleToScreen(button.ClientRectangle));
                Assert.True(form.ClientRectangle.Contains(bounds), $"{name}: {bounds}, client {form.ClientRectangle}");
                Assert.True(button.Width >= 100, name);
                Assert.True(button.Height >= 28, name);
            }
            Assert.False(Field<Button>(form, "_install").Enabled);
            form.Close();
        });
    }

    [Fact]
    public void SearchAndFavouriteFilterWorkWithoutChangingGameFiles()
    {
        OnSta(() =>
        {
            using var form = new MainForm(preview: true);
            form.Show();
            Application.DoEvents();
            var games = Field<ListView>(form, "_games");
            Assert.Equal(5, games.Items.Count);
            var search = Field<TextBox>(form, "_search");
            search.Text = "Spider-Man";
            Assert.Single(games.Items.Cast<ListViewItem>());
            Assert.Equal("Spider-Man 3", games.Items[0].Text);
            search.Text = "no matches here";
            Assert.Empty(games.Items.Cast<ListViewItem>());
            search.Text = "";
            games.Items[0].Selected = true;
            Field<Button>(form, "_favorite").PerformClick();
            Field<ComboBox>(form, "_libraryFilter").SelectedItem = "Favorites";
            Assert.Single(games.Items.Cast<ListViewItem>());
            form.Close();
        });
    }

    [Fact]
    public void RendererOverrideDoesNotChangeDetectedCandidate()
    {
        OnSta(() =>
        {
            using var form = new MainForm(preview: true);
            form.Show();
            Application.DoEvents();
            var games = Field<ListView>(form, "_games");
            games.Items[0].Selected = true;
            var detected = (GameCandidate)games.Items[0].Tag!;
            var combo = Field<ComboBox>(form, "_engineChoice");
            Assert.True(combo.Items.Count > detected.AllApis.Count);
            combo.SelectedIndex = combo.Items.Count - 1;
            Assert.Equal(GraphicsApi.DirectX9, detected.Api);
            form.Close();
        });
    }

    static T Field<T>(MainForm form, string name) => (T)typeof(MainForm).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form)!;
    static void OnSta(Action action, Func<string>? phase = null)
    {
        Exception? failure = null;
        var thread = new Thread(() => { try { action(); } catch (Exception ex) { failure = ex; } });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(15)), "UI test did not finish. " + phase?.Invoke());
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
