using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;

namespace Dlss5CompatApp;

internal enum PhoenixOperation { Status, Set, ResetHistory, Screenshot, Capture, Save, ValidateModel }
internal enum PhoenixSetting
{
    Backend, ExecutionDevice, WorkerEngine, OffloadAgeFrames, OffloadAgeMs, CpuThreads,
    ModelSource, ModelFolder, ModelDll, ResolutionMode, Upscaler, ResolutionQuality,
    ResolutionAspect, ResolutionRatioWidth, ResolutionRatioHeight, OverrideStrengths,
    Tone, Structure, Advanced, AutoMask, Skin, MotionX, MotionY, Depth, NativeOptions,
    Intensity, Style, UICorrection, Divider, NeuralUplift, HostExposure, HostExposureScale,
    HostGlobalTone, HostJitter, HostBridge, HostWhiteNits, HostHdr, NRAutoExposure, NrFeature,
    HostUnjitter, ResetAfterSwitch, ResetEvery, Experimental, TransformerBlocks,
    AutomaticBudget, BudgetMs, MinimumBlocks, BudgetMode, DesiredFPS, RefinementPasses,
    Block1Repeats, Block2Repeats, Block3Repeats, Block4Repeats, Block5Repeats, Block6Repeats,
    Block7Repeats, Block8Repeats, Comparison, SourceA, SourceB, DifferenceGain,
    ComparisonLabels, ComparisonLines, ForceReset, ExternalReload, LogLevel,
    ModelId, NvidiaModel, NvidiaDllFolder, ModelLibraryDirectory,
    MotionEstimation, MotionProvider, FlowAnalysisHeight, MotionDiagnosticView, FlowConsistencyPixels, FlowCostThreshold,
    SceneDetection, PipelineMonitoring, PipelinePriority, MonitoringSampleInterval, MaximumFrameAgeMs,
    AdjustMotionResolution, AdjustNeuralResolution, AdjustCentralBlocks, AdjustNetworkPasses,
    MinimumFlowHeight, MinimumNetworkPasses, MinimumNeuralScale, StartupRecovery,
    ProcessingChain, AdditionalScaler, ScalerSharpness, MultiscaleFusion, FusionLayers, FusionSecondScale, FusionThirdScale,
    ChainStage1, ChainStage2, ChainStage3, ChainStage4, ChainNeural, ChainScale, ChainColor, ChainFusion,
    CompareZoom, CompareLoupe, LoupeX, LoupeY, ScreenshotReceipt,
    PhoenixAppearance, PhoenixIntensity, PhoenixStyle, VsrQuality,
    ChainBrightness, ChainContrast, ChainSaturation, ChainGamma,
    ProcessingEnabled, HookStage, ReuseDlssUpscalingResources, ProcessingScale,
    FilterDomain, DownsampleFilter, UpsampleFilter, ScaleReconstruction, Encoding, SignalEncoding,
    DiffuseWhiteOverride, DiffuseWhiteNits, Exposure, ExposureMode, BlockHudless, UiCorrectionMode
}

internal sealed record PhoenixChoice(object Value, string Label)
{
    public override string ToString() => Label;
}

internal sealed record PhoenixSettingDefinition(PhoenixSetting Key, string Label, string Group,
    Type ValueType, decimal Minimum = 0, decimal Maximum = 0, IReadOnlyList<PhoenixChoice>? Choices = null)
{
    public string WireKey => Key.ToString();

    public void Validate(object value)
    {
        if (value.GetType() != ValueType)
            throw new ArgumentException($"{WireKey} requires {ValueType.Name}.");
        if (value is float number && (!float.IsFinite(number) || number < (float)Minimum || number > (float)Maximum) ||
            value is int integer && (integer < Minimum || integer > Maximum))
            throw new ArgumentOutOfRangeException(WireKey);
        if (Choices is not null && !Choices.Any(choice => Equals(choice.Value, value)))
            throw new ArgumentException($"Unsupported {WireKey} value.");
        if (value is string text)
        {
            if (text.Contains('\0') || text.Contains('\r') || text.Contains('\n') || Encoding.UTF8.GetByteCount(text) >= 4096)
                throw new ArgumentException($"Invalid {WireKey} string.");
            if (Key == PhoenixSetting.ExecutionDevice && !ValidDevice(text))
                throw new ArgumentException("ExecutionDevice requires a supported device ID, not a runner name.");
        }
    }

    internal static bool ValidDevice(string value) => value is "native" or "cpu" or "sycl:cpu" ||
        Regex.IsMatch(value, @"\A(?:vulkan:|hip:|sycl:)[0-9a-f]{32}\z", RegexOptions.CultureInvariant) ||
        Regex.IsMatch(value, @"\Acuda:GPU-[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}\z", RegexOptions.CultureInvariant);
}

internal static class PhoenixControlContract
{
    public const string Schema = "phoenix-control/1";
    // Wire names and bounds follow native/support/routing.h::visit_settings and Settings::valid.
    public static IReadOnlyList<PhoenixSettingDefinition> Settings { get; } = Build().AsReadOnly();
    public static PhoenixSettingDefinition Definition(PhoenixSetting key) =>
        Settings.FirstOrDefault(item => item.Key == key) ?? throw new ArgumentOutOfRangeException(nameof(key));

