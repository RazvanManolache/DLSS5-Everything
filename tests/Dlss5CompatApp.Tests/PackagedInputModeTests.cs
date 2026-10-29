using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Dlss5CompatApp.Tests;

public sealed class PackagedInputModeTests
{
    [Theory]
    [InlineData(true, false, false)]
    [InlineData(true, true, true)]
    [InlineData(false, false, true)]
    [InlineData(false, true, true)]
    public void EngineDllPresenceDoesNotForceNativeInputWhenStandaloneIsAvailable(bool standalone, bool explicitNative, bool expectedNative)
    {
        using var fixture = new Fixture(standalone);
        var game = fixture.Game with { PreferredRoute = explicitNative ? GameRoutePreference.Native : null };
        var plan = PackagedPhoenixPlan.For(game, fixture.Payload)!;
        Assert.Equal(expectedNative, plan.NativeGameInput);
        Assert.Equal(expectedNative ? "game_dlss" : "feeder_d3d12", plan.InputProvider);
        Assert.True(plan.SupportsInProcessBackbuffer);
    }

    [Fact]
    public void NativeOnlyAddonExplainsItsResolutionRequirementBeforeInstallation()
    {
        using var fixture = new Fixture(false);
        var checks = InstallerEngine.CheckPreflight(fixture.Game, fixture.Payload);
        Assert.Contains(checks.Findings, finding => finding.Code == "native-input-requirements" &&
            finding.Severity == PreflightSeverity.Warning && finding.Message.Contains("DLAA"));
    }

    [Fact]
    public async Task AutomaticInstallAndSavedProfilesAgreeOnBackbufferInput()
    {
        using var fixture = new Fixture(true);
        var engine = new InstallerEngine(_ => { });
        await engine.InstallAsync(fixture.Game, fixture.Payload);
        fixture.AssertBackbufferInput();
        var config = JsonNode.Parse(File.ReadAllText(fixture.Config))!;
        config["input_provider"] = "game_dlss";
        File.WriteAllText(fixture.Config, config.ToJsonString());
        IniEditor.SetValue(fixture.Ini, "Phoenix.Compatibility", "StandaloneInput", "0");
        IniEditor.SetValue(fixture.Ini, "Phoenix.Compatibility", "Enabled", "0");
        await engine.InstallAsync(fixture.Game, fixture.Payload);
        fixture.AssertBackbufferInput();
    }

    [Fact]
    public async Task VerificationDoesNotReinterpretAutomaticNativeExecutionAsForcedGameInput()
    {
        using var fixture = new Fixture(true);
        await new InstallerEngine(_ => { }).InstallAsync(fixture.Game, fixture.Payload);
        var installed = InstallerEngine.InstalledGame(fixture.Game with { HasNativeDlssSignal = false });
        Assert.Equal(InstallRoute.X64DirectPhoenix, installed.Route);
        Assert.Null(installed.PreferredRoute);
        Assert.False(PackagedPhoenixPlan.For(installed, fixture.Payload)!.NativeGameInput);
    }

    [Fact]
    public async Task InputRepairPreservesModelRendererAndArbitraryTuning()
    {
        using var fixture = new Fixture(true);
        var engine = new InstallerEngine(_ => { });
        await engine.InstallAsync(fixture.Game with { PreferredRoute = GameRoutePreference.Native }, fixture.Payload);
        IniEditor.SetValue(fixture.Ini, "Phoenix", "Skin", "2.75");
        IniEditor.SetValue(fixture.Ini, "Phoenix", "Backend", "3");
        IniEditor.SetValue(fixture.Ini, "Phoenix.Compatibility", "ExecutionMode", "auto");
        var config = JsonNode.Parse(File.ReadAllText(fixture.Config))!;
        config["user_extension"] = "keep this too";
        File.WriteAllText(fixture.Config, config.ToJsonString());
        var originalModel = config["weight_source"]!.ToJsonString();
        var originalAddon = File.ReadAllBytes(Path.Combine(fixture.Game.Root, "Phoenix.addon64"));
        await engine.RepairPackagedInputAsync(fixture.Game, fixture.Payload);
        fixture.AssertBackbufferInput();
        var repaired = JsonNode.Parse(File.ReadAllText(fixture.Config))!;
        Assert.Equal(originalModel, repaired["weight_source"]!.ToJsonString());
        Assert.Equal("keep this too", repaired["user_extension"]!.GetValue<string>());
        Assert.Equal("2.75", IniEditor.GetValue(fixture.Ini, "Phoenix", "Skin"));
        Assert.Equal("3", IniEditor.GetValue(fixture.Ini, "Phoenix", "Backend"));
        Assert.Equal("auto", IniEditor.GetValue(fixture.Ini, "Phoenix.Compatibility", "ExecutionMode"));
        Assert.Equal(originalAddon, File.ReadAllBytes(Path.Combine(fixture.Game.Root, "Phoenix.addon64")));
        var installed = InstallerEngine.InstalledGame(fixture.Game);
        Assert.Null(installed.PreferredRoute);
        Assert.False(PackagedPhoenixPlan.For(installed, fixture.Payload)!.NativeGameInput);
        await engine.RepairPackagedInputAsync(fixture.Game, fixture.Payload);
        fixture.AssertBackbufferInput();
    }

