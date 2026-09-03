using System.Diagnostics;
using System.Net;

namespace Dlss5CompatApp;

static class DosGameLauncher
{
    static readonly string[] SupportedExtensions = [".exe", ".com", ".bat"];

    public sealed record DosLaunchSession(Process Process, string Runtime, string ConfigPath, string TargetPath);

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
        int frameIterations = 1)
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
        ValidatePayload(payload);

        var runtime = PrepareRuntime(payload);
        WriteFeedConfig(runtime, frameIterations);
        var configPath = WriteGameConfig(runtime, target);
        var process = StartDosBox(runtime, configPath);
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

    static void ValidatePayload(PayloadInfo payload)
    {
        var missingText = MissingPayloadText(payload);
        if (missingText.Length > 0)
            throw new InvalidOperationException("Payload is missing: " + missingText);
    }

    static bool CanLaunchDos(PayloadInfo payload) =>
        MissingPayloadText(payload).Length == 0;

    static string MissingPayloadText(PayloadInfo payload)
    {
        var missing = new List<string>();
        if (payload.DosBoxExe is null) missing.Add("DOSBox Staging portable package");
        if (ResolvedReShade64(payload) is null) missing.Add("ReShade64.dll");
        if (payload.RenoDxDlss5Addon is null) missing.Add("renodx-dlss5.addon64");
        if (!payload.HasCoreDlss) missing.Add("verified matching nvngx_dlss.dll and nvngx_dlssnr.dll");

        foreach (var required in AppRequiredFiles())
        {
            if (!File.Exists(required.Value))
                missing.Add(required.Key);
        }

        return string.Join(", ", missing);
    }

    static string PrepareRuntime(PayloadInfo payload)
    {
        var dosbox = payload.DosBoxExe ?? throw new InvalidOperationException("DOSBox Staging is missing.");
        var runtime = Path.GetDirectoryName(dosbox) ?? throw new InvalidOperationException("DOSBox path is invalid.");

        CopyRequired(ResolvedReShade64(payload)!, Path.Combine(runtime, "opengl32.dll"));
        CopyRequired(AppRequiredFile("Runtime", "x64-dx9-dx11", "dlss5-feed.addon64"), Path.Combine(runtime, "dlss5-feed.addon64"));
        CopyRequired(AppRequiredFile("Runtime", "host64", "dlss5-feed-host64.exe"), Path.Combine(runtime, "host64", "dlss5-feed-host64.exe"));
        CopyRequired(payload.RenoDxDlss5Addon!, Path.Combine(runtime, "host64", "renodx-dlss5.addon64"));

        foreach (var dll in payload.NvidiaDlls.Concat(payload.StreamlineDlls))
            CopyRequired(dll, Path.Combine(runtime, "host64", Path.GetFileName(dll)));

        CopyRequired(AppRequiredFile("Runtime", "shaders", "DLSS5_Feed.fx"), Path.Combine(runtime, "reshade-shaders", "Shaders", "DLSS5_Feed.fx"));
        CopyRequired(AppRequiredFile("Runtime", "shaders", "ReShade.fxh"), Path.Combine(runtime, "reshade-shaders", "Shaders", "ReShade.fxh"));
        WriteReShadeIni(runtime);
        WritePreset(runtime);
        return runtime;
    }

    static void WriteReShadeIni(string runtime)
    {
        var text = """
        [ADDON]
        AddonPath=.\
        DisabledAddons=

        [GENERAL]
        EffectSearchPaths=.\reshade-shaders\Shaders\**
        NoDebugInfo=1
        PerformanceMode=0
        PreprocessorDefinitions=RESHADE_DEPTH_INPUT_IS_REVERSED=1,DLSS5_MV_PROVIDER=0,DLSS5_GEOM_FIT=0
        PresetPath=.\ReShadePreset.ini
        SkipLoadingDisabledEffects=0
        StartupPresetPath=
        TextureSearchPaths=.\reshade-shaders\Textures\**
        TutorialProgress=4

        [INPUT]
        ForceShortcutModifiers=1
        InputProcessing=2
        KeyEffects=0,0,0,0
        KeyFPS=0,0,0,0
        KeyFrametime=0,0,0,0
        KeyNextPreset=0,0,0,0
        KeyOverlay=36,0,0,0
        KeyPreviousPreset=0,0,0,0
        KeyReload=0,0,0,0
        KeyScreenshot=0,0,0,0

        [OVERLAY]
        ShowClock=0
        ShowFPS=0
        ShowFrameTime=0
        ShowOverlay=0
        ShowPresetName=0
        ShowPresetTransitionMessage=0
        ShowScreenshotMessage=0
        TutorialProgress=4

        [SCREENSHOT]
        ClearAlpha=1
        FileFormat=1
        FileNaming=%AppName% %Date% %Time%_%Count%
        SaveBeforeShot=0
        SaveOverlayShot=0
        SavePath=.\
        SavePresetFile=0
        """;
        File.WriteAllText(Path.Combine(runtime, "ReShade.ini"), text.ReplaceLineEndings(Environment.NewLine));
    }

    static void WritePreset(string runtime)
    {
        var text = """
        [GENERAL]
        Techniques=DLSS5_Feed@DLSS5_Feed.fx
        TechniqueSorting=DLSS5_Feed@DLSS5_Feed.fx,DLSS5_Feed_Debug@DLSS5_Feed.fx
        """;
        File.WriteAllText(Path.Combine(runtime, "ReShadePreset.ini"), text.ReplaceLineEndings(Environment.NewLine));
    }

    static void WriteFeedConfig(string runtime, int frameIterations)
    {
        var source = AppRequiredFile("Configs", "dlss5-feed-64.cfg");
        var target = Path.Combine(runtime, "dlss5-feed.cfg");
        File.Copy(source, target, overwrite: true);
        SetKey(target, "compare_mode", "1");
        SetKey(target, "iterations", Math.Clamp(frameIterations, 1, 10).ToString(System.Globalization.CultureInfo.InvariantCulture));
        SetKey(target, "source_auto", "1");
        SetKey(target, "source_width", "0");
        SetKey(target, "source_height", "0");
    }

    static string WriteGameConfig(string runtime, string dosProgram)
    {
        var gameDir = Path.GetDirectoryName(dosProgram) ?? throw new InvalidOperationException("DOS game path is invalid.");
        var fileName = Path.GetFileName(dosProgram);
        var baseName = Path.GetFileNameWithoutExtension(dosProgram);
        var safeName = string.Concat(baseName.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        var config = Path.Combine(runtime, "dosgames", safeName + "-dlss5.conf");
        Directory.CreateDirectory(Path.GetDirectoryName(config)!);

        var command = Path.GetExtension(dosProgram).Equals(".bat", StringComparison.OrdinalIgnoreCase)
            ? $"call {QuoteDos(fileName)}"
            : QuoteDos(fileName);

        var text = $"""
        [sdl]
        output = opengl
        window_size = 1280x960
        presentation_mode = host-rate
        vsync = on

        [render]
        aspect = on
        viewport = fit
        integer_scaling = off
        shader = interpolation/sharp

        [mixer]
        nosound = false

        [cpu]
        cycles = auto

        [autoexec]
        mount c "{gameDir}"
        c:
        {command}
        exit
        """;
        File.WriteAllText(config, text.ReplaceLineEndings(Environment.NewLine));
        return config;
    }

    static Process StartDosBox(string runtime, string configPath)
    {
        var start = new ProcessStartInfo
        {
            FileName = Path.Combine(runtime, "dosbox.exe"),
            WorkingDirectory = runtime,
            UseShellExecute = true
        };
        start.ArgumentList.Add("--noprimaryconf");
        start.ArgumentList.Add("--conf");
        start.ArgumentList.Add(configPath);
        return Process.Start(start) ?? throw new InvalidOperationException("DOSBox did not start.");
    }

    static IEnumerable<KeyValuePair<string, string>> AppRequiredFiles()
    {
        yield return new("Runtime/x64-dx9-dx11/dlss5-feed.addon64", AppRequiredFile("Runtime", "x64-dx9-dx11", "dlss5-feed.addon64"));
        yield return new("Runtime/host64/dlss5-feed-host64.exe", AppRequiredFile("Runtime", "host64", "dlss5-feed-host64.exe"));
        yield return new("Runtime/shaders/DLSS5_Feed.fx", AppRequiredFile("Runtime", "shaders", "DLSS5_Feed.fx"));
        yield return new("Runtime/shaders/ReShade.fxh", AppRequiredFile("Runtime", "shaders", "ReShade.fxh"));
        yield return new("Configs/dlss5-feed-64.cfg", AppRequiredFile("Configs", "dlss5-feed-64.cfg"));
    }

    static string AppRequiredFile(params string[] parts) =>
        Path.Combine(new[] { AppContext.BaseDirectory }.Concat(parts).ToArray());

    static string? AppOptionalFile(params string[] parts)
    {
        var candidate = AppRequiredFile(parts);
        return File.Exists(candidate) ? candidate : null;
    }

    static string? ResolvedReShade64(PayloadInfo payload) =>
        payload.ReShade64Dll ?? AppOptionalFile("Runtime", "reshade", "ReShade64.dll");

    static void CopyRequired(string source, string destination)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.Copy(source, destination, overwrite: true);
    }

    static void SetKey(string path, string key, string value)
    {
        var lines = File.Exists(path) ? File.ReadAllLines(path).ToList() : [];
        for (var i = 0; i < lines.Count; i++)
        {
            if (!lines[i].StartsWith(key + "=", StringComparison.OrdinalIgnoreCase))
                continue;
            lines[i] = key + "=" + value;
            File.WriteAllLines(path, lines);
            return;
        }
        lines.Add(key + "=" + value);
        File.WriteAllLines(path, lines);
    }

    static string QuoteDos(string value) =>
        value.Contains(' ', StringComparison.Ordinal) ? "\"" + value + "\"" : value;

    static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch { }
    }
}