    static List<PhoenixSettingDefinition> Build()
    {
        var list = new List<PhoenixSettingDefinition>();
        void B(PhoenixSetting key, string label, string group) => list.Add(new(key, label, group, typeof(bool)));
        void N(PhoenixSetting key, string label, string group, decimal min, decimal max, bool integer = false) =>
            list.Add(new(key, label, group, integer ? typeof(int) : typeof(float), min, max));
        void C(PhoenixSetting key, string label, string group, int start, params string[] choices) =>
            list.Add(new(key, label, group, typeof(int), start, start + choices.Length - 1,
                Array.AsReadOnly(choices.Select((text, index) => new PhoenixChoice(start + index, text)).ToArray())));
        void S(PhoenixSetting key, string label, string group, params string[] choices) =>
            list.Add(new(key, label, group, typeof(string), Choices: choices.Length == 0 ? null :
                Array.AsReadOnly(choices.Select(text => new PhoenixChoice(text, text switch
                {
                    "" or "configured" => "Use the game's configured model", "original" => "Original", "small-fp8" => "Lightweight FP8", "model_folder" => "Model folder on disk", "nvidia_dll" => "NVIDIA model DLL",
                    "auto" => "Automatic", "portable" => "Portable kernels", "matrix" => "Matrix kernels", "fp8" => "FP8 kernels", _ => text
                })).ToArray())));

        C(PhoenixSetting.Backend, "Enhancement engine", "Rendering", 0, "Input (NR bypass)", "NVIDIA DLSS5 DLL", "Input | NVIDIA DLSS5 | Phoenix DLSS5", "Phoenix DLSS5");
        S(PhoenixSetting.ExecutionDevice, "Processing device", "Rendering");
        S(PhoenixSetting.WorkerEngine, "Execution backend", "Rendering", "auto", "portable", "matrix", "fp8");
        B(PhoenixSetting.NeuralUplift, "Neural rendering", "Rendering");
        B(PhoenixSetting.OverrideStrengths, "Override strengths", "Rendering");
        N(PhoenixSetting.Tone, "Local tone", "Rendering", 0, 2);
        N(PhoenixSetting.Structure, "Local structure", "Rendering", 0, 2);
        B(PhoenixSetting.Advanced, "Customize skin / guides", "Rendering");
        B(PhoenixSetting.AutoMask, "Automatic skin mask", "Rendering");
        N(PhoenixSetting.Skin, "Skin structure", "Rendering", -1, 2);
        N(PhoenixSetting.MotionX, "Motion X scale", "Rendering", -4, 4);
        N(PhoenixSetting.MotionY, "Motion Y scale", "Rendering", -4, 4);
        C(PhoenixSetting.Depth, "Depth", "Rendering", -1, "Game depth", "Normal depth", "Inverted depth");
        B(PhoenixSetting.HostUnjitter, "Unjitter input", "Rendering");
        B(PhoenixSetting.PhoenixAppearance, "Experimental appearance controls", "Phoenix appearance");
        N(PhoenixSetting.PhoenixIntensity, "Intensity", "Phoenix appearance", 0, 2);
        C(PhoenixSetting.PhoenixStyle, "Style", "Phoenix appearance", 0, "Default", "Natural", "Cinematic");
        C(PhoenixSetting.Comparison, "Comparison view", "Comparison", 0, "Three-way", "Pair wipe", "Absolute RGB", "Signed RGB", "Heatmap", "Luminance difference");
        C(PhoenixSetting.SourceA, "Source A", "Comparison", 0, "Input", "NVIDIA DLSS5", "Phoenix DLSS5");
        C(PhoenixSetting.SourceB, "Source B", "Comparison", 0, "Input", "NVIDIA DLSS5", "Phoenix DLSS5");
        N(PhoenixSetting.Divider, "Split position", "Comparison", .1m, .9m);
        N(PhoenixSetting.DifferenceGain, "Difference gain", "Comparison", 1, 64);
        B(PhoenixSetting.ComparisonLabels, "Labels", "Comparison");
        B(PhoenixSetting.ComparisonLines, "Separator lines", "Comparison");
        S(PhoenixSetting.ModelSource, "Model source", "Execution", "configured", "model_folder", "nvidia_dll");
        S(PhoenixSetting.ModelFolder, "Model folder", "Execution");
        S(PhoenixSetting.ModelDll, "Model DLL", "Execution");
        N(PhoenixSetting.CpuThreads, "CPU threads (0 = automatic)", "Execution", 0, 65535, true);
        N(PhoenixSetting.OffloadAgeFrames, "Maximum offload age (frames)", "Execution", 1, 6000, true);
        N(PhoenixSetting.OffloadAgeMs, "Maximum offload age (ms)", "Execution", 1, 120000, true);
        C(PhoenixSetting.ResolutionMode, "Resolution mode", "Execution", 0, "Normal", "Super resolution");
        C(PhoenixSetting.Upscaler, "Upscaler", "Execution", 0, "Spatial", "NVIDIA DLSS SR", "Intel XeSS");
        C(PhoenixSetting.ResolutionQuality, "Processing quality", "Execution", 0, "Quality (2/3 scale)", "Performance (1/2 scale)", "Ultra performance (1/3 scale)");
        C(PhoenixSetting.ResolutionAspect, "Aspect ratio", "Execution", 0, "Game", "16:9", "16:10", "21:9", "4:3", "Custom");
        N(PhoenixSetting.ResolutionRatioWidth, "Custom ratio width", "Execution", 1, 100, true);
        N(PhoenixSetting.ResolutionRatioHeight, "Custom ratio height", "Execution", 1, 100, true);
        B(PhoenixSetting.NativeOptions, "NVIDIA DLSS5-only overrides", "NVIDIA DLSS5");
        N(PhoenixSetting.Intensity, "Intensity", "NVIDIA DLSS5", 0, 2);
        C(PhoenixSetting.Style, "Style", "NVIDIA DLSS5", 0, "Default", "Natural", "Cinematic");
        C(PhoenixSetting.UICorrection, "UI correction", "NVIDIA DLSS5", 0, "Off", "On");
        N(PhoenixSetting.HostExposure, "Host exposure", "NVIDIA DLSS5", .05m, 4);
        N(PhoenixSetting.HostExposureScale, "Host exposure scale", "NVIDIA DLSS5", .05m, 4);
        N(PhoenixSetting.HostGlobalTone, "Host global tone", "NVIDIA DLSS5", 0, 2);
        C(PhoenixSetting.HostJitter, "Host jitter", "NVIDIA DLSS5", 0, "Off", "On");
        C(PhoenixSetting.HostBridge, "HDR bridge", "NVIDIA DLSS5", 0, "Off", "On");
        N(PhoenixSetting.HostWhiteNits, "White level (nits)", "NVIDIA DLSS5", 1, 10000);
        C(PhoenixSetting.HostHdr, "Host domain", "NVIDIA DLSS5", 0, "SDR", "HDR");
        B(PhoenixSetting.NRAutoExposure, "NR automatic exposure", "NVIDIA DLSS5");
        list.Add(new(PhoenixSetting.NrFeature, "NR feature", "NVIDIA DLSS5", typeof(int), 13, 18,
            Array.AsReadOnly(new[] { new PhoenixChoice(13, "13 - Ray Reconstruction (legacy)"), new PhoenixChoice(18, "18 - Neural Rendering") })));
        N(PhoenixSetting.ResetAfterSwitch, "Reset after switch", "History / Budget", 1, 120, true);
        N(PhoenixSetting.ResetEvery, "Reset interval (0 = off)", "History / Budget", 0, 600, true);
        B(PhoenixSetting.ForceReset, "Force reset", "History / Budget");
        B(PhoenixSetting.Experimental, "Experimental network controls", "History / Budget");
        N(PhoenixSetting.TransformerBlocks, "Transformer blocks", "History / Budget", 1, 8, true);
        N(PhoenixSetting.MinimumBlocks, "Minimum blocks", "History / Budget", 1, 8, true);
        B(PhoenixSetting.AutomaticBudget, "Automatic budget", "History / Budget");
        N(PhoenixSetting.BudgetMs, "GPU budget (ms)", "History / Budget", .1m, 100);
        C(PhoenixSetting.BudgetMode, "Budget target", "History / Budget", 0, "Phoenix DLSS5 GPU milliseconds", "Game presentation FPS");
        N(PhoenixSetting.DesiredFPS, "Desired presentation FPS", "History / Budget", 10, 360);
        N(PhoenixSetting.RefinementPasses, "Refinement passes", "History / Budget", 1, 4, true);
        for (int index = 0; index < 8; index++)
            N((PhoenixSetting)((int)PhoenixSetting.Block1Repeats + index), $"Block {index + 1} repeats", "History / Budget", 0, 4, true);
        B(PhoenixSetting.ExternalReload, "External reload", "History / Budget");
        C(PhoenixSetting.LogLevel, "Logging", "History / Budget", 0, "Quiet", "Normal", "Verbose");
        S(PhoenixSetting.ModelId,"Neural model","Execution","","original","small-fp8");
        C(PhoenixSetting.NvidiaModel,"NVIDIA model selection","NVIDIA DLSS5",-1,"Match Phoenix","DLL default","Original","Lightweight FP8");
        S(PhoenixSetting.NvidiaDllFolder,"Shared NVIDIA library folder","Execution");
        S(PhoenixSetting.ModelLibraryDirectory,"Shared native model library","Execution");
        B(PhoenixSetting.MotionEstimation,"Estimate missing motion","Motion");
        C(PhoenixSetting.MotionProvider,"Motion source","Motion",0,"Automatic (prefer engine)","Engine vectors","NVIDIA Optical Flow","Zero motion");
        N(PhoenixSetting.FlowAnalysisHeight,"Motion analysis height","Motion",64,2160,true);
        C(PhoenixSetting.MotionDiagnosticView,"Motion display","Motion",0,"Processed image","Motion vectors","Motion confidence");
        N(PhoenixSetting.FlowConsistencyPixels,"Motion consistency tolerance (pixels)","Motion",.1m,20);
        N(PhoenixSetting.FlowCostThreshold,"Motion cost threshold (per mille)","Motion",1,1000);
        B(PhoenixSetting.SceneDetection,"Detect scene changes","Motion");
        C(PhoenixSetting.PipelineMonitoring,"Pipeline monitoring","Pipeline budget",0,"Off","Basic","Detailed sampled GPU stages");
        C(PhoenixSetting.PipelinePriority,"Processing priority","Pipeline budget",0,"Keep quality fixed","Prefer responsiveness","Prefer image quality");
        N(PhoenixSetting.MonitoringSampleInterval,"GPU timing sample interval","Pipeline budget",1,600,true);
        N(PhoenixSetting.MaximumFrameAgeMs,"Maximum frame age (ms)","Pipeline budget",1,1000);
        B(PhoenixSetting.AdjustMotionResolution,"Allow motion resolution adjustment","Pipeline budget");
        B(PhoenixSetting.AdjustNeuralResolution,"Allow neural scale adjustment","Pipeline budget");
        B(PhoenixSetting.AdjustCentralBlocks,"Allow central block adjustment","Pipeline budget");
        B(PhoenixSetting.AdjustNetworkPasses,"Allow whole-network pass adjustment","Pipeline budget");
        N(PhoenixSetting.MinimumFlowHeight,"Minimum motion height","Pipeline budget",64,2160,true);
        N(PhoenixSetting.MinimumNetworkPasses,"Minimum whole-network passes","Pipeline budget",1,10,true);
        N(PhoenixSetting.MinimumNeuralScale,"Minimum neural scale (%)","Pipeline budget",10,100);
        B(PhoenixSetting.StartupRecovery,"Restore last working settings after failure","Pipeline budget");
        B(PhoenixSetting.ProcessingChain,"Enable processing chain","Processing chain");
        list.Add(new(PhoenixSetting.AdditionalScaler,"Additional upscaler","Processing chain",typeof(int),0,3,
            [new(0,"Existing scaler"),new(1,"NVIDIA Image Scaling"),new(3,"Tiny native 2x")]));
        N(PhoenixSetting.ScalerSharpness,"Scaling sharpness","Processing chain",0,1);
        B(PhoenixSetting.MultiscaleFusion,"Experimental multiscale fusion","Processing chain");
        N(PhoenixSetting.FusionLayers,"Fusion layers","Processing chain",2,3,true);
        N(PhoenixSetting.FusionSecondScale,"Second layer scale","Processing chain",.1m,1);
        N(PhoenixSetting.FusionThirdScale,"Third layer scale","Processing chain",.1m,1);
        foreach(var key in new[]{PhoenixSetting.ChainStage1,PhoenixSetting.ChainStage2,PhoenixSetting.ChainStage3,PhoenixSetting.ChainStage4})
            C(key,"Stage "+(key-PhoenixSetting.ChainStage1+1),"Processing chain",0,"Neural","Scale","Color","Fusion");
        B(PhoenixSetting.ChainNeural,"Neural stage","Processing chain");B(PhoenixSetting.ChainScale,"Scaling stage","Processing chain");
        B(PhoenixSetting.ChainColor,"Color stage","Processing chain");B(PhoenixSetting.ChainFusion,"Fusion stage","Processing chain");
        N(PhoenixSetting.CompareZoom,"Comparison zoom","Comparison",1,8);
        B(PhoenixSetting.CompareLoupe,"Comparison loupe","Comparison");
        N(PhoenixSetting.LoupeX,"Loupe horizontal position","Comparison",0,1);
        N(PhoenixSetting.LoupeY,"Loupe vertical position","Comparison",0,1);
        B(PhoenixSetting.ScreenshotReceipt,"Save screenshot settings receipt","Comparison");
        C(PhoenixSetting.VsrQuality,"Video super resolution quality","Processing chain",1,"1","2","3","4");
        N(PhoenixSetting.ChainBrightness,"Brightness","Processing chain",-2,2);
        N(PhoenixSetting.ChainContrast,"Contrast","Processing chain",0,4);
        N(PhoenixSetting.ChainSaturation,"Saturation","Processing chain",0,2);
        N(PhoenixSetting.ChainGamma,"Gamma","Processing chain",.1m,4);
        B(PhoenixSetting.ProcessingEnabled,"Explicit processing pipeline","Image processing");
        C(PhoenixSetting.HookStage,"Processing stage","Image processing",0,"Off","Render (before SR/RR)","Upscaled","Present");
        C(PhoenixSetting.ReuseDlssUpscalingResources,"Reuse upscaling guides at Present","Image processing",0,"Never","If available","Required");
        N(PhoenixSetting.ProcessingScale,"Processing scale (%)","Image processing",0,200);
        C(PhoenixSetting.FilterDomain,"Filter domain","Image processing",0,"scRGB nonlinear","Signed PQ","Linear");
        C(PhoenixSetting.DownsampleFilter,"Downsample filter","Image processing",0,"Area","Bilinear","Nearest");
        C(PhoenixSetting.UpsampleFilter,"Upsample filter","Image processing",0,"Bilinear","Bicubic","Nearest");
        C(PhoenixSetting.ScaleReconstruction,"Scale reconstruction","Image processing",0,"RGB residual","Processed image");
        C(PhoenixSetting.Encoding,"Input encoding","Image processing",0,"Automatic","Linear Rec.709","sRGB","HDR10","scRGB","scRGB nonlinear");
        C(PhoenixSetting.SignalEncoding,"Signal encoding","Image processing",0,"sRGB","PQ","Linear");
        B(PhoenixSetting.DiffuseWhiteOverride,"Override diffuse white","Image processing");
        N(PhoenixSetting.DiffuseWhiteNits,"Diffuse white (nits)","Image processing",48,500);
        N(PhoenixSetting.Exposure,"Exposure","Image processing",0,100);
        C(PhoenixSetting.ExposureMode,"Exposure mode","Image processing",0,"Off","On","Inverse");
        B(PhoenixSetting.BlockHudless,"Block HUD-less processing","Image processing");
        C(PhoenixSetting.UiCorrectionMode,"UI correction policy","Image processing",0,"Automatic","Off","On");
        return list;
    }
}

