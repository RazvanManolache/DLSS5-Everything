using System.Buffers.Binary;
using System.Text;
using System.Text.Json;

namespace Dlss5CompatApp.Tests;

public sealed class GameRouteOverrideTests
{
    [Fact]
    public void AutoRetainsDetectionAndExplicitRouteDoesNotMutateIt()
    {
        using var fixture = new Fixture();
        var detected = fixture.Game with { HasNativeDlssSignal = false };
        Assert.Equal(InstallRoute.X64DxgiFeeder, detected.Route);
        var native = new GameProfile { PreferredRoute = GameRoutePreference.Native }.Apply(detected);
        Assert.Equal(InstallRoute.X64DirectPhoenix, native.Route);
        Assert.False(native.HasNativeDlssSignal);
        Assert.Null(detected.PreferredRoute);
        Assert.Equal(detected, new GameProfile().Apply(native));
        Assert.Equal(InstallRoute.X64DxgiFeeder,
            (fixture.Game with { PreferredRoute = GameRoutePreference.Feeder }).Route);
        Assert.Equal(InstallRoute.X64DirectPhoenix, fixture.Game.Route);
    }

    [Fact]
    public void SavedOverridesRoundTripAndApplyAfterApiOverride()
    {
        using var fixture = new Fixture();
        var path = fixture.Workspace.FilePath("library.json");
        var state = new LibraryState();
        state.Profiles[fixture.Game.ExePath] = new() { ApiOverride = GraphicsApi.DirectX11,
            PreferredRoute = GameRoutePreference.Feeder, ProxyName = "D3D11.DLL" };
        state.Save(path);
        var profile = LibraryState.Load(path).ProfileFor(fixture.Game.ExePath);
        Assert.Equal(state.ProfileFor(fixture.Game.ExePath), profile);
        var effective = profile.Apply(fixture.Game);
        effective.ValidateOverrides();
        Assert.Equal(GraphicsApi.DirectX11, effective.Api);
        Assert.Equal(GameRoutePreference.Feeder, effective.PreferredRoute);
        Assert.Equal("d3d11.dll", effective.ProxyName);
        Assert.Equal(InstallRoute.X64DxgiFeeder, effective.Route);
        Assert.Equal(GraphicsApi.DirectX12, fixture.Game.Api);
        Assert.Null(fixture.Game.ProxyName);
        var oldProfile = JsonSerializer.Deserialize<GameProfile>("{}");
        Assert.Equal(fixture.Game, oldProfile!.Apply(fixture.Game));
    }

    [Theory]
    [InlineData("../dxgi.dll")]
    [InlineData("C:\\outside\\dxgi.dll")]
    [InlineData("dxgi.dll ")]
    [InlineData("")]
    [InlineData("openvr_api.dll")]
    [InlineData("renodx.dll")]
    public void InvalidSavedProxyCannotBeLoadedOrOverwriteLibrary(string proxy)
    {
        using var fixture = new Fixture();
        var path = fixture.Workspace.FilePath("library.json");
        var state = new LibraryState();
        state.Save(path);
        var original = File.ReadAllBytes(path);
        state.Profiles[fixture.Game.ExePath] = new() { ProxyName = proxy };
        Assert.Throws<InvalidDataException>(() => state.Save(path));
        Assert.Equal(original, File.ReadAllBytes(path));
        Assert.Throws<InvalidDataException>(() => JsonSerializer.Deserialize<GameProfile>(
            "{\"ProxyName\":" + JsonSerializer.Serialize(proxy) + "}", LocalData.Json));
    }

