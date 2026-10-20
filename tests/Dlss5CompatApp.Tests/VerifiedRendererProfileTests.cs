using System.Text.Json;

namespace Dlss5CompatApp.Tests;

public sealed class VerifiedRendererProfileTests
{
    [Fact]
    public void AutomaticLibraryOptionsKeepTheVerifiedHookAndInputRoute()
    {
        var detected = new GameCandidate("C:/Games/Any", "C:/Games/Any/game.exe", "Any", "game.exe", CpuArch.X64,
            GraphicsApi.DirectX11, "verified", 200000)
        {
            ProxyName = "d3d11.dll", PreferredRoute = GameRoutePreference.Feeder,
            VerifiedInputRoute = GameRoutePreference.Feeder, VerifiedProxyName = "d3d11.dll"
        };
        Assert.Equal("d3d11.dll", new GameProfile().Apply(detected).ProxyName);
        Assert.Equal(GameRoutePreference.Feeder, new GameProfile().Apply(detected).PreferredRoute);
        Assert.Null(new GameProfile { ApiOverride = GraphicsApi.OpenGl }.Apply(detected).ProxyName);
    }

    [Fact]
    public void LearnsActualRendererAndInvalidatesAfterExecutableChanges()
    {
        using var workspace = new TestWorkspace();
        var exe = workspace.WritePe("game/any-name.exe", CpuArch.X64);
        var game = new GameCandidate(Path.GetDirectoryName(exe)!, exe, "Any title", "any-name.exe",
            CpuArch.X64, GraphicsApi.DirectX12, "strings", new FileInfo(exe).Length, HasNativeDlssSignal: true);
        var folder = workspace.FilePath("profiles");
        var reply = Reply(123, "Vulkan", 10);
        Assert.False(VerifiedRendererProfiles.Record(game, reply, 124, folder));
        Assert.False(Directory.Exists(folder));
        Assert.True(VerifiedRendererProfiles.Record(game, reply, 123, folder));
        var learned = VerifiedRendererProfiles.Apply(game, folder);
        Assert.Equal(GraphicsApi.Vulkan, learned.Api);
        Assert.Equal(GameRoutePreference.Feeder, learned.PreferredRoute);
        Assert.Contains(GraphicsApi.Vulkan, learned.AllApis);
        var renamed = VerifiedRendererProfiles.Apply(game with { DisplayName = "Renamed title" }, folder);
        Assert.Equal(learned.Api, renamed.Api);
        Assert.Equal(learned.PreferredRoute, renamed.PreferredRoute);
        Assert.Equal("Renamed title", renamed.DisplayName);
        using (var file = new FileStream(exe, FileMode.Open, FileAccess.Write)) { file.Position = file.Length - 1; file.WriteByte(42); }
        Assert.Equal(game, VerifiedRendererProfiles.Apply(game, folder));
    }

    [Fact]
    public void IncompleteOutputCannotTeachProfileAndTranslatedInputIsPreserved()
    {
        using var workspace = new TestWorkspace();
        var exe = workspace.WritePe("game/legacy.exe", CpuArch.X86);
        var game = new GameCandidate(Path.GetDirectoryName(exe)!, exe, "Legacy", "legacy.exe", CpuArch.X86,
            GraphicsApi.DirectX7OrOlder, "imports", new FileInfo(exe).Length);
        var folder = workspace.FilePath("profiles");
        Assert.False(VerifiedRendererProfiles.Record(game, Reply(123, "Direct3D 11", 0), 123, folder));
        Assert.False(VerifiedRendererProfiles.Record(game, Reply(123, "invented API", 10), 123, folder));
        Assert.True(VerifiedRendererProfiles.Record(game, Reply(123, "Direct3D 11", 10), 123, folder));
        var learned = VerifiedRendererProfiles.Apply(game, folder);
        Assert.Equal(GraphicsApi.DirectX7OrOlder, learned.Api);
        Assert.Equal(InstallRoute.X86DirectXLegacyViaDgVoodoo, learned.Route);
    }

    [Theory]
    [InlineData((int)GraphicsApi.Glide31, (int)GraphicsApi.DirectX7OrOlder)]
    [InlineData((int)GraphicsApi.DirectX8, (int)GraphicsApi.DirectX8DgVoodoo)]
    public void AWorkingLegacyAlternateIsRememberedInsteadOfTheOriginalGuess(int guessedValue, int provenValue)
    {
        var guessed = (GraphicsApi)guessedValue;
        var proven = (GraphicsApi)provenValue;
        using var workspace = new TestWorkspace();
        var exe = workspace.WritePe("game/arbitrary.exe", CpuArch.X86);
        var game = new GameCandidate(Path.GetDirectoryName(exe)!, exe, "Unrelated", "arbitrary.exe", CpuArch.X86,
            guessed, "imports", new FileInfo(exe).Length, [guessed, proven]);
        var folder = workspace.FilePath("profiles");
        Assert.True(VerifiedRendererProfiles.Record(game.WithApi(proven), Reply(123, "Direct3D 11", 10), 123, folder));
        var learned = VerifiedRendererProfiles.Apply(game, folder);
        Assert.Equal(proven, learned.Api);
        Assert.Equal(game.WithApi(proven).Route, learned.Route);
    }

