namespace Dlss5CompatApp.Tests;

public sealed class ConfiguredRendererModuleTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SmallBootstrapUsesActiveConfiguredRendererInsteadOfOptionalBackend(bool directDraw)
    {
        using var workspace = new TestWorkspace();
        var exe=workspace.FilePath("arbitrary.exe");
        File.WriteAllBytes(exe,TestWorkspace.PeBytes(CpuArch.X86,[])[..(48*1024)]);
        workspace.WritePe("RenderD3D.dll",CpuArch.X86,"ddraw.dll");
        File.WriteAllBytes(workspace.FilePath("RenderOpenGL.dll"),TestWorkspace.PeBytes(CpuArch.X86,[],"wglCreateContext glBindTexture"));
        File.WriteAllText(workspace.FilePath("arbitrary.ini"), directDraw
            ? "DrawDll RenderD3D.dll\r\n//DrawDll RenderOpenGL.dll\r\n"
            : "//DrawDll RenderD3D.dll\r\nDrawDll RenderOpenGL.dll\r\n");
        var game=GameScanner.ScanSingleExe(exe);
        Assert.NotNull(game);
        Assert.Equal(directDraw ? GraphicsApi.DirectX7OrOlder : GraphicsApi.OpenGl,game.Api);
    }

    [Theory]
    [InlineData("../outside.dll",false)]
    [InlineData("RenderOpenGL.dll",true)]
    public void TraversalAndMismatchedConfiguredModulesAreNotRendererEvidence(string selected,bool wrongArchitecture)
    {
        using var workspace=new TestWorkspace();
        var exe=workspace.WritePe("arbitrary.exe",CpuArch.X86);
        File.WriteAllBytes(workspace.FilePath("RenderOpenGL.dll"),TestWorkspace.PeBytes(wrongArchitecture ? CpuArch.X64 : CpuArch.X86,[],"wglCreateContext"));
        File.WriteAllText(workspace.FilePath("arbitrary.ini"),"DrawDll "+selected);
        Assert.Equal(GraphicsApi.Unknown,GameScanner.ScanSingleExe(exe)!.Api);
    }
}
