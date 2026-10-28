namespace Dlss5CompatApp.Tests;

public class PhoenixControlCliTests
{
    [Fact]
    public void PairedScreenshotUsesObservedSessionAndDedicatedWireOperation()
    {
        using var workspace=new TestWorkspace();
        string[] args=["--phoenix-control",workspace.FilePath("game.exe"),"--op","screenshot_pair",
            "--pid","123","--output",workspace.FilePath("reply.json")];
        Assert.Throws<ArgumentException>(()=>PhoenixControlCli.Parse(args));
        var request=PhoenixControlCli.Parse([..args,"--session","123-created"]);
        Assert.Equal(PhoenixOperation.ScreenshotPair,request.Operation);
        Assert.Equal("screenshot_pair",PhoenixControlClient.WireOperation(request.Operation));
        Assert.Null(request.Settings);
    }
    [Theory]
    [InlineData("{\"Tone\":1.2,\"OverrideStrengths\":true}")]
    [InlineData("{\"Backend\":3,\"ExecutionDevice\":\"native\",\"Advanced\":true,\"AutoMask\":true,\"Skin\":2}")]
    [InlineData("{\"Experimental\":true,\"TransformerBlocks\":4,\"MinimumBlocks\":2}")]
    public void ScriptedEditsUseTheApplicationsTypedContract(string json) =>
        Assert.NotEmpty(PhoenixControlCli.ParseSettings(json).Copy());

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"Tone\":1,\"Tone\":2}")]
    [InlineData("{\"Backend\":1.5}")]
    [InlineData("{\"Backend\":99}")]
    [InlineData("{\"Tone\":true}")]
    [InlineData("{\"Advanced\":1}")]
    [InlineData("{\"Skin\":3}")]
    [InlineData("{\"ExecutionDevice\":\"cuda\"}")]
    [InlineData("{\"MinimumBlocks\":8,\"TransformerBlocks\":1}")]
    [InlineData("{\"NotASetting\":1}")]
    public void BadBatchCannotReachTheGame(string json) =>
        Assert.ThrowsAny<Exception>(() => PhoenixControlCli.ParseSettings(json));

    [Fact]
    public void MutationRequiresAnObservedSessionAndExplicitProcess()
    {
        using var workspace = new TestWorkspace();
        string[] Arguments(params string[] tail) => ["--phoenix-control", workspace.FilePath("game.exe"),
            "--pid", "123", "--output", workspace.FilePath("reply.json"), .. tail];
        Assert.Throws<ArgumentException>(() => PhoenixControlCli.Parse(Arguments("--op", "set", "--settings", "{\"Backend\":3}")));
        var request = PhoenixControlCli.Parse(Arguments("--op", "set", "--session", "123-created", "--settings", "{\"Backend\":3}"));
        Assert.Equal(PhoenixOperation.Set, request.Operation);
        Assert.Equal(123, request.Pid);
        Assert.Equal("123-created", request.Session);
        Assert.Throws<ArgumentException>(() => PhoenixControlCli.Parse(Arguments("--op", "status", "--timeout", "0")));
        Assert.Throws<ArgumentException>(() => PhoenixControlCli.Parse(Arguments("--op", "status", "--settings", "{}")));
        Assert.Throws<ArgumentException>(() => PhoenixControlCli.Parse(Arguments("--op", "status", "--op", "save")));
    }
}
