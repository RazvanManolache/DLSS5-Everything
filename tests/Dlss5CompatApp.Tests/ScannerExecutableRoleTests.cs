namespace Dlss5CompatApp.Tests;

public sealed class ScannerExecutableRoleTests
{
    [Fact]
    public async Task InstalledRuntimeToolsAreNotRediscoveredAsGames()
    {
        using var workspace = new TestWorkspace();
        var game = workspace.WritePe("game/game.exe", CpuArch.X86, "d3d9.dll");
        workspace.WritePe("game/PhoenixRuntime/win-x86/phoenix_frame.exe", CpuArch.X86, "d3d9.dll");
        workspace.WritePe("library/phoenix-runtimes/version/PhoenixRuntime/win-x64/phoenix_frame.exe", CpuArch.X64, "d3d12.dll");
        workspace.WritePe("library/phoenix-companions/version/tool.exe", CpuArch.X86, "d3d9.dll");
        var results = await GameScanner.ScanAsync(workspace.Root);
        Assert.Single(results);
        Assert.True(LocalPaths.Same(game, results[0].ExePath));
    }

    [Fact]
    public void AngleUsesItsOutputApisInsteadOfDesktopOpenGlStrings()
    {
        using var workspace = new TestWorkspace();
        var exe = workspace.WritePe("game/arbitrary.exe", CpuArch.X64, "libEGL.dll", "libGLESv2.dll");
        var egl = workspace.WritePe("game/libEGL.dll", CpuArch.X64);
        TestWorkspace.SetPhoenixExports(egl, "eglGetProcAddress", "eglInitialize");
        File.WriteAllBytes(workspace.FilePath("game/libGLESv2.dll"), TestWorkspace.PeBytes(CpuArch.X64, [], "ANGLE"));
        var game = GameScanner.ScanSingleExe(exe)!;
        Assert.Equal(GraphicsApi.Dxgi, game.Api);
        Assert.Contains(GraphicsApi.Vulkan, game.AllApis);
        Assert.Contains(GraphicsApi.DirectX12, game.AllApis);
        Assert.Contains("ANGLE", game.TranslatorKinds);
        Assert.True(game.RuntimeSelectionRequired);
        Assert.Contains("runtime backend", game.DisplayApi);
        Assert.True(GameScanner.IsAngleLayout(exe));
        File.Delete(egl);
        Assert.False(GameScanner.IsAngleLayout(exe));
    }

