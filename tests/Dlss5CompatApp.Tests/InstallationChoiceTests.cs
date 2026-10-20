using System.Reflection;
using System.Text.Json;

namespace Dlss5CompatApp.Tests;

public sealed class InstallationChoiceTests
{
    static GameCandidate Game(GraphicsApi api, params GraphicsApi[] alternatives) =>
        new(@"C:\Games\Fixture", @"C:\Games\Fixture\fixture.exe", "Fixture", "fixture.exe", CpuArch.X64,
            api, "test imports", 160 * 1024, alternatives.Length > 0 ? alternatives : [api]);

    [Theory]
    [InlineData("Unknown")]
    [InlineData("Dxgi")]
    public void UnknownOrUnspecifiedRendererRequiresAChoice(string apiName)
    {
        var api = Enum.Parse<GraphicsApi>(apiName);
        var game = Game(api);
        Assert.True(InstallationChoice.NeedsSelection(game, game, false));
        var choices = InstallationChoice.Options(game);
        Assert.Contains(choices, choice => choice.Api == GraphicsApi.DirectX11);
        Assert.Contains(choices, choice => choice.Api == GraphicsApi.DirectX12);
        Assert.All(choices, choice => Assert.NotEqual(InstallRoute.Unsupported, choice.Route));
    }

    [Fact]
    public void MultipleDetectedRenderersPromptUnlessExplicitOrLiveVerified()
    {
        var game = Game(GraphicsApi.DirectX11, GraphicsApi.DirectX11, GraphicsApi.DirectX12, GraphicsApi.Vulkan);
        Assert.True(InstallationChoice.NeedsSelection(game, game, false));
        Assert.False(InstallationChoice.NeedsSelection(game, game, true));
        Assert.False(InstallationChoice.NeedsSelection(game with { HasVerifiedRenderer = true }, game, false));
        Assert.Equal(new[] { GraphicsApi.DirectX11, GraphicsApi.DirectX12, GraphicsApi.Vulkan }, InstallationChoice.Options(game).Select(choice => choice.Api));
    }

    [Fact]
    public void ConcreteSingleRendererIsNotConfusedByGenericDxgiImports()
    {
        var game = Game(GraphicsApi.DirectX11, GraphicsApi.Dxgi, GraphicsApi.DirectX11);
        Assert.False(InstallationChoice.NeedsSelection(game, game, false));
        Assert.Single(InstallationChoice.Options(game));
        Assert.True(InstallationChoice.NeedsSelection(game with { RuntimeSelectionRequired = true }, game, false));
    }

    [Fact]
    public void UnsupportedChoiceOffersOnlySupportedDetectedAlternatives()
    {
        var game = Game(GraphicsApi.DirectX8, GraphicsApi.DirectX8, GraphicsApi.DirectX11);
        Assert.True(InstallationChoice.NeedsSelection(game, game, true));
        Assert.Equal(GraphicsApi.DirectX11, Assert.Single(InstallationChoice.Options(game)).Api);
    }

    [Fact]
    public void ChangingRendererResetsIncompatibleOverridesAndUsesItsArguments()
    {
        var game = Game(GraphicsApi.DirectX12, GraphicsApi.DirectX12, GraphicsApi.DirectX11) with
        {
            PreferredRoute = GameRoutePreference.Native, SuggestedArguments = "-dx12 -custom",
            SuggestedArgumentsByApi = new Dictionary<GraphicsApi, string> { [GraphicsApi.DirectX11] = "-dx11" }
        };
        var choices = InstallationChoice.Options(game);
        Assert.Equal("-dx12 -custom", choices.Single(choice => choice.Api == GraphicsApi.DirectX12).SuggestedArguments);
        var alternate = choices.Single(choice => choice.Api == GraphicsApi.DirectX11);
        Assert.Null(alternate.PreferredRoute);
        Assert.Equal("-dx11", alternate.SuggestedArguments);
    }

    [Fact]
    public void FailedNativeRouteOffersCaptureBeforeOtherDetectedRenderers()
    {
        var native = Game(GraphicsApi.DirectX12, GraphicsApi.DirectX12, GraphicsApi.Vulkan) with { HasNativeDlssSignal = true };
        var attempted = new HashSet<string> { InstallationChoice.RouteKey(native) };
        var choices = InstallationChoice.RepairOptions(native, attempted);
        Assert.Equal(GraphicsApi.DirectX12, choices[0].Api);
        Assert.Equal(GameRoutePreference.Feeder, choices[0].PreferredRoute);
        Assert.Contains(choices, choice => choice.Api == GraphicsApi.Vulkan);
        Assert.DoesNotContain(choices, choice => attempted.Contains(InstallationChoice.RouteKey(choice)));
        Assert.Equal(choices.Count, choices.Select(InstallationChoice.RouteKey).Distinct().Count());
    }

