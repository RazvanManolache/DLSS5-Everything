using System.Text.Json;
using System.Text.Json.Nodes;
using Dlss5CompatApp;
namespace Dlss5CompatApp.Tests;

public sealed class PhoenixPipelineTests
{
    [Fact]
    public void PortablePackagePinsDependenciesAndRejectsChangedOrEscapingFiles()
    {
        var root=Path.Combine(Path.GetTempPath(),"phoenix-portable-test-"+Guid.NewGuid().ToString("N"));
        var provider=Path.Combine(root,"portable-runtime");Directory.CreateDirectory(provider);
        try
        {
            var files=new JsonObject();
            foreach(var name in new[]{"phoenix_runtime_cpp.dll","phoenix_native_broker.exe","phoenix_cpp_devices.exe"})
            {
                var path=Path.Combine(provider,name);File.WriteAllText(path,"test dependency "+name);
                files[name]=OptionalModelLibrary.HashFile(path);
            }
            var manifest=Path.Combine(root,"phoenix-runtime.json");
            void Save()=>File.WriteAllText(manifest,new JsonObject { ["portable_runtime"]=new JsonObject {
                ["root"]="portable-runtime",["files"]=files.DeepClone() } }.ToJsonString());
            Save();var exe=Path.Combine(root,"PhoenixVideoPlayer.exe");
            Assert.Equal(provider,PhoenixMediaPlayer.PortablePackage(exe,root).Root);
            var runtime=Path.Combine(provider,"phoenix_runtime_cpp.dll");var original=File.ReadAllText(runtime);
            File.WriteAllText(runtime,"changed dependency");
            Assert.Throws<InvalidDataException>(()=>PhoenixMediaPlayer.PortablePackage(exe,root));
            File.WriteAllText(runtime,original);files["../outside.dll"]=new string('0',64);Save();
            Assert.Throws<ArgumentException>(()=>PhoenixMediaPlayer.PortablePackage(exe,root));
        }
        finally { Directory.Delete(root,true); }
    }
    [Fact]
    public void NativeDeviceInventoryFiltersUnsupportedEntriesAndKeepsConcreteDevices()
    {
        var devices=PhoenixMediaPlayer.ParseDevices("""
            {"devices":[{"id":"cpu","label":"CPU C++"},
            {"id":"cuda:GPU-12345678","label":"NVIDIA GPU"},
            {"id":"vulkan:abcdef","label":"AMD GPU","vendor":4098,"capable":true},
            {"id":"vulkan:unsupported","label":"Unsupported","capable":false},
            {"id":"bad/file","label":"Invalid"},{"id":"cpu","label":"Duplicate"}]}
            """);
        Assert.Equal(new[]{"cpu","cuda:GPU-12345678","vulkan:abcdef"},devices.Select(x=>x.Id));
        Assert.Equal(4098,devices[2].Vendor);
        foreach(var device in devices)(new PhoenixMediaRecipe {ExecutionDevice=device.Id}).Validate();
        Assert.Throws<InvalidDataException>(()=>(new PhoenixMediaRecipe {ExecutionDevice="../runtime"}).Validate());
    }
    [Fact]
    public void OrderedStagesAndModelSpecificTuningReachTheRecipe()
    {
        var recipe = new PhoenixMediaRecipe("small-fp8", Fusion: true) {
            Order = [2, 1, 0, 3], Passes = 3, Repeats = [0, 2, 1, 1, 1, 1, 1, 1],
            Blocks = 6, TargetFps = 30, MaximumProcessingMs = 20, Brightness = .15f,
            Priority = 1, AdjustBlocks = true, SecondScale = .4f, FusionLayers = 3
        };
        recipe.Validate();
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(recipe.ToDocument(Path.GetTempPath())));
        var pipeline = document.RootElement.GetProperty("pipeline"); var tuning = document.RootElement.GetProperty("tuning");
        Assert.Equal(2, pipeline.GetProperty("ChainStage1").GetInt32()); Assert.True(pipeline.GetProperty("ChainFusion").GetBoolean());
        Assert.Equal(.15f, pipeline.GetProperty("ChainBrightness").GetSingle()); Assert.Equal(3, pipeline.GetProperty("FusionLayers").GetInt32());
        Assert.Equal(6, tuning.GetProperty("central_blocks").GetInt32()); Assert.Equal(3, tuning.GetProperty("network_passes").GetInt32());
        Assert.Equal(0, tuning.GetProperty("repeats")[0].GetInt32()); Assert.Equal(30, tuning.GetProperty("target_fps").GetSingle());
        Assert.Throws<InvalidDataException>(() => (recipe with { Order = [1, 1, 0, 3] }).Validate());
        Assert.Throws<InvalidDataException>(() => (recipe with { Brightness = float.NaN }).Validate());
        Assert.Throws<InvalidDataException>(() => (recipe with { MinimumBlocks = 7 }).Validate());
    }
    [Fact]
    public void RecoveryRequestsDoNotPromoteUnprovenSettingsOrOverwriteInstallationHistory()
    {
        var root = Path.Combine(Path.GetTempPath(), "phoenix-recovery-test-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            int checks = 0; var recovery = new PhoenixRecoveryService(Path.Combine(root, "game.exe"), root, _ => ++checks);
            var document = recovery.Read(); document["working"] = new JsonObject { ["Backend"] = 3, ["NeuralUplift"] = true, ["ModelId"] = "original" };
            document["pending"] = new JsonObject { ["ModelId"] = "small-fp8" };
            LocalData.WriteAtomic(recovery.JournalPath, document.ToJsonString());
            var installJournal = Path.Combine(root, "installation.json"); File.WriteAllText(installJournal, "preserve installation history");
            recovery.DisableProcessing(); var selected = recovery.Read();
            Assert.Equal(2, checks); Assert.True(selected["working"]!["NeuralUplift"]!.GetValue<bool>());
            Assert.Equal("small-fp8", selected["pending"]!["ModelId"]!.GetValue<string>());
            Assert.False(selected["requested"]!["NeuralUplift"]!.GetValue<bool>());
            Assert.Equal(0, selected["requested"]!["Backend"]!.GetValue<int>());
            recovery.RestoreWorking(); Assert.True(recovery.Read()["requested"]!["NeuralUplift"]!.GetValue<bool>());
            Assert.Equal("preserve installation history", File.ReadAllText(installJournal));
            var previous = File.ReadAllText(recovery.JournalPath);
            var refused = new PhoenixRecoveryService(Path.Combine(root, "game.exe"), root, _ => throw new InvalidOperationException("Application is running"));
            Assert.Throws<InvalidOperationException>(refused.DisableProcessing); Assert.Equal(previous, File.ReadAllText(recovery.JournalPath));
        }
        finally { Directory.Delete(root, true); }
    }
}
