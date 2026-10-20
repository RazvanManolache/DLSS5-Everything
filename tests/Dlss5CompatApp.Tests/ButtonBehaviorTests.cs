using System.Diagnostics;
using System.Reflection;

namespace Dlss5CompatApp.Tests;

public sealed class ButtonBehaviorTests
{
    [Theory]
    [InlineData(1060, 740)]
    [InlineData(1400, 900)]
    public void InstallAndVerifyAreSeparateVisibleActionsWithIndependentAvailability(int width, int height) => OnSta(() =>
    {
        using var form = Preview(width, height);
        var install = Field<Button>(form, "_install");
        var verify = Field<Button>(form, "_verify");
        Assert.Equal("Install", install.Text);
        Assert.Equal("Verify & fix", verify.Text);
        Assert.True(install.Visible);
        Assert.True(verify.Visible);
        Assert.DoesNotContain(Descendants(form).OfType<CheckBox>(), checkbox => checkbox.Text == "Launch and close to verify");
        Assert.True(form.ClientRectangle.Contains(form.RectangleToClient(verify.RectangleToScreen(verify.ClientRectangle))));
        var game = (GameCandidate)Field<ListView>(form, "_games").SelectedItems[0].Tag!;
        var cache = Field<Dictionary<string, bool>>(form, "_installed");
        Set(form, "_preview", false);
        cache[game.ExePath] = false;
        Call(form, "UpdateButtons");
        Assert.False(verify.Enabled);
        cache[game.ExePath] = true;
        Call(form, "UpdateButtons");
        Assert.True(verify.Enabled);
        Capture(form, "install-verify-" + width);
        Call(form, "SetBusy", true);
        Assert.False(install.Enabled);
        Assert.False(verify.Enabled);
        Set(form, "_preview", true);
    });

    [Fact]
    public void CancellingAmbiguousInstallationChangesNoChoiceOrGameFiles() => OnSta(() =>
    {
        using var form = Preview();
        var original = (GameCandidate)Field<ListView>(form, "_games").SelectedItems[0].Tag!;
        var game = original with { Api = GraphicsApi.DirectX11, PossibleApis = [GraphicsApi.DirectX11, GraphicsApi.Vulkan], HasVerifiedRenderer = false };
        var games = Field<List<GameCandidate>>(form, "_allGames");
        games.Clear(); games.Add(game);
        Call(form, "RenderGameRows", game.ExePath);
        Field<ListView>(form, "_games").Items[0].Selected = true;
        Application.DoEvents();
        var calls = 0;
        form.ChooseInstallation = choices => { calls++; Assert.Equal(2, choices.Count); return null; };
        var method = typeof(MainForm).GetMethod("ResolveInstallChoice", BindingFlags.Instance | BindingFlags.NonPublic)!;
        Assert.Null(method.Invoke(form, [game]));
        Assert.Equal(1, calls);
        Assert.Equal(0, Field<ComboBox>(form, "_engineChoice").SelectedIndex);
        Assert.Contains("No game files changed", Field<string>(form, "_operationOutcome"));
    });

