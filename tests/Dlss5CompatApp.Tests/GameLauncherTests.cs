namespace Dlss5CompatApp.Tests;

public class GameLauncherTests
{
    static GameCandidate Game(GraphicsApi api) => new(@"C:\Games\Example", @"C:\Games\Example\Game.exe", "Example", "Game.exe", CpuArch.X64, api, "fixture", 200000);

    [Fact]
    public void StoreIdentityIsDerivedFromTheInstalledManifestForEveryRenderer()
    {
        using var workspace = new TestWorkspace();
        var exe = workspace.WritePe("steamapps/common/Any folder/game.exe", CpuArch.X64);
        File.WriteAllText(workspace.FilePath("steamapps/appmanifest_456.acf"), "\"appid\" \"456\"\n\"installdir\" \"Any folder\"");
        var game = new GameCandidate(Path.GetDirectoryName(exe)!, exe, "Unrelated title", "game.exe", CpuArch.X64,
            GraphicsApi.DirectX11, "imports", 200000);
        var plan = GameLauncher.Create(game, "");
        Assert.Equal("456", plan.Environment["SteamAppId"]);
        Assert.Equal("456", plan.Environment["SteamGameId"]);
        Assert.False(plan.UseShellExecute);
    }

    [Fact]
    public void PlayUsesCurrentArgumentsWithoutACommandShell()
    {
        var plan = GameLauncher.Create(Game(GraphicsApi.DirectX12), "-windowed -width 1280");
        Assert.Equal("-windowed -width 1280", plan.Arguments);
        Assert.Equal(@"C:\Games\Example\Game.exe", plan.FileName);
        Assert.Equal(@"C:\Games\Example", plan.WorkingDirectory);
        Assert.False(plan.UseShellExecute);
    }

    [Fact]
    public void OpenXrHasSameLayerEnvironmentAsManualLauncher()
    {
        var plan = GameLauncher.Create(Game(GraphicsApi.OpenXr), "");
        Assert.Equal("XR_APILAYER_DLSS5_everything", plan.Environment["XR_ENABLE_API_LAYERS"]);
        Assert.EndsWith(@"_DLSS5_OpenXR\openxr", plan.Environment["XR_API_LAYER_PATH"]);
        Assert.Equal("1", plan.Environment["DLSS5_OPENXR_PROCESS_EVERY"]);
    }

    [Theory]
    [InlineData("a\nb")]
    [InlineData("a\0b")]
    public void InvalidArgumentsAreRejected(string arguments) => Assert.Throws<ArgumentException>(() => GameLauncher.Create(Game(GraphicsApi.DirectX11), arguments));
}
