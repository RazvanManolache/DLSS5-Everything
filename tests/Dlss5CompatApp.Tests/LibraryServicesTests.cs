using System.Net;
using System.Text;
using System.Text.Json;

namespace Dlss5CompatApp.Tests;

public sealed class LibraryServicesTests
{
    static GameCandidate Candidate(string root, string name = "game.exe") => new(root, Path.Combine(root, name),
        Path.GetFileName(root), name, CpuArch.X64, GraphicsApi.DirectX11, "imports", 160 * 1024,
        [GraphicsApi.DirectX11, GraphicsApi.DirectX12], "-dx11",
        new Dictionary<GraphicsApi, string> { [GraphicsApi.DirectX12] = "-dx12" });

    [Fact]
    public void SettingsRoundTripAndPathIdentity()
    {
        using var w = new TestWorkspace();
        var file = w.FilePath("state.json"); var exe = w.FilePath("game.exe");
        var state = LibraryState.Load(file);
        state.ScanRoots.Add(w.Root); state.ScanRoots.Add(w.Root.ToUpperInvariant() + "\\");
        state.ManualPaths.Add(exe); state.HiddenPaths.Add(exe); state.FavouritePaths.Add(exe);
        state.CachedGamePaths.Add(exe); state.CachedGamePaths.Add(exe.ToUpperInvariant());
        state.Theme = "system"; state.GroupByStore = false;
        state.Profiles[exe] = new() { ApiOverride = GraphicsApi.DirectX12, LaunchArguments = "--test \"two words\"",
            FrameIterations = 10, ForceVrEyeSplit = true, ArtworkPath = w.FilePath("cover.png") };
        LibraryState.Save(state, file);
        var loaded = LibraryState.Load(file);
        Assert.Single(loaded.ScanRoots); Assert.Single(loaded.ManualPaths);
        Assert.Single(loaded.CachedGamePaths);
        Assert.Equal("system", loaded.Theme); Assert.False(loaded.GroupByStore);
        Assert.Equal(state.Profiles[exe], loaded.ProfileFor(exe.ToUpperInvariant()));
        Assert.Contains("DirectX12", File.ReadAllText(file));
        Assert.Equal(1, loaded.Revision);
        Assert.DoesNotContain(w.Root, LibraryState.DefaultPath, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("light")] [InlineData("dark")] [InlineData("system")]
    public void AllThemesPersist(string theme)
    {
        using var w = new TestWorkspace();
        var path = w.FilePath("settings.json");
        new LibraryState { Theme = theme }.Save(path);
        Assert.Equal(theme, LibraryState.Load(path).Theme);
    }

    [Fact]
    public void StaleSaveDoesNotLoseNewerSettings()
    {
        using var w = new TestWorkspace(); var path = w.FilePath("state.json");
        var first = LibraryState.Load(path); var stale = LibraryState.Load(path);
        first.ScanRoots.Add(w.Root); first.Theme = "dark"; first.Save(path);
        stale.Theme = "light";
        Assert.Throws<InvalidOperationException>(() => stale.Save(path));
        Assert.Single(LibraryState.Load(path).ScanRoots);
        Assert.Equal("dark", LibraryState.Load(path).Theme);
    }

    [Theory]
    [InlineData("{broken")] [InlineData("null")] [InlineData("{\"SchemaVersion\":2}")]
    public void CorruptOrFutureStateIsNeverSilentlyReplaced(string text)
    {
        using var w = new TestWorkspace(); var path = w.FilePath("state.json"); File.WriteAllText(path, text);
        Assert.ThrowsAny<Exception>(() => LibraryState.Load(path));
        Assert.ThrowsAny<Exception>(() => new LibraryState().Save(path));
        Assert.Equal(text, File.ReadAllText(path));
    }

    [Theory]
    [InlineData("relative.exe")] [InlineData("C:relative.exe")] [InlineData("\\\\server\\games\\game.exe")]
    [InlineData("\\\\?\\C:\\games\\game.exe")] [InlineData("C:\\games\\game.exe:stream")]
    public void StateRejectsRelativeNetworkDeviceAndAdsPaths(string path)
    {
        using var w = new TestWorkspace(); var file = w.FilePath("state.json");
        var state = new LibraryState { ManualPaths = [path] };
        Assert.ThrowsAny<ArgumentException>(() => state.Save(file)); Assert.False(File.Exists(file));
    }

    [Theory]
    [InlineData(0)] [InlineData(11)]
    public void InvalidFrameIterationsCannotPersist(int iterations)
    {
        using var w = new TestWorkspace();
        var state = new LibraryState(); state.Profiles[w.FilePath("game.exe")] = new() { FrameIterations = iterations };
        Assert.Throws<InvalidDataException>(() => state.Save(w.FilePath("state.json")));
    }

    [Fact]
    public void InvalidOverrideArgumentsAndThemeCannotPersist()
    {
        using var w = new TestWorkspace(); var file = w.FilePath("state.json"); var key = w.FilePath("game.exe");
        var state = new LibraryState(); state.Profiles[key] = new() { ApiOverride = (GraphicsApi)999 };
        Assert.Throws<InvalidDataException>(() => state.Save(file));
        state.Profiles[key] = new() { LaunchArguments = "--test\nstart bad.exe" };
        Assert.Throws<InvalidDataException>(() => state.Save(file));
        state.Profiles.Clear(); state.Theme = "unknown";
        Assert.Throws<InvalidDataException>(() => state.Save(file));
    }

    [Fact]
    public void ProjectionFiltersGroupsAndUnhidesWithoutMutatingDetection()
    {
        using var w = new TestWorkspace(); var game = Candidate(w.Root);
        var other = game with { ExePath = w.FilePath("other.exe"), DisplayName = "Other" };
        var state = new LibraryState(); state.Profiles[game.ExePath] = new() { ApiOverride = GraphicsApi.DirectX12, LaunchArguments = "" };
        state.FavouritePaths.Add(game.ExePath); state.HiddenPaths.Add(other.ExePath);
        LibraryGame[] games = [new(game, "Steam", true), new(game), new(other, "Epic")];
        var entry = Assert.Single(state.Project(games));
        Assert.Equal(GraphicsApi.DirectX11, entry.DetectedGame.Api);
        Assert.Equal(GraphicsApi.DirectX12, entry.EffectiveGame.Api);
        Assert.Equal("", entry.EffectiveGame.SuggestedArguments);
        Assert.Equal("-dx11", game.SuggestedArguments);
        Assert.Single(state.Project(games, LibraryFilter.Installed));
        Assert.Single(state.Project(games, LibraryFilter.Favourites));
        Assert.Empty(state.Project(games, query: "does not exist"));
        Assert.Equal(2, state.Project(games, includeHidden: true).Count);
        state.HiddenPaths.Clear();
        Assert.Equal(2, state.Group(state.Project(games)).Count);
        state.GroupByStore = false; Assert.Single(state.Group(state.Project(games)));
        state.Profiles[game.ExePath].ApiOverride = null; state.Profiles[game.ExePath].LaunchArguments = null;
        Assert.Equal(game, state.ProfileFor(game.ExePath).Apply(game));
    }

    [Fact]
    public void HistoryKeepsAllOutcomesAndRecoversPastPartialWrites()
    {
        using var w = new TestWorkspace(); var file = w.FilePath("history.jsonl");
        var warnings = new List<string>(); var entries = new List<InstallHistoryEntry>();
        foreach (var action in Enum.GetValues<HistoryAction>())
            foreach (var outcome in Enum.GetValues<HistoryOutcome>())
            {
                var row = new InstallHistoryEntry { GamePath = w.Root, GameName = "Test", Action = action, Outcome = outcome,
                    Message = outcome == HistoryOutcome.Failure ? "Access denied; operation failed." : "Operation completed.", Timestamp = DateTimeOffset.UtcNow.AddSeconds(entries.Count) };
                InstallHistory.Append(row, file); entries.Add(row);
            }
        File.AppendAllText(file, "{partial");
        var final = entries[0] with { Id = Guid.NewGuid(), Message = "After partial write" };
        InstallHistory.Append(final, file); InstallHistory.Append(final, file);
        var loaded = InstallHistory.Load(file, warnings.Add);
        Assert.Equal(entries.Count + 1, loaded.Count); Assert.NotEmpty(warnings);
        Assert.Contains(loaded, e => e.Outcome == HistoryOutcome.Failure && e.Action == HistoryAction.Restore);
        Assert.Contains("Failure", loaded.First(e => e.Outcome == HistoryOutcome.Failure).Summary);
        Assert.Equal(loaded.OrderByDescending(e => e.Timestamp), loaded);
    }

    [Fact]
    public async Task ParallelHistoryAppendsDoNotLoseRows()
    {
        using var w = new TestWorkspace(); var file = w.FilePath("history.jsonl");
        await Task.WhenAll(Enumerable.Range(0, 12).Select(i => Task.Run(() => InstallHistory.Append(new()
            { GamePath = w.Root, GameName = "Test", Action = HistoryAction.Install, Outcome = HistoryOutcome.Success, Message = "Operation " + i }, file))));
        Assert.Equal(12, InstallHistory.Load(file).Count);
        Assert.Throws<InvalidDataException>(() => InstallHistory.Append(new() { GamePath = w.Root, GameName = "Test" }, file));
        Assert.Throws<InvalidDataException>(() => InstallHistory.Append(new() { GamePath = w.Root, GameName = "Test", Message = "Outcome omitted" }, file));
    }

    [Fact]
    public void DiagnosticsReadActualBoundedTailsAndNeverClaimRuntimeSuccess()
    {
        using var w = new TestWorkspace(); var game = Candidate(w.Root);
        var empty = RuntimeDiagnostics.Collect(game);
        Assert.False(empty.HasLogs); Assert.False(empty.RuntimeVerified);
        Assert.All(empty.Sources, s => Assert.Equal(DiagnosticFileStatus.Missing, s.Status));
        File.WriteAllText(w.FilePath("ReShade.log"), "");
        File.WriteAllText(w.FilePath("dlss5-feed.log"), new string('x', RuntimeDiagnostics.TailBytes + 100) + "\nActual error: access denied");
        var report = RuntimeDiagnostics.Collect(game);
        var source = Assert.Single(report.Sources, s => s.Label == "dlss5-feed.log");
        Assert.True(source.Truncated); Assert.Contains("Actual error: access denied", source.Text);
        Assert.True(Encoding.UTF8.GetByteCount(source.Text) <= RuntimeDiagnostics.TailBytes);
        Assert.Equal(DiagnosticFileStatus.Empty, report.Sources.First(s => s.Label == "ReShade.log").Status);
        Assert.True(report.HasLogs); Assert.False(report.RuntimeVerified);
        Assert.Contains("not verified", report.Text);
        Assert.Throws<ArgumentException>(() => RuntimeDiagnostics.Collect(game with { Root = w.FilePath("elsewhere") }));
    }

    [Fact]
    public void LockedLogIsUnreadableRatherThanSuccessful()
    {
        using var w = new TestWorkspace(); var path = w.FilePath("dlss5-feed.log"); File.WriteAllText(path, "evidence");
        using var locked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var report = RuntimeDiagnostics.Collect(Candidate(w.Root));
        Assert.Equal(DiagnosticFileStatus.Unreadable, report.Sources.First(s => s.Path == path).Status);
        Assert.False(report.HasLogs); Assert.False(report.RuntimeVerified);
    }

    [Fact]
    public void InstalledEvidenceAllowsDirectoriesButRejectsMissingAndEscapingEntries()
    {
        using var w = new TestWorkspace(); var game = Candidate(w.Root); File.WriteAllText(game.ExePath, "fixture");
        var backup = w.FilePath("_DLSS5_Compat_Backup"); Directory.CreateDirectory(backup);
        Directory.CreateDirectory(w.FilePath("host64")); File.WriteAllText(w.FilePath("proxy.dll"), "fixture");
        var manifest = new InstallManifest { GameRoot = w.Root, ExeRelativePath = "game.exe", Route = "X64DxgiFeeder", Added = ["host64", "proxy.dll"] };
        var path = Path.Combine(backup, "manifest.json");
        void Save() => File.WriteAllText(path, JsonSerializer.Serialize(manifest));
        Save(); Assert.True(RuntimeDiagnostics.HasInstallManifest(game));
        manifest.Replaced.Add(new ManifestFile { RelativePath = "old.addon64" });
        Save(); Assert.False(RuntimeDiagnostics.HasInstallManifest(game));
        manifest.Retired.Add("old.addon64"); Save(); Assert.True(RuntimeDiagnostics.HasInstallManifest(game));
        manifest.Retired.Add("proxy.dll"); Save(); Assert.False(RuntimeDiagnostics.HasInstallManifest(game));
        manifest.Retired.Remove("proxy.dll");
        manifest.Added.Add("missing.dll"); Save(); Assert.False(RuntimeDiagnostics.HasInstallManifest(game));
        manifest.Added = ["../outside.dll"]; Save(); Assert.False(RuntimeDiagnostics.HasInstallManifest(game));
        File.WriteAllText(path, "{broken"); Assert.False(RuntimeDiagnostics.HasInstallManifest(game));
        File.WriteAllText(path, "{}"); Assert.False(RuntimeDiagnostics.HasInstallManifest(game));
    }

    [Fact]
    public void CachedNestedExecutableRetainsInstallRootAndManualUnknownApiCanBeOverridden()
    {
        using var w = new TestWorkspace();
        var nested = w.WritePe("Title/Binaries/Win64/game.exe", CpuArch.X64, "d3d11.dll");
        var cached = GameScanner.ScanSingleExe(nested);
        Assert.NotNull(cached); Assert.True(LocalPaths.Same(w.FilePath("Title"), cached.Root));
        var unknown = w.WritePe("Unknown/game.exe", CpuArch.X64);
        var detected = GameScanner.ScanSingleExe(unknown);
        Assert.NotNull(detected); Assert.Equal(GraphicsApi.Unknown, detected.Api);
        Assert.Equal(GraphicsApi.DirectX12, new GameProfile { ApiOverride = GraphicsApi.DirectX12 }.Apply(detected).Api);
        Assert.Equal(GraphicsApi.Unknown, detected.Api);
        Assert.True(LocalPaths.IsInside(w.Root, Path.GetPathRoot(w.Root)!));
        Assert.False(LocalPaths.IsInside(w.FilePath("TitleExtra"), w.FilePath("Title")));
    }

    [Theory]
    [InlineData("DXVK", CpuArch.X64, false, true)]
    [InlineData("vkd3d", CpuArch.X64, false, true)]
    [InlineData("DXVK", CpuArch.X86, false, false)]
    [InlineData("DXVK", CpuArch.X64, true, false)]
    [InlineData("mention only", CpuArch.X64, false, false)]
    internal void WrapperDetectionRequiresMatchingArchitectureAndEvidence(string kind, CpuArch wrapperArch, bool reshade, bool expected)
    {
        using var w = new TestWorkspace(); var exe = w.WritePe("game.exe", CpuArch.X64, "d3d11.dll");
        var text = kind + " vkGetInstanceProcAddr" + (reshade ? " ReShade" : "");
        File.WriteAllBytes(w.FilePath("d3d11.dll"), TestWorkspace.PeBytes(wrapperArch, [], text));
        var wrappers = GameScanner.IdentifyVulkanWrappers(exe, GraphicsApi.DirectX11);
        Assert.Equal(expected, wrappers.Count > 0);
        var scanned = GameScanner.ScanSingleExe(exe);
        Assert.NotNull(scanned); Assert.Equal(expected ? GraphicsApi.Vulkan : GraphicsApi.DirectX11, scanned.Api);
        if (expected) Assert.Contains("wrapper:", scanned.Detection);
    }

    [Fact]
    public async Task ScannerEnforcesDepthSizeEntryLimitsAndCancellation()
    {
        using var w = new TestWorkspace(); w.WritePe("Deep/More/game.exe", CpuArch.X64, "d3d11.dll");
        Assert.Empty(await GameScanner.ScanAsync(w.Root, cancellationToken: default, limits: new(MaxDepth: 0)));
        w.WritePe("game.exe", CpuArch.X64, "d3d11.dll");
        Assert.Empty(await GameScanner.ScanAsync(w.Root, limits: new(MaxExecutableBytes: 100)));
        var warnings = new List<string>();
        _ = await GameScanner.ScanAsync(w.Root, limits: new(MaxEntries: 1), warning: warnings.Add);
        Assert.Contains(warnings, s => s.Contains("limit"));
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => GameScanner.ScanAsync(w.Root, cancellationToken: cancel.Token, limits: new()));
    }