    [Theory]
    [InlineData(false, "d3d10.dll")]
    [InlineData(true, "d3d10.dll")]
    [InlineData(false, "d3d10_1.dll")]
    [InlineData(true, "d3d10_1.dll")]
    public void DirectD3D10ImportHasPreciseClassificationAndKeepsDxgiHook(bool x64, string library)
    {
        using var workspace = new TestWorkspace();
        var architecture = x64 ? CpuArch.X64 : CpuArch.X86;
        var game = GameScanner.ScanSingleExe(workspace.WritePe("arbitrary.exe", architecture, library))!;
        Assert.Equal(library == "d3d10_1.dll" ? GraphicsApi.DirectX10_1 : GraphicsApi.DirectX10, game.Api);
        Assert.Equal(architecture == CpuArch.X86 ? InstallRoute.X86DxgiFeeder : InstallRoute.X64DxgiFeeder, game.Route);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DynamicallyLoadedD3D10UsesDxgiRoute(bool x64)
    {
        using var workspace = new TestWorkspace();
        var architecture = x64 ? CpuArch.X64 : CpuArch.X86;
        var path = workspace.FilePath("arbitrary.exe");
        File.WriteAllBytes(path, TestWorkspace.PeBytes(architecture, [], "D3D10CreateDeviceAndSwapChain"));
        Assert.Equal(GraphicsApi.DirectX10, GameScanner.ScanSingleExe(path)!.Api);
    }

    [Fact]
    public void SourceVulkanCapabilityIncludesItsLaunchSwitchWithoutReplacingTheDefault()
    {
        using var workspace = new TestWorkspace();
        var executable = workspace.WritePe("game/arbitrary.exe", CpuArch.X86);
        workspace.WritePe("game/bin/engine.dll", CpuArch.X86);
        workspace.WritePe("game/bin/shaderapidx9.dll", CpuArch.X86, "d3d9.dll");
        workspace.WritePe("game/bin/shaderapivk.dll", CpuArch.X86);
        File.WriteAllBytes(workspace.FilePath("game/bin/materialsystem.dll"), TestWorkspace.PeBytes(CpuArch.X86, [], "-vulkan shaderapivk"));
        var translation = workspace.WritePe("game/bin/dxvk_d3d9.dll", CpuArch.X86);
        TestWorkspace.SetPhoenixExports(translation, "Direct3DCreate9");
        var game = GameScanner.ScanSingleExe(executable)!;
        Assert.Equal(GraphicsApi.DirectX9, game.Api);
        Assert.Contains(GraphicsApi.Vulkan, game.AllApis);
        Assert.Equal("-vulkan", game.WithApi(GraphicsApi.Vulkan).SuggestedArguments);
        File.Delete(translation);
        Assert.DoesNotContain(GraphicsApi.Vulkan, GameScanner.ScanSingleExe(executable)!.AllApis);
    }

    [Fact]
    public void ExplicitDirectDrawImportRemainsAvailableBesideGlide()
    {
        using var workspace = new TestWorkspace();
        var game = GameScanner.ScanSingleExe(workspace.WritePe("arbitrary.exe", CpuArch.X86, "glide3x.dll", "ddraw.dll"))!;
        Assert.Contains(GraphicsApi.Glide31, game.AllApis);
        Assert.Contains(GraphicsApi.DirectX7OrOlder, game.AllApis);
    }

    [Fact]
    public async Task ARealPeEntryPointInABinFileIsDetectedButDataAndLibrariesAreNot()
    {
        using var workspace = new TestWorkspace();
        var program = workspace.WritePe("rendered.bin", CpuArch.X86, "d3d9.dll");
        var bytes = File.ReadAllBytes(program);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(0x96), 0x22);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(0x98 + 16), 0x1000);
        File.WriteAllBytes(program, bytes);
        var library = workspace.FilePath("library.bin");
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(0x96), 0x2022);
        File.WriteAllBytes(library, bytes);
        File.WriteAllText(workspace.FilePath("data.bin"), "ordinary data");
        var games = await GameScanner.ScanAsync(workspace.Root);
        Assert.True(LocalPaths.Same(Assert.Single(games).ExePath, program));
        Assert.NotNull(GameScanner.ScanSingleExe(program));
        Assert.Null(GameScanner.ScanSingleExe(library));
    }

    [Fact]
    public void OptionalXrStringsDoNotDefaultAnOrdinaryEngineToAHeadsetRoute()
    {
        using var workspace = new TestWorkspace();
        var path = workspace.FilePath("arbitrary.exe");
        File.WriteAllBytes(path, TestWorkspace.PeBytes(CpuArch.X64, [],
            "xrCreateInstance openxr_loader.dll D3D12CreateDevice D3D11CreateDevice vkCreateInstance"));
        var game = GameScanner.ScanSingleExe(path)!;
        Assert.Equal(GraphicsApi.DirectX12, game.Api);
        Assert.Contains(GraphicsApi.OpenXr, game.AllApis);
    }

    [Fact]
    public void DirectXrLoaderImportStillIdentifiesAnXrApplication()
    {
        using var workspace = new TestWorkspace();
        var path = workspace.FilePath("arbitrary.exe");
        File.WriteAllBytes(path, TestWorkspace.PeBytes(CpuArch.X64, ["openxr_loader.dll"], "D3D12CreateDevice"));
        Assert.Equal(GraphicsApi.OpenXr, GameScanner.ScanSingleExe(path)!.Api);
    }

    [Fact]
    public void RendererModulesOutrankAnAudioLibrarysUnusedGraphicsStrings()
    {
        using var workspace = new TestWorkspace();
        var executable = workspace.WritePe("game/arbitrary.exe", CpuArch.X64, "sdl264.dll");
        File.WriteAllBytes(workspace.FilePath("game/sdl264.dll"),
            TestWorkspace.PeBytes(CpuArch.X64, [], "CreateDXGIFactory D3D11CreateDevice"));
        workspace.WritePe("game/renderer_opengl2_x86_64.dll", CpuArch.X64, "opengl32.dll");
        Assert.Equal(GraphicsApi.OpenGl, GameScanner.ScanSingleExe(executable)!.Api);
    }

    [Fact]
    public void EngineModuleCanIdentifyARendererBehindASmallLaunchStub()
    {
        using var workspace = new TestWorkspace();
        var executable = workspace.WritePe("game/arbitrary.exe", CpuArch.X86, "tier0.dll");
        workspace.WritePe("game/bin/shaderapidx9.dll", CpuArch.X86, "d3d9.dll");
        Assert.Equal(GraphicsApi.DirectX9, GameScanner.ScanSingleExe(executable)!.Api);
    }

    [Fact]
    public void ImportedOpenGlOutranksUnusedDirect3DStrings()
    {
        using var workspace = new TestWorkspace();
        var path = workspace.FilePath("arbitrary.exe");
        File.WriteAllBytes(path, TestWorkspace.PeBytes(CpuArch.X86, ["opengl32.dll"],
            "D3D11CreateDevice D3D12CreateDevice CreateDXGIFactory"));
        Assert.Equal(GraphicsApi.OpenGl, GameScanner.ScanSingleExe(path)!.Api);
    }

    [Fact]
    public async Task EmbeddedBrowserAndMapEditorDoNotInheritAGamesRendererRole()
    {
        using var workspace = new TestWorkspace();
        var game = workspace.WritePe("game/rendered.exe", CpuArch.X64, "d3d11.dll");
        workspace.WritePe("game/SceneBrowser.exe", CpuArch.X64, "libcef.dll", "d3d11.dll");
        workspace.WritePe("game/maped2/arbitrary.exe", CpuArch.X86, "opengl32.dll");
        var found = await GameScanner.ScanAsync(workspace.Root, limits: new());
        Assert.True(LocalPaths.Same(Assert.Single(found).ExePath, game));
        Assert.NotNull(GameScanner.ScanSingleExe(game));
    }

    [Fact]
    public void BrowserWordAloneCannotExcludeAnActualRenderer()
    {
        using var workspace = new TestWorkspace();
        var game = workspace.WritePe("BrowserAdventure.exe", CpuArch.X64, "d3d11.dll");
        Assert.NotNull(GameScanner.ScanSingleExe(game));
    }
}
