using System.Buffers.Binary;

namespace Dlss5CompatApp.Tests;

public sealed class CliRendererSelectionTests
{
    [Fact]
    public void RemixInstallationTargetsTheVerifiedBackendInsteadOfTheFrontend()
    {
        using var workspace=new TestWorkspace();
        var frontend=workspace.WritePe("game.exe",CpuArch.X86,"d3d9.dll");
        Directory.CreateDirectory(workspace.FilePath("bin/.trex"));
        var bridge=workspace.WritePe("bin/d3d9.dll",CpuArch.X86,"kernel32.dll");
        TestWorkspace.SetPhoenixExports(bridge,"Direct3DCreate9","remixapi_InitializeLibrary","remixapi_RegisterCallbacks");
        var renderer=workspace.WritePe("bin/.trex/NvRemixBridge.exe",CpuArch.X64,"vulkan-1.dll");
        var bytes=File.ReadAllBytes(renderer);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(0x96),0x22);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(0x98+16),0x1000);
        File.WriteAllBytes(renderer,bytes);
        var options=CliInstaller.Parse(["--install",frontend,"--renderer",renderer,"--offline"]);
        var game=CliInstaller.DetectInstallTarget(frontend,options);
        Assert.Equal(Path.GetFullPath(renderer),game.ExePath);
        Assert.Equal(CpuArch.X64,game.Arch);
        Assert.Equal(GraphicsApi.Vulkan,game.Api);
        options.ApiOverride=GraphicsApi.DirectX9;
        Assert.Throws<ArgumentException>(()=>CliInstaller.DetectInstallTarget(frontend,options));
        options.ApiOverride=null;
        options.RendererPath=workspace.WritePe("unrelated.exe",CpuArch.X64,"vulkan-1.dll");
        Assert.Throws<InvalidOperationException>(()=>CliInstaller.DetectInstallTarget(frontend,options));
    }
    [Fact]
    public void ProxySelectionUsesExistingRestrictedNames()
    {
        Assert.Equal("d3d11.dll",CliInstaller.Parse(["--install","game.exe","--api","DirectX11","--proxy","d3d11.dll"]).ProxyOverride);
        Assert.Throws<ArgumentException>(()=>CliInstaller.Parse(["--install","game.exe","--proxy","../other.dll"]));
    }
    [Fact]
    public void ExplicitRendererSelectionIsConfigurationNotVerification()
    {
        using var workspace=new TestWorkspace();
        var exe=workspace.WritePe("arbitrary.exe",CpuArch.X86,"d3d10.dll");
        var bytes=File.ReadAllBytes(exe);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(0x96),0x22);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(0x98+16),0x1000);
        File.WriteAllBytes(exe,bytes);
        var options=CliInstaller.Parse(["--install",exe,"--api","DirectX9","--offline"]);
        var game=GameScanner.ScanSingleExe(exe,options.ApiOverride)!;
        Assert.Equal(GraphicsApi.DirectX9,game.Api);
        Assert.False(game.HasVerifiedRenderer);
        Assert.False(options.AcknowledgeAntiCheatRisk);
    }

    [Fact]
    public void ExplicitApiCanAdmitASmallPackedExecutableWithoutAdmittingRenamedLibraries()
    {
        using var workspace=new TestWorkspace();
        var exe=workspace.FilePath("arbitrary.exe");
        var bytes=TestWorkspace.PeBytes(CpuArch.X86,[])[..(96*1024)];
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(0x96),0x22);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(0x98+16),0x1000);
        File.WriteAllBytes(exe,bytes);
        Assert.Null(GameScanner.ScanSingleExe(exe));
        Assert.Equal(GraphicsApi.DirectX9,GameScanner.ScanSingleExe(exe,GraphicsApi.DirectX9)!.Api);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(0x96),0x2022);
        File.WriteAllBytes(exe,bytes);
        Assert.Null(GameScanner.ScanSingleExe(exe,GraphicsApi.DirectX9));
    }

    [Theory]
    [InlineData("Unknown")]
    [InlineData("DirectX999")]
    public void InvalidApiDoesNotReachInstallation(string api) => Assert.Throws<ArgumentException>(()=>CliInstaller.Parse(["--install","game.exe","--api",api]));
}
