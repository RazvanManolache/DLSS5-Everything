using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace Dlss5CompatApp;

static class DosGameLauncher
{
    static readonly string[] SupportedExtensions = [".exe", ".com", ".bat"];

    public sealed record DosLaunchSession(Process Process, string Runtime, string ConfigPath, string TargetPath);

    internal static int PrepareFromArgs(string[] args)
    {
        try
        {
            if (args.Length != 6 || args[2] != "--payload" || args[4] != "--output")
                throw new ArgumentException("Usage: --prepare-dos <program> --payload <folder> --output <session.json>");
            var target = ResolveDosTarget(Path.GetFullPath(args[1]));
            var payload = PayloadScanner.Scan(Path.GetFullPath(args[3]));
            ValidatePayload(payload);
            var runtime = PrepareRuntime(payload);
            var profile = WriteGameConfig(runtime, target);
            File.WriteAllText(Path.GetFullPath(args[5]), JsonSerializer.Serialize(new
            {
                schema = "dlss5-dos-session/1", target, runtime, profile,
                executable = Path.Combine(runtime, "app", "DosboxEnhanced.exe"),
                installation = "DOSBox Enhanced built-in renderer; no ReShade injection",
                process_started = false
            }));
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine("DOS preparation failed: " + error.Message);
            return 1;
        }
    }

    public static bool IsDosLaunchCommand(string[] args)
    {
        if (args.Length >= 2 && args[0].Equals("--dos", StringComparison.OrdinalIgnoreCase))
            return IsSupportedDosTarget(args[1]);
        return args.Length == 1 && IsSupportedDosTarget(args[0]);
    }

    public static Task<int> RunFromArgsAsync(string[] args)
    {
        try
        {
            var requested = Path.GetFullPath(args[0].Equals("--dos", StringComparison.OrdinalIgnoreCase) ? args[1] : args[0]);
            var payloadRoot = Path.Combine(AppContext.BaseDirectory, "Payload");
            Launch(requested, payloadRoot, allowUpdate: true, new Progress<BootstrapProgress>(_ => { }), CancellationToken.None);
            return Task.FromResult(0);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "DOS DLSS5 launch failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return Task.FromResult(1);
        }
    }

    public static bool IsSupportedDosTargetPath(string path) => IsSupportedDosTarget(path);

    public static string ResolveDosTargetPath(string requested) => ResolveDosTarget(Path.GetFullPath(requested));

    public static DosLaunchSession Launch(
        string requested,
        string payloadRoot,
        bool allowUpdate,
        IProgress<BootstrapProgress>? progress,
        CancellationToken cancellationToken,
        int frameIterations = 1,
        PhoenixSettings? phoenixSettings = null)
    {
        var target = ResolveDosTarget(Path.GetFullPath(requested));
        var payload = PayloadScanner.Scan(payloadRoot);
        if (!CanLaunchDos(payload) && allowUpdate)
        {
            try
            {
                PayloadBootstrapper.EnsureCurrentAsync(payloadRoot, progress ?? new Progress<BootstrapProgress>(_ => { }), cancellationToken)
                    .GetAwaiter()
                    .GetResult();
            }
            catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.Forbidden)
            {
                payload = PayloadScanner.Scan(payloadRoot);
                if (!CanLaunchDos(payload))
                    throw new InvalidOperationException("GitHub rate limit blocked the payload update, and the local Payload folder is still missing files needed for DOS launch: " + MissingPayloadText(payload));
            }
        }

        payload = PayloadScanner.Scan(payloadRoot);
        if (allowUpdate)
            DosGraphicsSetup.EnsureAsync(payload,message => progress?.Report(new BootstrapProgress(message,96)),cancellationToken)
                .GetAwaiter().GetResult();
        ValidatePayload(payload);