    [Fact]
    public void AcceptedInstallationChoiceUpdatesTheActualRendererAndArguments() => OnSta(() =>
    {
        using var form = Preview();
        var original = (GameCandidate)Field<ListView>(form, "_games").SelectedItems[0].Tag!;
        var game = original with
        {
            Api = GraphicsApi.DirectX11, PossibleApis = [GraphicsApi.DirectX11, GraphicsApi.Vulkan], HasVerifiedRenderer = false,
            SuggestedArgumentsByApi = new Dictionary<GraphicsApi, string> { [GraphicsApi.Vulkan] = "-vulkan" }
        };
        var games = Field<List<GameCandidate>>(form, "_allGames");
        games.Clear(); games.Add(game);
        Call(form, "RenderGameRows", game.ExePath);
        Field<ListView>(form, "_games").Items[0].Selected = true;
        Application.DoEvents();
        form.ChooseInstallation = choices => choices.Single(choice => choice.Api == GraphicsApi.Vulkan);
        var method = typeof(MainForm).GetMethod("ResolveInstallChoice", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var chosen = Assert.IsType<GameCandidate>(method.Invoke(form, [game]));
        Assert.Equal(GraphicsApi.Vulkan, chosen.Api);
        Assert.True(Field<ComboBox>(form, "_engineChoice").SelectedIndex > 0);
        Assert.Equal("-vulkan", Field<TextBox>(form, "_launchArguments").Text);
        Assert.Null(Field<LibraryState>(form, "_libraryState").ProfileFor(game.ExePath).ApiOverride);
    });

    [Fact]
    public void RepairDialogShowsFailureAndRequiresExplicitInstallOrCancel() => OnSta(() =>
    {
        using var form = Preview();
        var game = (GameCandidate)Field<ListView>(form, "_games").SelectedItems[0].Tag!;
        Exception? failure = null;
        var observed = false;
        using var timer = new System.Windows.Forms.Timer { Interval = 100 };
        timer.Tick += (_, _) =>
        {
            var dialog = Application.OpenForms.Cast<Form>().FirstOrDefault(window => window.Owner == form);
            if (dialog is null) return;
            timer.Stop();
            try
            {
                observed = true;
                Assert.Contains("Verification failed", dialog.Text);
                Assert.Contains(Descendants(dialog).OfType<TextBox>(), text => text.Text.Contains("No processed output"));
                Assert.Contains(Descendants(dialog).OfType<Button>(), button => button.Text == "Install and verify");
                Assert.Contains(Descendants(dialog).OfType<Button>(), button => button.Text == "Cancel");
                Assert.Single(Descendants(dialog).OfType<ComboBox>());
                Capture(dialog, "repair-choice");
            }
            catch (Exception error) { failure = error; }
            finally { dialog.DialogResult = DialogResult.Cancel; dialog.Close(); }
        };
        timer.Start();
        var method = typeof(MainForm).GetMethod("ChooseInstallPlan", BindingFlags.Instance | BindingFlags.NonPublic)!;
        Assert.Null(method.Invoke(form, [game, new GameCandidate[] { game }, true, "No processed output"]));
        Assert.True(observed);
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    });

    [Theory]
    [InlineData(1060, 740)]
    [InlineData(1400, 900)]
    [InlineData(1920, 1080)]
    public void ActualListAndPreviewButtonsSwitchBothWaysAndKeepSelection(int width, int height) => OnSta(() =>
    {
        using var form = Preview(width, height);
        var games = Field<ListView>(form, "_games");
        var selected = games.Items[0];
        var list = Field<Button>(form, "_listCommand");
        var previews = Field<Button>(form, "_tilesCommand");
        Assert.Equal("List", list.Text);
        Assert.Equal("Previews", previews.Text);
        Assert.NotNull(previews.Image);
        var tools = Field<FlowLayoutPanel>(form, "_libraryViewTools");
        Assert.True(tools.ClientRectangle.Contains(list.Bounds));
        Assert.True(tools.ClientRectangle.Contains(previews.Bounds));
        previews.PerformClick();
        Assert.Equal(View.LargeIcon, games.View);
        Assert.Equal("primary", previews.Tag);
        Assert.Null(list.Tag);
        Assert.Same(selected, Assert.Single(games.SelectedItems.Cast<ListViewItem>()));
        Capture(form, "previews-" + width);
        list.PerformClick();
        Assert.Equal(View.Details, games.View);
        Assert.Equal("primary", list.Tag);
        Assert.Null(previews.Tag);
        Assert.Same(selected, Assert.Single(games.SelectedItems.Cast<ListViewItem>()));
        Capture(form, "list-" + width);
    });

    [Fact]
    public void EveryNavigationButtonAndSetupLinkChangesThePage() => OnSta(() =>
    {
        using var form = Preview();
        foreach (var (page, button) in Field<Dictionary<string, Button>>(form, "_navigation"))
        {
            button.PerformClick();
            Assert.Equal(page, Field<string>(form, "_activePage"));
            Assert.True(Field<Dictionary<string, Panel>>(form, "_pages")[page].Visible);
        }
        Call(form, "ShowPage", "Library");
        Field<Button>(form, "_setupComponents").PerformClick();
        Assert.Equal("Components", Field<string>(form, "_activePage"));
    });

    [Fact]
    public void FavoriteHideUnhideAndFiltersChangeTheActualLibrary() => OnSta(() =>
    {
        using var form = Preview();
        var game = (GameCandidate)Field<ListView>(form, "_games").SelectedItems[0].Tag!;
        Field<Button>(form, "_favorite").PerformClick();
        Assert.Contains(game.ExePath, Field<LibraryState>(form, "_libraryState").FavouritePaths);
        Field<ComboBox>(form, "_libraryFilter").SelectedIndex = 2;
        Assert.Single(Field<ListView>(form, "_games").Items.Cast<ListViewItem>());
        Field<Button>(form, "_hide").PerformClick();
        Assert.Empty(Field<ListView>(form, "_games").Items.Cast<ListViewItem>());
        Call(form, "ShowPage", "Settings");
        var hidden = Field<ListBox>(form, "_hiddenList");
        hidden.SelectedIndex = 0;
        Button(form, "Show again").PerformClick();
        Assert.Empty(Field<LibraryState>(form, "_libraryState").HiddenPaths);
        Call(form, "ShowPage", "Library");
        Assert.Single(Field<ListView>(form, "_games").Items.Cast<ListViewItem>());
    });

    [Fact]
    public void SortAndArtworkMenuClicksChangeTheirRealState() => OnSta(() =>
    {
        using var form = Preview();
        var tools = Field<FlowLayoutPanel>(form, "_libraryViewTools");
        var sort = tools.Controls.OfType<Button>().Single(item => item.AccessibleName == "Sort games").ContextMenuStrip!;
        sort.Items[3].PerformClick();
        Assert.Equal(4, Field<int>(form, "_sortColumn"));
        Assert.True(Field<bool>(form, "_sortAscending"));
        sort.Items[3].PerformClick();
        Assert.False(Field<bool>(form, "_sortAscending"));
        var artworkButton = tools.Controls.OfType<Button>().Single(item => item.AccessibleName == "Game artwork");
        var artwork = artworkButton.ContextMenuStrip!;
        artworkButton.PerformClick(); Application.DoEvents();
        var automatic = (ToolStripMenuItem)artwork.Items[0];
        Assert.True(automatic.Checked);
        automatic.PerformClick();
        Assert.False(Field<AppPreferences>(form, "_appPreferences").AutomaticArtwork);
        automatic.PerformClick();
        Assert.True(Field<AppPreferences>(form, "_appPreferences").AutomaticArtwork);
        artwork.Hide();
    });

    [Fact]
    public void SelectionDependentButtonsDisableInsteadOfSilentlyDoingNothing() => OnSta(() =>
    {
        using var form = Preview();
        foreach (var command in new[] { "Controls", "Compare", "Enable / disable", "Remove from library", "Cancel download", "Copy selected", "Remove", "Show again" })
            Assert.False(Button(form, command).Enabled, command);
        var state = Field<LibraryState>(form, "_libraryState");
        state.ScanRoots.Add(@"C:\ExampleGames");
        Call(form, "RefreshLibrarySettings");
        Field<ListBox>(form, "_rootsList").SelectedIndex = 0;
        Call(form, "ShowPage", "Settings");
        var remove = Button(form, "Remove");
        Assert.True(remove.Enabled);
        remove.PerformClick();
        Assert.Empty(state.ScanRoots);
        Assert.False(remove.Enabled);
    });

    [Fact]
    public void PlayAndOpenFolderDispatchDifferentTargetsWithoutStartingProcesses() => OnSta(() =>
    {
        using var workspace = new TestWorkspace();
        using var form = Preview();
        var root = workspace.FilePath("Game");
        Directory.CreateDirectory(root);
        var exe = Path.Combine(root, "Game.exe");
        File.WriteAllText(exe, "test fixture, never executed");
        var game = new GameCandidate(root, exe, "Fixture game", "Game.exe", CpuArch.X64, GraphicsApi.DirectX11, "test", 100);
        var games = Field<List<GameCandidate>>(form, "_allGames");
        games.Clear(); games.Add(game);
        Call(form, "RenderGameRows", game.ExePath);
        Field<ListView>(form, "_games").Items[0].Selected = true;
        var starts = new List<ProcessStartInfo>();
        form.StartExternalProcess = start => { starts.Add(start); return null; };
        Set(form, "_preview", false);
        Field<AppPreferences>(form, "_appPreferences").AutomaticArtwork = false;
        Call(form, "UpdateButtons");
        Field<TextBox>(form, "_launchArguments").Text = "-windowed -test";
        Field<Button>(form, "_runExe").PerformClick();
        Assert.Equal(exe, Assert.Single(starts).FileName);
        Assert.Equal("-windowed -test", starts[0].Arguments);
        Assert.Equal(root, starts[0].WorkingDirectory);
        Field<Button>(form, "_openFolder").PerformClick();
        Assert.Equal(2, starts.Count);
        Assert.Equal("explorer.exe", starts[1].FileName);
        Assert.StartsWith("/select,", starts[1].Arguments);
        Assert.Contains(exe, starts[1].Arguments);
        Set(form, "_preview", true);
    });

    [Fact]
    public void ResourceAndCopyButtonsReachTheirSystemBoundaryWithoutSideEffects() => OnSta(() =>
    {
        using var form = Preview();
        var starts = new List<ProcessStartInfo>();
        form.StartExternalProcess = start => { starts.Add(start); return null; };
        Call(form, "ShowPage", "Resources");
        var page = Field<Dictionary<string, Panel>>(form, "_pages")["Resources"];
        foreach (var button in Descendants(page).OfType<Button>()) button.PerformClick();
        Assert.Equal(10, starts.Count);
        Assert.All(starts, start => { Assert.Equal("https", new Uri(start.FileName).Scheme); Assert.True(start.UseShellExecute); });
        var copies = new List<string>();
        form.CopyText = copies.Add;
        Call(form, "ShowPage", "History");
        Button(form, "Copy activity").PerformClick();
        Assert.NotEmpty(Assert.Single(copies));
        var history = Field<ListView>(form, "_historyView");
        var entry = new InstallHistoryEntry { GameName = "Fixture", Message = "Synthetic test", Action = HistoryAction.Install, Outcome = HistoryOutcome.Success };
        history.Items.Add(new ListViewItem("fixture") { Tag = entry }).Selected = true;
        Assert.True(Button(form, "Copy selected").Enabled);
        Button(form, "Copy selected").PerformClick();
        Assert.Equal(entry.Summary, copies[1]);
    });

    [Fact]
    public void ContextMenuMirrorsDisabledMainActionsAndBusyStateProtectsCommands() => OnSta(() =>
    {
        using var form = Preview();
        var games = Field<ListView>(form, "_games");
        games.ContextMenuStrip!.Show(games, new Point(10, 10)); Application.DoEvents();
        Assert.False(games.ContextMenuStrip.Items[0].Enabled);
        Assert.False(games.ContextMenuStrip.Items[1].Enabled);
        Assert.False(games.ContextMenuStrip.Items[6].Enabled);
        Assert.False(games.ContextMenuStrip.Items[7].Enabled);
        games.ContextMenuStrip.Hide();
        Call(form, "SetBusy", true);
        foreach (var command in new[] { "Import Phoenix DLSS5 build", "Import model / NGX DLL", "Import add-on", "Set up Vulkan" })
            Assert.False(Button(form, command).Enabled);
        Assert.All(Field<Dictionary<string, Button>>(form, "_navigation").Values, button => Assert.True(button.Enabled));
    });

    [Theory]
    [InlineData("Controls", "Rendering")]
    [InlineData("Compare", "Comparison")]
    public void ControlsAndCompareOpenTheCorrectEditorTab(string command, string tab) => OnSta(() =>
    {
        using var workspace = new TestWorkspace();
        using var form = Preview();
        var game = new GameCandidate(workspace.Root, workspace.FilePath("Game.exe"), "Fixture", "Game.exe", CpuArch.X64, GraphicsApi.DirectX12, "test", 100);
        var candidates = Field<List<GameCandidate>>(form, "_allGames");
        candidates.Clear(); candidates.Add(game);
        Call(form, "RenderGameRows", game.ExePath);
        Field<ListView>(form, "_games").Items[0].Selected = true;
        File.WriteAllText(workspace.FilePath(PhoenixConfiguration.FileName), "{}");
        Set(form, "_preview", false);
        Call(form, "UpdateButtons");
        Assert.True(Button(form, command).Enabled);
        string? actualTab = null;
        using var timer = new System.Windows.Forms.Timer { Interval = 30 };
        timer.Tick += (_, _) =>
        {
            if (form.OwnedForms.OfType<PhoenixLiveForm>().FirstOrDefault() is not { } editor) return;
            actualTab = Descendants(editor).OfType<TabControl>().First().SelectedTab?.Text;
            timer.Stop();
            editor.Close();
        };
        timer.Start();
        Button(form, command).PerformClick();
        Assert.Equal(tab, actualTab);
        Assert.Equal("{}", File.ReadAllText(workspace.FilePath(PhoenixConfiguration.FileName)));
        Set(form, "_preview", true);
    });

    [Fact]
    public void AddonEnableDisableButtonRenamesAndRestoresTheSelectedFixtureOnly() => OnSta(() =>
    {
        using var workspace = new TestWorkspace();
        var addon = workspace.WritePe("CustomAddons/Extra.addon64", CpuArch.X64);
        var bytes = File.ReadAllBytes(addon);
        using var form = new MainForm(preview: true, payloadRoot: workspace.Root) { StartPosition = FormStartPosition.Manual, Location = new(-20000, -20000), ShowInTaskbar = false };
        form.Show(); Application.DoEvents();
        Call(form, "ShowPage", "Add-ons");
        Set(form, "_preview", false);
        Call(form, "RefreshAddons");
        var addons = Field<ListView>(form, "_addonList");
        addons.Items[0].Selected = true;
        Assert.True(Button(form, "Enable / disable").Enabled);
        Button(form, "Enable / disable").PerformClick();
        Assert.False(File.Exists(addon));
        Assert.Equal(bytes, File.ReadAllBytes(addon + ".disabled"));
        addons.Items[0].Selected = true;
        Button(form, "Enable / disable").PerformClick();
        Assert.Equal(bytes, File.ReadAllBytes(addon));
        Assert.False(File.Exists(addon + ".disabled"));
        Set(form, "_preview", true);
    });

    static MainForm Preview(int width = 1400, int height = 900)
    {
        var form = new MainForm(preview: true) { Size = new(width, height), StartPosition = FormStartPosition.Manual, Location = new(-20000, -20000), ShowInTaskbar = false };
        form.Show(); Application.DoEvents();
        return form;
    }
    static void Capture(Form form, string name)
    {
        if (Environment.GetEnvironmentVariable("DLSS5_BUTTON_CAPTURE_DIR") is not { Length: > 0 } directory) return;
        Directory.CreateDirectory(directory);
        using var bitmap = new Bitmap(form.Width, form.Height);
        form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
        bitmap.Save(Path.Combine(directory, name + ".png"));
    }
    static Button Button(MainForm form, string command) => Field<List<(Button Button, string Command)>>(form, "_commandButtons").First(entry => entry.Command == command).Button;
    static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;
    static void Set(object target, string name, object value) => target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);
    static void Call(object target, string name, params object[] arguments) => target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, arguments);
    static IEnumerable<Control> Descendants(Control control) => control.Controls.Cast<Control>().SelectMany(child => new[] { child }.Concat(Descendants(child)));
    static void OnSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() => { try { action(); } catch (Exception ex) { failure = ex; } });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "Button test did not finish.");
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