    [Theory]
    [InlineData((int)GraphicsApi.DirectX10, "Direct3D 10")]
    [InlineData((int)GraphicsApi.DirectX10_1, "Direct3D 10.1")]
    [InlineData((int)GraphicsApi.DirectX10_1, "Direct3D 10 / 10.1")]
    public void RuntimeDx10EvidenceNeverBecomesDx11(int input, string reported)
    {
        using var workspace = new TestWorkspace();
        var exe = workspace.WritePe("game/arbitrary.exe", CpuArch.X86);
        var game = new GameCandidate(Path.GetDirectoryName(exe)!, exe, "Any", "arbitrary.exe", CpuArch.X86,
            (GraphicsApi)input, "imports", new FileInfo(exe).Length);
        var profiles = workspace.FilePath("profiles");
        Assert.True(VerifiedRendererProfiles.Record(game, Reply(123, reported, 10), 123, profiles));
        Assert.Equal(game.Api, VerifiedRendererProfiles.Apply(game, profiles).Api);
    }

    [Fact]
    public void OldDx10ToDx11ProfileDoesNotOverrideNewConcreteDetection()
    {
        using var workspace = new TestWorkspace();
        var exe = workspace.WritePe("game/arbitrary.exe", CpuArch.X86);
        var game = new GameCandidate(Path.GetDirectoryName(exe)!, exe, "Any", "arbitrary.exe", CpuArch.X86,
            GraphicsApi.DirectX10, "imports", new FileInfo(exe).Length);
        var profiles = workspace.FilePath("profiles");
        Assert.True(VerifiedRendererProfiles.Record(game, Reply(123, "Direct3D 11", 10), 123, profiles));
        var file = Directory.GetFiles(profiles, "*.json").Single();
        var profile = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(file))!;
        profile["Version"] = 2;
        File.WriteAllText(file, profile.ToJsonString());
        Assert.Equal(GraphicsApi.DirectX10, VerifiedRendererProfiles.Apply(game, profiles).Api);
    }

    [Fact]
    public void ExistingTranslatorUsesOutputHookButItsRemovalRestoresTheFullInputRoute()
    {
        using var workspace = new TestWorkspace();
        var exe = workspace.WritePe("game/arbitrary.exe", CpuArch.X86);
        var game = new GameCandidate(Path.GetDirectoryName(exe)!, exe, "Any", "arbitrary.exe", CpuArch.X86,
            GraphicsApi.Dxgi, "wrapper:dgVoodoo/ddraw.dll", new FileInfo(exe).Length)
        { DetectedInputApis = [GraphicsApi.DirectX7OrOlder], TranslatorKinds = ["dgVoodoo"] };
        var profiles = workspace.FilePath("profiles");
        Assert.True(VerifiedRendererProfiles.Record(game, Reply(123, "Direct3D 11", 10), 123, profiles));
        Assert.Equal(GraphicsApi.DirectX11, VerifiedRendererProfiles.Apply(game, profiles).Api);
        var bare = game with { Api = GraphicsApi.DirectX7OrOlder, TranslatorKinds = [] };
        Assert.Equal(InstallRoute.X86DirectXLegacyViaDgVoodoo, VerifiedRendererProfiles.Apply(bare, profiles).Route);
        var changedOutput = game with { Api = GraphicsApi.DirectX12 };
        Assert.Equal(GraphicsApi.DirectX12, VerifiedRendererProfiles.Apply(changedOutput, profiles).Api);
    }

    [Theory]
    [InlineData((int)CpuArch.X86)]
    [InlineData((int)CpuArch.X64)]
    public void RuntimeEvidenceRefinesAngleWithoutGuessingFromItsName(int arch)
    {
        using var workspace = new TestWorkspace();
        var exe = workspace.WritePe("game/arbitrary.exe", (CpuArch)arch);
        var game = new GameCandidate(Path.GetDirectoryName(exe)!, exe, "Any", "arbitrary.exe", (CpuArch)arch,
            GraphicsApi.Dxgi, "ANGLE runtime selection", new FileInfo(exe).Length)
        { TranslatorKinds = ["ANGLE"], RuntimeSelectionRequired = true };
        var profiles = workspace.FilePath("profiles");
        Assert.True(VerifiedRendererProfiles.Record(game, Reply(123, "Direct3D 12", 10), 123, profiles));
        Assert.Equal(GraphicsApi.DirectX12, VerifiedRendererProfiles.Apply(game, profiles).Api);
    }

    static PhoenixControlResponse Reply(int pid, string api, int delivered)
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            api, phase = 2, delivered, presented = delivered, status = "Running",
            applied_settings = new { Backend = 3, ExecutionDevice = "native" },
            helper = new { error = "", execution = new { kind = "native" }, phoenix = new { submitted_frames = delivered, status = "Own output written back" } }
        }));
        return new("session", pid, "id", true, "observed", null, document.RootElement.Clone(), null);
    }
}