internal sealed class PhoenixControlSettings
{
    readonly Dictionary<string, object> values = new(StringComparer.Ordinal);
    public PhoenixControlSettings Set(PhoenixSetting key, bool value) => Put(key, value);
    public PhoenixControlSettings Set(PhoenixSetting key, int value) => Put(key, value);
    public PhoenixControlSettings Set(PhoenixSetting key, float value) => Put(key, value);
    public PhoenixControlSettings Set(PhoenixSetting key, string value) => Put(key, value);
    PhoenixControlSettings Put(PhoenixSetting key, object value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var definition = PhoenixControlContract.Definition(key);
        definition.Validate(value);
        values[definition.WireKey] = value;
        return this;
    }
    internal Dictionary<string, object> Copy()
    {
        if (values.Count == 0) throw new ArgumentException("At least one setting is required.");
        if (values.TryGetValue("MinimumBlocks", out var minimum) && values.TryGetValue("TransformerBlocks", out var blocks) && (int)minimum > (int)blocks)
            throw new ArgumentException("MinimumBlocks cannot exceed TransformerBlocks.");
        return new(values, StringComparer.Ordinal);
    }
}

internal sealed record PhoenixControlResponse(string Session, int Pid, string Id, bool Ok,
    string Phase, string? Error, JsonElement Snapshot, string? ScreenshotPostfix);