    [Fact]
    public void FailedProxyOffersAlternativeAndNeverRepeatsAttemptedRoutes()
    {
        var game = Game(GraphicsApi.DirectX11);
        var attempted = new HashSet<string> { InstallationChoice.RouteKey(game) };
        var alternate = Assert.Single(InstallationChoice.RepairOptions(game, attempted));
        Assert.Equal("d3d11.dll", alternate.ProxyName);
        attempted.Add(InstallationChoice.RouteKey(alternate));
        Assert.Empty(InstallationChoice.RepairOptions(alternate, attempted));
        Assert.Equal(InstallationChoice.RouteKey(game), InstallationChoice.RouteKey(game with { ProxyName = "dxgi.dll" }));
    }

    [Fact]
    public void VerifyReadsInstalledChoiceInsteadOfEditedUiOrChangedDetection()
    {
        using var workspace = new TestWorkspace();
        var (game, manifest, path) = InstalledFixture(workspace);
        manifest.Api = GraphicsApi.DirectX11;
        manifest.PreferredRoute = GameRoutePreference.Feeder;
        manifest.ProxyName = "d3d11.dll";
        manifest.LaunchArguments = "-dx11 -windowed";
        File.WriteAllText(path, JsonSerializer.Serialize(manifest));
        var before = File.ReadAllBytes(path);
        var installed = InstallerEngine.InstalledGame(game.WithApi(GraphicsApi.Vulkan));
        Assert.Equal(GraphicsApi.DirectX11, installed.Api);
        Assert.Equal("d3d11.dll", installed.ProxyName);
        Assert.Equal("-dx11 -windowed", installed.SuggestedArguments);
        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.Equal(InstallRoute.X64DxgiFeeder, installed.Route);
    }

    [Theory]
    [InlineData("X64VulkanFeeder", "Vulkan")]
    [InlineData("X64DirectPhoenix", "DirectX12")]
    [InlineData("X64OpenGlFeeder", "OpenGl")]
    public void LegacyJournalsRecoverUnambiguousInstalledApi(string route, string expected)
    {
        using var workspace = new TestWorkspace();
        var (game, manifest, path) = InstalledFixture(workspace);
        manifest.Route = route;
        File.WriteAllText(path, JsonSerializer.Serialize(manifest));
        Assert.Equal(Enum.Parse<GraphicsApi>(expected), InstallerEngine.InstalledGame(game).Api);
    }

    [Fact]
    public void VerifyRefusesIncompleteOrMismatchedInstallWithoutChangingFiles()
    {
        using var workspace = new TestWorkspace();
        var (game, manifest, path) = InstalledFixture(workspace);
        var pending = Path.Combine(Path.GetDirectoryName(path)!, "pending.json");
        File.WriteAllText(pending, "interrupted");
        Assert.Contains("Recover", Assert.Throws<InvalidOperationException>(() => InstallerEngine.InstalledGame(game)).Message);
        File.Delete(pending);
        manifest.Api = GraphicsApi.Vulkan;
        File.WriteAllText(path, JsonSerializer.Serialize(manifest));
        Assert.Contains("cannot be identified", Assert.Throws<InvalidOperationException>(() => InstallerEngine.InstalledGame(game)).Message);
        Assert.Equal(GraphicsApi.Vulkan, JsonSerializer.Deserialize<InstallManifest>(File.ReadAllText(path))!.Api);
    }

    [Fact]
    public async Task ExistingInstallVerificationRejectsDestructiveOptionsBeforeAnyAction()
    {
        using var workspace = new TestWorkspace();
        var config = new SmokeTestConfig { VerifyExistingInstall = true, PayloadRoot = workspace.FilePath("absent-payload") };
        await Assert.ThrowsAsync<InvalidOperationException>(() => SmokeTestRunner.RunAsync(config, workspace.Root));
        Assert.False(Directory.Exists(config.PayloadRoot));
    }

    static (GameCandidate Game, InstallManifest Manifest, string Path) InstalledFixture(TestWorkspace workspace)
    {
        var exe = workspace.WritePe("fixture.exe", CpuArch.X64, "d3d11.dll");
        File.WriteAllText(workspace.FilePath("Phoenix.addon64"), "fixture");
        var path = workspace.FilePath("_DLSS5_Compat_Backup/manifest.json");
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        var manifest = new InstallManifest
        {
            GameRoot = workspace.Root, ExeRelativePath = "fixture.exe", Route = "X64DxgiFeeder", Added = ["Phoenix.addon64"]
        };
        File.WriteAllText(path, JsonSerializer.Serialize(manifest));
        return (new(workspace.Root, exe, "Fixture", "fixture.exe", CpuArch.X64, GraphicsApi.DirectX11, "fixture", 160 * 1024), manifest, path);
    }
}
