namespace Dlss5CompatApp.Tests;

public sealed class LegacyDriverModuleTests
{
    [Theory]
    [InlineData("e2driver/e2_d3d8_driver_mfc.dll")]
    [InlineData("gg_dx8r.dll")]
    public void RendererDriverCarriesTheApiWhenTheLauncherDoesNot(string driver)
    {
        using var workspace=new TestWorkspace();
        var exe=workspace.WritePe("arbitrary.exe",CpuArch.X86);
        workspace.WritePe(driver,CpuArch.X86,"d3d8.dll");
        Assert.Equal(GraphicsApi.DirectX8,GameScanner.ScanSingleExe(exe)!.Api);
    }

    [Theory]
    [InlineData(true, false, true)]
    [InlineData(false, false, false)]
    [InlineData(true, true, false)]
    public void DuniaRequiresImportAndMatchingArchitecture(bool imported, bool wrongArchitecture, bool expected)
    {
        using var workspace = new TestWorkspace();
        var exe = workspace.WritePe("arbitrary.exe", CpuArch.X86, imported ? ["Dunia.dll"] : []);
        workspace.WritePe("Dunia.dll", wrongArchitecture ? CpuArch.X64 : CpuArch.X86, "d3d9.dll");
        Assert.Equal(expected ? GraphicsApi.DirectX9 : GraphicsApi.Unknown, GameScanner.ScanSingleExe(exe)!.Api);
    }

    [Fact]
    public void DriverOfAnotherArchitectureIsNotInputEvidence()
    {
        using var workspace=new TestWorkspace();
        var exe=workspace.WritePe("arbitrary.exe",CpuArch.X86);
        workspace.WritePe("gg_dx8r.dll",CpuArch.X64,"d3d8.dll");
        Assert.Equal(GraphicsApi.Unknown,GameScanner.ScanSingleExe(exe)!.Api);
    }
}
