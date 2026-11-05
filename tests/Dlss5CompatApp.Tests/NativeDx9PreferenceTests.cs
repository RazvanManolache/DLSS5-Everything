namespace Dlss5CompatApp.Tests;

public sealed class NativeDx9PreferenceTests
{
    [Theory]
    [InlineData((int)CpuArch.X86)]
    [InlineData((int)CpuArch.X64)]
    public void ExistingDgVoodooKeepsItsOutputEvenWhenNativeD3D9IsAvailable(int bits)
    {
        var selected = GameScanner.ChoosePreferredApi([
            new(GraphicsApi.DirectX9, "imports"),
            new(GraphicsApi.DirectX11, "wrapper:dgVoodoo/d3d9.dll")], (CpuArch)bits);
        Assert.Equal((GraphicsApi.DirectX11, "wrapper:dgVoodoo/d3d9.dll"), selected);
    }

    [Theory]
    [InlineData("wrapper:dgVoodoo/d3d8.dll", (int)GraphicsApi.DirectX8)]
    [InlineData("wrapper:dgVoodoo/ddraw.dll", (int)GraphicsApi.DirectX7OrOlder)]
    [InlineData("wrapper:dgVoodoo/glide2x.dll", (int)GraphicsApi.Glide245)]
    [InlineData("wrapper:DXVK/d3d9.dll", (int)GraphicsApi.DirectX9)]
    public void RequiredOrDifferentTranslatorsKeepTheirOutput(string wrapper, int input)
    {
        var selected = GameScanner.ChoosePreferredApi([
            new((GraphicsApi)input, "imports"),
            new(GraphicsApi.DirectX11, wrapper)], CpuArch.X86);
        Assert.Equal((GraphicsApi.DirectX11, wrapper), selected);
    }

    [Fact]
    public void MixedNativeRenderersDoNotInferThatD3D9IsTheActiveRenderer()
    {
        var selected = GameScanner.ChoosePreferredApi([
            new(GraphicsApi.DirectX9, "imports"), new(GraphicsApi.DirectX11, "imports"),
            new(GraphicsApi.DirectX11, "wrapper:dgVoodoo/d3d9.dll")], CpuArch.X86);
        Assert.Equal(GraphicsApi.DirectX11, selected.Api);
    }

    [Fact]
    public void RecognizedTranslatorIsPreservedButAConfigurationAloneDoesNotRequireOne()
    {
        var selected = GameScanner.ChoosePreferredApi([
            new(GraphicsApi.DirectX9, "imports"), new(GraphicsApi.DirectX7OrOlder, "imports"),
            new(GraphicsApi.DirectX11, "wrapper:dgVoodoo/d3d9.dll")], CpuArch.X86);
        Assert.Equal((GraphicsApi.DirectX11, "wrapper:dgVoodoo/d3d9.dll"), selected);
        using var workspace = new TestWorkspace();
        var exe = workspace.WritePe("arbitrary.exe", CpuArch.X86, "d3d9.dll", "ddraw.dll");
        File.WriteAllText(workspace.FilePath("dgVoodoo.conf"), "[General]\n");
        Assert.Equal(InstallRoute.X86Dx9NativeFeeder, GameScanner.ScanSingleExe(exe)!.Route);
    }

    [Fact]
    public void StaleConfigurationDoesNotReenableTranslationButExplicitOutputStillCan()
    {
        using var workspace = new TestWorkspace();
        var exe = workspace.WritePe("arbitrary.exe", CpuArch.X86, "d3d9.dll");
        File.WriteAllText(workspace.FilePath("dgVoodoo.conf"), "[General]\nOutputAPI=d3d11\n");
        var game = GameScanner.ScanSingleExe(exe)! with
        {
            TranslatorKinds = ["dgVoodoo"],
            DetectedInputApis = [GraphicsApi.DirectX9]
        };
        Assert.Equal(InstallRoute.X86Dx9NativeFeeder, game.Route);
        var addon = workspace.WriteDll("payload/Phoenix.addon32", CpuArch.X86, [],
            "CnrPHOENIXCompatibilityStatus", "AddonInit", "AddonUninit");
        var payload = PayloadScanner.Scan(Path.GetDirectoryName(addon)!);
        Assert.Equal("d3d9.dll", PackagedPhoenixPlan.For(game, payload)!.Proxy);
        Assert.Equal(InstallRoute.Unsupported, PackagedPhoenixPlan.For(game, payload)!.Translation);
        Assert.Equal(InstallRoute.X86Dx9ViaDgVoodoo,
            PackagedPhoenixPlan.For(game.WithApi(GraphicsApi.DirectX11), payload)!.Translation);
    }
}