internal sealed record PhoenixProcessIdentity(int Pid, ulong CreationFileTime, string ImagePath);
internal sealed class PhoenixSessionChangedException(string message) : IOException(message);
internal sealed class PhoenixProcessExitedException() : IOException("Phoenix DLSS5 process has exited.");

internal sealed class PhoenixControlClient
{
    readonly string expectedDirectory;
    readonly string gameDirectory;
    readonly string? expectedImage;
    readonly Func<int, PhoenixProcessIdentity> inspectProcess;
    readonly Func<ulong> ticks;
    readonly string mutexName;
    public string BridgeDirectory { get; }
    internal string GameDirectory => gameDirectory;

    internal bool CanEditOffline()
    {
        if (!File.Exists(Path.Combine(BridgeDirectory, "endpoint.json"))) return true;
        try { ReadEndpoint(); return false; }
        catch (PhoenixSessionChangedException) { return true; }
        catch (IOException)
        {
            try
            {
                var endpoint = ReadJson(Path.Combine(BridgeDirectory, "endpoint.json"), 16384);
                using var process = Process.GetProcessById(endpoint.GetProperty("pid").GetInt32());
                return process.HasExited;
            }
            catch (ArgumentException) { return true; }
            catch { return false; }
        }
        catch { return false; }
    }

    public PhoenixControlClient(string directory, string? expectedProcessPath = null)
        : this(directory, expectedProcessPath, InspectProcess, GetTickCount64) { }

    internal PhoenixControlClient(string directory, string expectedProcessPath, Process ownedProcess)
        : this(directory, expectedProcessPath,
            pid => pid == ownedProcess.Id ? InspectProcessHandle(ownedProcess.SafeHandle, pid) : InspectProcess(pid),
            GetTickCount64) { }

