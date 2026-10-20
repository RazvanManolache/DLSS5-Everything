namespace Dlss5CompatApp.Tests;

public class ComponentCliTests
{
    [Fact]
    public void DependencyUpdateUsesTheSharedUpdaterWithoutRequiringAModelDownload()
    {
        using var workspace = new TestWorkspace();
        var request = ComponentCli.Parse(["--components-update", "--payload", workspace.Root]);
        Assert.True(request.Update);
        Assert.Null(request.ModelPath);
        Assert.Null(request.PhoenixPath);
        Assert.True(ComponentCli.IsUpdateCommand(["--components-update"]));
        Assert.False(ComponentCli.IsImportCommand(["--components-update"]));
    }

    [Fact]
    public void ImportRequiresExplicitSourcesAndDestination()
    {
        using var workspace = new TestWorkspace();
        var request = ComponentCli.Parse(["--components-import", "--payload", workspace.Root, "--phoenix", "selected.addon64", "--model", "nvngx_dlssnr.dll"]);
        Assert.Equal(workspace.Root, request.PayloadRoot);
        Assert.Equal("selected.addon64", request.PhoenixPath);
        Assert.Equal("nvngx_dlssnr.dll", request.ModelPath);
        Assert.False(ComponentCli.IsImportCommand(["--ui-preview"]));
        Assert.Equal(Path.GetFullPath("Payload"), ComponentCli.Parse(["--components-import", "--payload", "Payload", "--model", "nvngx_dlssnr.dll"]).PayloadRoot);
    }

    [Theory]
    [InlineData("--components-import")]
    [InlineData("--components-import --payload")]
    [InlineData("--components-import --payload C:\\Payload")]
    [InlineData("--components-import --phoenix selected.addon64")]
    [InlineData("--components-import --payload C:\\Payload --model")]
    [InlineData("--components-import --payload C:\\Payload --unknown value")]
    [InlineData("--components-import --payload C:\\Payload --payload C:\\Other --model x.dll")]
    public void RejectsIncompleteOrAmbiguousCommands(string command) =>
        Assert.Throws<ArgumentException>(() => ComponentCli.Parse(command.Split(' ')));

    [Fact]
    public async Task BadCommandDoesNotCreateDestination()
    {
        using var workspace = new TestWorkspace();
        var root = workspace.FilePath("untouched");
        Assert.Equal(1, await ComponentCli.RunAsync(["--components-import", "--payload", root]));
        Assert.False(Directory.Exists(root));
    }
}
