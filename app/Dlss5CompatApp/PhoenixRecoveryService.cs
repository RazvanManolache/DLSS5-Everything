using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Dlss5CompatApp;

// Runtime health is separate from InstallerEngine's file-installation journal.
// Requests are consumed by Phoenix before provider initialization. Selecting a
// new runtime never promotes it to working: only completed frames may do that.
internal sealed class PhoenixRecoveryService
{
    readonly string executable;
    readonly Action<string> requireClosed;
    internal string JournalPath { get; }
    internal PhoenixRecoveryService(string executable, string? directory = null, Action<string>? requireClosed = null)
    {
        this.executable = Path.GetFullPath(executable); LocalPaths.RequireNoLinks(this.executable);
        this.requireClosed = requireClosed ?? RequireClosed;
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.Unicode.GetBytes(this.executable))).ToLowerInvariant();
        JournalPath = Path.Combine(directory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Phoenix", "Recovery"), digest + ".json");
    }
    internal JsonObject Read()
    {
        if (!File.Exists(JournalPath)) return new JsonObject { ["schema"] = "phoenix-recovery/1",
            ["working"] = new JsonObject { ["Backend"] = 0, ["NeuralUplift"] = false }, ["pending"] = null, ["failures"] = 0, ["reason"] = "" };
        var document = JsonNode.Parse(LocalData.ReadText(JournalPath, 262144))?.AsObject()
            ?? throw new InvalidDataException("Phoenix recovery journal is invalid.");
        if (document["schema"]?.GetValue<string>() != "phoenix-recovery/1" || document["working"] is not JsonObject ||
            !document.ContainsKey("pending") || (document["pending"] is not null && document["pending"] is not JsonObject))
            throw new InvalidDataException("Phoenix recovery journal has an unsupported format.");
        return document;
    }
    internal void RestoreWorking() => Request(null);
    internal void DisableProcessing() => Request(settings => {
        if (settings["schema"]?.GetValue<string>() == "phoenix-media/1")
        {
            settings["model"] = "none"; var runtime = settings["runtime"]!.AsObject(); runtime["spatial_only"] = true;
            runtime.Remove("weight_source"); runtime.Remove("tiny_model_path"); settings.Remove("tuning");
            settings["pipeline"] = new JsonObject { ["PipelineMonitoring"] = 0, ["StartupRecovery"] = true };
        }
        else { settings["Backend"] = 0; settings["NeuralUplift"] = false; }
    });
    internal void SelectInstalledModel(string componentRoot, string id)
    {
        var root = OptionalModelLibrary.Root(componentRoot);
        var model = OptionalModelLibrary.Installed(root, verifyWeights: true).SingleOrDefault(x => x.id == id)
            ?? throw new InvalidDataException("The selected model is missing or failed verification.");
        var folder = Path.GetDirectoryName(OptionalModelLibrary.Contained(root, model.manifest))!;
        Request(settings => {
            if (settings["schema"]?.GetValue<string>() == "phoenix-media/1")
            {
                settings["model"] = model.model; var runtime = settings["runtime"]!.AsObject(); runtime["spatial_only"] = false;
                runtime["weight_source"] = new JsonObject { ["kind"] = "model_folder", ["path"] = folder, ["model"] = model.model };
            }
            else
            {
                settings["ModelSource"] = "model_folder"; settings["ModelFolder"] = folder; settings["ModelId"] = model.model;
                settings["Backend"] = 3; settings["NeuralUplift"] = true; settings["ModelLibraryDirectory"] = root;
            }
        });
    }
    void Request(Action<JsonObject>? change)
    {
        requireClosed(executable);
        using var locked = LocalData.Lock(JournalPath);
        var document = Read(); var selected = (JsonObject)document["working"]!.DeepClone(); change?.Invoke(selected);
        document["requested"] = selected;
        document["application"] = executable;
        // Check again immediately before publishing, after any model validation.
        requireClosed(executable);
        LocalData.WriteAtomic(JournalPath, document.ToJsonString(LocalData.Json));
    }
    static void RequireClosed(string executable)
    {
        foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(executable)))
        {
            using (process)
                if (!process.HasExited) throw new InvalidOperationException("Close " + Path.GetFileName(executable) + " before changing its Phoenix recovery settings.");
        }
    }
}
