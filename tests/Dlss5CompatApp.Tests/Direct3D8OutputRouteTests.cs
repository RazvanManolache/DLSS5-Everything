namespace Dlss5CompatApp.Tests;

public sealed class Direct3D8OutputRouteTests
{
    [Fact]
    public void DynamicallyLoadedD3D8DriverCanUseItsVerifiedLocalTranslator()
    {
        using var workspace=new TestWorkspace();
        var exe=workspace.WritePe("arbitrary.exe",CpuArch.X86);
        var module=workspace.FilePath("e2driver/e2_d3d8_driver_mfc.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(module)!);
        File.WriteAllBytes(module,TestWorkspace.PeBytes(CpuArch.X86,[],"Direct3DCreate8"));
        var wrapper=workspace.WritePe("d3d8.dll",CpuArch.X86,"d3d9.dll");
        TestWorkspace.SetPhoenixExports(wrapper,"Direct3DCreate8");
        Assert.Equal(GraphicsApi.DirectX9,GameScanner.ScanSingleExe(exe)!.Api);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExistingD3D8TranslatorHooksItsD3D9OutputForExecutableOrDriverImports(bool driver)
    {
        using var workspace=new TestWorkspace();
        var exe=workspace.WritePe("arbitrary.exe",CpuArch.X86,driver ? [] : ["d3d8.dll"]);
        if(driver)workspace.WritePe("gg_dx8r.dll",CpuArch.X86,"d3d8.dll");
        var wrapper=workspace.WritePe("d3d8.dll",CpuArch.X86,"d3d9.dll");
        TestWorkspace.SetPhoenixExports(wrapper,"Direct3DCreate8");
        var game=GameScanner.ScanSingleExe(exe)!;
        Assert.Equal(GraphicsApi.DirectX9,game.Api);
        Assert.Contains("D3D8ToD3D9",game.TranslatorKinds);
        Assert.Contains(GraphicsApi.DirectX8,game.AllApis);
        Assert.Equal(InstallRoute.X86Dx9NativeFeeder,game.Route);
    }

    [Theory]
    [InlineData(false,true)]
    [InlineData(true,false)]
    public void UnrelatedOrMismatchedDllCannotReplaceTheInputApi(bool correctExport,bool correctArchitecture)
    {
        using var workspace=new TestWorkspace();
        var exe=workspace.WritePe("arbitrary.exe",CpuArch.X86,"d3d8.dll");
        var wrapper=workspace.WritePe("d3d8.dll",correctArchitecture ? CpuArch.X86 : CpuArch.X64,"d3d9.dll");
        if(correctExport)TestWorkspace.SetPhoenixExports(wrapper,"Direct3DCreate8");
        Assert.DoesNotContain("D3D8ToD3D9",GameScanner.ScanSingleExe(exe)!.TranslatorKinds);
    }
}
