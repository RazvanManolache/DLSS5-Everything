using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text;
using System.Security.Cryptography;

namespace Dlss5CompatApp;

internal sealed record PhoenixMediaRecipe(string Model = "original", bool Motion = true,
    bool SceneDetection = true, int Monitoring = 0, int FlowHeight = 180,
    int Scaler = 0, bool Fusion = false)
{
    public string ExecutionDevice { get; init; } = "native";
    public IReadOnlyList<PhoenixMediaPlayer.Device> ExecutionDevices { get; init; } = [];
    public int[] Order { get; init; } = [0, 3, 1, 2];
    public bool Neural { get; init; } = true;
    public bool Scale { get; init; } = true;
    public bool Color { get; init; } = true;
    public int FusionLayers { get; init; } = 2;
    public float SecondScale { get; init; } = .5f;
    public float ThirdScale { get; init; } = .75f;
    public float Brightness { get; init; }
    public float Contrast { get; init; } = 1;
    public float Saturation { get; init; } = 1;
    public float Gamma { get; init; } = 1;
    public int Passes { get; init; } = 1;
    public float NeuralScale { get; init; } = 100;
    public int[] Repeats { get; init; } = [1, 1, 1, 1, 1, 1, 1, 1];
    public int Priority { get; init; }
    public int Blocks { get; init; } = 8;
    public int MinimumBlocks { get; init; } = 1;
    public float TargetFps { get; init; }
    public float MaximumProcessingMs { get; init; }
    public float MaximumAgeMs { get; init; } = 50;
    public bool AdjustFlow { get; init; } = true;
    public bool AdjustScale { get; init; }
    public bool AdjustBlocks { get; init; }
    public bool AdjustPasses { get; init; }
    public object ToDocument(string componentRoot) => new
    {
        schema = "phoenix-media/1", model = Model,
        component_root = Path.GetFullPath(componentRoot),
        model_library = OptionalModelLibrary.Root(componentRoot),
        tuning = new { network_passes = Passes, neural_scale = NeuralScale, repeats = Repeats,
            central_blocks = Blocks, minimum_central_blocks = MinimumBlocks, target_fps = TargetFps, max_processing_ms = MaximumProcessingMs },
        pipeline = new Dictionary<string, object>
        {
            ["MotionEstimation"] = Motion, ["MotionProvider"] = 0,
            ["FlowAnalysisHeight"] = FlowHeight, ["SceneDetection"] = SceneDetection,
            ["PipelineMonitoring"] = Monitoring, ["ProcessingChain"] = true,
            ["AdditionalScaler"] = Scaler, ["MultiscaleFusion"] = Fusion,
            ["ChainStage1"] = Order[0], ["ChainStage2"] = Order[1], ["ChainStage3"] = Order[2], ["ChainStage4"] = Order[3],
            ["ChainNeural"] = Neural, ["ChainScale"] = Scale, ["ChainColor"] = Color, ["ChainFusion"] = Fusion,
            ["FusionLayers"] = FusionLayers, ["FusionSecondScale"] = SecondScale, ["FusionThirdScale"] = ThirdScale,
            ["ChainBrightness"] = Brightness, ["ChainContrast"] = Contrast, ["ChainSaturation"] = Saturation, ["ChainGamma"] = Gamma,
            ["PipelinePriority"] = Priority, ["MaximumFrameAgeMs"] = MaximumAgeMs,
            ["AdjustMotionResolution"] = AdjustFlow, ["AdjustNeuralResolution"] = AdjustScale,
            ["AdjustCentralBlocks"] = AdjustBlocks, ["AdjustNetworkPasses"] = AdjustPasses
        }
    };
    public void Validate()
    {
        if (ExecutionDevice != "native" && ExecutionDevice != "cpu" &&
            !System.Text.RegularExpressions.Regex.IsMatch(ExecutionDevice, @"^(cuda|vulkan|hip|sycl):[A-Za-z0-9_.:-]{1,128}$"))
            throw new InvalidDataException("Choose an available native processing device.");
        if (Model is not ("original" or "small-fp8") || Monitoring is < 0 or > 2 ||
            FlowHeight is < 64 or > 2160 || Scaler is not (0 or 1 or 3) ||
            Order is null || !Order.Order().SequenceEqual(new[] { 0, 1, 2, 3 }) ||
            Repeats is null || Repeats.Length != 8 || Repeats.Any(x => x is < 0 or > 4) ||
            Passes is < 1 or > 10 || Priority is < 0 or > 2 || FusionLayers is < 2 or > 3 ||
            Blocks is < 1 or > 8 || MinimumBlocks < 1 || MinimumBlocks > Blocks ||
            !Range(TargetFps, 0, 1000) || !Range(MaximumProcessingMs, 0, 1000) ||
            !Range(NeuralScale, 10, 100) || !Range(SecondScale, .1f, 1) || !Range(ThirdScale, .1f, 1) ||
            !Range(MaximumAgeMs, 1, 1000) || !Range(Brightness, -2, 2) || !Range(Contrast, 0, 4) ||
            !Range(Saturation, 0, 2) || !Range(Gamma, .1f, 4) || (!Scale && Scaler != 0))
            throw new InvalidDataException("Unsupported Phoenix media settings.");
    }
    static bool Range(float value, float minimum, float maximum) => float.IsFinite(value) && value >= minimum && value <= maximum;
}

