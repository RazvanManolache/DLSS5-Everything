namespace Dlss5CompatApp.Tests;

public sealed class DirectDrawOutputRouteTests
{
    [Theory]
    [InlineData(true, true, true, true)]
    [InlineData(false, true, true, false)]
    [InlineData(true, false, true, false)]
    [InlineData(true, true, false, false)]
    public void ExistingDirectDrawTranslatorHooksItsD3D9OutputWithoutReplacingIt(
        bool imported, bool exportsFrontEnd, bool matchingArchitecture, bool expected)
    {
        using var workspace = new TestWorkspace();
        var exe = workspace.WritePe("game.exe", CpuArch.X86, imported ? ["ddraw.dll"] : []);
        var wrapper = workspace.WritePe("ddraw.dll", matchingArchitecture ? CpuArch.X86 : CpuArch.X64, "d3d9.dll");
        if (exportsFrontEnd) TestWorkspace.SetPhoenixExports(wrapper, "DirectDrawCreate");
        var game = GameScanner.ScanSingleExe(exe)!;
        if (!expected) { Assert.DoesNotContain("DDrawToD3D9", game.TranslatorKinds); return; }
        Assert.Equal(GraphicsApi.DirectX9, game.Api);
        Assert.Contains("DDrawToD3D9", game.TranslatorKinds);
        Assert.Contains(GraphicsApi.DirectX7OrOlder, game.AllApis);
        Assert.Equal(InstallRoute.X86Dx9NativeFeeder, game.Route);
    }
}
