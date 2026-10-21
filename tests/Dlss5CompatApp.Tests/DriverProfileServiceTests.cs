using System.Text.Json;
using Dlss5CompatApp;

namespace Dlss5CompatApp.Tests;

public sealed class DriverProfileServiceTests
{
    sealed class DirectoryScope : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "phoenix-driver-tests-" + Guid.NewGuid().ToString("N"));
        public DirectoryScope() => Directory.CreateDirectory(Root);
        public void Dispose() { foreach (var path in Directory.EnumerateFiles(Root)) File.Delete(path); Directory.Delete(Root); }
    }
    sealed class FakeDriver : IDriverProfile
    {
        readonly Dictionary<uint, uint> persisted;
        readonly Dictionary<uint, uint> staged;
        public bool FailCommit;
        public FakeDriver(Dictionary<uint, uint> values) { persisted = values; staged = new(values); }
        public uint? Read(uint setting) => staged.TryGetValue(setting, out var value) ? value : null;
        public void Write(uint setting, uint? value) { if (value is { } number) staged[setting] = number; else staged.Remove(setting); }
        public void Commit()
        {
            if (FailCommit) throw new IOException("Injected driver save failure");
            persisted.Clear(); foreach (var pair in staged) persisted[pair.Key] = pair.Value;
        }
        public void Dispose() { }
    }
    const uint Limit = 0x10835002, Hdr = 0x00DD48FB;

    [Fact]
    public void RepeatedChangesPreserveTheOriginalInheritedValue()
    {
        using var folder = new DirectoryScope(); var values = new Dictionary<uint, uint>();
        var executable = Path.Combine(folder.Root, "game.exe");
        IDriverProfile Open(string _) => new FakeDriver(values);
        DriverProfileService.Apply(executable, new Dictionary<uint, uint?> { [Limit] = 60 }, Open, folder.Root);
        DriverProfileService.Apply(executable, new Dictionary<uint, uint?> { [Limit] = 120 }, Open, folder.Root);
        Assert.Equal(120u, values[Limit]);
        Assert.Empty(DriverProfileService.Restore(executable, Open, folder.Root));
        Assert.False(values.ContainsKey(Limit));
    }
    [Fact]
    public void RestoreKeepsExternalChangesAndRestoresOurOtherSettings()
    {
        using var folder = new DirectoryScope(); var values = new Dictionary<uint, uint> { [Limit] = 30 };
        var executable = Path.Combine(folder.Root, "game.exe");
        IDriverProfile Open(string _) => new FakeDriver(values);
        DriverProfileService.Apply(executable, new Dictionary<uint, uint?> { [Limit] = 60, [Hdr] = 1 }, Open, folder.Root);
        values[Limit] = 90;
        var conflicts = DriverProfileService.Restore(executable, Open, folder.Root);
        Assert.Single(conflicts); Assert.Equal(90u, values[Limit]); Assert.False(values.ContainsKey(Hdr));
    }
    [Fact]
    public void FailedCommitKeepsDurableRestorationEvidence()
    {
        using var folder = new DirectoryScope(); var values = new Dictionary<uint, uint> { [Limit] = 30 };
        var executable = Path.Combine(folder.Root, "game.exe");
        Assert.Throws<IOException>(() => DriverProfileService.Apply(executable,
            new Dictionary<uint, uint?> { [Limit] = 60 }, _ => new FakeDriver(values) { FailCommit = true }, folder.Root));
        Assert.Equal(30u, values[Limit]);
        using var document = JsonDocument.Parse(File.ReadAllText(DriverProfileService.JournalPath(executable, folder.Root)));
        Assert.Equal("pending", document.RootElement.GetProperty("State").GetString());
    }
    [Fact]
    public void UnsupportedSettingsNeverOpenTheDriver()
    {
        var opened = false;
        Assert.Throws<InvalidDataException>(() => DriverProfileService.Apply("game.exe",
            new Dictionary<uint, uint?> { [123] = 1 }, _ => { opened = true; throw new Exception(); }));
        Assert.False(opened);
    }
    [Fact]
    public void MediaRecipeCarriesTheSelectedNativeControlsAndCentralModelRoot()
    {
        var recipe = new PhoenixMediaRecipe("small-fp8", true, true, 0, 180, 1, true); recipe.Validate();
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(recipe.ToDocument(Path.GetTempPath())));
        var root = document.RootElement;
        Assert.Equal("small-fp8", root.GetProperty("model").GetString());
        Assert.Equal(0, root.GetProperty("pipeline").GetProperty("PipelineMonitoring").GetInt32());
        Assert.True(root.GetProperty("pipeline").GetProperty("MultiscaleFusion").GetBoolean());
        Assert.Equal(OptionalModelLibrary.Root(Path.GetTempPath()), root.GetProperty("model_library").GetString());
    }
    [Fact]
    public void MediaLaunchRecipesRemainIndependentAndPreserveAlteredFiles()
    {
        using var folder = new DirectoryScope();
        var first = PhoenixMediaPlayer.PublishRecipe("{\"model\":\"original\"}", folder.Root);
        var second = PhoenixMediaPlayer.PublishRecipe("{\"model\":\"small-fp8\"}", folder.Root);
        Assert.NotEqual(first, second);
        Assert.Equal("{\"model\":\"original\"}", File.ReadAllText(first));
        Assert.Equal(first, PhoenixMediaPlayer.PublishRecipe("{\"model\":\"original\"}", folder.Root));
        File.WriteAllText(first, "preserve changed recipe");
        Assert.Throws<InvalidDataException>(() => PhoenixMediaPlayer.PublishRecipe("{\"model\":\"original\"}", folder.Root));
        Assert.Equal("preserve changed recipe", File.ReadAllText(first));
        Assert.Throws<InvalidDataException>(() => new PhoenixMediaRecipe(Scaler: 2).Validate());
    }
}
