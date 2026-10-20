using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Dlss5CompatApp;

public sealed record PhoenixSettings
{
    public bool Enabled { get; init; } = true;
    public string Runner { get; init; } = "native";
    public string ModelId { get; init; } = "original";
    public bool CopyNvidiaDllToGame { get; init; } = true;
    public string? NvidiaDllFolder { get; init; }
    public string? ModelLibraryDirectory { get; init; }
    public string? ExecutionDeviceId { get; init; }
    public string CompatibilityExecutionMode { get; init; } = "auto";
    // Optional addon-relative or absolute locations. Null preserves the game's
    // configured location; an explicit missing folder must not select a default.
    public string? NativeRuntimePath { get; init; }
    public string? PortableRuntimePath { get; init; }
    public string? PortableDevice { get; init; }
    public bool? PortableAllowUnvalidated { get; init; }
    // Feeder evaluations, not Phoenix's separate 1..4 RefinementPasses.
    public int Iterations { get; init; } = 1;
    public int RefinementPasses { get; init; } = 1;
    public int Comparison { get; init; }
    public int SourceA { get; init; }
    public int SourceB { get; init; } = 2;
    public float Intensity { get; init; } = 1;
    public float Divider { get; init; } = 0.5f;
    public bool OverrideStrengths { get; init; }
    public float LocalTone { get; init; } = 1;
    public float LocalStructure { get; init; } = 1;
    public bool Advanced { get; init; }
    public float SkinStructure { get; init; } = -1;
    public bool AutoMask { get; init; }
    public float MotionX { get; init; } = 1;
    public float MotionY { get; init; } = 1;
    public int Depth { get; init; } = -1;
    public int CpuThreads { get; init; }
    public int ResolutionMode { get; init; }
    public int ResolutionQuality { get; init; }
    public int ResolutionAspect { get; init; }
    public int ResolutionRatioWidth { get; init; } = 16;
    public int ResolutionRatioHeight { get; init; } = 9;
    public int Upscaler { get; init; }
    public float DifferenceGain { get; init; } = 1;
    public bool ComparisonLabels { get; init; } = true;
    public bool ComparisonLines { get; init; } = true;
    public string DirectTransfer { get; init; } = "srgb";
    public string? BundlePath { get; init; }
    public string? RuntimeRoot { get; init; }
    public int FrameLimit { get; init; } = 4000000;
}

public static class PhoenixConfiguration
{
    public const string FileName = "Phoenix.json";
    public const string GameInputProvider = "game_dlss";
    public const string FeederInputProvider = "feeder_d3d12";
    public static IReadOnlyList<string> Runners { get; } =
        Array.AsReadOnly(new[] { "native", "nvidia", "cuda", "vulkan", "hip", "cpu", "sycl" });
    public static IReadOnlyList<string> ExecutionModes { get; } =
        Array.AsReadOnly(new[] { "auto", "in_process", "helper", "portable", "wow64_library", "wow64_driver" });

    internal static bool RequiresNativeNr(PhoenixSettings settings) => settings.Runner == "nvidia" ||
        (settings.Comparison != 0 && (settings.SourceA == 1 || settings.SourceB == 1));

    public static Dictionary<string, object?> Create(string directory, string modelPath, PhoenixSettings? settings = null,
        string inputProvider = GameInputProvider)
    {
        settings ??= new();
        Validate(settings);
        if (inputProvider is not (GameInputProvider or FeederInputProvider)) throw new ArgumentException("Unknown Phoenix DLSS5 input provider.", nameof(inputProvider));
        directory = Path.GetFullPath(directory);
        modelPath = Path.GetFullPath(modelPath);
        var folder = Directory.Exists(modelPath);
        if (folder ? !File.Exists(Path.Combine(modelPath, "model.json")) : !File.Exists(modelPath))
            throw new FileNotFoundException("Phoenix DLSS5 needs an existing model DLL or a folder containing model.json.", modelPath);
        if (folder)
        {
            using var manifest = JsonDocument.Parse(LocalData.ReadText(Path.Combine(modelPath, "model.json"), 65536));
            if (manifest.RootElement.TryGetProperty("model", out var model) && model.GetString() != settings.ModelId)
                throw new InvalidDataException("The selected model folder does not contain the selected neural model.");
        }
        var source = new Dictionary<string, object?> { ["kind"] = folder ? "model_folder" : "nvidia_dll", ["path"] = modelPath };
        source["model"] = settings.ModelId;
        if (!folder)
        {
            using var file = File.OpenRead(modelPath);
            source["sha256"] = Convert.ToHexString(SHA256.HashData(file)).ToLowerInvariant();
        }
        var result = new Dictionary<string, object?>
        {
            ["schema"] = "phoenix-live/1", ["armed"] = settings.Enabled,
            ["input_provider"] = inputProvider, ["direct_transfer"] = settings.DirectTransfer,
            ["sandbox"] = directory, ["in_place_game_directory"] = directory,
            ["in_place_approval"] = "I approve driving this installed game in place",
            ["weight_source"] = source, ["frame_limit"] = settings.FrameLimit,
            ["color_domain"] = "game_nr_sdr_proxy", ["require_gpu_ready_file"] = false
        };
        // Embedded production addons replace these paths with their extracted C++ runtime.
        // Development addons require the caller to supply the matching compiled bundle/runtime.
        if (!string.IsNullOrWhiteSpace(settings.BundlePath)) result["bundle"] = Path.GetFullPath(settings.BundlePath);
        if (!string.IsNullOrWhiteSpace(settings.RuntimeRoot))
            result["portable_cpp"] = new Dictionary<string, string> { ["runtime_root"] = Path.GetFullPath(settings.RuntimeRoot) };
        if (!string.IsNullOrWhiteSpace(settings.NvidiaDllFolder)) result["nvidia_dll_folder"] = Path.GetFullPath(settings.NvidiaDllFolder);
        if (!string.IsNullOrWhiteSpace(settings.ModelLibraryDirectory)) result["model_library_directory"] = Path.GetFullPath(settings.ModelLibraryDirectory);
        return result;
    }