    [Fact]
    public async Task MultipleRootsAndManualGamesMergeWithoutLosingStore()
    {
        using var w = new TestWorkspace(); var exe = w.WritePe("SteamGame/game.exe", CpuArch.X64, "d3d11.dll");
        var manual = w.WritePe("Manual/game.exe", CpuArch.X64, "d3d12.dll");
        var options = new LibraryDiscoveryOptions { GogGames = [new("GOG", "1", "Store name", Path.GetDirectoryName(exe)!)] };
        var state = new LibraryState { ScanRoots = [Path.GetDirectoryName(exe)!, Path.GetDirectoryName(manual)!], ManualPaths = [manual, exe] };
        var result = await LibraryDiscovery.DiscoverAsync(state, options);
        Assert.Equal(2, result.Games.Count); Assert.Empty(result.Warnings);
        var store = result.Games.Single(g => LocalPaths.Same(g.DetectedGame.ExePath, exe));
        Assert.Equal("GOG", store.Store); Assert.Equal("Store name", store.DetectedGame.DisplayName);
        Assert.All(result.Games, g => Assert.False(g.Installed));
    }

    [Fact]
    public void StoreDiscoveryReadsAllFiveFormatsAndKeepsSeparateInstallations()
    {
        using var w = new TestWorkspace();
        var steam = w.FilePath("Steam"); var apps = Path.Combine(steam, "steamapps"); Directory.CreateDirectory(apps);
        var steamGame = Path.Combine(apps, "common", "Game"); Directory.CreateDirectory(steamGame);
        File.WriteAllText(Path.Combine(apps, "appmanifest_1.acf"), "\"AppState\" { \"appid\" \"1\" \"name\" \"Test game\" \"installdir\" \"Game\" }");
        var second = w.FilePath("SecondSteam"); var secondApps = Path.Combine(second, "steamapps"); Directory.CreateDirectory(Path.Combine(secondApps, "common", "Game"));
        File.WriteAllText(Path.Combine(secondApps, "appmanifest_1.acf"), File.ReadAllText(Path.Combine(apps, "appmanifest_1.acf")));
        File.WriteAllText(Path.Combine(apps, "libraryfolders.vdf"), "\"libraryfolders\" { \"1\" { \"path\" \"" + second.Replace("\\", "\\\\") + "\" } }");
        var epic = w.FilePath("Manifests"); Directory.CreateDirectory(epic);
        var epicGame = w.FilePath("EpicGame"); Directory.CreateDirectory(epicGame);
        File.WriteAllText(Path.Combine(epic, "one.item"), JsonSerializer.Serialize(new { AppName = "2", DisplayName = "Epic title", InstallLocation = epicGame }));
        var gog = w.FilePath("GogGame"); var ubi = w.FilePath("UbiGame"); Directory.CreateDirectory(gog); Directory.CreateDirectory(ubi);
        var xbox = w.FilePath("XboxGames/Title"); Directory.CreateDirectory(Path.Combine(xbox, "Content"));
        File.WriteAllText(Path.Combine(xbox, "Content", "MicrosoftGame.config"), "<Game><Identity Name=\"Test.Xbox\"/><ShellVisuals DefaultDisplayName=\"Xbox title\"/></Game>");
        var options = new LibraryDiscoveryOptions { SteamRoots = [steam], EpicManifestDirectories = [epic],
            GogGames = [new("", "3", "GOG title", gog), new("", "duplicate", "Duplicate", gog)],
            UbisoftGames = [new("", "4", "Ubi title", ubi)], XboxPackageRoots = [Path.Combine(xbox, "Content")], XboxDriveRoots = [w.Root] };
        var warnings = new List<string>(); var result = LibraryDiscovery.DiscoverStores(options, warnings.Add);
        Assert.Equal(6, result.Count); Assert.Empty(warnings);
        Assert.Equal(2, result.Count(g => g.Store == "Steam")); Assert.Single(result, g => g.Store == "Xbox");
        Assert.Contains(result, g => g.Store == "Epic"); Assert.Contains(result, g => g.Store == "GOG"); Assert.Contains(result, g => g.Store == "Ubisoft");
        Assert.False(File.Exists(w.FilePath("library.json")));
    }

