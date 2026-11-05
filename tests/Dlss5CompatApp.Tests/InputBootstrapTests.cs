using System.Buffers.Binary;

namespace Dlss5CompatApp.Tests;

public sealed class InputBootstrapTests
{
    [Theory]
    [InlineData("dinput8.dll", false, true)]
    [InlineData("dinput8.dll", true, false)]
    [InlineData("dinput.dll", false, true)]
    [InlineData("dinput.dll", true, false)]
    public void StartupEngineDependencyCanSupplyInputButDelayImportsCannot(string proxy, bool delayed, bool expected)
    {
        using var workspace = new TestWorkspace();
        var exe = workspace.WritePe("game/game.exe", CpuArch.X86, "d3d9.dll", "engine.dll");
        var module = workspace.WritePe("game/engine.dll", CpuArch.X86, delayed ? [] : [proxy]);
        if (delayed) TestWorkspace.SetDelayImports(module, proxy);
        Assert.Equal(expected, InputBootstrap.CanUse(GameScanner.ScanSingleExe(exe)!, proxy));
        File.WriteAllBytes(module, TestWorkspace.PeBytes(CpuArch.X64, [proxy]));
        Assert.False(InputBootstrap.CanUse(GameScanner.ScanSingleExe(exe)!, proxy));
    }

