using System.Diagnostics;
using System.Reflection;
using System.Text.Json;

namespace Dlss5CompatApp.Tests;

public class MyGamesTests
{
    [Fact]
    public void StartupLibraryIsSeparateFromDiscoveryAndRequiresInstallEvidence() => OnSta(() =>
    {
        using var workspace = new TestWorkspace();
        using var form = Preview();
        var installed = AddGame(workspace, "Installed", true);
        var plain = AddGame(workspace, "Not installed", false);
        var broken = AddGame(workspace, "Incomplete install", true);
        File.Delete(Path.Combine(broken.Root, "Phoenix.addon64"));
        SetCandidates(form, installed, plain, broken);
        Call(form, "ShowPage", "My Games");
        var mine = Field<ListView>(form, "_myGames");
        Assert.NotSame(mine, Field<ListView>(form, "_games"));
        Assert.Equal(installed.ExePath, ((GameCandidate)Assert.Single(mine.Items.Cast<ListViewItem>()).Tag!).ExePath);
        Assert.False(Field<TextBox>(form, "_scanRoot").Visible);
        Assert.True(Field<Button>(form, "_runExe").Visible);
        Assert.Equal("primary", Field<Button>(form, "_runExe").Tag);
        Field<TextBox>(form, "_search").Text = "Not installed";
        Field<ComboBox>(form, "_componentFilter").SelectedIndex = 1;
        Assert.Single(mine.Items.Cast<ListViewItem>());
        Assert.Equal("1 installed game", Field<Label>(form, "_gameCount").Text);
        Field<TextBox>(form, "_myGamesSearch").Text = "missing";
        Assert.Empty(mine.Items.Cast<ListViewItem>());
        Field<TextBox>(form, "_myGamesSearch").Clear();
        Assert.Single(mine.Items.Cast<ListViewItem>());
        Field<TextBox>(form, "_myGamesSearch").Text = "missing";
        Call(form, "ShowPage", "Library");
        Assert.True(Field<TextBox>(form, "_scanRoot").Visible);
        Assert.Equal("Not installed", Field<TextBox>(form, "_search").Text);
        Set(form, "_preview", true);
    });

    [Theory]
    [InlineData(false, 1060, 740)]
    [InlineData(true, 1060, 740)]
    [InlineData(false, 1920, 1080)]
    [InlineData(true, 1920, 1080)]
    public void MyGamesViewsKeepSelectionAndLaunchTheInstalledExe(bool tiles, int width, int height) => OnSta(() =>
    {
        using var workspace = new TestWorkspace();
        using var form = Preview(width, height);
        var alpha = AddGame(workspace, "Alpha", true);
        var beta = AddGame(workspace, "Beta", true);
        SetCandidates(form, alpha, beta);
        Call(form, "ShowPage", "My Games");
        var mine = Field<ListView>(form, "_myGames");
        mine.Items[0].Selected = false; mine.Items[1].Selected = true;
        var selected = mine.Items[1];
        Field<Button>(form, tiles ? "_myGamesPreviews" : "_myGamesList").PerformClick();
        Assert.Equal(tiles ? View.LargeIcon : View.Details, mine.View);
        Assert.Same(selected, Assert.Single(mine.SelectedItems.Cast<ListViewItem>()));
        var starts = new List<ProcessStartInfo>();
        form.StartExternalProcess = info => { starts.Add(info); return null; };
        Set(form, "_preview", false);
        Call(form, "UpdateButtons");
        Field<Button>(form, "_runExe").PerformClick();
        Set(form, "_preview", true);
        Assert.Equal(beta.ExePath, Assert.Single(starts).FileName);
        Call(form, "ShowPage", "Library");
        Call(form, "ShowPage", "My Games");
        Assert.Equal(beta.ExePath, ((GameCandidate)mine.SelectedItems[0].Tag!).ExePath);
        Field<Button>(form, "_favorite").PerformClick();
        Field<CheckBox>(form, "_myGamesFavorites").Checked = true;
        Assert.Equal(beta.ExePath, ((GameCandidate)Assert.Single(mine.Items.Cast<ListViewItem>()).Tag!).ExePath);
        foreach (var name in new[] { "_myGamesList", "_myGamesPreviews", "_runExe" })
        {
            var control = Field<Control>(form, name);
            Assert.True(form.ClientRectangle.Contains(form.RectangleToClient(control.RectangleToScreen(control.ClientRectangle))), name);
        }
        Capture(form, (tiles ? "MyGames-previews-" : "MyGames-list-") + width);
        Set(form, "_preview", true);
    });

    [Fact]
    public void RefreshRemovesUninstalledGamesAndAddsNewInstalls() => OnSta(() =>
    {
        using var workspace = new TestWorkspace();
        using var form = Preview();
        var old = AddGame(workspace, "Old", true);
        var next = AddGame(workspace, "Next", false);
        SetCandidates(form, old, next);
        Call(form, "ShowPage", "My Games");
        File.Delete(Path.Combine(old.Root, "_DLSS5_Compat_Backup", "manifest.json"));
        WriteInstall(next);
        var refresh = Field<List<(Button Button, string Command)>>(form, "_commandButtons").Single(entry => entry.Command == "Refresh My Games").Button;
        refresh.PerformClick();
        Assert.Equal(next.ExePath, ((GameCandidate)Assert.Single(Field<ListView>(form, "_myGames").Items.Cast<ListViewItem>()).Tag!).ExePath);
        Set(form, "_preview", true);
    });