    [Fact]
    public void StoreMetadataRejectsTraversalDtdStaleLocationsAndOversizedInput()
    {
        using var w = new TestWorkspace(); var apps = w.FilePath("steamapps"); Directory.CreateDirectory(apps);
        File.WriteAllText(Path.Combine(apps, "appmanifest_1.acf"), "\"AppState\" { \"appid\" \"1\" \"installdir\" \"../../escape\" }");
        var epic = w.FilePath("Epic"); Directory.CreateDirectory(epic);
        File.WriteAllText(Path.Combine(epic, "bad.item"), "{bad"); File.WriteAllText(Path.Combine(epic, "huge.item"), new string('x', 300));
        File.WriteAllText(Path.Combine(epic, "stale.item"), JsonSerializer.Serialize(new { InstallLocation = w.FilePath("missing"), DisplayName = "Stale" }));
        var xbox = w.FilePath("Xbox"); Directory.CreateDirectory(xbox);
        File.WriteAllText(Path.Combine(xbox, "MicrosoftGame.config"), "<!DOCTYPE Game [<!ENTITY x SYSTEM 'file:///private'>]><Game>&x;</Game>");
        var warnings = new List<string>();
        var result = LibraryDiscovery.DiscoverStores(new() { SteamRoots = [w.Root], EpicManifestDirectories = [epic], XboxPackageRoots = [xbox], MaxManifestBytes = 256 }, warnings.Add);
        Assert.Empty(result); Assert.True(warnings.Count >= 4);
    }

