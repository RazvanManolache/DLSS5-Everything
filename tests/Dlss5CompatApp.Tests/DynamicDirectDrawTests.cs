using System.Text;

namespace Dlss5CompatApp.Tests;

public sealed class DynamicDirectDrawTests
{
    static GameCandidate Fixture(TestWorkspace workspace, string renderer,
        CpuArch wrapperArch=CpuArch.X86, bool exported=true, string marker="cnc-ddraw")
    {
        var exe=workspace.WritePe("game/game.exe",CpuArch.X86,"ddraw.dll");
        var wrapper=workspace.WriteDll("game/ddraw.dll",wrapperArch,["kernel32.dll"],
            exported ? ["DirectDrawCreate"] : ["OtherExport"]);
        using(var stream=File.Open(wrapper,FileMode.Append))
            stream.Write(Encoding.ASCII.GetBytes(marker+"\0Direct3DCreate9\0d3d9.dll\0wglCreateContext\0opengl32.dll\0"));
        File.WriteAllText(workspace.FilePath("game/ddraw.ini"),"[ddraw]\nrenderer="+renderer+"\n");
        return GameScanner.ScanSingleExe(exe)!;
    }

    [Fact]
    public void ExplicitDynamicD3D9WrapperExposesOutputAndPreservesSource()
    {
        using var workspace=new TestWorkspace();var game=Fixture(workspace,"direct3d9");
        Assert.Contains(GraphicsApi.DirectX9,game.AllApis);
        Assert.Contains(GraphicsApi.DirectX7OrOlder,game.AllApis);
    }

    [Theory]
    [InlineData("auto")]
    [InlineData("gdi")]
    [InlineData("opengl")]
    public void OtherOrFallbackRendererDoesNotClaimD3D9(string renderer)
    {
        using var workspace=new TestWorkspace();
        Assert.DoesNotContain(GraphicsApi.DirectX9,Fixture(workspace,renderer).AllApis);
    }

    [Theory]
    [InlineData("opengl")]
    [InlineData("openglcore")]
    public void ExplicitDynamicOpenGlWrapperExposesOutput(string renderer)
    {
        using var workspace=new TestWorkspace();var game=Fixture(workspace,renderer);
        Assert.Contains(GraphicsApi.OpenGl,game.AllApis);
        Assert.Contains(GraphicsApi.DirectX7OrOlder,game.AllApis);
    }

    [Fact]
    public void WrongArchitectureIsNotAWorkingTranslator()
    {
        using var workspace=new TestWorkspace();
        Assert.DoesNotContain(GraphicsApi.DirectX9,Fixture(workspace,"direct3d9",CpuArch.X64).AllApis);
    }

    [Fact]
    public void ExportsAndKnownWrapperIdentityAreRequired()
    {
        using var workspace=new TestWorkspace();
        Assert.DoesNotContain(GraphicsApi.DirectX9,Fixture(workspace,"direct3d9",exported:false).AllApis);
        Assert.DoesNotContain(GraphicsApi.DirectX9,Fixture(workspace,"direct3d9",marker:"unknown").AllApis);
    }
}
