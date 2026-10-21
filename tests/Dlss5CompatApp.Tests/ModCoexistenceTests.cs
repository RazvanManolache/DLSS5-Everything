using Dlss5CompatApp;

namespace Dlss5CompatApp.Tests;

public sealed class ModCoexistenceTests : IDisposable
{
    readonly string root = Path.Combine(Path.GetTempPath(), "phoenix-loading-" + Guid.NewGuid().ToString("N"));
    public ModCoexistenceTests() { Directory.CreateDirectory(root); File.WriteAllText(Path.Combine(root, "game.exe"), "fixture"); }
    GameCandidate Game(GraphicsApi api) => new(root, Path.Combine(root, "game.exe"), "fixture", "fixture", CpuArch.X64, api, "fixture", 7);
    public void Dispose()
    {
        foreach (var file in Directory.EnumerateFiles(root)) File.Delete(file);
        foreach (var directory in Directory.EnumerateDirectories(root)) Directory.Delete(directory);
        Directory.Delete(root);
    }
    [Fact]
    public void OccupiedDxgiSuggestsD3d11OnlyForD3d11AndPreservesItsContents()
    {
        var path = Path.Combine(root, "dxgi.dll"); File.WriteAllText(path, "Unrelated mod");
        var plan = ModCoexistencePlanner.Inspect(Game(GraphicsApi.DirectX11));
        Assert.Equal("d3d11.dll", plan.SuggestedProxy); Assert.True(plan.ManualReview);
        Assert.Single(plan.Components); Assert.Equal(64, plan.Components[0].Sha256.Length);
        Assert.Equal("Unrelated mod", File.ReadAllText(path));
        Assert.Null(ModCoexistencePlanner.Inspect(Game(GraphicsApi.DirectX12)).SuggestedProxy);
    }
    [Fact]
    public void DirectoryOccupiesTheProxySlotAndCannotBeReplaced()
    {
        Directory.CreateDirectory(Path.Combine(root, "dxgi.dll"));
        var plan = ModCoexistencePlanner.Inspect(Game(GraphicsApi.DirectX12));
        Assert.Null(plan.SuggestedProxy); Assert.Single(plan.Components); Assert.Empty(plan.Components[0].Sha256);
    }
    [Fact]
    public void SpecializedRoutesDoNotInventAnAlternateProxy()
    {
        Assert.Null(ModCoexistencePlanner.Inspect(Game(GraphicsApi.Vulkan)).SuggestedProxy);
        Assert.Equal("dxgi.dll", ModCoexistencePlanner.Inspect(Game(GraphicsApi.DirectX12)).SuggestedProxy);
    }
}
