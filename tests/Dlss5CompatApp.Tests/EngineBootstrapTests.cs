namespace Dlss5CompatApp.Tests;

public sealed class EngineBootstrapTests
{
    [Fact]
    public void SourceEngineContentFolderIsDerivedAndExplicitArgumentsArePreserved()
    {
        using var workspace = new TestWorkspace();
        var exe = workspace.WritePe("engine/arbitrary.exe", CpuArch.X86);
        workspace.WritePe("engine/bin/engine.dll", CpuArch.X86);
        Directory.CreateDirectory(workspace.FilePath("engine/custom content"));
        File.WriteAllText(workspace.FilePath("engine/custom content/gameinfo.txt"), "\"GameInfo\"\n{\nSteamAppId 555\n}\n");
        var bootstrap = EngineBootstrap.Find(exe)!;
        Assert.Equal("-game \"custom content\"", bootstrap.Arguments);
        var game = new GameCandidate(Path.GetDirectoryName(exe)!, exe, "Unrelated", "arbitrary.exe", CpuArch.X86, GraphicsApi.DirectX9, "engine", 200000);
        Assert.Equal("-game \"custom content\" -novid", GameLauncher.Create(game, "-novid").Arguments);
        Assert.Equal("-novid -game other-content", GameLauncher.Create(game, "-novid -game other-content").Arguments);
    }

    [Fact]
    public void SharedSourceEngineUsesStoreAppIdAndLeavesAmbiguousContentUnselected()
    {
        using var workspace = new TestWorkspace();
        var exe = workspace.WritePe("steamapps/common/arbitrary/engine.exe", CpuArch.X86);
        workspace.WritePe("steamapps/common/arbitrary/bin/engine.dll", CpuArch.X86);
        foreach (var (name, app) in new[] { ("first", "111"), ("second", "555") })
        {
            var directory = workspace.FilePath("steamapps/common/arbitrary/" + name);
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "gameinfo.txt"), "GameInfo\n{\nSteamAppId " + app + "\n}\n");
        }
        Assert.Null(EngineBootstrap.Find(exe));
        File.WriteAllText(workspace.FilePath("steamapps/appmanifest_555.acf"), "\"AppState\"\n{\n\"appid\" \"555\"\n\"installdir\" \"arbitrary\"\n}");
        Assert.Equal("-game \"second\"", EngineBootstrap.Find(exe)!.Arguments);
    }

    [Fact]
    public void DerivesProjectArgumentsFromAnExactChildTargetWithoutTitleRules()
    {
        using var workspace = new TestWorkspace();
        var child = workspace.WritePe("package/Project/Binaries/Win64/renderer.exe", CpuArch.X64);
        var bootstrap = workspace.WritePe("package/unrelated-bootstrap.exe", CpuArch.X86);
        var result = EngineBootstrap.Find(child, (path, id) => LocalPaths.Same(path, bootstrap)
            ? id == 201 ? "Project/Binaries/Win64/renderer.exe" : "Project -projectarg=value" : null);
        Assert.NotNull(result);
        Assert.True(LocalPaths.Same(Path.GetDirectoryName(bootstrap)!, result.WorkingDirectory));
        Assert.Equal("Project -projectarg=value", result.Arguments);
        Assert.True(LocalPaths.Same(bootstrap, result.Bootstrap));
    }

    [Theory]
    [InlineData("Project/Binaries/Win64/other.exe", "Project")]
    [InlineData("../outside.exe", "Project")]
    [InlineData("C:/outside.exe", "Project")]
    [InlineData("Project/Binaries/Win64/renderer.exe", "Project\nother")]
    public void UnrelatedOrMalformedBootstrapResourcesCannotChangeLaunch(string target, string arguments)
    {
        using var workspace = new TestWorkspace();
        var child = workspace.WritePe("package/Project/Binaries/Win64/renderer.exe", CpuArch.X64);
        var bootstrap = workspace.WritePe("package/any.exe", CpuArch.X86);
        Assert.Null(EngineBootstrap.Find(child, (path, id) => LocalPaths.Same(path, bootstrap) ? id == 201 ? target : arguments : null));
    }
}