        var settings = phoenixSettings ?? new PhoenixSettings { Iterations = Math.Clamp(frameIterations, 1, 10) };
        var runtime = PrepareRuntime(payload, settings);
        var configPath = WriteGameConfig(runtime, target);
        var process = Process.Start(CreateStartInfo(runtime, configPath))
            ?? throw new InvalidOperationException("DOSBox Enhanced did not start.");
        return new DosLaunchSession(process, runtime, configPath, target);
    }

    public static void CleanupSession(DosLaunchSession session)
    {
        TryDelete(session.ConfigPath);
    }

    static bool IsSupportedDosFile(string path) =>
        File.Exists(path) &&
        SupportedExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    static bool IsSupportedDosTarget(string path) =>
        IsSupportedDosFile(path) || Directory.Exists(path);

    static string ResolveDosTarget(string requested)
    {
        if (IsSupportedDosFile(requested))
            return requested;
        if (!Directory.Exists(requested))
            throw new FileNotFoundException("DOS game file or folder was not found.", requested);

        var candidates = Directory.EnumerateFiles(requested, "*", SearchOption.TopDirectoryOnly)
            .Where(IsSupportedDosFile)
            .Select(path => new { Path = path, Score = DosEntryScore(path) })
            .Where(x => x.Score < 10_000)
            .OrderBy(x => x.Score)
            .ThenBy(x => x.Path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (candidates.Length == 0)
            throw new InvalidOperationException("That folder does not contain a DOS .exe, .com, or .bat entry point.");

        return candidates[0].Path;
    }

    static int DosEntryScore(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        if (ToolOrSetupName(name)) return 10_000;

        var score = ext.Equals(".bat", StringComparison.OrdinalIgnoreCase) ? 0 :
                    ext.Equals(".com", StringComparison.OrdinalIgnoreCase) ? 20 : 40;
        if (name.Equals("start", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("run", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("play", StringComparison.OrdinalIgnoreCase))
            score -= 10;
        return score;
    }

    static bool ToolOrSetupName(string name)
    {
        var lower = name.ToLowerInvariant();
        return lower.Contains("setup", StringComparison.Ordinal) ||
               lower.Contains("install", StringComparison.Ordinal) ||
               lower.Contains("uninst", StringComparison.Ordinal) ||
               lower.Contains("config", StringComparison.Ordinal) ||
               lower.Contains("sound", StringComparison.Ordinal) ||
               lower.Contains("setsound", StringComparison.Ordinal) ||
               lower.Contains("readme", StringComparison.Ordinal);
    }

    static bool CanLaunchDos(PayloadInfo payload) => MissingPayloadText(payload).Length == 0;

    internal static string PackageRoot(string executable) =>
        Path.GetDirectoryName(Path.GetDirectoryName(Path.GetFullPath(executable)))!;

    internal static string MissingPayloadText(PayloadInfo payload)
    {
        var missing = new List<string>();
        if (payload.DosBoxEnhancedExe is null || !File.Exists(payload.DosBoxEnhancedExe) ||
            !Path.GetFileName(payload.DosBoxEnhancedExe).Equals("DosboxEnhanced.exe", StringComparison.OrdinalIgnoreCase))
            missing.Add("DOSBox Enhanced portable package (build external/dosbox-enhanced or update components)");
        else
        {
            var root = PackageRoot(payload.DosBoxEnhancedExe);
            foreach (var relative in RequiredRuntimeFiles)
            {
                var file = Path.Combine(root, relative);
                if (!File.Exists(file) || PeReader.GetArchitecture(file) != CpuArch.X64)
                    missing.Add("DOSBox Enhanced " + relative + " (Windows x64)");
            }
            if (PeReader.GetArchitecture(payload.DosBoxEnhancedExe) != CpuArch.X64)
                missing.Add("DOSBox Enhanced Windows x64 application");
            if (!File.Exists(Path.Combine(root, "runtime", "native", "native_manifest.json")))
                missing.Add("DOSBox Enhanced compiled native RTX kernel bundle");
            // The current Enhanced bridge initializes Streamline/NGX even with SR/FG
            // disabled. Keep their complete signed runtime set installed by its setup tool.
            foreach (var name in DosGraphicsSetup.Missing(root))
                missing.Add("DOSBox Enhanced runtime/nvidia/" + name + " (run its Setup graphics command)");
        }
        if (!payload.HasCoreDlss) missing.Add("separately supplied nvngx_dlssnr.dll model");
        return string.Join(", ", missing);
    }

    static readonly string[] RequiredRuntimeFiles =
        ["runtime/dosbox_pure_libretro.dll", "runtime/dosbox_nvngx.dll", "runtime/phoenix_native.dll"];

    static void ValidatePayload(PayloadInfo payload)
    {
        var missing = MissingPayloadText(payload);
        if (missing.Length > 0) throw new InvalidOperationException("Payload is missing: " + missing);
    }

    internal static string PrepareRuntime(PayloadInfo payload, PhoenixSettings? settings = null)
    {
        ValidatePayload(payload);
        var package = PackageRoot(payload.DosBoxEnhancedExe!);
        // Same-volume staging normally links immutable binaries, avoiding a fresh
        // portable-app copy for each game. Settings, caches and saves are never linked.
        var runtime = Path.Combine(payload.Root, ".dos-sessions", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(runtime);
        LinkOrCopy(payload.DosBoxEnhancedExe!, Path.Combine(runtime, "app", "DosboxEnhanced.exe"));
        foreach (var relative in RequiredRuntimeFiles)
            LinkOrCopy(Path.Combine(package, relative), Path.Combine(runtime, relative));
        var model = payload.NvidiaDlls.First(path => Path.GetFileName(path).Equals("nvngx_dlssnr.dll", StringComparison.OrdinalIgnoreCase));
        using var input = File.OpenRead(model);
        var graphics = new
        {
            nr_dll = Path.GetFullPath(model),
            sr_dll = Path.Combine(package, "runtime", "nvidia", "nvngx_dlss.dll"),
            streamline = Path.Combine(package, "runtime", "nvidia", "sl.interposer.dll"),
            native_bundle = Path.Combine(package, "runtime", "native"),
            weight_source = new { kind = "nvidia_dll", path = Path.GetFullPath(model), sha256 = Convert.ToHexString(SHA256.HashData(input)).ToLowerInvariant() },
            cache = Path.Combine(runtime, "data", "graphics-cache")
        };
        File.WriteAllText(Path.Combine(runtime, "graphics.json"), JsonSerializer.Serialize(graphics));
        File.WriteAllText(Path.Combine(runtime, "session-settings.json"), JsonSerializer.Serialize(settings ?? new PhoenixSettings()));
        return runtime;
    }

    internal static string WriteGameConfig(string runtime, string dosProgram)
    {
        dosProgram = Path.GetFullPath(dosProgram);
        var name = Path.GetFileName(dosProgram);
        var directory = Path.GetDirectoryName(dosProgram)!;
        if (!IsSupportedDosFile(dosProgram) || Path.GetFileNameWithoutExtension(name).Length > 8 ||
            Path.GetExtension(name).Length > 4 || name.IndexOfAny([' ', '\r', '\n', '"', '&', '|', '<', '>', '%']) >= 0)
            throw new ArgumentException("DOS quick launch requires an existing DOS 8.3 filename. Use DOSBox Enhanced's environment browser for other names.");
        if (directory.Any(character => character > 127) || directory.IndexOfAny(['\r', '\n', '"']) >= 0)
            throw new ArgumentException("DOSBox Enhanced needs an ASCII host folder without quotes or newlines.");
        var settings = JsonSerializer.Deserialize<PhoenixSettings>(File.ReadAllText(Path.Combine(runtime, "session-settings.json")))!;
        var profile = new
        {
            Id = "phoenix-session-" + Path.GetFileName(runtime),
            Name = Path.GetFileNameWithoutExtension(name),
            Drives = new[] { new { Letter = "C", Folder = directory, Type = "dir" } },
            Startup = "C:\\" + name, Cycles = "auto", Memory = 32,
            Effects = new
            {
                settings.Enabled, settings.Intensity, Tone = settings.LocalTone, Structure = settings.LocalStructure,
                Compare = settings.Comparison != 0,
                Passes = Math.Clamp(Math.Max(settings.Iterations, settings.RefinementPasses), 1, 4),
                Renderer = "phoenix", NvidiaUpscaling = false, NvidiaFrameGeneration = false, InputResize = false
            }
        };
        var config = Path.Combine(runtime, "session-profile.json");
        File.WriteAllText(config, JsonSerializer.Serialize(profile));
        return config;
    }

    internal static ProcessStartInfo CreateStartInfo(string runtime, string profile)
    {
        var start = new ProcessStartInfo(Path.Combine(runtime, "app", "DosboxEnhanced.exe"))
        { WorkingDirectory = runtime, UseShellExecute = false };
        start.ArgumentList.Add("--profile");
        start.ArgumentList.Add(profile);
        return start;
    }

    static void LinkOrCopy(string source, string destination)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        if (!CreateHardLinkW(destination, source, 0)) File.Copy(source, destination);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool CreateHardLinkW(string file, string existing, nint reserved);

    static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { }
    }
}
