namespace Dlss5CompatApp.Tests;

public sealed class GlideOutputRouteTests
{
    [Theory]
    [InlineData(true, true, true, true)]
    [InlineData(false, true, true, false)]
    [InlineData(true, false, true, false)]
    [InlineData(true, true, false, false)]
    public void ImportedGlideTranslatorSelectsItsD3D9Output(bool imported, bool exportsFrontend, bool sameArchitecture, bool expected)
    {
        using var workspace = new TestWorkspace();
        var exe = workspace.WritePe("game.exe", CpuArch.X86, imported ? ["glide.dll"] : []);
        var wrapper = workspace.WritePe("glide.dll", sameArchitecture ? CpuArch.X86 : CpuArch.X64, "d3d9.dll");
        if (exportsFrontend) TestWorkspace.SetPhoenixExports(wrapper, "_grGlideInit@0");
        var game = GameScanner.ScanSingleExe(exe)!;
        Assert.Equal(expected, game.TranslatorKinds.Contains("GlideToD3D9"));
        if (expected)
        {
            Assert.Equal(GraphicsApi.DirectX9, game.Api);
            File.WriteAllText(workspace.FilePath("dgVoodoo.conf"), "[General]");
            Assert.Equal(InstallRoute.X86Dx9NativeFeeder, game.Route);
        }
    }
}
