namespace Dlss5CompatApp.Tests;

public sealed class MixedLegacyRendererTests
{
    [Theory]
    [InlineData("glide.dll", (int)GraphicsApi.Glide211)]
    [InlineData("glide2x.dll", (int)GraphicsApi.Glide245)]
    [InlineData("glide3x.dll", (int)GraphicsApi.Glide31)]
    public void ImportedDirectDrawDoesNotHideANamedDynamicGlideRenderer(string library, int api)
    {
        using var workspace = new TestWorkspace();
        var exe = workspace.WritePe("game/game.exe", CpuArch.X86, "ddraw.dll");
        File.AppendAllText(exe, "\0" + library.ToUpperInvariant() + "\0");
        var game = GameScanner.ScanSingleExe(exe)!;
        Assert.Contains(GraphicsApi.DirectX7OrOlder, game.DetectedInputApis);
        Assert.Contains((GraphicsApi)api, game.DetectedInputApis);
        Assert.Contains((GraphicsApi)api, game.PossibleApis!);
        Assert.Equal(GraphicsApi.DirectX7OrOlder, game.Api);
    }

    [Fact]
    public void SiblingCapabilityDoesNotAddAnUnrequestedLegacyCompanion()
    {
        using var workspace = new TestWorkspace();
        var exe = workspace.WritePe("game/game.exe", CpuArch.X86, "ddraw.dll", "optional.dll");
        var sibling = workspace.WriteDll("game/optional.dll", CpuArch.X86, []);
        File.AppendAllText(sibling, "\0glide3x.dll\0");
        Assert.DoesNotContain(GraphicsApi.Glide31, GameScanner.ScanSingleExe(exe)!.DetectedInputApis);
    }

    [Theory]
    [InlineData((int)InstallRoute.X86DirectXLegacyViaDgVoodoo, (int)InstallRoute.X86Glide31ViaDgVoodoo)]
    [InlineData((int)InstallRoute.X86Glide31ViaDgVoodoo, (int)InstallRoute.X86DirectXLegacyViaDgVoodoo)]
    [InlineData((int)InstallRoute.X86Glide31NapalmViaDgVoodoo, (int)InstallRoute.X86DirectXLegacyViaDgVoodoo)]
    public void MenuAndGameplayTranslationsRemainACompleteChain(int primary, int companion)
    {
        var game = new GameCandidate("C:/fixture", "C:/fixture/game.exe", "Fixture", "game.exe",
            CpuArch.X86, GraphicsApi.Dxgi, "fixture", 160000)
        { DetectedInputApis = [GraphicsApi.DirectX7OrOlder, GraphicsApi.Glide31] };
        Assert.Equal([(InstallRoute)companion], PackagedPhoenixPlan.RequiredCompanions(game, (InstallRoute)primary));
        Assert.Empty(PackagedPhoenixPlan.RequiredCompanions(game, InstallRoute.Unsupported));
        Assert.Empty(PackagedPhoenixPlan.RequiredCompanions(game, InstallRoute.X86Dx9NativeFeeder));
        Assert.Empty(PackagedPhoenixPlan.RequiredCompanions(game with { Arch = CpuArch.X64 }, (InstallRoute)primary));
    }

    [Fact]
    public async Task ReinstallKeepsBothInterfacesAndRestoreRemovesOnlyItsOwnedFiles()
    {
        using var workspace = new TestWorkspace();
        var exe = workspace.WritePe("game/game.exe", CpuArch.X86, "ddraw.dll");
        File.AppendAllText(exe, "\0glide3x.dll\0");
        var installedGlide = workspace.FilePath("game/Glide3x.dll");
        var userNotes = workspace.FilePath("game/notes.txt");
        File.WriteAllText(userNotes, "User data must survive installation and restoration.");
        var addon64 = workspace.WriteDll("payload/Phoenix.addon64", CpuArch.X64, [],
            "CnrPHOENIXSummary", "CnrPHOENIXRegister", "CnrPHOENIXFeederVersion", "CnrPHOENIXFeederEvaluate",
            "CnrPHOENIXCompatibilityStatus", "AddonInit", "AddonUninit");
        TestWorkspace.SetEmbeddedPhoenixRuntime(addon64);
        workspace.WriteDll("payload/Phoenix.addon32", CpuArch.X86, [], "CnrPHOENIXCompatibilityStatus", "AddonInit", "AddonUninit");
        workspace.WriteDll("payload/ReShade32.dll", CpuArch.X86, [], "ReShadeRegisterAddon", "ReShadeUnregisterAddon");
        workspace.WriteDll("payload/nvngx_dlssnr.dll", CpuArch.X64, []);
        workspace.WriteDll("payload/dgvoodoo2/MS/x86/DDraw.dll", CpuArch.X86, [], "DirectDrawCreate");
        workspace.WriteDll("payload/dgvoodoo2/MS/x86/D3DImm.dll", CpuArch.X86, []);
        var glide = workspace.WriteDll("payload/dgvoodoo2/3Dfx/x86/Glide3x.dll", CpuArch.X86, [], "grGlideInit", "grBufferSwap");
        workspace.WriteDll("payload/legacy-com/PhoenixLegacyDDraw32.dll", CpuArch.X86, [], "DirectDrawCreate", "DllGetClassObject");
        var payload = PayloadScanner.Scan(workspace.FilePath("payload"));
        var game = GameScanner.ScanSingleExe(exe)!.WithApi(GraphicsApi.Dxgi) with { TranslatorKinds = ["dgVoodoo"] };
        var plan = PackagedPhoenixPlan.For(game, payload)!;
        Assert.Equal(InstallRoute.X86DirectXLegacyViaDgVoodoo, plan.Translation);
        Assert.Contains(InstallRoute.X86Glide31ViaDgVoodoo, plan.CompanionTranslations);
        Assert.Equal(3, plan.TranslationFiles(payload).Count);
        var installer = new InstallerEngine(_ => { });
        for (var install = 0; install != 2; ++install)
        {
            await installer.InstallAsync(game, payload);
            Assert.True(File.Exists(Path.Combine(game.Root, "DDraw.dll")));
            Assert.True(File.Exists(Path.Combine(game.Root, "D3DImm.dll")));
            Assert.Equal(File.ReadAllBytes(glide), File.ReadAllBytes(installedGlide));
        }
        await installer.RestoreAsync(game.Root);
        Assert.False(File.Exists(installedGlide));
        Assert.Equal("User data must survive installation and restoration.", File.ReadAllText(userNotes));
        Assert.False(File.Exists(Path.Combine(game.Root, "DDraw.dll")));
        Assert.False(File.Exists(Path.Combine(game.Root, "dxgi.dll")));
    }
}