    public static string Write(string directory, string modelPath, PhoenixSettings? settings = null,
        string inputProvider = GameInputProvider)
    {
        settings ??= new();
        var path = WriteSidecar(directory, modelPath, settings, inputProvider);
        directory = Path.GetDirectoryName(path)!;
        foreach (var iniName in new[] { "ReShade.ini", "ReShadeVR.ini" })
            foreach (var pair in Controls(settings))
                IniEditor.SetValue(Path.Combine(directory, iniName), "Phoenix", pair.Key, pair.Value);
        // Feeder configuration is owned and journaled separately by its caller.
        return path;
    }

    internal static string WriteSidecar(string directory, string modelPath, PhoenixSettings settings,
        string inputProvider = GameInputProvider)
    {
        directory = Path.GetFullPath(directory);
        ValidateWritePaths(directory);
        var config = Create(directory, modelPath, settings, inputProvider);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, FileName);
        File.WriteAllText(path, JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true }));
        return path;
    }

    internal static void ValidateWritePaths(string directory)
    {
        // Check every configuration path before the first write, including a legacy feed path.
        LocalPaths.RequireNoLinks(directory);
        foreach (var name in new[] { FileName, "ReShade.ini", "ReShadeVR.ini", "dlss5-feed.cfg" })
        {
            var target = Path.Combine(directory, name);
            LocalPaths.RequireNoLinks(target);
            if (Directory.Exists(target)) throw new IOException("Configuration target is a directory: " + target);
        }
    }

    public static IReadOnlyDictionary<string, string> Controls(PhoenixSettings settings)
    {
        Validate(settings);
        return new Dictionary<string, string>
        {
            ["Backend"] = settings.Enabled ? settings.Runner == "nvidia" ? "1" : "3" : "0",
            ["ExecutionDevice"] = Device(settings), ["ModelSource"] = "configured",
            ["ModelId"] = settings.ModelId, ["NvidiaModel"] = "-1",
            ["NvidiaDllFolder"] = settings.NvidiaDllFolder ?? "",
            ["ModelLibraryDirectory"] = settings.ModelLibraryDirectory ?? "",
            ["NeuralUplift"] = settings.Enabled ? "1" : "0",
            ["Intensity"] = settings.Intensity.ToString("R", CultureInfo.InvariantCulture),
            ["Comparison"] = settings.Comparison.ToString(CultureInfo.InvariantCulture),
            ["SourceA"] = settings.SourceA.ToString(CultureInfo.InvariantCulture),
            ["SourceB"] = settings.SourceB.ToString(CultureInfo.InvariantCulture),
            ["Divider"] = settings.Divider.ToString("R", CultureInfo.InvariantCulture),
            ["ComparisonLabels"] = settings.ComparisonLabels ? "1" : "0",
            ["ComparisonLines"] = settings.ComparisonLines ? "1" : "0",
            ["OverrideStrengths"] = settings.OverrideStrengths ? "1" : "0",
            ["Tone"] = settings.LocalTone.ToString("R", CultureInfo.InvariantCulture),
            ["Structure"] = settings.LocalStructure.ToString("R", CultureInfo.InvariantCulture),
            ["Advanced"] = settings.Advanced ? "1" : "0",
            ["Skin"] = settings.SkinStructure.ToString("R", CultureInfo.InvariantCulture),
            ["AutoMask"] = settings.AutoMask ? "1" : "0",
            ["MotionX"] = settings.MotionX.ToString("R", CultureInfo.InvariantCulture),
            ["MotionY"] = settings.MotionY.ToString("R", CultureInfo.InvariantCulture),
            ["Depth"] = settings.Depth.ToString(CultureInfo.InvariantCulture),
            ["CpuThreads"] = settings.CpuThreads.ToString(CultureInfo.InvariantCulture),
            ["ResolutionQuality"] = settings.ResolutionQuality.ToString(CultureInfo.InvariantCulture),
            ["ResolutionAspect"] = settings.ResolutionAspect.ToString(CultureInfo.InvariantCulture),
            ["ResolutionRatioWidth"] = settings.ResolutionRatioWidth.ToString(CultureInfo.InvariantCulture),
            ["ResolutionRatioHeight"] = settings.ResolutionRatioHeight.ToString(CultureInfo.InvariantCulture),
            ["DifferenceGain"] = settings.DifferenceGain.ToString("R", CultureInfo.InvariantCulture),
            ["RefinementPasses"] = settings.RefinementPasses.ToString(CultureInfo.InvariantCulture),
            ["ResolutionMode"] = settings.ResolutionMode.ToString(CultureInfo.InvariantCulture),
            ["Upscaler"] = settings.Upscaler.ToString(CultureInfo.InvariantCulture), ["ExternalReload"] = "0"
        };
    }

    static void Validate(PhoenixSettings settings)
    {
        if (!Runners.Contains(settings.Runner, StringComparer.Ordinal)) throw new ArgumentException("Unknown Phoenix DLSS5 runner.");
        if (!ExecutionModes.Contains(settings.CompatibilityExecutionMode, StringComparer.Ordinal)) throw new ArgumentException("Unknown Phoenix DLSS5 execution mechanism.");
        if (settings.ModelId is not ("original" or "small-fp8")) throw new ArgumentException("Unknown neural model.");
        if (settings.ModelLibraryDirectory is { Length: > 0 } library &&
            (!Path.IsPathFullyQualified(library) || library.Length >= 4096 || library.IndexOfAny(['\0', '\r', '\n']) >= 0))
            throw new ArgumentException("The shared model library must be an absolute path.");
        if (settings.NvidiaDllFolder is { Length: > 0 } folder &&
            (!Path.IsPathFullyQualified(folder) || folder.Length >= 4096 || folder.IndexOfAny(['\0', '\r', '\n']) >= 0))
            throw new ArgumentException("The NVIDIA DLL search folder must be an absolute path.");
        if (settings.Iterations is < 1 or > 10 || settings.RefinementPasses is < 1 or > 4)
            throw new ArgumentOutOfRangeException(nameof(settings), "Iterations must be 1..10; RefinementPasses must be 1..4.");
        if (settings.Comparison is < 0 or > 5 || settings.SourceA is < 0 or > 2 || settings.SourceB is < 0 or > 2 ||
            !float.IsFinite(settings.Intensity) || settings.Intensity is < 0 or > 2 ||
            !float.IsFinite(settings.Divider) || settings.Divider is < 0.1f or > 0.9f || settings.FrameLimit is < 1 or > 4000000)
            throw new ArgumentOutOfRangeException(nameof(settings), "Invalid Phoenix DLSS5 controls or frame limit.");
        if (settings.DirectTransfer is not ("linear" or "srgb")) throw new ArgumentException("DirectTransfer must be linear or srgb.");
        if (!float.IsFinite(settings.LocalTone) || settings.LocalTone is < 0 or > 2 ||
            !float.IsFinite(settings.LocalStructure) || settings.LocalStructure is < 0 or > 2 ||
            !float.IsFinite(settings.SkinStructure) || settings.SkinStructure is < -1 or > 2 ||
            !float.IsFinite(settings.MotionX) || settings.MotionX is < -4 or > 4 ||
            !float.IsFinite(settings.MotionY) || settings.MotionY is < -4 or > 4 || settings.Depth is < -1 or > 1 ||
            settings.CpuThreads is < 0 or > 65535 || settings.ResolutionMode is < 0 or > 1 ||
            settings.ResolutionQuality is < 0 or > 2 || settings.ResolutionAspect is < 0 or > 5 ||
            settings.ResolutionRatioWidth is < 1 or > 100 || settings.ResolutionRatioHeight is < 1 or > 100 ||
            settings.Upscaler is < 0 or > 2 || !float.IsFinite(settings.DifferenceGain) || settings.DifferenceGain is < 1 or > 64)
            throw new ArgumentOutOfRangeException(nameof(settings), "Invalid Phoenix DLSS5 strength, CPU, resolution or comparison setting.");
        _ = Device(settings);
    }

    static string Device(PhoenixSettings settings)
    {
        if (settings.Runner is "native" or "nvidia") return "native";
        if (settings.Runner == "cpu") return "cpu";
        var id = settings.ExecutionDeviceId ?? "";
        var valid = settings.Runner == "cuda"
            ? Regex.IsMatch(id, "^cuda:GPU-[0-9a-f]{8}(-[0-9a-f]{4}){3}-[0-9a-f]{12}$")
            : Regex.IsMatch(id, "^" + settings.Runner + ":[0-9a-f]{32}$") || (settings.Runner == "sycl" && id == "sycl:cpu");
        if (!valid) throw new ArgumentException("Choose a concrete Phoenix DLSS5 ExecutionDeviceId for " + settings.Runner + ".");
        return id;
    }
}