    [Fact]
    public async Task ExplicitNativeInputIsNotSilentlyOverwrittenByRepair()
    {
        using var fixture = new Fixture(true);
        var game = fixture.Game with { PreferredRoute = GameRoutePreference.Native };
        var engine = new InstallerEngine(_ => { });
        await engine.InstallAsync(game, fixture.Payload);
        var before = File.ReadAllBytes(fixture.Config);
        await Assert.ThrowsAsync<InvalidOperationException>(() => engine.RepairPackagedInputAsync(game, fixture.Payload));
        Assert.Equal(before, File.ReadAllBytes(fixture.Config));
        Assert.False(InstallerEngine.HasPending(game.Root));
    }

    sealed class Fixture : IDisposable
    {
        public TestWorkspace Workspace { get; } = new();
        public GameCandidate Game { get; }
        public PayloadInfo Payload { get; }
        public string Config => Path.Combine(Game.Root, "Phoenix.json");
        public string Ini => Path.Combine(Game.Root, "ReShade.ini");
        public Fixture(bool standalone)
        {
            var exe = Workspace.WritePe("game/game.exe", CpuArch.X64, "d3d12.dll");
            Game = new(Path.GetDirectoryName(exe)!, exe, "Unrelated fixture", "game.exe", CpuArch.X64, GraphicsApi.DirectX12,
                "DLL evidence; input resolution is not known until runtime", 160 * 1024, HasNativeDlssSignal: true);
            var addon = Dll("payload/Phoenix.addon64", "CnrPHOENIXSummary", "CnrPHOENIXRegister", "CnrPHOENIXFeederVersion",
                "CnrPHOENIXFeederEvaluate", "CnrPHOENIXCompatibilityStatus", "CnrPHOENIXInProcessBackbufferVersion", "AddonInit", "AddonUninit");
            TestWorkspace.SetEmbeddedPhoenixRuntime(addon);
            using (var marker = new FileStream(addon, FileMode.Open, FileAccess.Write))
            {
                marker.Position = 0x11000;
                marker.Write(Encoding.ASCII.GetBytes(standalone ? "StandaloneInput" : "Direct mode currently requires DLAA/native-size motion guides"));
            }
            Dll("payload/ReShade64.dll"); Dll("payload/nvngx_dlssnr.dll");
            Payload = PayloadScanner.Scan(Workspace.FilePath("payload"));
        }
        public void AssertBackbufferInput()
        {
            using var config = JsonDocument.Parse(File.ReadAllText(Config));
            Assert.Equal("feeder_d3d12", config.RootElement.GetProperty("input_provider").GetString());
            foreach (var name in new[] { "ReShade.ini", "ReShadeVR.ini" })
            {
                Assert.Equal("1", IniEditor.GetValue(Path.Combine(Game.Root, name), "Phoenix.Compatibility", "Enabled"));
                Assert.Equal("1", IniEditor.GetValue(Path.Combine(Game.Root, name), "Phoenix.Compatibility", "StandaloneInput"));
            }
        }
        string Dll(string relative, params string[] exports)
        {
            var path = Workspace.WritePe(relative, CpuArch.X64);
            var bytes = File.ReadAllBytes(path);
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(0x96), 0x2022);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0x98 + 108), 16);
            File.WriteAllBytes(path, bytes); TestWorkspace.SetPhoenixExports(path, exports);
            return path;
        }
        public void Dispose() => Workspace.Dispose();
    }
}
