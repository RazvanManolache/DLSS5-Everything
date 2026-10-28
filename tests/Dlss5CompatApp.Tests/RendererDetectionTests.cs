namespace Dlss5CompatApp.Tests;

public sealed class RendererDetectionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ConcreteDirectDrawImportOutranksOptionalOpenGlCapability(bool sdlModule)
    {
        using var workspace = new TestWorkspace();
        var exe = workspace.FilePath("arbitrary.exe");
        if (sdlModule)
        {
            File.WriteAllBytes(exe, TestWorkspace.PeBytes(CpuArch.X86, ["SDL.dll"]));
            File.WriteAllBytes(workspace.FilePath("SDL.dll"), TestWorkspace.PeBytes(CpuArch.X86,
                ["ddraw.dll"], "wglCreateContext glBindTexture"));
        }
        else File.WriteAllBytes(exe, TestWorkspace.PeBytes(CpuArch.X86, ["ddraw.dll"],
            "wglCreateContext glBindTexture"));
        var game = GameScanner.ScanSingleExe(exe)!;
        Assert.Equal(GraphicsApi.DirectX7OrOlder, game.Api);
        Assert.Equal(InstallRoute.X86DirectXLegacyViaDgVoodoo, game.Route);
        Assert.Contains(GraphicsApi.OpenGl, game.AllApis);
    }

    [Theory]
    [InlineData("D3D10CreateDevice1", (int)GraphicsApi.DirectX10_1)]
    [InlineData("D3D10CreateDeviceAndSwapChain1", (int)GraphicsApi.DirectX10_1)]
    [InlineData("D3D10CreateDeviceAndSwapChain", (int)GraphicsApi.DirectX10)]
    public void DynamicDx10EntryPointsAreDistinguished(string marker, int expected)
    {
        using var workspace = new TestWorkspace();
        var exe = workspace.FilePath("arbitrary.exe");
        File.WriteAllBytes(exe, TestWorkspace.PeBytes(CpuArch.X64, [], marker));
        var game = GameScanner.ScanSingleExe(exe)!;
        Assert.Equal((GraphicsApi)expected, game.Api);
        Assert.Equal(InstallRoute.X64DxgiFeeder, game.Route);
        Assert.Equal(new[] { (GraphicsApi)expected }, game.DetectedInputApis);
    }

    [Theory]
    [InlineData((int)CpuArch.X86)]
    [InlineData((int)CpuArch.X64)]
    public void ConcreteDx10ImportOutranksUnrelatedNewerCapabilityStrings(int architecture)
    {
        using var workspace = new TestWorkspace();
        var exe = workspace.FilePath("arbitrary.exe");
        File.WriteAllBytes(exe, TestWorkspace.PeBytes((CpuArch)architecture, ["d3d10_1.dll", "dxgi.dll"],
            "D3D11CreateDevice D3D12CreateDevice"));
        var game = GameScanner.ScanSingleExe(exe)!;
        Assert.Equal(GraphicsApi.DirectX10_1, game.Api);
        Assert.DoesNotContain(GraphicsApi.Dxgi, game.DetectedInputApis);
    }

    [Theory]
    [InlineData("D3D_DRIVER_TYPE_WARP")]
    [InlineData("EnumWarpAdapter")]
    [InlineData("software_renderer WARP")]
    public void SoftwareCapabilityWarnsWithoutPretendingItIsTheActiveAdapter(string markers)
    {
        using var workspace = new TestWorkspace();
        var exe = workspace.FilePath("arbitrary.exe");
        File.WriteAllBytes(exe, TestWorkspace.PeBytes(CpuArch.X64, ["d3d11.dll"], markers));
        var game = GameScanner.ScanSingleExe(exe)!;
        Assert.Equal(GraphicsApi.DirectX11, game.Api);
        Assert.True(game.RuntimeSelectionRequired);
        Assert.Contains(game.CompatibilityWarnings, warning => warning.Contains("not known from static scanning"));
        var preflight = InstallPreflight.Check(workspace.Root, exe, [], processProvider: () => []);
        Assert.True(preflight.CanInstall);
        Assert.Contains(preflight.Findings, finding => finding.Code == "software-renderer-possible" &&
            finding.Severity == PreflightSeverity.Warning);
    }

    [Fact]
    public void AGameTitleContainingWarpIsNotSoftwareRendererEvidence()
    {
        using var workspace = new TestWorkspace();
        var exe = workspace.FilePath("arbitrary.exe");
        File.WriteAllBytes(exe, TestWorkspace.PeBytes(CpuArch.X64, ["d3d11.dll"], "SPACE WARP"));
        Assert.Empty(GameScanner.ScanSingleExe(exe)!.CompatibilityWarnings);
    }

    [Fact]
    public void PreviousSoftwareSkipIsWarningOnlyAndLargeLogReadsAreBounded()
    {
        using var workspace = new TestWorkspace();
        var exe = workspace.WritePe("game/arbitrary.exe", CpuArch.X64, "d3d11.dll");
        File.WriteAllText(workspace.FilePath("game/ReShade.log"), new string('x', 300000) +
            "\nSkipping device because the driver type is 'D3D_DRIVER_TYPE_WARP' or 'D3D_DRIVER_TYPE_REFERENCE'.\n");
        var game = GameScanner.ScanSingleExe(exe)!;
        Assert.Contains(game.CompatibilityWarnings, warning => warning.Contains("previous-run evidence"));
        var preflight = InstallPreflight.Check(workspace.Root, exe, [], processProvider: () => []);
        Assert.True(preflight.CanInstall);
        Assert.Contains(preflight.Findings, finding => finding.Code == "software-renderer-observed");
    }

    [Fact]
    public void OrdinaryReferenceCountWarningsAreNotMistakenForSoftwareDevices()
    {
        using var workspace = new TestWorkspace();
        var exe = workspace.WritePe("game/arbitrary.exe", CpuArch.X64, "d3d11.dll");
        File.WriteAllText(workspace.FilePath("game/ReShade.log"), "WARN Reference count for ID3D11Device is inconsistent (3).");
        Assert.Empty(RendererDiagnostics.Inspect(exe));
    }

    [Theory]
    [InlineData("ddraw.dll", (int)GraphicsApi.DirectX7OrOlder, (int)InstallRoute.X86DirectXLegacyViaDgVoodoo)]
    [InlineData("d3d8.dll", (int)GraphicsApi.DirectX8, (int)InstallRoute.X86Dx8ViaD3D8To9)]
    [InlineData("glide2x.dll", (int)GraphicsApi.Glide245, (int)InstallRoute.X86Glide245ViaDgVoodoo)]
    public void CleanLegacyExecutableStillRequestsTheFullTranslationChain(string import, int api, int route)
    {
        using var workspace = new TestWorkspace();
        var game = GameScanner.ScanSingleExe(workspace.WritePe("arbitrary.exe", CpuArch.X86, import))!;
        Assert.Equal((GraphicsApi)api, game.Api);
        Assert.Equal((InstallRoute)route, game.Route);
        Assert.Contains((GraphicsApi)api, game.DetectedInputApis);
        Assert.Empty(game.TranslatorKinds);
    }
}