    [Fact]
    public void GamingRootCannotEscapeItsDriveAndDiscoveryHasGlobalMetadataLimit()
    {
        using var w = new TestWorkspace();
        File.WriteAllBytes(w.FilePath(".GamingRoot"), "RGBX\0\0\0\0"u8.ToArray().Concat(Encoding.Unicode.GetBytes("..\\outside\0")).ToArray());
        var warnings = new List<string>();
        Assert.Empty(LibraryDiscovery.DiscoverStores(new() { XboxDriveRoots = [w.Root] }, warnings.Add));
        Assert.Contains(warnings, s => s.Contains("GamingRoot"));
        warnings.Clear();
        _ = LibraryDiscovery.DiscoverStores(new() { MaxManifests = 1, GogGames = Enumerable.Range(0, 10).Select(i => new StoreInstall("GOG", i.ToString(), "Game", w.Root)).ToList() }, warnings.Add);
        Assert.Contains(warnings, s => s.Contains("limit"));
    }

    [Fact]
    public async Task ReparsePointsCannotEscapeScanningPersistenceOrDiagnostics()
    {
        using var w = new TestWorkspace(); var target = w.FilePath("Target"); Directory.CreateDirectory(target);
        w.WritePe("Target/game.exe", CpuArch.X64, "d3d11.dll");
        var link = w.FilePath("Link");
        // Junctions require no developer mode and exercise the same reparse checks as symlinks.
        using (var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe")
            { Arguments = $"/c mklink /J \"{link}\" \"{target}\"", UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true })!)
        {
            await process.WaitForExitAsync(); Assert.Equal(0, process.ExitCode);
        }
        try
        {
            Assert.Throws<IOException>(() => new LibraryState().Save(Path.Combine(link, "state.json")));
            await Assert.ThrowsAsync<IOException>(() => GameScanner.ScanAsync(link, limits: new()));
            var report = RuntimeDiagnostics.Collect(Candidate(link));
            Assert.All(report.Sources, s => Assert.Equal(DiagnosticFileStatus.Unreadable, s.Status));
            var result = await LibraryDiscovery.DiscoverAsync(new() { ScanRoots = [link] }, new());
            Assert.Empty(result.Games); Assert.NotEmpty(result.Warnings); Assert.False(File.Exists(Path.Combine(target, "state.json")));
        }
        finally { Directory.Delete(link); }
    }
}