    [Fact]
    public void ThemeNavigationAndSortingDoNotResetUnappliedGameOptions() => OnSta(() =>
    {
        using var workspace = new TestWorkspace();
        using var form = Preview();
        SetCandidates(form, AddGame(workspace, "Installed", true));
        Call(form, "ShowPage", "My Games");
        Field<TextBox>(form, "_launchArguments").Text = "-windowed -pending";
        Field<NumericUpDown>(form, "_frameIterations").Value = 2;
        Call(form, "SortMyGames", 1);
        Call(form, "ShowPage", "Settings");
        Field<ComboBox>(form, "_themeChoice").SelectedItem = "Light";
        Call(form, "ShowPage", "My Games");
        Assert.Equal("-windowed -pending", Field<TextBox>(form, "_launchArguments").Text);
        Assert.Equal(2, Field<NumericUpDown>(form, "_frameIterations").Value);
    });

    [Fact]
    public void StartupWithoutAGameScanShowsMyGames() => OnSta(() =>
    {
        // Preview page explicitly set to the production startup, with no downloads or disk settings.
        using var form = new MainForm(preview: true, previewPage: "My Games") { StartPosition = FormStartPosition.Manual, Location = new(-20000, -20000), ShowInTaskbar = false };
        form.Show(); Application.DoEvents();
        Assert.Equal("My Games", Field<string>(form, "_activePage"));
        Assert.Empty(Field<ListView>(form, "_myGames").Items.Cast<ListViewItem>());
        Assert.True(Field<FlowLayoutPanel>(form, "_myGamesEmpty").Visible);
        Assert.Equal("No DLSS games installed yet", Field<Label>(form, "_myGamesEmptyTitle").Text);
    });

    static MainForm Preview(int width = 1400, int height = 900)
    {
        var form = new MainForm(preview: true) { Size = new(width, height), StartPosition = FormStartPosition.Manual, Location = new(-20000, -20000), ShowInTaskbar = false };
        form.Show(); Application.DoEvents();
        Field<ComboBox>(form, "_themeChoice").SelectedItem = "Dark";
        Field<AppPreferences>(form, "_appPreferences").AutomaticArtwork = false;
        return form;
    }
    static GameCandidate AddGame(TestWorkspace workspace, string title, bool install)
    {
        var exe = workspace.WritePe(title + "/Game.exe", CpuArch.X64);
        var root = Path.GetDirectoryName(exe)!;
        var game = new GameCandidate(root, exe, title, "Game.exe", CpuArch.X64, GraphicsApi.DirectX11, "test fixture", 100);
        if (install) WriteInstall(game);
        return game;
    }
    static void WriteInstall(GameCandidate game)
    {
        Directory.CreateDirectory(Path.Combine(game.Root, "_DLSS5_Compat_Backup"));
        File.WriteAllText(Path.Combine(game.Root, "Phoenix.addon64"), "test fixture");
        var manifest = new InstallManifest { GameRoot = game.Root, ExeRelativePath = "Game.exe", Route = "X64DxgiFeeder", Added = ["Phoenix.addon64"] };
        File.WriteAllText(Path.Combine(game.Root, "_DLSS5_Compat_Backup", "manifest.json"), JsonSerializer.Serialize(manifest));
    }
    static void SetCandidates(MainForm form, params GameCandidate[] candidates)
    {
        // Only isolated fixture manifests are examined; never execute their binaries or save user preferences.
        var all = Field<List<GameCandidate>>(form, "_allGames");
        all.Clear(); all.AddRange(candidates);
        var cache = Field<Dictionary<string, bool>>(form, "_installed");
        form.InstallEvidence = RuntimeDiagnostics.HasInstallManifest;
        cache.Clear();
        foreach (var game in candidates) cache[game.ExePath] = RuntimeDiagnostics.HasInstallManifest(game);
        Field<LibraryState>(form, "_libraryState").ScanRoots.Clear();
        Call(form, "RenderGameRows", candidates[0].ExePath);
    }
    static void Capture(Form form, string name)
    {
        if (Environment.GetEnvironmentVariable("DLSS5_DARK_CAPTURE_DIR") is not { Length: > 0 } directory) return;
        Directory.CreateDirectory(directory);
        using var bitmap = new Bitmap(form.Width, form.Height);
        form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
        bitmap.Save(Path.Combine(directory, name + ".png"));
    }
    static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;
    static void Set(object target, string name, object value) => target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);
    static void Call(object target, string name, params object[] arguments) => target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, arguments);
    static void OnSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() => { try { action(); } catch (Exception ex) { failure = ex; } });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)));
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