    [Fact]
    public async Task ExistingDirectDrawToDx9FrontendSurvivesTranslatedOutputInstall()
    {
        using var workspace = new TestWorkspace();
        var exe = workspace.WritePe("game/game.exe", CpuArch.X86, "ddraw.dll", "dinput.dll");
        var draw = workspace.WriteDll("game/ddraw.dll", CpuArch.X86, ["d3d9.dll"], "DirectDrawCreate");
        var original = File.ReadAllBytes(draw);
        var game = GameScanner.ScanSingleExe(exe)! with { TranslateD3D9 = true };
        Assert.Equal(GraphicsApi.DirectX9, game.Api);
        var payload = Payload(workspace);
        var plan = PackagedPhoenixPlan.For(game, payload)!;
        Assert.Equal(InputBootstrap.LegacyProxyName, plan.Proxy);
        Assert.Equal(InstallRoute.X86Dx9ViaDgVoodoo, plan.Translation);
        var engine = new InstallerEngine(_ => { });
        await engine.InstallAsync(game, payload);
        Assert.Equal(original, File.ReadAllBytes(draw));
        Assert.True(File.Exists(Path.Combine(game.Root, InputBootstrap.LegacyProxyName)));
        Assert.True(File.Exists(Path.Combine(game.Root, "d3d9.dll")));
        await engine.RestoreAsync(game.Root);
        Assert.Equal(original, File.ReadAllBytes(draw));
        Assert.False(File.Exists(Path.Combine(game.Root, InputBootstrap.LegacyProxyName)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OnlyOrdinaryInputImportSuppliesAnEarlyBootstrap(bool delayed)
    {
        using var workspace = new TestWorkspace();
        var exe = workspace.WritePe("game/game.exe", CpuArch.X86, delayed ? ["d3d11.dll"] : ["d3d11.dll", "dinput8.dll"]);
        if (delayed) TestWorkspace.SetDelayImports(exe, "dinput8.dll");
        var game = GameScanner.ScanSingleExe(exe)!;
        Assert.Equal(!delayed, InputBootstrap.CanUse(game));
        Assert.Equal(!delayed, (game with { ProxyName = InputBootstrap.ProxyName }).OverrideValidationError is null);
        Assert.Equal(GraphicsApi.DirectX11, game.Api);
    }

    [Theory]
    [InlineData((int)CpuArch.X86)]
    [InlineData((int)CpuArch.X64)]
    public async Task InstallerOwnsOneEarlyProxyPreservesTranslatorAndRestores(int bits)
    {
        using var workspace = new TestWorkspace();
        var arch = (CpuArch)bits;
        var exe = workspace.WritePe("game/game.exe", arch, "ddraw.dll", "dinput8.dll");
        var game = GameScanner.ScanSingleExe(exe)!;
        var payload = Payload(workspace);
        var plan = PackagedPhoenixPlan.For(game, payload)!;
        Assert.Equal(InputBootstrap.ProxyName, plan.Proxy);
        Assert.Equal(arch == CpuArch.X86 ? InstallRoute.X86DirectXLegacyViaDgVoodoo : InstallRoute.X64DirectXLegacyViaDgVoodoo, plan.Translation);
        var engine = new InstallerEngine(_ => { });
        await engine.InstallAsync(game, payload);
        Assert.True(File.Exists(Path.Combine(game.Root, "dinput8.dll")));
        Assert.False(File.Exists(Path.Combine(game.Root, "dxgi.dll")));
        Assert.True(File.Exists(Path.Combine(game.Root, "DDraw.dll")));
        Assert.True(File.Exists(Path.Combine(game.Root, "D3DImm.dll")));
        await engine.InstallAsync(game, payload);
        await engine.RestoreAsync(game.Root);
        Assert.False(File.Exists(Path.Combine(game.Root, "dinput8.dll")));
        Assert.False(File.Exists(Path.Combine(game.Root, "DDraw.dll")));
        Assert.True(File.Exists(exe));
    }

    [Fact]
    public void ExistingInputModIsPreservedAndExplicitOverrideIsRejected()
    {
        using var workspace = new TestWorkspace();
        var exe = workspace.WritePe("game/game.exe", CpuArch.X86, "ddraw.dll", "dinput8.dll");
        var input = workspace.WriteDll("game/dinput8.dll", CpuArch.X86, [], "DirectInput8Create");
        var before = File.ReadAllBytes(input);
        var game = GameScanner.ScanSingleExe(exe)!;
        Assert.False(InputBootstrap.CanUse(game));
        Assert.NotNull((game with { ProxyName = InputBootstrap.ProxyName }).OverrideValidationError);
        Assert.Equal("dxgi.dll", PackagedPhoenixPlan.For(game, Payload(workspace))!.Proxy);
        Assert.Equal(before, File.ReadAllBytes(input));
    }

    [Fact]
    public void NativeDx11KeepsItsDefaultButAllowsExplicitValidatedEarlyLoading()
    {
        using var workspace = new TestWorkspace();
        var exe = workspace.WritePe("game/game.exe", CpuArch.X86, "d3d11.dll", "dinput8.dll");
        var game = GameScanner.ScanSingleExe(exe)!;
        var payload = Payload(workspace);
        Assert.Equal("dxgi.dll", PackagedPhoenixPlan.For(game, payload)!.Proxy);
        Assert.Equal(InputBootstrap.ProxyName, PackagedPhoenixPlan.For(game with { ProxyName = InputBootstrap.ProxyName }, payload)!.Proxy);
        Assert.Null((game with { ProxyName = InputBootstrap.ProxyName }).OverrideValidationError);
        Assert.NotNull((game.WithApi(GraphicsApi.Vulkan) with { ProxyName = InputBootstrap.ProxyName }).OverrideValidationError);
    }

    [Fact]
    public async Task MissingInputExportFailsBeforeOpeningAnInstallTransaction()
    {
        using var workspace = new TestWorkspace();
        var exe = workspace.WritePe("game/game.exe", CpuArch.X86, "ddraw.dll", "dinput8.dll");
        var game = GameScanner.ScanSingleExe(exe)!;
        var payload = Payload(workspace);
        TestWorkspace.SetPhoenixExports(payload.ReShade32Dll!, "ReShadeRegisterAddon", "ReShadeUnregisterAddon");
        Assert.Contains("DirectInput8Create", payload.MissingFor(game));
        await Assert.ThrowsAsync<InvalidOperationException>(() => new InstallerEngine(_ => { }).InstallAsync(game, payload));
        Assert.Equal(Path.GetFullPath(exe), Assert.Single(Directory.GetFiles(game.Root)));
        Assert.Empty(Directory.GetDirectories(game.Root));
        Assert.False(InstallerEngine.HasPending(game.Root));
    }

    static PayloadInfo Payload(TestWorkspace workspace)
    {
        foreach (var arch in new[] { CpuArch.X86, CpuArch.X64 })
        {
            var suffix = arch == CpuArch.X86 ? "32" : "64";
            var addon = Dll(workspace, "payload/Phoenix.addon" + suffix, arch,
                "CnrPHOENIXSummary", "CnrPHOENIXRegister", "CnrPHOENIXFeederVersion", "CnrPHOENIXFeederEvaluate",
                "CnrPHOENIXCompatibilityStatus", "AddonInit", "AddonUninit");
            if (arch == CpuArch.X64) TestWorkspace.SetEmbeddedPhoenixRuntime(addon);
            Dll(workspace, "payload/ReShade" + suffix + ".dll", arch,
                "ReShadeRegisterAddon", "ReShadeUnregisterAddon", "DirectInput8Create",
                "DirectInputCreateA", "DirectInputCreateW", "DirectInputCreateEx");
            foreach (var name in new[] { "DDraw.dll", "D3DImm.dll" })
                Dll(workspace, "payload/dgvoodoo2/MS/" + (arch == CpuArch.X86 ? "x86/" : "x64/") + name, arch);
        }
        Dll(workspace, "payload/dgvoodoo2/MS/x86/D3D9.dll", CpuArch.X86);
        Dll(workspace, "payload/nvngx_dlssnr.dll", CpuArch.X64);
        return PayloadScanner.Scan(workspace.FilePath("payload"));
    }

    static string Dll(TestWorkspace workspace, string name, CpuArch arch, params string[] exports)
    {
        var path = workspace.WritePe(name, arch);
        var bytes = File.ReadAllBytes(path);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(0x96), 0x2022);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0x98 + (arch == CpuArch.X64 ? 108 : 92)), 16);
        File.WriteAllBytes(path, bytes);
        TestWorkspace.SetPhoenixExports(path, exports);
        return path;
    }
}