public sealed class AppUpdateServiceTests
{
    sealed class FakeHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("https://api.github.com/repos/RazvanManolache/DLSS5-Everything/releases/latest", request.RequestUri!.AbsoluteUri);
            return send(request, cancellationToken);
        }
    }

    [Theory]
    [InlineData("1.2.0", "v1.10.0", true)] [InlineData("1.2.0", "v1.2.0", false)]
    [InlineData("1.2.0", "v1.1.9", false)] [InlineData("1.2.0-rc.1", "v1.2.0", true)]
    [InlineData("1.2.0.0", "v1.2.0+build", false)]
    public async Task ExplicitCheckComparesVersionsAndUsesOnlyCurrentRepository(string current, string latest, bool newer)
    {
        using var handler = new FakeHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(JsonSerializer.Serialize(new { tag_name = latest, html_url = "https://evil.test/execute.exe" })) }));
        using var client = new HttpClient(handler);
        Assert.Equal(0, handler.Calls);
        var result = await AppUpdateService.CheckAsync(current, client);
        Assert.True(result.CheckSucceeded); Assert.Equal(newer, result.Newer); Assert.Equal(1, handler.Calls);
        Assert.Equal("github.com", result.ReleasesUrl.Host); Assert.Contains("RazvanManolache/DLSS5-Everything/releases/", result.ReleasesUrl.AbsoluteUri);
    }

    [Theory]
    [InlineData(403, "{}", "Failed")] [InlineData(429, "{}", "Failed")]
    [InlineData(404, "{}", "NoRelease")] [InlineData(200, "{}", "Failed")]
    [InlineData(200, "bad json", "Failed")] [InlineData(200, "{\"tag_name\":\"main\"}", "Failed")]
    [InlineData(200, "{\"tag_name\":\"v9.0.0\",\"draft\":true}", "Failed")]
    [InlineData(302, "", "Failed")]
    public async Task FailedChecksAreNeverReportedAsUpToDate(int status, string body, string expected)
    {
        using var handler = new FakeHandler((_, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(body) }));
        using var client = new HttpClient(handler);
        var result = await AppUpdateService.CheckAsync("1.0.0", client);
        Assert.Equal(expected, result.Status.ToString()); Assert.False(result.CheckSucceeded); Assert.False(result.Newer); Assert.NotNull(result.Error);
    }

    [Fact]
    public async Task OfflineOversizedAndCancelledChecksCannotBecomeSuccess()
    {
        using var offlineHandler = new FakeHandler((_, _) => throw new HttpRequestException("offline"));
        using var offline = new HttpClient(offlineHandler);
        Assert.Equal(AppUpdateStatus.Failed, (await AppUpdateService.CheckAsync("1.0", offline)).Status);
        using var largeHandler = new FakeHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(new string('x', 128 * 1024 + 1)) }));
        using var large = new HttpClient(largeHandler);
        Assert.Equal(AppUpdateStatus.Failed, (await AppUpdateService.CheckAsync("1.0", large)).Status);
        Assert.Equal(AppUpdateStatus.Failed, (await AppUpdateService.CheckAsync("unknown", large)).Status);
        Assert.Equal(1, largeHandler.Calls);
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => AppUpdateService.CheckAsync("1.0", large, cancel.Token));
        Assert.Equal(1, largeHandler.Calls);
    }
}