    [Fact]
    public void UnknownSavedRouteFailsValidationAndDeserialization()
    {
        var profile = new GameProfile { PreferredRoute = (GameRoutePreference)999 };
        Assert.Throws<InvalidDataException>(() => profile.ValidateOverrides());
        Assert.Throws<InvalidDataException>(() => JsonSerializer.Serialize(profile, LocalData.Json));
        Assert.Throws<InvalidDataException>(() => JsonSerializer.Deserialize<GameProfile>("{\"PreferredRoute\":999}", LocalData.Json));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<GameProfile>("{\"PreferredRoute\":\"Unknown\"}", LocalData.Json));
    }

    [Theory]
    [InlineData(CpuArch.X86, GraphicsApi.DirectX12, GameRoutePreference.Native, null)]
    [InlineData(CpuArch.X64, GraphicsApi.DirectX11, GameRoutePreference.Native, null)]
    [InlineData(CpuArch.X64, GraphicsApi.Dxgi, GameRoutePreference.Native, null)]
    [InlineData(CpuArch.X64, GraphicsApi.OpenVr, GameRoutePreference.Native, null)]
    [InlineData(CpuArch.Unknown, GraphicsApi.DirectX11, GameRoutePreference.Feeder, null)]
    [InlineData(CpuArch.X64, GraphicsApi.DirectX12, null, "d3d11.dll")]
    [InlineData(CpuArch.X64, GraphicsApi.Dxgi, null, "d3d11.dll")]
    [InlineData(CpuArch.X64, GraphicsApi.OpenGl, null, "dxgi.dll")]
    [InlineData(CpuArch.X64, GraphicsApi.OpenXr, null, "dxgi.dll")]
    [InlineData(CpuArch.X64, GraphicsApi.OpenVr, null, "dxgi.dll")]
    [InlineData(CpuArch.X64, GraphicsApi.Vulkan, null, "dxgi.dll")]
    [InlineData(CpuArch.X86, GraphicsApi.DirectX9, null, "dxgi.dll")]
    [InlineData(CpuArch.X64, GraphicsApi.DirectX12, (GameRoutePreference)999, null)]
    public async Task UnsupportedOverridesFailBeforeAnyInstallationWrites(
        object architecture, object graphicsApi, object? preference, string? proxy)
    {
        using var fixture = new Fixture();
        var candidate = fixture.Game with { Arch = (CpuArch)architecture, Api = (GraphicsApi)graphicsApi,
            PreferredRoute = preference is null ? null : (GameRoutePreference)preference, ProxyName = proxy };
        Assert.NotNull(candidate.OverrideValidationError);
        Assert.Equal(InstallRoute.Unsupported, candidate.Route);
        Assert.Throws<InvalidOperationException>(() => candidate.ValidateOverrides());
        var result = InstallerEngine.CheckPreflight(candidate, fixture.Payload);
        Assert.False(result.CanInstall);
        Assert.Contains(candidate.OverrideValidationError!, result.DisplayText);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Install(candidate));
        Assert.Equal(new[] { "game.exe" }, Directory.GetFiles(candidate.Root).Select(Path.GetFileName));
        Assert.Empty(Directory.GetDirectories(candidate.Root));
    }

    [Theory]
    [InlineData(CpuArch.X86, "dxgi.dll")]
    [InlineData(CpuArch.X86, "d3d11.dll")]
    [InlineData(CpuArch.X64, "dxgi.dll")]
    [InlineData(CpuArch.X64, "d3d11.dll")]
    public async Task SelectedDx11ProxyIsInstalledJournaledAndRestored(object architecture, string proxy)
    {
        var arch = (CpuArch)architecture;
        using var fixture = new Fixture(arch);
        var game = fixture.Game with { Api = GraphicsApi.DirectX11, PreferredRoute = GameRoutePreference.Feeder, ProxyName = proxy };
        var ini = Path.Combine(game.Root, "ReShade.ini");
        var original = Encoding.UTF8.GetBytes("[USER]\r\nKeep=original\n");
        File.WriteAllBytes(ini, original);
        var exeBytes = File.ReadAllBytes(game.ExePath);
        await fixture.Install(game);
        Assert.Equal(File.ReadAllBytes(arch == CpuArch.X86 ? fixture.Payload.ReShade32Dll! : fixture.Payload.ReShade64Dll!),
            File.ReadAllBytes(Path.Combine(game.Root, proxy)));
        var other = proxy == "dxgi.dll" ? "d3d11.dll" : "dxgi.dll";
        Assert.False(File.Exists(Path.Combine(game.Root, other)));
        var manifest = fixture.ReadManifest();
        Assert.Contains(proxy, manifest.Added);
        Assert.Equal(game.Route.ToString(), manifest.Route);
        await fixture.Engine.RestoreAsync(game.Root);
        Assert.False(File.Exists(Path.Combine(game.Root, proxy)));
        Assert.Equal(original, File.ReadAllBytes(ini));
        Assert.Equal(exeBytes, File.ReadAllBytes(game.ExePath));
    }

    [Theory]
    [InlineData(CpuArch.X86)]
    [InlineData(CpuArch.X64)]
    public async Task SwitchingProxyUsesNewDestinationAndPreservesSavedControls(object architecture)
    {
        var arch = (CpuArch)architecture;
        using var fixture = new Fixture(arch);
        var game = fixture.Game with { Api = GraphicsApi.DirectX11, PreferredRoute = GameRoutePreference.Feeder, ProxyName = "d3d11.dll" };
        await fixture.Install(game);
        var ini = Path.Combine(game.Root, "ReShade.ini");
        IniEditor.SetValue(ini, "USER", "Keep", "saved-control");
        IniEditor.SetValue(ini, "PROXY", "EnableProxyLibrary", "1");
        IniEditor.SetValue(ini, "PROXY", "ProxyLibrary", @".\openvr_api.dlss5-original.dll");
        await fixture.Install(game with { ProxyName = "dxgi.dll" });
        Assert.False(File.Exists(Path.Combine(game.Root, "d3d11.dll")));
        Assert.True(File.Exists(Path.Combine(game.Root, "dxgi.dll")));
        Assert.Contains("Keep=saved-control", File.ReadAllText(ini));
        Assert.Contains("EnableProxyLibrary=0", File.ReadAllText(ini));
        Assert.DoesNotContain(@".\openvr_api.dlss5-original.dll", File.ReadAllText(ini));
        await fixture.Engine.RestoreAsync(game.Root);
        Assert.False(File.Exists(Path.Combine(game.Root, "dxgi.dll")));
        Assert.False(File.Exists(Path.Combine(game.Root, "d3d11.dll")));
        Assert.False(File.Exists(ini));
    }

    [Fact]
    public async Task SavedRouteOverrideControlsInstallAndSwitchesBackToNative()
    {
        using var fixture = new Fixture();
        var originalModel = fixture.Workspace.WritePe("game/nvngx_dlssnr.dll", CpuArch.X64);
        var modelBytes = File.ReadAllBytes(originalModel);
        var path = fixture.Workspace.FilePath("library.json");
        var state = new LibraryState();
        state.Profiles[fixture.Game.ExePath] = new() { PreferredRoute = GameRoutePreference.Feeder, ProxyName = "dxgi.dll" };
        state.Save(path);
        var feeder = LibraryState.Load(path).ProfileFor(fixture.Game.ExePath).Apply(fixture.Game);
        await fixture.Install(feeder);
        Assert.Equal(InstallRoute.X64DxgiFeeder.ToString(), fixture.ReadManifest().Route);
        Assert.True(File.Exists(Path.Combine(feeder.Root, "host64/Phoenix.addon64")));
        Assert.False(File.Exists(Path.Combine(feeder.Root, "Phoenix.addon64")));
        var native = new GameProfile { PreferredRoute = GameRoutePreference.Native }.Apply(fixture.Game with { HasNativeDlssSignal = false });
        await fixture.Install(native);
        Assert.Equal(InstallRoute.X64DirectPhoenix.ToString(), fixture.ReadManifest().Route);
        Assert.True(File.Exists(Path.Combine(native.Root, "Phoenix.addon64")));
        Assert.False(File.Exists(Path.Combine(native.Root, "host64/Phoenix.addon64")));
        Assert.Equal(modelBytes, File.ReadAllBytes(originalModel));
        await fixture.Engine.RestoreAsync(native.Root);
        Assert.Equal(modelBytes, File.ReadAllBytes(originalModel));
        Assert.False(File.Exists(Path.Combine(native.Root, "dxgi.dll")));
    }

    [Theory]
    [InlineData("dxgi.dll")]
    [InlineData("d3d11.dll")]
    public async Task SelectedUnmanagedProxyBlocksBeforeWrites(string proxy)
    {
        using var fixture = new Fixture();
        var game = fixture.Game with { Api = GraphicsApi.DirectX11, ProxyName = proxy };
        var file = Path.Combine(game.Root, proxy);
        File.WriteAllText(file, "unmanaged-original");
        Assert.False(InstallerEngine.CheckPreflight(game, fixture.Payload).CanInstall);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Install(game));
        Assert.Equal("unmanaged-original", File.ReadAllText(file));
        Assert.False(Directory.Exists(Path.Combine(game.Root, "_DLSS5_Compat_Backup")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OwnedProxyOriginalSurvivesRestoreOrInterruptedOverride(bool interrupt)
    {
        using var fixture = new Fixture();
        var game = fixture.Game with { Api = GraphicsApi.DirectX11, ProxyName = "d3d11.dll" };
        var proxy = Path.Combine(game.Root, "d3d11.dll");
        var original = Encoding.UTF8.GetBytes("previously-backed-up game original\r\n");
        File.WriteAllBytes(proxy, original);
        var backup = Path.Combine(game.Root, "_DLSS5_Compat_Backup");
        Directory.CreateDirectory(backup);
        File.WriteAllBytes(Path.Combine(backup, "d3d11.dll"), original);
        // A validated legacy ownership journal is the only way an existing proxy may be replaced.
        File.WriteAllText(Path.Combine(backup, "manifest.json"), JsonSerializer.Serialize(new InstallManifest
        {
            Version = 1, GameRoot = game.Root, ExeRelativePath = "game.exe", Route = "X64DxgiFeeder",
            Replaced = [new ManifestFile { RelativePath = "d3d11.dll" }]
        }));
        if (interrupt)
        {
            var failed = new InstallerEngine(message =>
            {
                if (message == "Copied d3d11.dll") throw new IOException("simulated interruption after chosen proxy write");
            });
            await Assert.ThrowsAsync<IOException>(() => fixture.Install(game, failed));
            Assert.True(InstallerEngine.HasPending(game.Root));
            Assert.Equal(File.ReadAllBytes(fixture.Payload.ReShade64Dll!), File.ReadAllBytes(proxy));
            await fixture.Engine.RecoverAsync(game.Root);
            Assert.False(InstallerEngine.HasPending(game.Root));
        }
        else
        {
            await fixture.Install(game);
            var entry = Assert.Single(fixture.ReadManifest().Replaced, item => item.RelativePath == "d3d11.dll");
            Assert.Equal(original, File.ReadAllBytes(Path.Combine(backup, entry.BackupRelativePath)));
            await fixture.Engine.RestoreAsync(game.Root);
        }
        Assert.Equal(original, File.ReadAllBytes(proxy));
    }

    [Fact]
    public async Task UnselectedUnmanagedProxyIsNeverRemovedOrJournaled()
    {
        using var fixture = new Fixture();
        var game = fixture.Game with { Api = GraphicsApi.DirectX11, ProxyName = "d3d11.dll" };
        var file = Path.Combine(game.Root, "dxgi.dll");
        File.WriteAllText(file, "external-original");
        var result = InstallerEngine.CheckPreflight(game, fixture.Payload);
        Assert.True(result.CanInstall);
        Assert.True(result.HasWarnings);
        await fixture.Install(game);
        Assert.Equal("external-original", File.ReadAllText(file));
        Assert.DoesNotContain("dxgi.dll", fixture.ReadManifest().Added);
        Assert.DoesNotContain(fixture.ReadManifest().Replaced, item => item.RelativePath == "dxgi.dll");
        await fixture.Engine.RestoreAsync(game.Root);
        Assert.Equal("external-original", File.ReadAllText(file));
    }

    [Fact]
    public async Task ExplicitDxgiProxyBypassesOnlyTheImplicitOpenVrHeuristic()
    {
        using var fixture = new Fixture();
        var game = fixture.Game with { Api = GraphicsApi.DirectX11, Detection = "rendersystemdx11.dll", ProxyName = "dxgi.dll" };
        var openVr = Path.Combine(game.Root, "openvr_api.dll");
        File.WriteAllText(openVr, "game-original-openvr");
        await fixture.Install(game);
        Assert.Equal("game-original-openvr", File.ReadAllText(openVr));
        Assert.True(File.Exists(Path.Combine(game.Root, "dxgi.dll")));
        Assert.False(File.Exists(Path.Combine(game.Root, "openvr_api.dlss5-original.dll")));
        Assert.DoesNotContain(fixture.ReadManifest().Replaced, file => file.RelativePath == "openvr_api.dll");
        await fixture.Engine.RestoreAsync(game.Root);
        Assert.Equal("game-original-openvr", File.ReadAllText(openVr));
    }

    [Theory]
    [InlineData("dxgi.dll")]
    [InlineData("d3d11.dll")]
    public async Task SelectedLockedOrLinkedProxyIsRejected(string proxy)
    {
        using var fixture = new Fixture();
        var game = fixture.Game with { Api = GraphicsApi.DirectX11, ProxyName = proxy };
        var file = Path.Combine(game.Root, proxy);
        File.WriteAllText(file, "original");
        using (var locked = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None))
            Assert.False(InstallerEngine.CheckPreflight(game, fixture.Payload).CanInstall);
        File.Delete(file);
        var outsideDirectory = fixture.Workspace.FilePath("outside");
        Directory.CreateDirectory(outsideDirectory);
        var outside = Path.Combine(outsideDirectory, "original.dll");
        File.WriteAllText(outside, "outside-original");
        SwapperTestLinks.CreateJunction(file, outsideDirectory);
        try
        {
            Assert.False(InstallerEngine.CheckPreflight(game, fixture.Payload).CanInstall);
            await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Install(game));
            Assert.Equal("outside-original", File.ReadAllText(outside));
            Assert.False(Directory.Exists(Path.Combine(game.Root, "_DLSS5_Compat_Backup")));
        }
        finally { Directory.Delete(file); }
    }

    sealed class Fixture : IDisposable
    {
        public TestWorkspace Workspace { get; } = new();
        public GameCandidate Game { get; }
        public PayloadInfo Payload { get; }
        public InstallerEngine Engine { get; } = new(_ => { });
        readonly PhoenixSettings settings;

        public Fixture(CpuArch arch = CpuArch.X64)
        {
            var exe = Workspace.WritePe("game/game.exe", arch, "d3d12.dll");
            Game = new(Path.GetDirectoryName(exe)!, exe, "Override fixture", "Override fixture", arch,
                GraphicsApi.DirectX12, "fixture", 1, HasNativeDlssSignal: true);
            Dll("payload/ReShade64.dll", CpuArch.X64);
            Dll("payload/ReShade32.dll", CpuArch.X86);
            Dll("payload/nvngx_dlss.dll", CpuArch.X64);
            Dll("payload/nvngx_dlssnr.dll", CpuArch.X64);
            var phoenix = Dll("payload/Phoenix.addon64", CpuArch.X64);
            TestWorkspace.SetPhoenixFeederExports(phoenix);
            var bundle = Workspace.FilePath("compiled-bundle"); Directory.CreateDirectory(bundle);
            var runtime = Workspace.FilePath("compiled-runtime"); Directory.CreateDirectory(runtime);
            File.WriteAllText(Path.Combine(runtime, "phoenix_runtime_cpp.dll"), "CPU fixture; not executable");
            File.WriteAllText(Path.Combine(runtime, "phoenix_native_cpp.json"), "{}");
            settings = new() { Runner = "cpu", BundlePath = bundle, RuntimeRoot = runtime };
            Payload = PayloadScanner.Scan(Workspace.FilePath("payload"));
        }

        string Dll(string name, CpuArch arch)
        {
            var path = Workspace.WritePe(name, arch);
            var bytes = File.ReadAllBytes(path);
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(0x96), 0x2022);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0x98 + (arch == CpuArch.X64 ? 108 : 92)), 16);
            File.WriteAllBytes(path, bytes);
            return path;
        }

        public Task Install(GameCandidate game, InstallerEngine? engine = null) =>
            (engine ?? Engine).InstallAsync(game, Payload, phoenixSettings: settings);
        public InstallManifest ReadManifest() => JsonSerializer.Deserialize<InstallManifest>(
            File.ReadAllText(Path.Combine(Game.Root, "_DLSS5_Compat_Backup/manifest.json")))!;
        public void Dispose() => Workspace.Dispose();
    }
}
