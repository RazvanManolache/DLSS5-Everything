using System.Buffers.Binary;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text;

namespace Dlss5CompatApp.Tests;

public sealed class WorkflowSafetyTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InvalidPayloadAtStartupLeavesARecoverableWindow(bool x86Only)
    {
        OnSta(() =>
        {
            using var workspace = new TestWorkspace();
            var model = workspace.FilePath("nvngx_dlssnr.dll");
            if (x86Only) WriteDll(workspace, "nvngx_dlssnr.dll", CpuArch.X86);
            else File.WriteAllText(model, "invalid DLL");
            using var form = new MainForm(preview: true, payloadRoot: workspace.Root);
            Assert.False(Field<PayloadInfo>(form, "_payload").HasCoreDlss);
            Assert.DoesNotContain("Component scan failed:", Field<Label>(form, "_componentMessage").Text);
            Assert.False(Field<Button>(form, "_install").Enabled);
            Assert.True(Field<Button>(form, "_updatePayload").Enabled);

            WriteDll(workspace, "nvngx_dlssnr.dll", CpuArch.X64);
            Invoke(form, "RefreshPayload");
            Assert.NotNull(Field<PayloadInfo?>(form, "_payload"));
            Assert.DoesNotContain("Component scan failed:", Field<Label>(form, "_componentMessage").Text);
            Invoke(form, "SetOutcome", "Installation failed: keep this result");
            Invoke(form, "RefreshPayload");
            Assert.Equal("Installation failed: keep this result", Field<Label>(form, "_componentMessage").Text);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public void InvalidModelRefreshRemovesPreviouslyReadyModel()
    {
        OnSta(() =>
        {
            using var fixture = new WorkflowFixture();
            using var form = new MainForm(preview: true, payloadRoot: fixture.PayloadRoot);
            Assert.True(Field<PayloadInfo>(form, "_payload").IsReadyFor(fixture.Game.Route));
            File.WriteAllText(Path.Combine(fixture.PayloadRoot, "nvngx_dlssnr.dll"), "corrupted after scanning");
            Invoke(form, "RefreshPayload");
            var payload = Field<PayloadInfo>(form, "_payload");
            Assert.False(payload.HasCoreDlss);
            Assert.False(payload.IsReadyFor(fixture.Game.Route));
            return Task.CompletedTask;
        });
    }

    [Theory]
    [InlineData(DialogResult.No)]
    [InlineData(DialogResult.Cancel)]
    public void TemporaryRunDeclinedAntiCheatReviewDoesNotInstallOrLaunch(DialogResult answer)
    {
        OnSta(async () =>
        {
            using var fixture = new WorkflowFixture();
            File.WriteAllText(Path.Combine(fixture.Game.Root, "EasyAntiCheat_EOS_Setup.exe"), "marker only");
            using var form = new MainForm(preview: true, payloadRoot: fixture.PayloadRoot);
            Assert.True(Field<PayloadInfo>(form, "_payload").IsReadyFor(fixture.Game.Route));
            var originals = Directory.GetFiles(fixture.Game.Root).ToDictionary(path => path, File.ReadAllBytes);
            var prompts = 0;
            form.TemporaryInstallPrompt = (message, title, buttons, defaultButton) =>
            {
                prompts++;
                Assert.Equal("Anti-cheat warning", title);
                Assert.Contains("EasyAntiCheat_EOS_Setup.exe", message);
                Assert.Equal(MessageBoxButtons.YesNo, buttons);
                Assert.Equal(MessageBoxDefaultButton.Button2, defaultButton);
                return answer;
            };
            await (Task)Invoke(form, "RunTemporaryWindowsGameAsync", fixture.Game)!;
            Assert.Equal(1, prompts);
            Assert.Equal(originals.Keys.OrderBy(path => path), Directory.GetFiles(fixture.Game.Root).OrderBy(path => path));
            foreach (var (path, contents) in originals) Assert.Equal(contents, File.ReadAllBytes(path));
            Assert.False(Directory.Exists(Path.Combine(fixture.Game.Root, "_DLSS5_Compat_Backup")));
            Assert.False(Field<bool>(form, "_busy"));
            Assert.Contains("cancelled", Field<Label>(form, "_componentMessage").Text);
            Assert.DoesNotContain("Launched ", Field<TextBox>(form, "_log").Text);
        });
    }

    [Fact]
    public void TemporaryReviewRequiresExplicitYesAndCannotOverrideErrors()
    {
        OnSta(() =>
        {
            using var workspace = new TestWorkspace();
            using var form = new MainForm(preview: true, payloadRoot: workspace.Root);
            var warnings = new InstallPreflightResult([new("anti-cheat", PreflightSeverity.Warning, "marker")]);
            foreach (var answer in new[] { DialogResult.None, DialogResult.No, DialogResult.Cancel, DialogResult.OK, DialogResult.Yes })
            {
                form.TemporaryInstallPrompt = (_, _, _, _) => answer;
                Assert.Equal(answer == DialogResult.Yes, form.ConfirmTemporaryInstall(warnings));
            }
            form.TemporaryInstallPrompt = (_, title, buttons, _) =>
            {
                Assert.Equal("Installation blocked", title);
                Assert.Equal(MessageBoxButtons.OK, buttons);
                return DialogResult.Yes;
            };
            Assert.False(form.ConfirmTemporaryInstall(new([new("mod-conflict", PreflightSeverity.Error, "blocked")])));
            form.TemporaryInstallPrompt = (_, _, _, _) => throw new InvalidOperationException("Clean checks must not prompt.");
            Assert.True(form.ConfirmTemporaryInstall(new([])));
            return Task.CompletedTask;
        });
    }

    [Fact]
    public void BoundSaveAndReloadFilterByOverrideAndAutomaticKeepsDetectedApis()
    {
        OnSta(() =>
        {
            using var fixture = new WorkflowFixture();
            using var form = new MainForm(preview: true, payloadRoot: fixture.PayloadRoot);
            form.Show();
            Application.DoEvents();
            var state = new LibraryState();
            typeof(MainForm).GetField("_libraryState", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(form, state);
            var candidates = Field<List<GameCandidate>>(form, "_allGames");
            candidates.Clear();
            candidates.Add(fixture.Game);
            Invoke(form, "RenderGameRows", fixture.Game.ExePath);
            var rows = Field<ListView>(form, "_games");
            rows.Items[0].Selected = true;
            Application.DoEvents();
            Descendants(form).OfType<CheckBox>().Single(control => control.AccessibleName == "Show advanced game options").Checked = true;
            var renderer = Field<ComboBox>(form, "_engineChoice");
            renderer.SelectedItem = renderer.Items.Cast<object>().Skip(1).Single(item =>
                (GraphicsApi)item.GetType().GetProperty("Api")!.GetValue(item)! == GraphicsApi.Vulkan);
            Field<Button>(form, "_saveProfile").PerformClick();
            Assert.Equal(GraphicsApi.Vulkan, state.ProfileFor(fixture.Game.ExePath).ApiOverride);

            var filter = Field<ComboBox>(form, "_apiFilter");
            filter.SelectedItem = GraphicsApi.Vulkan;
            Assert.Single(rows.Items.Cast<ListViewItem>());
            Assert.Equal(fixture.Game, rows.Items[0].Tag);
            Assert.Contains(fixture.Game.DisplayName, rows.Items[0].ToolTipText);
            filter.SelectedItem = GraphicsApi.DirectX12;
            Assert.Empty(rows.Items.Cast<ListViewItem>());
            var saved = fixture.Workspace.FilePath("library.json");
            state.Save(saved);
            typeof(MainForm).GetField("_libraryState", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(form, LibraryState.Load(saved));
            filter.SelectedItem = GraphicsApi.Vulkan;
            Assert.Single(rows.Items.Cast<ListViewItem>());

            filter.SelectedIndex = 0;
            rows.Items[0].Selected = true;
            Application.DoEvents();
            renderer.SelectedIndex = 0;
            Assert.Equal(0, renderer.SelectedIndex);
            Assert.True(Field<Button>(form, "_saveProfile").Enabled);
            Assert.True(Field<Button>(form, "_saveProfile").Visible);
            Assert.False(Field<bool>(form, "_loadingProfile"));
            Assert.NotNull(Invoke(form, "SelectedBaseGame"));
            Field<Button>(form, "_saveProfile").PerformClick();
            Assert.True(Field<LibraryState>(form, "_libraryState").ProfileFor(fixture.Game.ExePath).ApiOverride is null,
                $"Renderer index after Save: {renderer.SelectedIndex}; {Field<TextBox>(form, "_log").Text}");
            filter.SelectedItem = GraphicsApi.DirectX11;
            Assert.Single(rows.Items.Cast<ListViewItem>());
            filter.SelectedItem = GraphicsApi.Vulkan;
            Assert.Empty(rows.Items.Cast<ListViewItem>());
            form.Close();
            return Task.CompletedTask;
        });
    }

    [Theory]
    [InlineData("-dx12 -windowed")]
    [InlineData("")]
    [InlineData("--profile \"Fresh session\"")]
    public void InstallCandidateAndGeneratedLauncherUseStagedArguments(string stagedArguments)
    {
        OnSta(() =>
        {
            using var fixture = new WorkflowFixture();
            using var form = new MainForm(preview: true, payloadRoot: fixture.PayloadRoot);
            form.Show();
            Application.DoEvents();
            const string savedArguments = "--old-saved-arguments";
            var state = Field<LibraryState>(form, "_libraryState");
            state.Profiles[fixture.Game.ExePath] = new() { LaunchArguments = savedArguments };
            var candidates = Field<List<GameCandidate>>(form, "_allGames");
            candidates.Clear();
            candidates.Add(fixture.Game);
            Invoke(form, "RenderGameRows", fixture.Game.ExePath);
            Field<ListView>(form, "_games").Items[0].Selected = true;
            Application.DoEvents();
            var arguments = Field<TextBox>(form, "_launchArguments");
            Assert.Equal(savedArguments, arguments.Text);
            arguments.Text = stagedArguments;

            var candidate = Assert.IsType<GameCandidate>(Invoke(form, "SelectedInstallGame"));
            Assert.Equal(stagedArguments, candidate.SuggestedArguments);
            Assert.Equal(savedArguments, state.ProfileFor(candidate.ExePath).LaunchArguments);
            Assert.Equal(fixture.Game.Route, candidate.Route);

            var engine = new InstallerEngine(_ => { });
            var manifest = new InstallManifest
            {
                GameRoot = candidate.Root,
                ExeRelativePath = Path.GetRelativePath(candidate.Root, candidate.ExePath),
                Route = candidate.Route.ToString()
            };
            typeof(InstallerEngine).GetMethod("WriteLauncherBatch", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(engine, [candidate, manifest]);
            var launcher = File.ReadAllText(InstallerEngine.LauncherPathFor(candidate));
            var expectedArguments = stagedArguments.Length == 0 ? "" : " " + stagedArguments;
            Assert.Contains("start \"\" %DLSS5_WAIT%\"%GAME_DIR%%GAME_EXE%\"" + expectedArguments + " %*", launcher);
            Assert.DoesNotContain(savedArguments, launcher);
            form.Close();
            return Task.CompletedTask;
        });
    }

    [Theory]
    [InlineData(1060, 740)]
    [InlineData(1400, 900)]
    [InlineData(1920, 1080)]
    public void BasicColumnsUseOnlyAvailableWidthAndRetainFullNameTooltip(int width, int height)
    {
        OnSta(() =>
        {
            using var workspace = new TestWorkspace();
            using var form = new MainForm(preview: true, payloadRoot: workspace.Root) { Size = new Size(width, height) };
            form.Show();
            Application.DoEvents();
            Field<CheckBox>(form, "_detailedColumns").Checked = false;
            Invoke(form, "ResizeGameColumns");
            var rows = Field<ListView>(form, "_games");
            var available = rows.ClientSize.Width;
            Assert.InRange(rows.Columns.Cast<ColumnHeader>().Sum(column => column.Width), available - 1, Math.Max(481, available));
            Assert.True(rows.Columns[1].Width == 0 || rows.Columns[1].Width is >= 70 and <= 90);
            Assert.Equal(58, rows.Columns[3].Width);
            Assert.Equal(100, rows.Columns[4].Width);
            Assert.True(rows.Columns[7].Width >= TextRenderer.MeasureText("Output verified", rows.Font).Width);
            foreach (ListViewItem row in rows.Items)
                Assert.Contains(((GameCandidate)row.Tag!).DisplayName, row.ToolTipText);
            form.Close();
            return Task.CompletedTask;
        });
    }

    [Fact]
    public void SavedNativeRouteReturnsAfterUnsavedFeederChoiceAndSelectionReload()
    {
        OnSta(() =>
        {
            using var fixture = new WorkflowFixture();
            using var form = new MainForm(preview: true, payloadRoot: fixture.PayloadRoot);
            form.Show();
            Application.DoEvents();
            var candidates = Field<List<GameCandidate>>(form, "_allGames");
            candidates.Clear();
            candidates.Add(fixture.Game);
            candidates.Add(fixture.Game with { ExePath = Path.GetFullPath(fixture.Workspace.WritePe("game/other.exe", CpuArch.X64)), DisplayName = "Other fixture" });
            Invoke(form, "RenderGameRows", fixture.Game.ExePath);
            var rows = Field<ListView>(form, "_games");
            var modern = rows.Items.Cast<ListViewItem>().Single(row => ((GameCandidate)row.Tag!).ExePath == fixture.Game.ExePath);
            var other = rows.Items.Cast<ListViewItem>().Single(row => row != modern);
            modern.Selected = true;
            Application.DoEvents();
            Descendants(form).OfType<CheckBox>().Single(control => control.AccessibleName == "Show advanced game options").Checked = true;
            var route = Field<ComboBox>(form, "_routeChoice");
            route.SelectedIndex = 1;
            Field<Button>(form, "_saveProfile").PerformClick();
            Assert.Equal(GameRoutePreference.Native, Field<LibraryState>(form, "_libraryState").ProfileFor(fixture.Game.ExePath).PreferredRoute);
            route.SelectedIndex = 2;
            Assert.Equal(InstallRoute.X64DxgiFeeder, Assert.IsType<GameCandidate>(Invoke(form, "SelectedGame")).Route);
            modern.Selected = false;
            other.Selected = true;
            other.Selected = false;
            modern.Selected = true;
            Assert.Equal(1, route.SelectedIndex);
            Assert.Equal(InstallRoute.X64DirectPhoenix, Assert.IsType<GameCandidate>(Invoke(form, "SelectedGame")).Route);
            form.Close();
            return Task.CompletedTask;
        });
    }

    [Fact]
    public void SelectedGameUsesUiRouteAndProxyWhileFilteringUsesSavedRoute()
    {
        OnSta(() =>
        {
            using var fixture = new WorkflowFixture();
            using var form = new MainForm(preview: true, payloadRoot: fixture.PayloadRoot);
            form.Show();
            Application.DoEvents();
            var detected = fixture.Game with { Api = GraphicsApi.DirectX11, PossibleApis = [GraphicsApi.DirectX11] };
            var candidates = Field<List<GameCandidate>>(form, "_allGames");
            candidates.Clear();
            candidates.Add(detected);
            Invoke(form, "RenderGameRows", detected.ExePath);
            Field<ListView>(form, "_games").Items[0].Selected = true;
            Application.DoEvents();
            Field<ComboBox>(form, "_routeChoice").SelectedIndex = 2;
            Field<ComboBox>(form, "_proxyChoice").SelectedIndex = 2;
            var selected = Assert.IsType<GameCandidate>(Invoke(form, "SelectedGame"));
            Assert.Equal(GameRoutePreference.Feeder, selected.PreferredRoute);
            Assert.Equal("d3d11.dll", selected.ProxyName);
            Assert.Equal(InstallRoute.X64DxgiFeeder, selected.Route);
            var state = Field<LibraryState>(form, "_libraryState");
            state.Profiles[detected.ExePath] = new() { PreferredRoute = GameRoutePreference.Native };
            Invoke(form, "RenderGameRows", detected.ExePath);
            Assert.Empty(Field<ListView>(form, "_games").Items.Cast<ListViewItem>());
            state.Profiles[detected.ExePath] = new() { PreferredRoute = GameRoutePreference.Feeder };
            Invoke(form, "RenderGameRows", detected.ExePath);
            Assert.Single(Field<ListView>(form, "_games").Items.Cast<ListViewItem>());
            form.Close();
            return Task.CompletedTask;
        });
    }

    [Fact]
    public void InspectedComponentFilterChangesRowsAndVisibleCountUsingCache()
    {
        OnSta(() =>
        {
            using var workspace = new TestWorkspace();
            using var form = new MainForm(preview: true, payloadRoot: workspace.Root);
            form.Show();
            Application.DoEvents();
            var rows = Field<ListView>(form, "_games");
            var originalCount = rows.Items.Count;
            var game = (GameCandidate)rows.Items[0].Tag!;
            var filter = Field<ComboBox>(form, "_componentFilter");
            Assert.Equal(0, filter.SelectedIndex);
            filter.SelectedIndex = 1;
            Assert.Empty(rows.Items.Cast<ListViewItem>());
            Field<Dictionary<string, ComponentInspectionReport>>(form, "_inspectedComponents")[game.ExePath] =
                new([], new(1, 0, 0, 0, false));
            Invoke(form, "RenderGameRows", game.ExePath);
            Assert.Single(rows.Items.Cast<ListViewItem>());
            Assert.StartsWith($"1 shown / {originalCount} discovered", Field<Label>(form, "_gameCount").Text);
            filter.SelectedIndex = 2;
            Assert.Empty(rows.Items.Cast<ListViewItem>());
            filter.SelectedIndex = 0;
            Assert.Equal(originalCount, rows.Items.Count);
            form.Close();
            return Task.CompletedTask;
        });
    }

    static IEnumerable<Control> Descendants(Control root) => root.Controls.Cast<Control>()
        .SelectMany(child => new[] { child }.Concat(Descendants(child)));
    static T Field<T>(MainForm form, string name) => (T)typeof(MainForm)
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form)!;
    static object? Invoke(MainForm form, string name, params object?[] arguments) => typeof(MainForm)
        .GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(form, arguments);

    static void OnSta(Func<Task> action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var task = action();
                var deadline = DateTime.UtcNow.AddSeconds(30);
                while (!task.IsCompleted)
                {
                    if (DateTime.UtcNow >= deadline) throw new TimeoutException("Workflow did not finish.");
                    Application.DoEvents();
                    Thread.Sleep(10);
                }
                task.GetAwaiter().GetResult();
            }
            catch (Exception ex) { failure = ex; }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(40)), "Workflow UI test did not finish.");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    static string WriteDll(TestWorkspace workspace, string relativePath, CpuArch architecture)
    {
        var path = workspace.WritePe(relativePath, architecture);
        var bytes = File.ReadAllBytes(path);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(0x96), 0x2022);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0x98 + (architecture == CpuArch.X64 ? 108 : 92)), 16);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    sealed class WorkflowFixture : IDisposable
    {
        public TestWorkspace Workspace { get; } = new();
        public string PayloadRoot => Workspace.FilePath("payload");
        public GameCandidate Game { get; }
        public WorkflowFixture()
        {
            var exe = Path.GetFullPath(Workspace.WritePe("game/workflow-fixture.exe", CpuArch.X64, "d3d12.dll"));
            Game = new(Path.GetDirectoryName(exe)!, exe, "Workflow safety fixture with a complete long display name", "workflow-fixture.exe",
                CpuArch.X64, GraphicsApi.DirectX12, "CPU fixture", 1, [GraphicsApi.DirectX12, GraphicsApi.DirectX11], HasNativeDlssSignal: true);
            WriteDll(Workspace, "payload/ReShade64.dll", CpuArch.X64);
            WriteDll(Workspace, "payload/nvngx_dlssnr.dll", CpuArch.X64);
            var addon = WriteDll(Workspace, "payload/Phoenix.addon64", CpuArch.X64);
            TestWorkspace.SetPhoenixFeederExports(addon);
        }
        public void Dispose() => Workspace.Dispose();
    }
}