internal static class PhoenixMediaPlayer
{
    internal sealed record Device(string Id, string Label, int Vendor = 0)
    { public override string ToString() => Label; }

    internal static string RuntimeManifest(string executable,string componentRoot) =>
        new[] { Path.Combine(componentRoot,"Media","phoenix-runtime.json"),
            Path.Combine(Path.GetDirectoryName(executable)!,"phoenix-runtime.json") }.FirstOrDefault(File.Exists)
            ?? throw new FileNotFoundException("The player runtime package is missing.");

    internal static string DeviceDiscoveryKey(string executable,string componentRoot)
    {
        var manifest=RuntimeManifest(executable,componentRoot);LocalPaths.RequireNoLinks(manifest);
        var content=LocalData.ReadText(manifest,65536);
        return Path.GetFullPath(executable)+"\n"+Path.GetFullPath(componentRoot)+"\n"+Path.GetFullPath(manifest)+"\n"+
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)));
    }

    internal static (string Root, JsonObject Files) PortablePackage(string executable, string componentRoot)
    {
        var manifest = RuntimeManifest(executable,componentRoot);
        LocalPaths.RequireNoLinks(manifest);
        var package = JsonNode.Parse(LocalData.ReadText(manifest, 65536))!.AsObject();
        var portable = package["portable_runtime"]?.AsObject() ?? throw new InvalidDataException("Update the player package to use portable processing.");
        var root = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(manifest)!, portable["root"]!.GetValue<string>()));
        LocalPaths.RequireNoLinks(root);
        var files = portable["files"]!.AsObject();
        if (files.Count is < 3 or > 512 || !files.ContainsKey("phoenix_runtime_cpp.dll") || !files.ContainsKey("phoenix_native_broker.exe"))
            throw new InvalidDataException("Incomplete portable runtime manifest.");
        foreach (var file in files)
        {
            var path = BoundedInstallPaths.Resolve(root, file.Key); LocalPaths.RequireNoLinks(path);
            if (!File.Exists(path) || !OptionalModelLibrary.HashFile(path).Equals(file.Value!.GetValue<string>(), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Portable runtime checksum differs: " + file.Key);
        }
        return (root, files);
    }

    internal static async Task<IReadOnlyList<Device>> DevicesAsync(string executable, string componentRoot)
    {
        // Loading drivers and querying devices happens in a bounded native
        // helper rather than on the UI thread or inside Everything's process.
        var package = await Task.Run(() => PortablePackage(executable, componentRoot));
        if (!package.Files.ContainsKey("phoenix_cpp_devices.exe")) throw new InvalidDataException("The native device query is missing.");
        var start = new ProcessStartInfo(Path.Combine(package.Root, "phoenix_cpp_devices.exe")) {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(package.Root);
        using var process = Process.Start(start) ?? throw new IOException("Cannot start the native device query.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var output = process.StandardOutput.ReadToEndAsync(timeout.Token); var error = process.StandardError.ReadToEndAsync(timeout.Token);
        try { await process.WaitForExitAsync(timeout.Token); }
        catch { try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } throw; }
        var text = await output; _ = await error;
        if (process.ExitCode != 0 || text.Length > 262144) throw new InvalidDataException("The native device query failed.");
        return ParseDevices(text);
    }

    internal static IReadOnlyList<Device> ParseDevices(string text)
    {
        using var document = JsonDocument.Parse(text); var devices = new List<Device>();
        foreach (var item in document.RootElement.GetProperty("devices").EnumerateArray())
        {
            var id = item.GetProperty("id").GetString()!;
            try { (new PhoenixMediaRecipe { ExecutionDevice = id }).Validate(); }
            catch (InvalidDataException) { continue; }
            if (id == "native" || devices.Any(x => x.Id == id) ||
                (item.TryGetProperty("capable", out var capable) && !capable.GetBoolean())) continue;
            var backend = id == "cpu" ? "CPU" : id.Split(':')[0].ToUpperInvariant();
            devices.Add(new(id, backend + " · " + item.GetProperty("label").GetString(), item.TryGetProperty("vendor", out var vendor) ? vendor.GetInt32() : 0));
        }
        return devices;
    }
    internal static string RecipePath => Path.Combine(LocalData.DirectoryPath, "Media", "recipe.json");
    internal static string SelectionPath => Path.Combine(LocalData.DirectoryPath, "Media", "player.json");
    internal static string? Find(string componentRoot)
    {
        foreach (var candidate in new[] {
            Path.Combine(componentRoot, "Media", "PhoenixVideoPlayer.exe"),
            Path.Combine(AppContext.BaseDirectory, "Runtime", "media", "PhoenixVideoPlayer.exe") })
            if (File.Exists(candidate)) return candidate;
        if (!File.Exists(SelectionPath)) return null;
        try
        {
            var path = JsonSerializer.Deserialize<string>(LocalData.ReadText(SelectionPath, 32768));
            return path is not null && File.Exists(path) ? path : null;
        }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException) { return null; }
    }
    internal static void Select(string path)
    {
        ValidateExecutable(path);
        LocalData.WriteAtomic(SelectionPath, JsonSerializer.Serialize(Path.GetFullPath(path)));
    }
    internal static void ValidateExecutable(string path)
    {
        LocalPaths.RequireNoLinks(path);
        if (!File.Exists(path) || !Path.GetFileName(path).Equals("PhoenixVideoPlayer.exe", StringComparison.OrdinalIgnoreCase)
            || PeReader.GetArchitecture(path) != CpuArch.X64)
            throw new InvalidDataException("Choose the Windows x64 PhoenixVideoPlayer.exe build.");
    }
    internal static ProcessStartInfo StartInfo(string executable, string componentRoot,
        PhoenixMediaRecipe recipe, string? input = null, string? recipePath = null)
    {
        ValidateExecutable(executable); recipe.Validate();
        if (input is not null && !File.Exists(input)) throw new FileNotFoundException("Media file is missing.", input);
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(executable)! };
        info.Environment["PHOENIX_COMPONENT_ROOT"] = Path.GetFullPath(componentRoot);
        info.Environment["PHOENIX_MODEL_LIBRARY"] = OptionalModelLibrary.Root(componentRoot);
        info.Environment["PHOENIX_MEDIA_RECIPE"] = recipePath ?? RecipePath;
        if (input is not null) info.ArgumentList.Add(Path.GetFullPath(input));
        return info;
    }
    internal static void Launch(string executable, string componentRoot, PhoenixMediaRecipe recipe, string? input = null)
    {
        var document = BuildLaunchRecipe(executable, componentRoot, recipe);
        var text = document.ToJsonString(LocalData.Json);
        var immutablePath = PublishRecipe(text);
        var info = StartInfo(executable, componentRoot, recipe, input, immutablePath);
        LocalData.WriteAtomic(RecipePath, text);
        using var process = Process.Start(info) ?? throw new IOException("Phoenix Video Player could not start.");
    }
    internal static string PublishRecipe(string document, string? directory = null)
    {
        var bytes = Encoding.UTF8.GetBytes(document);
        if (bytes.Length > 262144) throw new InvalidDataException("Phoenix media recipe exceeds 256 KiB.");
        var folder = directory ?? Path.Combine(LocalData.DirectoryPath, "Media", "Recipes");
        LocalPaths.RequireNoLinks(folder); Directory.CreateDirectory(folder);
        var digest = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var path = Path.Combine(folder, digest + ".json"); LocalPaths.RequireNoLinks(path);
        var temporary = Path.Combine(folder, ".recipe-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { file.Write(bytes); file.Flush(true); }
            File.Move(temporary, path, overwrite: false);
        }
        catch (IOException) when (File.Exists(path))
        {
            if (LocalData.ReadText(path, 262144) != document)
                throw new InvalidDataException("An existing Phoenix recipe differs from its content identity.");
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        return path;
    }

    internal static JsonObject BuildLaunchRecipe(string executable, string componentRoot, PhoenixMediaRecipe recipe)
    {
        recipe.Validate();
        var manifest = new[] { Path.Combine(componentRoot, "Media", "phoenix-runtime.json"),
            Path.Combine(Path.GetDirectoryName(executable)!, "phoenix-runtime.json") }.FirstOrDefault(File.Exists)
            ?? throw new FileNotFoundException("The player's Phoenix runtime package is missing. Install its matching native SDK package.");
        LocalPaths.RequireNoLinks(manifest);
        var runtimePackage = JsonNode.Parse(LocalData.ReadText(manifest, 65536))!.AsObject();
        if (runtimePackage["schema"]?.GetValue<string>() != "phoenix-media-runtime/1")
            throw new InvalidDataException("Unsupported Phoenix media runtime package.");
        string Resolve(string key)
        {
            var name = runtimePackage[key]?.GetValue<string>() ?? throw new InvalidDataException("Missing Phoenix runtime path: " + key);
            var file = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(manifest)!, name));
            LocalPaths.RequireNoLinks(file); return file;
        }
        var library = Resolve("native_library");
        if (!File.Exists(library) || PeReader.GetArchitecture(library) != CpuArch.X64 ||
            OptionalModelLibrary.HashFile(library) != runtimePackage["native_library_sha256"]?.GetValue<string>())
            throw new InvalidDataException("Phoenix native library identity differs from its package.");
        var document = JsonSerializer.SerializeToNode(recipe.ToDocument(componentRoot))!.AsObject();
        document["native_library"] = library;
        document["native_library_sha256"] = runtimePackage["native_library_sha256"]!.DeepClone();
        var runtime = new JsonObject { ["bundle_path"] = Resolve("bundle_path") };
        var modelRoot = OptionalModelLibrary.Root(componentRoot);
        var installed = OptionalModelLibrary.Installed(modelRoot).FirstOrDefault(m => m.model == recipe.Model);
        if (installed is not null)
            runtime["weight_source"] = new JsonObject { ["kind"] = "model_folder", ["model"] = recipe.Model,
                ["path"] = Path.GetDirectoryName(OptionalModelLibrary.Contained(modelRoot, installed.manifest)) };
        else
        {
            var dll = PayloadScanner.Scan(componentRoot).NvidiaDlls.FirstOrDefault(p => Path.GetFileName(p).Equals("nvngx_dlssnr.dll", StringComparison.OrdinalIgnoreCase))
                ?? throw new FileNotFoundException("Install an Original/Lightweight native export or the NVIDIA neural model in Components first.");
            LocalPaths.RequireNoLinks(dll);
            runtime["weight_source"] = new JsonObject { ["kind"] = "nvidia_dll", ["model"] = recipe.Model,
                ["path"] = dll, ["sha256"] = OptionalModelLibrary.HashFile(dll) };
        }
        if (recipe.Scaler == 3)
        {
            runtime["tiny_model_path"] = TinyModelLibrary.Find(componentRoot) ?? throw new FileNotFoundException("Download the tiny 2x model in Components first.");
            document["output_scale"] = 2;
        }
        document["execution_devices"] = JsonSerializer.SerializeToNode(recipe.ExecutionDevices.Select(x => new { id = x.Id, label = x.Label }));
        if (runtimePackage["portable_runtime"] is not null)
        {
            var portable = PortablePackage(executable, componentRoot);
            var available = new JsonObject {
                ["runtime_root"] = portable.Root, ["runtime_sha256"] = portable.Files["phoenix_runtime_cpp.dll"]!.DeepClone(),
                ["files"] = portable.Files.DeepClone(), ["allow_unvalidated"] = true };
            document["portable_package"] = available;
            if (recipe.ExecutionDevice != "native")
            {
                runtime["portable_runtime"] = available.DeepClone();
                runtime["portable_runtime"]!["backend"] = recipe.ExecutionDevice == "cpu" ? "cpu" : recipe.ExecutionDevice.Split(':')[0];
                runtime["portable_runtime"]!["device"] = recipe.ExecutionDevice;
            }
        }
        else if (recipe.ExecutionDevice != "native") throw new InvalidDataException("Update the player package to use portable processing.");
        document["runtime"] = runtime; return document;
    }
}
