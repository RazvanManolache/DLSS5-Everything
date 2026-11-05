using System.Text.Json;

namespace Dlss5CompatApp.Tests;

public sealed class WrapperProfileInvalidationTests
{
    static PhoenixControlResponse Reply(string api)
    {
        using var data=JsonDocument.Parse(JsonSerializer.Serialize(new {
            api,phase=2,delivered=10,presented=10,status="Running",
            applied_settings=new {Backend=3,ExecutionDevice="native"},
            helper=new {error="",execution=new {kind="native"},phoenix=new {submitted_frames=10,status="Own output written back"}}
        }));
        return new("session",123,"id",true,"observed",null,data.RootElement.Clone(),null);
    }

    static GameCandidate Game(TestWorkspace workspace)
    {
        var exe=workspace.WritePe("game/game.exe",CpuArch.X86,"d3d11.dll");
        return new(Path.GetDirectoryName(exe)!,exe,"Fixture","game.exe",CpuArch.X86,
            GraphicsApi.DirectX11,"imports",new FileInfo(exe).Length);
    }

    [Theory]
    [InlineData((int)GraphicsApi.DirectX9,"DDrawToD3D9")]
    [InlineData((int)GraphicsApi.OpenGl,"DDrawToOpenGL")]
    public void ChangedConcreteWrapperDoesNotReuseOldExecutableOnlyProfile(int output,string kind)
    {
        using var workspace=new TestWorkspace();var original=Game(workspace);var profiles=workspace.FilePath("profiles");
        Assert.True(VerifiedRendererProfiles.Record(original,Reply("Direct3D 11"),123,profiles));
        var current=original with {Api=(GraphicsApi)output,PossibleApis=[GraphicsApi.DirectX7OrOlder,(GraphicsApi)output],
            TranslatorKinds=[kind],DetectedInputApis=[GraphicsApi.DirectX7OrOlder]};
        var selected=VerifiedRendererProfiles.Apply(current,profiles);
        Assert.Equal((GraphicsApi)output,selected.Api);
        Assert.False(selected.HasVerifiedRenderer);
        Assert.DoesNotContain(GraphicsApi.DirectX11,selected.AllApis);
    }

    [Fact]
    public void ValidChainedOutputKeepsItsVerifiedProfile()
    {
        using var workspace=new TestWorkspace();var original=Game(workspace);var profiles=workspace.FilePath("profiles");
        Assert.True(VerifiedRendererProfiles.Record(original,Reply("Vulkan"),123,profiles));
        var current=original with {Api=GraphicsApi.Vulkan,PossibleApis=[GraphicsApi.DirectX7OrOlder,GraphicsApi.DirectX9,GraphicsApi.Vulkan],
            TranslatorKinds=["DDrawToD3D9","DXVK"],DetectedInputApis=[GraphicsApi.DirectX7OrOlder]};
        var selected=VerifiedRendererProfiles.Apply(current,profiles);
        Assert.True(selected.HasVerifiedRenderer);Assert.Equal(GraphicsApi.Vulkan,selected.Api);
    }

    [Fact]
    public void AmbiguousNativeRuntimeCanStillLearnItsProvenOutput()
    {
        using var workspace=new TestWorkspace();var original=Game(workspace);var profiles=workspace.FilePath("profiles");
        Assert.True(VerifiedRendererProfiles.Record(original,Reply("Vulkan"),123,profiles));
        var selected=VerifiedRendererProfiles.Apply(original,profiles);
        Assert.True(selected.HasVerifiedRenderer);Assert.Equal(GraphicsApi.Vulkan,selected.Api);
    }
}
