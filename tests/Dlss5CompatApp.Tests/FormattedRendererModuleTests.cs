namespace Dlss5CompatApp.Tests;

public sealed class FormattedRendererModuleTests
{
    [Theory]
    [InlineData(true, true, true)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    public void FormattedRendererReferenceFindsMatchingOpenGlModule(bool reference, bool sameArchitecture, bool expected)
    {
        using var workspace = new TestWorkspace();
        var exe=workspace.FilePath("arbitrary.exe");
        File.WriteAllBytes(exe, TestWorkspace.PeBytes(CpuArch.X86, [], reference ? "ref_%s.dll" : ""));
        File.WriteAllBytes(workspace.FilePath("ref_gl.dll"), TestWorkspace.PeBytes(sameArchitecture ? CpuArch.X86 : CpuArch.X64, [], "wglCreateContext glBindTexture"));
        var game=GameScanner.ScanSingleExe(exe)!;
        Assert.Equal(expected, game.Api == GraphicsApi.OpenGl);
        if (expected) Assert.Contains("module:ref_gl.dll", game.Detection);
    }
}