    internal PhoenixControlClient(string directory, string? expectedProcessPath,
        Func<int, PhoenixProcessIdentity> inspectProcess, Func<ulong> ticks)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        var directBridge = Path.GetFileName(root).Equals("bridge", StringComparison.OrdinalIgnoreCase) || File.Exists(Path.Combine(root, "endpoint.json"));
        expectedDirectory = directBridge ? Path.GetFullPath(Path.Combine(root, "..", "..")) : root;
        gameDirectory = expectedDirectory;
        expectedImage = expectedProcessPath is null ? null : Path.GetFullPath(expectedProcessPath);
        if (directBridge) BridgeDirectory = root;
        else
        {
            var configPath = Path.Combine(root, "Phoenix.json");
            var sandbox = root;
            if (File.Exists(configPath))
            {
                var config = ReadJson(configPath, 65536);
                if (config.TryGetProperty("sandbox", out var configured))
                {
                    var value = configured.GetString();
                    if (string.IsNullOrWhiteSpace(value)) throw new InvalidDataException("Phoenix DLSS5 sandbox is empty.");
                    sandbox = Path.GetFullPath(value, root);
                }
            }
            BridgeDirectory = Path.Combine(sandbox, "PhoenixOutput", "bridge");
            expectedDirectory = sandbox;
        }
        if (BridgeDirectory.StartsWith(@"\\", StringComparison.Ordinal) || new Uri(BridgeDirectory).IsUnc)
            throw new ArgumentException("Phoenix DLSS5 control requires a local mailbox.");
        this.inspectProcess = inspectProcess;
        this.ticks = ticks;
        mutexName = @"Local\PhoenixControl-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(BridgeDirectory.ToUpperInvariant())));
    }

    public Task<PhoenixControlResponse> StatusAsync(CancellationToken cancellationToken = default) => SendAsync(PhoenixOperation.Status, cancellationToken: cancellationToken);
    public Task<PhoenixControlResponse> SetAsync(PhoenixControlSettings settings, CancellationToken cancellationToken = default) => SendAsync(PhoenixOperation.Set, settings, cancellationToken: cancellationToken);
    public Task<PhoenixControlResponse> SendAsync(PhoenixOperation operation, PhoenixControlSettings? settings = null,
        int? frames = null, TimeSpan? timeout = null, CancellationToken cancellationToken = default, string? expectedSession = null)
    {
        var duration = timeout ?? TimeSpan.FromSeconds(5);
        if (duration.TotalMilliseconds < 1 || duration.TotalMilliseconds > 30000 || !double.IsFinite(duration.TotalMilliseconds))
            throw new ArgumentOutOfRangeException(nameof(timeout), "Bridge deadline must be 1..30000 ms.");
        if (!Enum.IsDefined(operation)) throw new ArgumentOutOfRangeException(nameof(operation));
        if ((operation == PhoenixOperation.Set) != (settings is not null)) throw new ArgumentException("Settings are required only for set.");
        if ((operation == PhoenixOperation.Capture) != frames.HasValue || frames is < 1 or > 8)
            throw new ArgumentOutOfRangeException(nameof(frames), "Capture requires 1..8 frames, exclusively.");
        var edits = settings?.Copy();
        var watch = Stopwatch.StartNew();
        // A Windows mutex is thread-affine. Keep acquisition, polling and release on this worker.
        return Task.Run(() => Exchange(operation, edits, frames, duration, watch, cancellationToken, expectedSession), cancellationToken);
    }

    PhoenixControlResponse Exchange(PhoenixOperation operation, Dictionary<string, object>? settings,
        int? frames, TimeSpan timeout, Stopwatch watch, CancellationToken cancellationToken, string? expectedSession)
    {
        void Check()
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (watch.Elapsed >= timeout) throw new TimeoutException("Phoenix DLSS5 mailbox timed out; a queued command may still execute before its expiry.");
        }
        void Pause()
        {
            Check();
            cancellationToken.WaitHandle.WaitOne(Math.Max(1, (int)Math.Min(25, (timeout - watch.Elapsed).TotalMilliseconds)));
            Check();
        }
        using var mutex = new Mutex(false, mutexName);
        bool acquired = false;
        try
        {
            while (!acquired)
            {
                Check();
                try { acquired = mutex.WaitOne(0); }
                catch (AbandonedMutexException) { acquired = true; }
                if (!acquired) Pause();
            }
            var endpoint = ReadEndpoint();
            if (expectedSession is not null && endpoint.Session != expectedSession)
                throw new PhoenixSessionChangedException("Phoenix DLSS5 session changed since the settings were observed; refresh before applying edits.");
            void VerifySession()
            {
                var current = ReadEndpoint();
                if (current != endpoint) throw new PhoenixSessionChangedException("Phoenix DLSS5 session changed during the command; reconnect.");
            }
            var requestPath = Path.Combine(BridgeDirectory, "request.json");
            var processingPath = Path.Combine(BridgeDirectory, "processing.json");
            while (File.Exists(requestPath) || File.Exists(processingPath))
            {
                VerifySession();
                RetireExitedRequest(requestPath, endpoint);
                RetireExitedRequest(processingPath, endpoint);
                if (!File.Exists(requestPath) && !File.Exists(processingPath)) break;
                Pause();
            }
            Check();
            VerifySession();
            var id = Guid.NewGuid().ToString("N");
            var request = new Dictionary<string, object>
            {
                ["schema"] = PhoenixControlContract.Schema, ["session"] = endpoint.Session,
                ["pid"] = endpoint.Pid, ["id"] = id, ["op"] = WireOperation(operation),
                ["expires_tick_ms"] = checked(ticks() + (ulong)Math.Max(1, Math.Floor((timeout - watch.Elapsed).TotalMilliseconds)))
            };
            if (settings is not null) request["settings"] = settings;
            if (frames.HasValue) request["frames"] = frames.Value;
            var bytes = JsonSerializer.SerializeToUtf8Bytes(request);
            if (bytes.Length > 65536) throw new InvalidDataException("Bridge request exceeds 64 KiB.");
            var temporary = Path.Combine(BridgeDirectory, ".request-" + id + ".tmp");
            try
            {
                using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    file.Write(bytes);
                    file.Flush(true);
                }
                Check();
                VerifySession();
                File.Move(temporary, requestPath, false);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
            var responsePath = Path.Combine(BridgeDirectory, "response.json");
            while (true)
            {
                Check();
                VerifySession();
                if (File.Exists(responsePath))
                {
                    var response = ReadJson(responsePath, 1024 * 1024);
                    if (response.TryGetProperty("id", out var responseId) && responseId.ValueKind == JsonValueKind.String && responseId.GetString() == id)
                    {
                        if (response.GetProperty("schema").GetString() != PhoenixControlContract.Schema ||
                            response.GetProperty("session").GetString() != endpoint.Session || response.GetProperty("pid").GetInt32() != endpoint.Pid)
                            throw new PhoenixSessionChangedException("Phoenix DLSS5 response identity does not match the connected process.");
                        var snapshot = response.GetProperty("snapshot");
                        if (snapshot.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Phoenix DLSS5 snapshot must be an object.");
                        Check();
                        VerifySession();
                        return new(endpoint.Session, endpoint.Pid, id, response.GetProperty("ok").GetBoolean(),
                            response.GetProperty("phase").GetString() ?? "", OptionalString(response, "error"), snapshot.Clone(), OptionalString(response, "screenshot_postfix"));
                    }
                }
                Pause();
            }
        }
        finally { if (acquired) mutex.ReleaseMutex(); }
    }

    sealed record Endpoint(string Session, int Pid);
    void RetireExitedRequest(string path, Endpoint endpoint)
    {
        if (!File.Exists(path)) return;
        // A killed game can leave its atomic claim behind. Expiry alone is not
        // permission to remove a live server's request: prove the old process
        // exited (or its PID was reused), then recheck the current endpoint.
        try
        {
            var pending = ReadJson(path, 65536);
            if (pending.GetProperty("schema").GetString() != PhoenixControlContract.Schema) return;
            var oldPid = pending.GetProperty("pid").GetInt32();
            var oldSession = pending.GetProperty("session").GetString();
            if (oldPid <= 0 || oldSession is null || oldSession == endpoint.Session) return;
            var parts = oldSession.Split('-');
            if (parts.Length < 2 || !ulong.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var created) ||
                !SessionMatchesProcess(oldSession, oldPid.ToString(CultureInfo.InvariantCulture) + "-" + created.ToString(CultureInfo.InvariantCulture))) return;
            bool exited;
            try
            {
                var identity = inspectProcess(oldPid);
                var processSession = oldPid.ToString(CultureInfo.InvariantCulture) + "-" + identity.CreationFileTime.ToString(CultureInfo.InvariantCulture);
                exited = identity.Pid == oldPid && !SessionMatchesProcess(oldSession, processSession);
            }
            catch (PhoenixProcessExitedException) { exited = true; }
            if (!exited) return;
            if (ReadEndpoint() != endpoint) throw new PhoenixSessionChangedException("Phoenix DLSS5 session changed during stale mailbox recovery.");
            // Keep the evidence, and never overwrite another retained request.
            File.Move(path, path + ".retired-" + Guid.NewGuid().ToString("N"), false);
        }
        catch (Exception error) when (error is FileNotFoundException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            // Unknown/malformed files are left alone; do not infer ownership.
        }
    }
    Endpoint ReadEndpoint()
    {
        var endpoint = ReadJson(Path.Combine(BridgeDirectory, "endpoint.json"), 16384);
        if (endpoint.GetProperty("schema").GetString() != PhoenixControlContract.Schema)
            throw new InvalidDataException("Unsupported Phoenix DLSS5 endpoint schema.");
        var pid = endpoint.GetProperty("pid").GetInt32();
        if (pid <= 0) throw new InvalidDataException("Invalid Phoenix DLSS5 endpoint PID.");
        var session = endpoint.GetProperty("session").GetString();
        var identity = inspectProcess(pid);
        var expectedSession = pid.ToString(CultureInfo.InvariantCulture) + "-" + identity.CreationFileTime.ToString(CultureInfo.InvariantCulture);
        if (identity.Pid != pid || !SessionMatchesProcess(session, expectedSession))
            throw new PhoenixSessionChangedException("Stale Phoenix DLSS5 endpoint: process creation time does not match the session.");
        var image = Path.GetFullPath(identity.ImagePath);
        if (expectedImage is not null ? !image.Equals(expectedImage, StringComparison.OrdinalIgnoreCase) :
            !image.StartsWith(Path.TrimEndingDirectorySeparator(expectedDirectory) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Phoenix DLSS5 endpoint belongs to a process outside the expected game path. Supply its exact executable path for an external sandbox.");
        return new(session!, pid);
    }

    internal static bool SessionMatchesProcess(string? session, string processIdentity)
    {
        if (session == processIdentity) return true; // Original native-game producer.
        if (session is null || session.Length > 128 || !session.StartsWith(processIdentity + "-", StringComparison.Ordinal)) return false;
        // Compatibility runtimes append HWND and creation tick so a device/runtime
        // reset changes the session without weakening the PID/creation-time check.
        var generation = session[(processIdentity.Length + 1)..].Split('-');
        return generation.Length == 2 && generation.All(value =>
            ulong.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) &&
            value == number.ToString(CultureInfo.InvariantCulture));
    }

    internal static string WireOperation(PhoenixOperation operation) => operation switch
    {
        PhoenixOperation.Status => "status", PhoenixOperation.Set => "set", PhoenixOperation.ResetHistory => "reset_history",
        PhoenixOperation.Screenshot => "screenshot", PhoenixOperation.Capture => "capture", PhoenixOperation.Save => "save",
        PhoenixOperation.ValidateModel => "validate_model", _ => throw new ArgumentOutOfRangeException(nameof(operation))
    };
    static string? OptionalString(JsonElement root, string key) => root.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    internal static JsonElement ReadJson(string path, int limit)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (file.Length > limit) throw new InvalidDataException($"Phoenix DLSS5 JSON exceeds {limit} bytes.");
        var bytes = new byte[limit + 1];
        int read = 0, count;
        while (read < bytes.Length && (count = file.Read(bytes, read, bytes.Length - read)) != 0) read += count;
        if (read > limit) throw new InvalidDataException($"Phoenix DLSS5 JSON exceeds {limit} bytes.");
        using var document = JsonDocument.Parse(bytes.AsMemory(0, read));
        if (document.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Phoenix DLSS5 JSON must be an object.");
        return document.RootElement.Clone();
    }

    internal static PhoenixProcessIdentity InspectProcess(int pid)
    {
        using var process = OpenProcess(0x1000, false, pid);
        if (process.IsInvalid)
        {
            if (Marshal.GetLastWin32Error() == 87) throw new PhoenixProcessExitedException();
            throw new IOException("Phoenix DLSS5 process identity cannot be read.");
        }
        return InspectProcessHandle(process, pid);
    }

    static PhoenixProcessIdentity InspectProcessHandle(SafeProcessHandle process, int pid)
    {
        // CreateProcess grants the launcher a handle before a game can tighten
        // its own process DACL. Reuse that handle for read-only identity checks;
        // never relax the PID, creation-time, liveness or exact-image checks.
        if (!GetProcessTimes(process, out var created, out _, out _, out _) || !GetExitCodeProcess(process, out var exitCode))
            throw new IOException("Phoenix DLSS5 process has exited or its identity cannot be read.");
        if (exitCode != 259) throw new PhoenixProcessExitedException();
        var image = new StringBuilder(32768);
        int length = image.Capacity;
        if (!QueryFullProcessImageName(process, 0, image, ref length)) throw new IOException("Phoenix DLSS5 process path cannot be read.");
        return new(pid, ((ulong)created.High << 32) | created.Low, image.ToString());
    }
    [StructLayout(LayoutKind.Sequential)]
    struct FileTime { public uint Low; public uint High; }
    [DllImport("kernel32.dll")] internal static extern ulong GetTickCount64();
    [DllImport("kernel32.dll", SetLastError = true)] static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int pid);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool GetProcessTimes(SafeProcessHandle process, out FileTime created, out FileTime exited, out FileTime kernel, out FileTime user);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool GetExitCodeProcess(SafeProcessHandle process, out uint exitCode);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, StringBuilder image, ref int length);
}

// The runtime loads [Phoenix] from ReShade's INI; Phoenix.json describes launch/model paths.
// Offline edits patch only changed INI keys and never regenerate the launch configuration.
internal sealed class PhoenixOfflineControls
{
    internal const int MaximumConfigurationBytes = 256 * 1024;
    readonly string directory;
    Dictionary<string, byte[]?> originals = new(StringComparer.OrdinalIgnoreCase);
    Dictionary<string, object>? loadedValues;
    public PhoenixOfflineControls(string directory) => this.directory = LocalPaths.Normalize(Path.GetFullPath(directory));

    public JsonElement Read()
    {
        using var gate = LocalData.Lock(directory);
        using var directories = PinDirectories();
        var observed = new Dictionary<string, byte[]?>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in new[] { "ReShade.ini", "ReShadeVR.ini" })
        {
            var path = Path.Combine(directory, name);
            using var file = OpenConfiguration(path, writable: false);
            observed[path] = file is null ? null : ReadBounded(file);
        }
        var values = Defaults();
        var warnings = new List<string>();
        // Keep VR as a fallback; the ordinary runtime's existing values take precedence.
        foreach (var file in observed.Reverse().Where(file => file.Value is not null))
            foreach (var pair in ParseSection(Decode(file.Value!).Text))
            {
                var definition = PhoenixControlContract.Settings.FirstOrDefault(item => item.WireKey.Equals(pair.Key, StringComparison.OrdinalIgnoreCase));
                if (definition is null) continue;
                try
                {
                    object value = definition.ValueType == typeof(string) ? pair.Value :
                        definition.ValueType == typeof(bool) ? pair.Value switch { "1" => true, "0" => false, _ => bool.Parse(pair.Value) } :
                        definition.ValueType == typeof(int) ? (object)int.Parse(pair.Value, CultureInfo.InvariantCulture) :
                        float.Parse(pair.Value, CultureInfo.InvariantCulture);
                    definition.Validate(value);
                    values[definition.WireKey] = value;
                }
                catch (Exception exception) when (exception is ArgumentException or FormatException or OverflowException)
                {
                    warnings.Add(Path.GetFileName(file.Key) + ": invalid " + pair.Key + " retained on disk");
                }
            }
        originals = observed;
        loadedValues = values;
        return JsonSerializer.SerializeToElement(new
        {
            settings = values,
            execution_devices = new[] { new { id = "native", label = "Rendering GPU (optimized NVIDIA)" }, new { id = "cpu", label = "CPU" }, new { id = "sycl:cpu", label = "SYCL CPU" } },
            offline_warnings = warnings, scope = "Offline configuration; runtime status unavailable"
        });
    }

    public void Apply(PhoenixControlSettings settings, Func<bool> canEditOffline)
        => Apply(settings, canEditOffline, beforeWrite: null);

    // The optional callback allows CPU tests to inject a failure after an earlier write.
    internal void Apply(PhoenixControlSettings settings, Func<bool> canEditOffline, Action<string>? beforeWrite)
    {
        using var gate = LocalData.Lock(directory);
        using var directories = PinDirectories();
        var edits = settings.Copy();
        var merged = new Dictionary<string, object>(loadedValues ?? throw new InvalidOperationException("Read the existing controls before editing."));
        foreach (var edit in edits) merged[edit.Key] = edit.Value;
        if ((int)merged["MinimumBlocks"] > (int)merged["TransformerBlocks"])
            throw new ArgumentException("MinimumBlocks cannot exceed TransformerBlocks.");
        if (!canEditOffline()) throw new IOException("A live Phoenix DLSS5 endpoint is available. Refresh and apply to the connected session.");
        var locked = new Dictionary<string, FileStream?>(StringComparer.OrdinalIgnoreCase);
        var pending = new List<PendingWrite>();
        var changed = new List<PendingWrite>();
        try
        {
            foreach (var path in originals.Keys) LocalPaths.RequireNoLinks(path);
            // Keep every existing destination locked until commit or rollback has finished.
            foreach (var (path, original) in originals)
            {
                var file = OpenConfiguration(path, writable: true);
                locked.Add(path, file);
                ValidateOriginal(path, file, original);
            }
            var targets = originals.Any(file => file.Value is not null)
                ? originals.Keys.Where(path => originals[path] is not null).ToArray()
                : new[] { Path.Combine(directory, "ReShade.ini") };
            foreach (var path in targets)
            {
                var original = originals[path];
                var (text, encoding) = Decode(original ?? []);
                var body = encoding.GetBytes(Patch(text, edits));
                var bytes = encoding.GetPreamble().Concat(body).ToArray();
                if (bytes.Length > MaximumConfigurationBytes)
                    throw new InvalidDataException("Phoenix DLSS5 configuration exceeds its size limit.");
                var temporary = Path.Combine(directory, ".phoenix-settings-" + Guid.NewGuid().ToString("N") + ".tmp");
                var stage = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, bufferSize: 1);
                pending.Add(new(path, temporary, stage, original));
                stage.Write(bytes);
                stage.Flush(true);
            }
            if (!canEditOffline()) throw new IOException("Phoenix DLSS5 started while preparing offline edits. Refresh before applying.");
            foreach (var (path, file) in locked) ValidateOriginal(path, file, originals[path]);
            foreach (var item in pending)
            {
                beforeWrite?.Invoke(item.Path);
                var destination = locked[item.Path];
                if (destination is null)
                {
                    // CREATE_NEW refuses a file supplied by an outside writer after validation.
                    destination = OpenConfiguration(item.Path, writable: true, createNew: true)!;
                    locked[item.Path] = destination;
                }
                changed.Add(item);
                // Replace contents through the held handle: releasing it to rename a staged file
                // would let an outside writer slip between validation, replacement and rollback.
                item.Stage.Position = 0;
                destination.Position = 0;
                item.Stage.CopyTo(destination);
                destination.SetLength(item.Stage.Length);
                destination.Flush(true);
            }
        }
        catch (Exception failure)
        {
            var errors = new List<Exception>();
            foreach (var item in changed.AsEnumerable().Reverse())
            {
                try
                {
                    var destination = locked[item.Path]!;
                    if (item.Original is null)
                    {
                        var disposition = new FileDisposition { DeleteFile = true };
                        if (!SetFileInformationByHandle(destination.SafeFileHandle, 4, ref disposition, (uint)Marshal.SizeOf<FileDisposition>()))
                            throw FileError("Could not remove newly created Phoenix DLSS5 configuration");
                    }
                    else
                    {
                        destination.Position = 0;
                        destination.Write(item.Original);
                        destination.SetLength(item.Original.Length);
                        destination.Flush(true);
                    }
                }
                catch (Exception rollbackFailure) { errors.Add(rollbackFailure); }
            }
            if (errors.Count > 0)
                throw new IOException("Phoenix DLSS5 settings failed and rollback could not restore every original configuration.",
                    new AggregateException(new[] { failure }.Concat(errors)));
            throw;
        }
        finally
        {
            foreach (var file in locked.Values) file?.Dispose();
            foreach (var item in pending)
            {
                item.Stage.Dispose();
                // A transient cleanup lock must not report a committed edit as a failed transaction.
                try { File.Delete(item.Temporary); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }

    sealed record PendingWrite(string Path, string Temporary, FileStream Stage, byte[]? Original);

    static void ValidateOriginal(string path, FileStream? file, byte[]? original)
    {
        LocalPaths.RequireNoLinks(path);
        if (file is null ? original is not null || File.Exists(path) || Directory.Exists(path) :
            original is null || !ReadBounded(file).AsSpan().SequenceEqual(original))
            throw new IOException(Path.GetFileName(path) + " changed on disk. Refresh before applying.");
    }

    static byte[] ReadBounded(FileStream file)
    {
        file.Position = 0;
        if (file.Length > MaximumConfigurationBytes)
            throw new InvalidDataException("Phoenix DLSS5 configuration exceeds its size limit.");
        var bytes = new byte[checked((int)file.Length)];
        file.ReadExactly(bytes);
        if (file.ReadByte() != -1) throw new InvalidDataException("Phoenix DLSS5 configuration grew beyond its size limit.");
        return bytes;
    }

    static (string Text, Encoding Encoding) Decode(byte[] bytes)
    {
        using var reader = new StreamReader(new MemoryStream(bytes), new UTF8Encoding(false), detectEncodingFromByteOrderMarks: true);
        var text = reader.ReadToEnd();
        return (text, reader.CurrentEncoding);
    }

    DirectoryPins PinDirectories()
    {
        var pins = new DirectoryPins();
        try
        {
            var paths = new Stack<string>();
            for (string? path = directory; path is not null; path = Path.GetDirectoryName(path)) paths.Push(path);
            while (paths.TryPop(out var path))
            {
                // No delete sharing pins every ancestor against rename/reparse substitution.
                var handle = CreateFile(path, 0, FileShare.ReadWrite, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
                pins.Handles.Add(handle);
                ValidateHandle(handle, directory: true);
            }
            return pins;
        }
        catch { pins.Dispose(); throw; }
    }

    static FileStream? OpenConfiguration(string path, bool writable, bool createNew = false)
    {
        LocalPaths.RequireNoLinks(path);
        var access = writable ? 0xC0000000u : 0x80000000u;
        if (createNew) access |= 0x00010000; // DELETE access permits rollback without releasing the lock.
        var handle = CreateFile(path, access, writable ? FileShare.None : FileShare.Read, IntPtr.Zero,
            createNew ? 1u : 3u, 0x00200000, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            if (!createNew && error == 2) return null;
            throw new IOException("Could not lock Phoenix DLSS5 configuration: " + path, new System.ComponentModel.Win32Exception(error));
        }
        try
        {
            ValidateHandle(handle, directory: false);
            return new FileStream(handle, writable ? FileAccess.ReadWrite : FileAccess.Read, bufferSize: 1);
        }
        catch { handle.Dispose(); throw; }
    }

    static void ValidateHandle(SafeFileHandle handle, bool directory)
    {
        if (handle.IsInvalid || !GetFileInformationByHandleEx(handle, 9, out var information, (uint)Marshal.SizeOf<FileAttributeTag>()))
            throw FileError("Could not inspect Phoenix DLSS5 configuration path");
        if ((information.Attributes & FileAttributes.ReparsePoint) != 0 ||
            ((information.Attributes & FileAttributes.Directory) != 0) != directory)
            throw new IOException("Phoenix DLSS5 configuration paths cannot be reparse points or an unexpected file type.");
    }

    static IOException FileError(string message) => new(message, new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()));

    sealed class DirectoryPins : IDisposable
    {
        internal readonly List<SafeFileHandle> Handles = [];
        public void Dispose() { foreach (var handle in Handles.AsEnumerable().Reverse()) handle.Dispose(); }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct FileAttributeTag { public FileAttributes Attributes; public uint ReparseTag; }
    [StructLayout(LayoutKind.Sequential)]
    struct FileDisposition { [MarshalAs(UnmanagedType.Bool)] public bool DeleteFile; }
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern SafeFileHandle CreateFile(string name, uint access, FileShare share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int informationClass, out FileAttributeTag information, uint size);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool SetFileInformationByHandle(SafeFileHandle handle, int informationClass, ref FileDisposition information, uint size);

    static Dictionary<string, string> ParseSection(string text)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        bool phoenix = false;
        using var reader = new StringReader(text);
        while (reader.ReadLine() is { } line)
        {
            line = line.Trim();
            if (line.StartsWith('[')) { phoenix = line.Equals("[Phoenix]", StringComparison.OrdinalIgnoreCase); continue; }
            if (!phoenix || line.StartsWith(';') || line.StartsWith('#')) continue;
            int equals = line.IndexOf('=');
            if (equals > 0) result[line[..equals].Trim()] = line[(equals + 1)..].Trim();
        }
        return result;
    }

    static string Patch(string text, Dictionary<string, object> edits)
    {
        var newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var remaining = new Dictionary<string, object>(edits, StringComparer.OrdinalIgnoreCase);
        var replacements = new Dictionary<string, object>(edits, StringComparer.OrdinalIgnoreCase);
        var result = new StringBuilder();
        bool phoenix = false, found = false;
        string Wire(object value) => value switch { bool flag => flag ? "1" : "0", float number => number.ToString("R", CultureInfo.InvariantCulture), _ => Convert.ToString(value, CultureInfo.InvariantCulture)! };
        void AppendMissing()
        {
            if (remaining.Count == 0) return;
            if (result.Length > 0 && result[^1] != '\n') result.Append(newline);
            foreach (var edit in remaining) result.Append(edit.Key).Append('=').Append(Wire(edit.Value)).Append(newline);
            remaining.Clear();
        }
        foreach (Match match in Regex.Matches(text, @"[^\r\n]*(?:\r\n|\n|\r|$)"))
        {
            if (match.Length == 0) continue;
            var line = match.Value;
            var trimmed = line.Trim();
            if (trimmed.StartsWith('['))
            {
                if (phoenix) AppendMissing();
                phoenix = trimmed.Equals("[Phoenix]", StringComparison.OrdinalIgnoreCase);
                found |= phoenix;
            }
            int equals = line.IndexOf('=');
            if (phoenix && equals > 0 && replacements.TryGetValue(line[..equals].Trim(), out var value))
            {
                var key = line[..equals].Trim();
                result.Append(line.AsSpan(0, equals + 1)).Append(Wire(value));
                if (line.EndsWith("\r\n", StringComparison.Ordinal)) result.Append("\r\n");
                else if (line.EndsWith('\n')) result.Append('\n');
                else if (line.EndsWith('\r')) result.Append('\r');
                remaining.Remove(key);
            }
            else result.Append(line);
        }
        if (!found)
        {
            if (result.Length > 0 && result[^1] != '\n') result.Append(newline);
            result.Append("[Phoenix]").Append(newline);
        }
        AppendMissing();
        return result.ToString();
    }

    static Dictionary<string, object> Defaults()
    {
        var result = PhoenixControlContract.Settings.ToDictionary(item => item.WireKey, item =>
            item.ValueType == typeof(bool) ? (object)false : item.ValueType == typeof(string) ? "" :
            item.ValueType == typeof(int) ? (object)(int)item.Minimum : (float)item.Minimum);
        void Set(object value, params PhoenixSetting[] keys) { foreach (var key in keys) result[key.ToString()] = value; }
        Set(3, PhoenixSetting.Backend);
        Set("native", PhoenixSetting.ExecutionDevice);
        Set("auto", PhoenixSetting.WorkerEngine);
        Set("configured", PhoenixSetting.ModelSource);
        Set(4, PhoenixSetting.OffloadAgeFrames);
        Set(100, PhoenixSetting.OffloadAgeMs);
        Set(16, PhoenixSetting.ResolutionRatioWidth);
        Set(9, PhoenixSetting.ResolutionRatioHeight);
        Set(1f, PhoenixSetting.Tone, PhoenixSetting.Structure, PhoenixSetting.MotionX, PhoenixSetting.MotionY,
            PhoenixSetting.Intensity, PhoenixSetting.HostExposure, PhoenixSetting.HostExposureScale, PhoenixSetting.HostGlobalTone, PhoenixSetting.DifferenceGain);
        Set(2f / 3f, PhoenixSetting.Divider);
        Set(true, PhoenixSetting.NeuralUplift, PhoenixSetting.NRAutoExposure, PhoenixSetting.ComparisonLabels, PhoenixSetting.ComparisonLines);
        Set(1, PhoenixSetting.HostJitter, PhoenixSetting.HostBridge, PhoenixSetting.ResetAfterSwitch,
            PhoenixSetting.MinimumBlocks, PhoenixSetting.RefinementPasses, PhoenixSetting.SourceA, PhoenixSetting.LogLevel,
            PhoenixSetting.Block1Repeats, PhoenixSetting.Block2Repeats, PhoenixSetting.Block3Repeats, PhoenixSetting.Block4Repeats,
            PhoenixSetting.Block5Repeats, PhoenixSetting.Block6Repeats, PhoenixSetting.Block7Repeats, PhoenixSetting.Block8Repeats);
        Set(18, PhoenixSetting.NrFeature);
        Set(500f, PhoenixSetting.HostWhiteNits);
        Set(8, PhoenixSetting.TransformerBlocks);
        Set(5f, PhoenixSetting.BudgetMs);
        Set(60f, PhoenixSetting.DesiredFPS);
        Set(2, PhoenixSetting.SourceB);
        Set(1f, PhoenixSetting.PhoenixIntensity, PhoenixSetting.ChainContrast, PhoenixSetting.ChainSaturation, PhoenixSetting.ChainGamma, PhoenixSetting.Exposure);
        Set(2, PhoenixSetting.VsrQuality, PhoenixSetting.HookStage);
        Set(1, PhoenixSetting.ReuseDlssUpscalingResources, PhoenixSetting.ExposureMode);
        Set(100f, PhoenixSetting.ProcessingScale, PhoenixSetting.DiffuseWhiteNits);
        Set(true, PhoenixSetting.BlockHudless);
        return result;
    }
}
