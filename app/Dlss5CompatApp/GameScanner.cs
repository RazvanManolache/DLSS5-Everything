using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Dlss5CompatApp;

static partial class GameScanner
{
    static readonly string[] ApiMarkers =
    [
        "D3D12CreateDevice",
        "D3D11CreateDevice",
        "D3D10CreateDevice",
        "D3D10CreateDevice1",
        "D3D10CreateDeviceAndSwapChain1",
        "CreateDXGIFactory",
        "Direct3DCreate9",
        "Direct3DCreate8",
        "DirectDrawCreateEx",
        "DirectDrawCreate",
        "glide.dll",
        "glide2x.dll",
        "glide3x.dll",
        "grGlideInit",
        "grSstWinOpen",
        "xrCreateInstance",
        "openxr_loader.dll",
        "openvr_api.dll",
        "vkCreateInstance",
        "wglCreateContext",
        "glBindTexture",
        "Initializing OpenGL display"
    ];

    static readonly string[] NativeDlssMarkers =
    [
        "NVSDK_NGX",
        "nvngx_dlss.dll",
        "sl.interposer.dll",
        "sl.dlss.dll",
        "Streamline"
    ];

    static readonly HashSet<string> SkipDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        "_dlss5_backup", "_dlss5_compat_backup", "reshade-shaders", "node_modules", ".git",
        "_redist", "prerequisites", "directx", "redist", "_commonredist", "dotnet",
        "installer", "installers", "support", "tools", "[modding] tools", "modding tools", "vcredist", "_support", "directx_redist",
        "easyanticheat", "battleye", "backup", "backups", "_backup", "bak", "old", "original", "originals",
        "_repos", "payload", "addons", "vendor", "dist", "publish", "runtime", "host64",
        "PhoenixRuntime", "phoenix-runtimes", "phoenix-companions",
        "source", "src", "obj", "docs", "screenshots", "streamline", "dgvoodoo2",
        "reshade", "rtx_remix_downloads", "dxwrapper_downloads", "local-release"
    };

    public static async Task<IReadOnlyList<GameCandidate>> ScanAsync(string root, IProgress<string>? progress = null, CancellationToken cancellationToken = default,
        GameScanLimits? limits = null, Action<string>? warning = null)
    {
        root = LocalPaths.Normalize(root);
        LocalPaths.RequireNoLinks(root);
        var boundedNames = limits is not null;
        limits ??= new();
        limits.Validate();
        return await Task.Run(() =>
        {
            var candidates = new List<GameCandidate>();
            var knownNames = boundedNames ? new Dictionary<string, string>() : LoadKnownGameNames(root, cancellationToken);
            foreach (var file in EnumerateExecutables(root, limits, cancellationToken, warning))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var name = Path.GetFileName(file);
                if (NotGameExe().IsMatch(name)) continue;

                long size;
                try { size = new FileInfo(file).Length; }
                catch { continue; }
                if (size > limits.MaxExecutableBytes) { warning?.Invoke("Executable exceeds scan limit: " + file); continue; }
                var smallGameLauncher = IsSmallGameLauncherCandidate(file);
                if (size < 128 * 1024 && !smallGameLauncher) continue;
                if (LooksLikeNonGameExecutable(file, size)) continue;

                var arch = PeReader.GetArchitecture(file);
                if (arch == CpuArch.Unknown) continue;

                var imports = PeReader.GetImports(file);
                var detections = DetectApis(file, imports);
                var emulator = EmulatorProfiles.ProfileFor(file);
                var nativeDlss = arch == CpuArch.X64 && DetectNativeDlssSignal(file, imports);
                var detected = ChoosePreferredApi(detections, arch, emulator, file, nativeDlss);
                if (detected.Api == GraphicsApi.Unknown && !LooksLikeGameExecutable(file)) continue;
                // A sibling VR loader is not evidence that an SDK command-line tool
                // renders anything. Keep explicit user-selected unknown executables possible.
                if (emulator is null && !LooksLikeGameExecutable(file) && !detections.Any(d =>
                    d.Api is not (GraphicsApi.OpenVr or GraphicsApi.OpenXr) || d.Via is "imports" or "strings")) continue;

                var gameRoot = GuessGameRoot(root, file);
                var displayName = GetDisplayName(gameRoot, file, knownNames);
                var suggestedArgumentsByApi = SuggestedArgumentsByApi(file, detections, arch);
                candidates.Add(FinalizeDetection(new GameCandidate(gameRoot, file, displayName, name, arch, detected.Api, DetectionSummary(detections, detected, emulator), size, PossibleApis(detections, arch, emulator), SuggestedArguments(detected.Api, suggestedArgumentsByApi), suggestedArgumentsByApi, nativeDlss), detections));
                if (candidates.Count % 10 == 0) progress?.Report($"Found {candidates.Count} candidate(s)...");
            }

            return candidates
                .OrderByDescending(x => x.Route != InstallRoute.Unsupported)
                .ThenBy(x => x.Root, StringComparer.OrdinalIgnoreCase)
                .ThenByDescending(x => x.Api == GraphicsApi.DirectX12)
                .ThenByDescending(x => x.Size)
                .GroupBy(x => x.ExePath, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToArray();
        }, cancellationToken);
    }

    public static GameCandidate? ScanSingleExe(string exePath) => ScanSingleExeBounded(exePath, 1024L * 1024 * 1024);

    public static GameCandidate? ScanSingleExeBounded(string exePath, long maxExecutableBytes)
    {
        exePath = LocalPaths.Normalize(exePath);
        LocalPaths.RequireNoLinks(exePath);
        if (maxExecutableBytes is < 1 or > 1024L * 1024 * 1024) throw new ArgumentOutOfRangeException(nameof(maxExecutableBytes));
        if (!File.Exists(exePath)) return null;
        if (!IsExecutableContainer(exePath)) return null;
        if (NotGameExe().IsMatch(Path.GetFileName(exePath))) return null;
        long size;
        try { size = new FileInfo(exePath).Length; }
        catch { return null; }
        if (size > maxExecutableBytes) return null;
        var smallGameLauncher = IsSmallGameLauncherCandidate(exePath);
        if ((size < 128 * 1024 && !smallGameLauncher) || LooksLikeNonGameExecutable(exePath, size)) return null;
        var arch = PeReader.GetArchitecture(exePath);
        var imports = PeReader.GetImports(exePath);
        var detections = DetectApis(exePath, imports);
        var emulator = EmulatorProfiles.ProfileFor(exePath);
        var nativeDlss = arch == CpuArch.X64 && DetectNativeDlssSignal(exePath, imports);
        var detected = ChoosePreferredApi(detections, arch, emulator, exePath, nativeDlss);
        if (arch == CpuArch.Unknown) return null;
        // An explicitly selected executable can have a dynamic/unknown API.
        var gameRoot = GuessSingleGameRoot(exePath);
        var suggestedArgumentsByApi = SuggestedArgumentsByApi(exePath, detections, arch);
        return FinalizeDetection(new GameCandidate(gameRoot, exePath, GetDisplayName(gameRoot, exePath, new Dictionary<string, string>()), Path.GetFileName(exePath), arch, detected.Api, DetectionSummary(detections, detected, emulator), size, PossibleApis(detections, arch, emulator), SuggestedArguments(detected.Api, suggestedArgumentsByApi), suggestedArgumentsByApi, nativeDlss), detections);
    }

    static GameCandidate FinalizeDetection(GameCandidate game, IReadOnlyList<ApiDetection> detections)
    {
        var inputs = detections.Where(d => d.Via == "imports").Select(d => d.Api).Distinct().ToArray();
        if (inputs.Length == 0) inputs = detections.Where(d => d.Via == "strings").Select(d => d.Api).Distinct().ToArray();
        if (inputs.Any(api => api is GraphicsApi.DirectX10 or GraphicsApi.DirectX10_1 or GraphicsApi.DirectX11 or GraphicsApi.DirectX12))
            inputs = inputs.Where(api => api != GraphicsApi.Dxgi).ToArray();
        var translators = detections.Where(d => d.Via.StartsWith("wrapper:", StringComparison.Ordinal))
            .Select(d => d.Via[8..].Split('/')[0]).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var findings = RendererDiagnostics.Inspect(game.ExePath);
        var runtimeSelected = translators.Contains("ANGLE");
        return VerifiedRendererProfiles.Apply(game with
        {
            DetectedInputApis = inputs,
            TranslatorKinds = translators,
            RuntimeSelectionRequired = runtimeSelected || findings.Count > 0,
            CompatibilityWarnings = findings.Select(finding => finding.Message).ToArray(),
            Detection = game.Detection + (runtimeSelected ? "; ANGLE backend selected at runtime; DXGI presentation hook, not a DX11 assertion" : "")
                + (findings.Count > 0 ? "; " + string.Join("; ", findings.Select(finding => finding.Message)) : "")
        });
    }

    static IEnumerable<string> EnumerateExecutables(string root, GameScanLimits limits, CancellationToken cancellationToken, Action<string>? warning)
    {
        var queue = new Queue<(string Dir, int Depth)>();
        queue.Enqueue((root, 0));
        var directories = 1;
        var entriesLeft = limits.MaxEntries;
        var directoryLimitReported = false;

        while (queue.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (dir, depth) = queue.Dequeue();
            FileSystemInfo[] entries;
            try { entries = new DirectoryInfo(dir).EnumerateFileSystemInfos().Take(entriesLeft + 1).ToArray(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { warning?.Invoke($"Could not enumerate {dir}: {ex.Message}"); continue; }

            foreach (var entry in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (entriesLeft-- <= 0) { warning?.Invoke("Scan entry limit reached: " + root); yield break; }
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                if (entry is DirectoryInfo subdir)
                {
                    if (depth < limits.MaxDepth && !ShouldSkipDirectory(subdir, root))
                    {
                        if (directories < limits.MaxDirectories) { queue.Enqueue((subdir.FullName, depth + 1)); directories++; }
                        else if (!directoryLimitReported) { warning?.Invoke("Scan directory limit reached: " + root); directoryLimitReported = true; }
                    }
                }
                else if (entry is FileInfo file && IsExecutableContainer(file.FullName))
                {
                    yield return file.FullName;
                }
            }
        }
    }

    static (GraphicsApi Api, string Via) ChoosePreferredApi(IReadOnlyList<ApiDetection> detections, CpuArch arch, EmulatorProfile? emulator = null, string? executable = null, bool nativeDlss = false)
    {
        var wrapper = detections.FirstOrDefault(d => d.Via.StartsWith("wrapper:", StringComparison.Ordinal));
        if (wrapper is not null) return (wrapper.Api, wrapper.Via);
        // Unity bundles several renderers and optional XR support. On Windows,
        // choose its supported D3D11 path explicitly rather than treating every
        // embedded capability string as the renderer used by this game.
        if (executable is not null && IsUnityLayout(executable) && detections.Any(d => d.Api == GraphicsApi.DirectX11))
            return FirstDetection(detections, GraphicsApi.DirectX11);
        // Importing a concrete renderer is stronger evidence than a fallback name embedded
        // in an engine/SDL binary. A nearby XR plugin is an option, not proof of VR mode.
        var concreteImports = detections.Where(d => d.Via == "imports" &&
            d.Api is GraphicsApi.DirectX12 or GraphicsApi.DirectX11 or GraphicsApi.DirectX10 or GraphicsApi.DirectX10_1 or GraphicsApi.DirectX9 or GraphicsApi.DirectX8
                or GraphicsApi.Vulkan or GraphicsApi.OpenGl).Select(d => d.Api).ToHashSet();
        var moduleImports = detections.Where(d => d.Via.StartsWith("module:", StringComparison.Ordinal) &&
            d.Via.EndsWith("/imports", StringComparison.Ordinal) && d.Api is GraphicsApi.DirectX12 or GraphicsApi.DirectX11 or GraphicsApi.DirectX10 or GraphicsApi.DirectX10_1
                or GraphicsApi.DirectX9 or GraphicsApi.DirectX8 or GraphicsApi.Vulkan or GraphicsApi.OpenGl)
            .Select(d => d.Api).ToHashSet();
        var apis = concreteImports.Count > 0 ? concreteImports : moduleImports.Count > 0 ? moduleImports : detections.Select(x => x.Api).ToHashSet();
        if (apis.Any(api => api is GraphicsApi.DirectX12 or GraphicsApi.DirectX11 or GraphicsApi.DirectX10 or GraphicsApi.DirectX10_1 or GraphicsApi.DirectX9 or GraphicsApi.OpenGl or GraphicsApi.Vulkan) &&
            !detections.Any(d => d.Via == "imports" && d.Api is GraphicsApi.OpenXr or GraphicsApi.OpenVr))
        {
            // General-purpose engines embed optional XR entry names even in
            // ordinary flat games. Keep XR in the available choices, but do
            // not default to a headset-only route based on strings alone.
            apis.Remove(GraphicsApi.OpenXr);
            apis.Remove(GraphicsApi.OpenVr);
        }
        if (apis.SetEquals([GraphicsApi.Vulkan])) return FirstDetection(detections, GraphicsApi.Vulkan);
        if (nativeDlss && detections.Any(d => d.Api == GraphicsApi.DirectX12))
            return FirstDetection(detections, GraphicsApi.DirectX12);
        var preferredApis = arch == CpuArch.X86
            ? new[]
            {
                GraphicsApi.DirectX11,
                GraphicsApi.DirectX10_1,
                GraphicsApi.DirectX10,
                GraphicsApi.DirectX9,
                GraphicsApi.Dxgi,
                GraphicsApi.OpenGl,
                GraphicsApi.Glide245,
                GraphicsApi.Glide31,
                GraphicsApi.Glide211,
                GraphicsApi.Glide31Napalm,
                GraphicsApi.DirectX8,
                GraphicsApi.DirectX8DgVoodoo,
                GraphicsApi.DirectX12,
                GraphicsApi.Vulkan,
                GraphicsApi.DirectX7OrOlder,
                GraphicsApi.OpenXr,
                GraphicsApi.OpenVr
            }
            : new[]
            {
                GraphicsApi.OpenXr,
                GraphicsApi.OpenVr,
                GraphicsApi.DirectX12,
                GraphicsApi.DirectX11,
                GraphicsApi.DirectX10_1,
                GraphicsApi.DirectX10,
                GraphicsApi.Dxgi,
                GraphicsApi.OpenGl,
                GraphicsApi.Vulkan,
                GraphicsApi.DirectX9,
                GraphicsApi.DirectX8,
                GraphicsApi.DirectX8DgVoodoo,
                GraphicsApi.Glide245,
                GraphicsApi.Glide31,
                GraphicsApi.Glide211,
                GraphicsApi.Glide31Napalm,
                GraphicsApi.DirectX7OrOlder
            };
        foreach (var preferred in preferredApis)
        {
            if (apis.Contains(preferred))
                return FirstDetection(detections, preferred);
        }

        if (emulator is not null)
        {
            var choices = emulator.Renderers.Select(EmulatorApi).ToArray();
            // Metadata never overrides observed evidence. Prefer a route this architecture can use.
            var choice = choices.FirstOrDefault(GraphicsApi.Unknown);
            return (choice, "emulator-metadata");
        }
        return (GraphicsApi.Unknown, "none");
    }

    static (GraphicsApi Api, string Via) FirstDetection(IReadOnlyList<ApiDetection> detections, GraphicsApi api)
    {
        var item = detections.First(x => x.Api == api);
        return (item.Api, item.Via);
    }

    static IReadOnlyList<GraphicsApi> PossibleApis(IReadOnlyList<ApiDetection> detections, CpuArch arch, EmulatorProfile? emulator = null)
    {
        var apis = detections.Select(x => x.Api)
            .Where(x => x != GraphicsApi.Unknown)
            .Distinct()
            .ToList();

        if (arch == CpuArch.X86 && apis.Contains(GraphicsApi.DirectX8) && !apis.Contains(GraphicsApi.DirectX8DgVoodoo))
            apis.Add(GraphicsApi.DirectX8DgVoodoo);
        if (arch == CpuArch.X86 && apis.Contains(GraphicsApi.Glide31) && !apis.Contains(GraphicsApi.Glide31Napalm))
            apis.Add(GraphicsApi.Glide31Napalm);

        return apis
            .OrderBy(ApiSortKey)
            .Concat(emulator?.Renderers.Select(EmulatorApi) ?? [])
            .Distinct()
            .ToArray();
    }

    static GraphicsApi EmulatorApi(EmulatorRenderer renderer) => renderer.Api switch
    {
        "dxgi" => GraphicsApi.Dxgi,
        "vulkan" => GraphicsApi.Vulkan,
        "opengl" => GraphicsApi.OpenGl,
        _ => GraphicsApi.Unknown
    };

    static int ApiSortKey(GraphicsApi api) => api switch
    {
        GraphicsApi.DirectX12 => 0,
        GraphicsApi.OpenXr => 1,
        GraphicsApi.OpenVr => 2,
        GraphicsApi.DirectX11 => 3,
        GraphicsApi.DirectX10_1 => 4,
        GraphicsApi.DirectX10 => 5,
        GraphicsApi.Dxgi => 4,
        GraphicsApi.DirectX9 => 5,
        GraphicsApi.OpenGl => 6,
        GraphicsApi.Vulkan => 7,
        GraphicsApi.Glide245 => 8,
        GraphicsApi.Glide31 => 9,
        GraphicsApi.Glide31Napalm => 10,
        GraphicsApi.Glide211 => 11,
        GraphicsApi.DirectX8 => 12,
        GraphicsApi.DirectX8DgVoodoo => 13,
        GraphicsApi.DirectX7OrOlder => 14,
        _ => 99
    };

    static string DetectionSummary(IReadOnlyList<ApiDetection> detections, (GraphicsApi Api, string Via) chosen, EmulatorProfile? emulator = null)
    {
        var sources = detections
            .Where(x => x.Api != GraphicsApi.Unknown)
            .Select(x => x.Via)
            .Prepend(chosen.Via)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(5)
            .ToArray();
        var summary = sources.Length == 0 ? chosen.Via : string.Join(", ", sources);
        return emulator is null ? summary : summary + "; emulator renderer hint: " + emulator.Name + " / " + emulator.System
            + ": " + emulator.Hint + "; runtime compatibility unverified";
    }

    static IReadOnlyDictionary<GraphicsApi, string> SuggestedArgumentsByApi(string file, IReadOnlyList<ApiDetection> detections, CpuArch arch)
    {
        var arguments = new Dictionary<GraphicsApi, string>();
        foreach (var api in PossibleApis(detections, arch))
        {
            var value = SuggestedArguments(file, api, detections);
            if (!string.IsNullOrWhiteSpace(value))
                arguments[api] = value;
        }

        return arguments;
    }

    static string SuggestedArguments(GraphicsApi api, IReadOnlyDictionary<GraphicsApi, string> arguments) =>
        arguments.TryGetValue(api, out var value) ? value : "";

    static bool DetectNativeDlssSignal(string file, IReadOnlyList<string> imports)
    {
        if (imports.Any(import =>
            import.StartsWith("nvngx_", StringComparison.OrdinalIgnoreCase) ||
            import.StartsWith("sl.", StringComparison.OrdinalIgnoreCase)))
            return true;

        var dir = Path.GetDirectoryName(file);
        if (dir is not null)
        {
            foreach (var name in new[] { "nvngx_dlss.dll", "sl.interposer.dll", "sl.dlss.dll", "sl.dlss_g.dll", "sl.dlss_nr.dll" })
            {
                if (File.Exists(Path.Combine(dir, name)))
                    return true;
            }
        }

        return SafeMarkers(file, NativeDlssMarkers).Count > 0;
    }

    static string SuggestedArguments(string file, GraphicsApi api, IReadOnlyList<ApiDetection> detections)
    {
        if (IsUnityLayout(file) && api == GraphicsApi.DirectX11)
            return "-force-d3d11";
        if (api == GraphicsApi.Vulkan && detections.Any(detection => detection.Via == "engine-option:-vulkan"))
            return "-vulkan";
        if (api == GraphicsApi.OpenGl)
        {
            var hasOlderDirectX = detections.Any(x => x.Api is GraphicsApi.DirectX8 or GraphicsApi.DirectX7OrOlder);
            if (!hasOlderDirectX)
                return "";

            if (HasRendererMarker(file, "-opengl"))
                return "-opengl";

            return "";
        }

        if (api is not (GraphicsApi.Glide211 or GraphicsApi.Glide245 or GraphicsApi.Glide31 or GraphicsApi.Glide31Napalm))
            return "";

        if (HasRendererMarker(file, "-3dfx", "VIDEO\\3DFX", "VIDEO/3DFX", "3DFX"))
            return "-3dfx -w";

        return "";
    }

    internal static bool IsUnityLayout(string executable)
    {
        var directory = Path.GetDirectoryName(executable)!;
        var data = Path.Combine(directory, Path.GetFileNameWithoutExtension(executable) + "_Data");
        return File.Exists(Path.Combine(directory, "UnityPlayer.dll")) ||
            Directory.Exists(data) && new[] { "globalgamemanagers", "mainData", "resources.assets" }
                .Any(name => File.Exists(Path.Combine(data, name)));
    }

    static IReadOnlyList<ApiDetection> DetectApis(string file, IReadOnlyList<string> imports, bool inspectModules = true)
    {
        var detections = new List<ApiDetection>();
        AddDetections(file, imports, "imports", detections);

        if (!inspectModules)
            return detections;

        var dir = Path.GetDirectoryName(file);
        if (dir is null) return detections;

        var knownRendererDetections = new List<ApiDetection>();
        foreach (var module in KnownRendererModules(file, dir))
        {
            foreach (var item in DetectApis(module, PeReader.GetImports(module), inspectModules: false))
                knownRendererDetections.Add(item with { Via = "module:" + Path.GetFileName(module) + "/" + item.Via });
        }
        detections.AddRange(knownRendererDetections);
        detections.AddRange(OpenXrPluginDetections(file, dir));
        detections.AddRange(OpenVrPluginDetections(file, dir));
        if (HasSourceVulkanRenderer(file, dir))
            detections.Add(new ApiDetection(GraphicsApi.Vulkan, "engine-option:-vulkan"));

        var inspectImportedSiblings = !detections.Any(x => x.Api is GraphicsApi.DirectX12 or GraphicsApi.DirectX11 or GraphicsApi.DirectX10 or GraphicsApi.DirectX10_1 or GraphicsApi.Dxgi or GraphicsApi.DirectX9 or GraphicsApi.OpenGl or GraphicsApi.Vulkan or GraphicsApi.DirectX8);
        if (inspectImportedSiblings)
        {
            foreach (var import in imports.Take(80))
            {
                if (!Path.GetFileName(import).Equals(import, StringComparison.Ordinal)) continue;
                if (IsGraphicsProxyOrAppDll(import))
                    continue;
                var sibling = Path.Combine(dir, import);
                if (!IsReadableModule(sibling)) continue;
                foreach (var item in DetectApis(sibling, PeReader.GetImports(sibling), inspectModules: false))
                    detections.Add(item with { Via = "module:" + import + "/" + item.Via });
            }
        }

        foreach (var api in detections.Select(d => d.Api).Distinct().ToArray())
            foreach (var wrapper in IdentifyVulkanWrappers(file, api))
                detections.Add(new ApiDetection(GraphicsApi.Vulkan, "wrapper:" + wrapper.Kind + "/" + Path.GetFileName(wrapper.Path)));

        // Existing translators own their front-end DLLs. Hook their output API
        // instead of replacing the translator or mistaking GLES for desktop GL.
        if (IsAngleLayout(file))
        {
            detections.Add(new(GraphicsApi.Dxgi, "wrapper:ANGLE/runtime-DXGI"));
            detections.Add(new(GraphicsApi.DirectX11, "capability:ANGLE/D3D11"));
            detections.Add(new(GraphicsApi.DirectX12, "capability:ANGLE/D3D12-presentation"));
            detections.Add(new(GraphicsApi.Vulkan, "capability:ANGLE/Vulkan"));
        }
        foreach (var name in imports.Where(name => new[] { "d3d9.dll", "d3d8.dll", "glide.dll", "glide2x.dll", "glide3x.dll", "ddraw.dll" }
                     .Contains(name, StringComparer.OrdinalIgnoreCase)))
        {
            var path = Path.Combine(dir, name);
            if (!IsReadableModule(path) || !PackagedPhoenixPlan.IsExistingDgVoodoo(path, PeReader.GetArchitecture(file))) continue;
            var targetImports = PeReader.GetImports(path);
            var markers = SafeMarkers(path, ["D3D11CreateDevice", "D3D12CreateDevice"]);
            var outputApi = IniEditor.GetValue(Path.Combine(dir, "dgVoodoo.conf"), "General", "OutputAPI") ?? "";
            if (outputApi.StartsWith("d3d12", StringComparison.OrdinalIgnoreCase))
                detections.Add(new(GraphicsApi.DirectX12, "wrapper:dgVoodoo/" + name));
            else if (outputApi.StartsWith("d3d11", StringComparison.OrdinalIgnoreCase) ||
                     targetImports.Contains("d3d11.dll", StringComparer.OrdinalIgnoreCase) || markers.Contains("D3D11CreateDevice"))
                detections.Add(new(GraphicsApi.DirectX11, "wrapper:dgVoodoo/" + name));
            else if (targetImports.Contains("d3d12.dll", StringComparer.OrdinalIgnoreCase) || markers.Contains("D3D12CreateDevice"))
                detections.Add(new(GraphicsApi.DirectX12, "wrapper:dgVoodoo/" + name));
            else
                detections.Add(new(GraphicsApi.Dxgi, "wrapper:dgVoodoo/" + name));
        }

        return detections
            .GroupBy(x => new { x.Api, x.Via })
            .Select(g => g.First())
            .ToArray();
    }

    public static IReadOnlyList<VulkanWrapper> IdentifyVulkanWrappers(string exePath, GraphicsApi api)
    {
        exePath = LocalPaths.Normalize(exePath);
        LocalPaths.RequireNoLinks(exePath);
        var arch = PeReader.GetArchitecture(exePath);
        if (arch == CpuArch.Unknown) return [];
        var names = api switch
        {
            GraphicsApi.DirectX12 => new[] { "d3d12.dll", "dxgi.dll" },
            GraphicsApi.DirectX11 => new[] { "d3d11.dll", "dxgi.dll" },
            GraphicsApi.DirectX10 or GraphicsApi.DirectX10_1 => new[] { "d3d10.dll", "d3d10_1.dll", "dxgi.dll" },
            GraphicsApi.Dxgi => new[] { "d3d12.dll", "d3d11.dll", "dxgi.dll" },
            GraphicsApi.DirectX9 => new[] { "d3d9.dll" },
            GraphicsApi.DirectX8 => new[] { "d3d8.dll" },
            GraphicsApi.DirectX7OrOlder => new[] { "ddraw.dll" },
            _ => Array.Empty<string>()
        };
        var wrappers = new List<VulkanWrapper>();
        foreach (var name in names)
        {
            var path = Path.Combine(Path.GetDirectoryName(exePath)!, name);
            if (!IsReadableModule(path) || PeReader.GetArchitecture(path) != arch || new FileInfo(path).Length > 64 * 1024 * 1024) continue;
            var markers = SafeMarkers(path, ["DXVK", "vkd3d", "vkGetInstanceProcAddr", "ReShade"]);
            string metadata;
            try
            {
                var version = System.Diagnostics.FileVersionInfo.GetVersionInfo(path);
                metadata = string.Join(" ", version.ProductName, version.FileDescription, version.CompanyName);
            }
            catch { metadata = ""; }
            if (markers.Contains("ReShade") || metadata.Contains("ReShade", StringComparison.OrdinalIgnoreCase)) continue;
            foreach (var kind in new[] { "DXVK", "vkd3d" })
                if (metadata.Contains(kind, StringComparison.OrdinalIgnoreCase) || markers.Contains(kind) && markers.Contains("vkGetInstanceProcAddr"))
                { wrappers.Add(new(path, kind, arch)); break; }
        }
        return wrappers;
    }

    internal static bool IsAngleLayout(string executable)
    {
        var directory = Path.GetDirectoryName(executable)!;
        var arch = PeReader.GetArchitecture(executable);
        if (arch == CpuArch.Unknown) return false;
        var egl = Path.Combine(directory, "libEGL.dll");
        var gles = Path.Combine(directory, "libGLESv2.dll");
        return new[] { egl, gles }.All(path => IsReadableModule(path) && PeReader.GetArchitecture(path) == arch) &&
            PayloadScanner.HasExports(egl, "eglGetProcAddress", "eglInitialize") &&
            SafeMarkers(gles, ["ANGLE"]).Contains("ANGLE") &&
            (PeReader.GetImports(executable).Contains("libEGL.dll", StringComparer.OrdinalIgnoreCase) ||
             SafeMarkers(executable, ["eglGetPlatformDisplay", "eglGetProcAddress"]).Count > 0);
    }

    static bool HasSourceVulkanRenderer(string executable, string directory)
    {
        var architecture = PeReader.GetArchitecture(executable);
        if (architecture == CpuArch.Unknown) return false;
        var engine = Path.Combine(directory, "bin", "engine.dll");
        var material = Path.Combine(directory, "bin", "materialsystem.dll");
        var renderer = Path.Combine(directory, "bin", "shaderapivk.dll");
        var translation = Path.Combine(directory, "bin", "dxvk_d3d9.dll");
        if (new[] { engine, material, renderer, translation }.Any(path =>
            !IsReadableModule(path) || PeReader.GetArchitecture(path) != architecture)) return false;
        // This is an optional engine renderer, not an active proxy to replace.
        // Offer its advertised switch while retaining Direct3D as the default.
        return SafeMarkers(material, ["-vulkan", "shaderapivk"]).Count == 2 &&
            PayloadScanner.HasExports(translation, "Direct3DCreate9");
    }

    static bool IsReadableModule(string path)
    {
        try { LocalPaths.RequireNoLinks(path); return File.Exists(path) && new FileInfo(path).Length <= 1024L * 1024 * 1024; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return false; }
    }

    static IReadOnlySet<string> SafeMarkers(string path, IEnumerable<string> markers) =>
        IsReadableModule(path) ? PeReader.FindMarkers(path, markers) : new HashSet<string>();

    static bool IsGraphicsProxyOrAppDll(string importName)
    {
        var name = Path.GetFileName(importName);
        if (name.Equals("d3d8.dll", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("d3d9.dll", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("d3d10.dll", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("d3d10_1.dll", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("d3d11.dll", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("d3d12.dll", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("dxgi.dll", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("ddraw.dll", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("d3dimm.dll", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("opengl32.dll", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("openxr_loader.dll", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("openvr_api.dll", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("vulkan-1.dll", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("glide.dll", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("glide2x.dll", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("glide3x.dll", StringComparison.OrdinalIgnoreCase))
            return true;

        return name.StartsWith("reshade", StringComparison.OrdinalIgnoreCase) ||
               name.StartsWith("dlss5-feed", StringComparison.OrdinalIgnoreCase) ||
               name.StartsWith("renodx-dlss5", StringComparison.OrdinalIgnoreCase) ||
               name.StartsWith("nvngx_", StringComparison.OrdinalIgnoreCase) ||
               name.StartsWith("sl.", StringComparison.OrdinalIgnoreCase);
    }

    static void AddDetections(string file, IReadOnlyList<string> imports, string via, List<ApiDetection> detections)
    {
        var importSet = imports.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (importSet.Contains("d3d12.dll")) detections.Add(new ApiDetection(GraphicsApi.DirectX12, via));
        if (importSet.Contains("d3d11.dll")) detections.Add(new ApiDetection(GraphicsApi.DirectX11, via));
        if (importSet.Contains("d3d10_1.dll")) detections.Add(new ApiDetection(GraphicsApi.DirectX10_1, via));
        if (importSet.Contains("d3d10.dll")) detections.Add(new ApiDetection(GraphicsApi.DirectX10, via));
        if (importSet.Contains("dxgi.dll"))
            detections.Add(new ApiDetection(GraphicsApi.Dxgi, via));
        if (importSet.Contains("d3d9.dll")) detections.Add(new ApiDetection(GraphicsApi.DirectX9, via));
        if (importSet.Contains("opengl32.dll")) detections.Add(new ApiDetection(GraphicsApi.OpenGl, via));
        if (importSet.Contains("openxr_loader.dll")) detections.Add(new ApiDetection(GraphicsApi.OpenXr, via));
        if (importSet.Contains("openvr_api.dll")) detections.Add(new ApiDetection(GraphicsApi.OpenVr, via));
        if (importSet.Contains("vulkan-1.dll")) detections.Add(new ApiDetection(GraphicsApi.Vulkan, via));
        if (importSet.Contains("glide.dll")) detections.Add(new ApiDetection(GraphicsApi.Glide211, via));
        if (importSet.Contains("glide2x.dll")) detections.Add(new ApiDetection(GraphicsApi.Glide245, via));
        if (importSet.Contains("glide3x.dll")) detections.Add(new ApiDetection(GraphicsApi.Glide31, via));
        if (importSet.Contains("d3d8.dll") || importSet.Contains("d3d8thk.dll")) detections.Add(new ApiDetection(GraphicsApi.DirectX8, via));
        // An explicit legacy import remains a real alternate renderer even
        // when the engine also links Glide or a newer Direct3D entry point.
        if (importSet.Contains("ddraw.dll") ||
            importSet.Contains("d3dim.dll") ||
            importSet.Contains("d3dim700.dll") ||
            importSet.Contains("d3drm.dll"))
            detections.Add(new ApiDetection(GraphicsApi.DirectX7OrOlder, via));

        var markers = SafeMarkers(file, ApiMarkers);
        var hasNewerMarker =
            markers.Contains("D3D12CreateDevice") ||
            markers.Contains("D3D11CreateDevice") ||
            markers.Contains("D3D10CreateDevice") ||
            markers.Contains("CreateDXGIFactory") ||
            markers.Contains("Direct3DCreate9") ||
            markers.Contains("Direct3DCreate8") ||
            markers.Contains("vkCreateInstance") ||
            markers.Contains("wglCreateContext") ||
            markers.Contains("glBindTexture") ||
            markers.Contains("Initializing OpenGL display") ||
            markers.Contains("xrCreateInstance") ||
            markers.Contains("openxr_loader.dll") ||
            markers.Contains("openvr_api.dll") ||
            markers.Contains("glide.dll") ||
            markers.Contains("glide2x.dll") ||
            markers.Contains("glide3x.dll") ||
            markers.Contains("grGlideInit") ||
            markers.Contains("grSstWinOpen");
        if (markers.Contains("D3D12CreateDevice")) detections.Add(new ApiDetection(GraphicsApi.DirectX12, "strings"));
        if (markers.Contains("D3D11CreateDevice")) detections.Add(new ApiDetection(GraphicsApi.DirectX11, "strings"));
        if (markers.Contains("D3D10CreateDevice1") || markers.Contains("D3D10CreateDeviceAndSwapChain1"))
            detections.Add(new ApiDetection(GraphicsApi.DirectX10_1, "strings"));
        else if (markers.Contains("D3D10CreateDevice"))
            detections.Add(new ApiDetection(GraphicsApi.DirectX10, "strings"));
        if (markers.Contains("CreateDXGIFactory"))
            detections.Add(new ApiDetection(GraphicsApi.Dxgi, "strings"));
        if (markers.Contains("Direct3DCreate9")) detections.Add(new ApiDetection(GraphicsApi.DirectX9, "strings"));
        if (markers.Contains("Direct3DCreate8")) detections.Add(new ApiDetection(GraphicsApi.DirectX8, "strings"));
        if (markers.Contains("xrCreateInstance") || markers.Contains("openxr_loader.dll")) detections.Add(new ApiDetection(GraphicsApi.OpenXr, "strings"));
        if (markers.Contains("openvr_api.dll")) detections.Add(new ApiDetection(GraphicsApi.OpenVr, "strings"));
        if (markers.Contains("vkCreateInstance")) detections.Add(new ApiDetection(GraphicsApi.Vulkan, "strings"));
        if (markers.Contains("glide.dll")) detections.Add(new ApiDetection(GraphicsApi.Glide211, "strings"));
        if (markers.Contains("glide2x.dll")) detections.Add(new ApiDetection(GraphicsApi.Glide245, "strings"));
        if (markers.Contains("glide3x.dll") || markers.Contains("grGlideInit") || markers.Contains("grSstWinOpen"))
            detections.Add(new ApiDetection(GraphicsApi.Glide31, "strings"));
        if (markers.Contains("wglCreateContext") ||
            markers.Contains("glBindTexture") ||
            markers.Contains("Initializing OpenGL display"))
            detections.Add(new ApiDetection(GraphicsApi.OpenGl, "strings"));
        if (!hasNewerMarker && (markers.Contains("DirectDrawCreateEx") || markers.Contains("DirectDrawCreate")))
            detections.Add(new ApiDetection(GraphicsApi.DirectX7OrOlder, "strings"));
    }

    static IEnumerable<string> KnownRendererModules(string file, string dir)
    {
        var self = Path.GetFileName(file);
        foreach (var name in new[]
        {
            "UnityPlayer.dll",
            "GameAssembly.dll",
            "game.dll",
            "D2DDraw.dll",
            "D2Direct3D.dll",
            "D2Glide.dll",
            "storm.dll",
            "rendersystemdx11.dll",
            "rendersystemdx9.dll",
            "rendersystemvulkan.dll",
            "renderer_opengl1_x86.dll",
            "renderer_opengl2_x86.dll",
            "renderer_opengl1_x86_64.dll",
            "renderer_opengl2_x86_64.dll",
            // Source 1 launch stubs import tier0, while the rendering API lives
            // in these engine-relative modules rather than the small executable.
            "bin\\shaderapidx9.dll",
            "bin\\shaderapidx11.dll",
            "bin\\shaderapigl.dll",
            "sys\\dx8.dll",
            "sys\\gl.dll"
        })
        {
            if (self.Equals(name, StringComparison.OrdinalIgnoreCase)) continue;
            var path = Path.Combine(dir, name);
            if (IsReadableModule(path)) yield return path;
        }
    }

    static IEnumerable<ApiDetection> OpenXrPluginDetections(string file, string dir)
    {
        var exeBase = Path.GetFileNameWithoutExtension(file);
        foreach (var candidate in new[]
        {
            Path.Combine(dir, "openxr_loader.dll"),
            Path.Combine(dir, exeBase + "_Data", "Plugins", "openxr_loader.dll"),
            Path.Combine(dir, exeBase + "_Data", "Plugins", "x86_64", "openxr_loader.dll"),
            Path.Combine(dir, exeBase + "_Data", "Plugins", "x86", "openxr_loader.dll")
        })
        {
            if (!File.Exists(candidate))
                continue;

            var relative = Path.GetRelativePath(dir, candidate);
            yield return new ApiDetection(GraphicsApi.OpenXr, "module:" + relative);
        }
    }

    static IEnumerable<ApiDetection> OpenVrPluginDetections(string file, string dir)
    {
        var exeBase = Path.GetFileNameWithoutExtension(file);
        foreach (var candidate in new[]
        {
            Path.Combine(dir, "openvr_api.dll"),
            Path.Combine(dir, exeBase + "_Data", "Plugins", "openvr_api.dll"),
            Path.Combine(dir, exeBase + "_Data", "Plugins", "x86_64", "openvr_api.dll"),
            Path.Combine(dir, exeBase + "_Data", "Plugins", "x86", "openvr_api.dll")
        })
        {
            if (!File.Exists(candidate))
                continue;

            var relative = Path.GetRelativePath(dir, candidate);
            yield return new ApiDetection(GraphicsApi.OpenVr, "module:" + relative);
        }
    }

    static bool ShouldSkipDirectory(DirectoryInfo dir, string? selectedRoot = null)
    {
        if ((dir.Attributes & FileAttributes.ReparsePoint) != 0) return true;
        if (SkipDirectories.Contains(dir.Name)) return true;
        if (dir.Name.StartsWith(".", StringComparison.Ordinal) && !dir.Name.Equals(".steam", StringComparison.OrdinalIgnoreCase)) return true;
        if (dir.Name.Contains("backup", StringComparison.OrdinalIgnoreCase)) return true;
        if (dir.Name.Contains("swapper", StringComparison.OrdinalIgnoreCase)) return true;
        if (dir.Name.StartsWith("DLSS5-Feeder-clean", StringComparison.OrdinalIgnoreCase)) return true;
        foreach (var protectedTree in new[] { @"\AppData\", @"\Windows\" })
            if (dir.FullName.Contains(protectedTree, StringComparison.OrdinalIgnoreCase) &&
                selectedRoot?.Contains(protectedTree, StringComparison.OrdinalIgnoreCase) != true) return true;
        return false;
    }

    static bool LooksLikeGameExecutable(string file)
    {
        var dir = Path.GetDirectoryName(file);
        if (dir is null) return false;

        var name = Path.GetFileNameWithoutExtension(file);
        if (name.EndsWith("-Win64-Shipping", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith("-Win32-Shipping", StringComparison.OrdinalIgnoreCase))
            return true;

        if (File.Exists(Path.Combine(dir, "UnityPlayer.dll")) ||
            Directory.Exists(Path.Combine(dir, name + "_Data")))
            return true;

        return false;
    }

    static bool IsSmallGameLauncherCandidate(string file)
    {
        var dir = Path.GetDirectoryName(file);
        if (dir is null) return false;

        if (LooksLikeGameExecutable(file))
            return true;

        // Small demoscene/older games can render directly without any sibling
        // engine module. Size alone must not exclude an observable graphics import.
        if (PeReader.GetImports(file).Any(name => new[] { "ddraw.dll", "d3dim.dll", "d3dim700.dll", "d3drm.dll", "d3d8.dll", "d3d9.dll", "d3d11.dll", "d3d12.dll", "opengl32.dll", "vulkan-1.dll" }
                .Contains(name, StringComparer.OrdinalIgnoreCase))) return true;

        if (new[] { GraphicsApi.DirectX9, GraphicsApi.DirectX8, GraphicsApi.Dxgi, GraphicsApi.DirectX7OrOlder }
            .Any(api => IdentifyVulkanWrappers(file, api).Count > 0)) return true;

        if (HasLocalRendererModuleEvidence(file, dir))
            return true;

        return IsDiabloLikeLayout(file, dir);
    }

    static bool IsExecutableContainer(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".exe" => true,
        // Some older Windows games place their real PE entry point in a BIN
        // beside a small launcher. Do not confuse data files or renamed DLLs
        // with launchable code.
        ".bin" => PeReader.IsExecutableImage(path),
        _ => false
    };

    static bool HasLocalRendererModuleEvidence(string file, string dir)
    {
        foreach (var module in KnownRendererModules(file, dir))
        {
            var detections = DetectApis(module, PeReader.GetImports(module), inspectModules: false);
            if (detections.Any(x => x.Api != GraphicsApi.Unknown))
                return true;
        }

        return false;
    }

    static bool HasRendererMarker(string file, params string[] markers)
    {
        if (SafeMarkers(file, markers).Count > 0)
            return true;

        var dir = Path.GetDirectoryName(file);
        if (dir is null) return false;

        foreach (var module in KnownRendererModules(file, dir))
        {
            if (SafeMarkers(module, markers).Count > 0)
                return true;
        }

        return false;
    }

    static bool IsDiabloLikeLayout(string file, string dir)
    {
        // The engine module set identifies this layout independently of its launcher name.
        return File.Exists(Path.Combine(dir, "D2Client.dll")) &&
               File.Exists(Path.Combine(dir, "D2gfx.dll")) &&
               File.Exists(Path.Combine(dir, "D2Win.dll")) &&
               File.Exists(Path.Combine(dir, "D2Common.dll"));
    }

    static bool LooksLikeNonGameExecutable(string file, long size)
    {
        var name = Path.GetFileNameWithoutExtension(file);
        if (CommonToolExe().IsMatch(name))
            return true;
        // Browser subprocesses inherit nearby engine modules too; those modules
        // do not turn an embedded Chromium helper into a game renderer.
        if ((name.Contains("browser", StringComparison.OrdinalIgnoreCase) ||
             name.Contains("webhelper", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(Path.GetFileName(Path.GetDirectoryName(file)), "cef", StringComparison.OrdinalIgnoreCase)) &&
            PeReader.GetImports(file).Contains("libcef.dll", StringComparer.OrdinalIgnoreCase)) return true;

        var full = Path.GetFullPath(file);
        if (full.Contains(@"\Steam\bin\", StringComparison.OrdinalIgnoreCase) ||
            full.Contains(@"\Steam\package\", StringComparison.OrdinalIgnoreCase) ||
            full.Contains(@"\Steam\tenfoot\", StringComparison.OrdinalIgnoreCase))
            return true;

        foreach (var segment in full.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            if (UtilityPathSegment().IsMatch(segment))
                return true;
        }

        try
        {
            var info = System.Diagnostics.FileVersionInfo.GetVersionInfo(file);
            if (info.ProductName?.Equals("SteamVR", StringComparison.OrdinalIgnoreCase) == true ||
                info.FileDescription?.Equals("VR Compositor", StringComparison.OrdinalIgnoreCase) == true) return true;
            var metadata = string.Join(" ", new[]
            {
                info.ProductName,
                info.FileDescription,
                info.InternalName,
                info.OriginalFilename,
                info.Comments
            }.Where(x => !string.IsNullOrWhiteSpace(x)));

            if (!string.IsNullOrWhiteSpace(metadata) && NonGameVersionMetadata().IsMatch(metadata))
                return true;
        }
        catch { }

        if (size < 512 * 1024 && TinyHelperName().IsMatch(name))
            return true;

        return false;
    }

    static string GuessGameRoot(string scanRoot, string exePath)
    {
        var root = Path.GetFullPath(scanRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var dir = Path.GetDirectoryName(Path.GetFullPath(exePath))!;
        var current = new DirectoryInfo(dir);

        while (current.Parent is not null && !current.FullName.Equals(root, StringComparison.OrdinalIgnoreCase))
        {
            var name = current.Name;
            if (name.Equals("Win64", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("Win32", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("Binaries", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("x64", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("x86", StringComparison.OrdinalIgnoreCase))
            {
                current = current.Parent;
                continue;
            }

            if (current.Parent.FullName.Equals(root, StringComparison.OrdinalIgnoreCase))
                return current.FullName;

            return current.FullName;
        }

        return dir;
    }

    static string GuessSingleGameRoot(string exePath)
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(exePath)!);
        while (dir.Parent is not null && new[] { "Win64", "Win32", "Binaries", "x64", "x86" }.Contains(dir.Name, StringComparer.OrdinalIgnoreCase))
            dir = dir.Parent;
        if (dir.Parent is not null && dir.Name.Equals("Content", StringComparison.OrdinalIgnoreCase) && File.Exists(Path.Combine(dir.FullName, "MicrosoftGame.config")))
            dir = dir.Parent;
        return dir.FullName;
    }

    static Dictionary<string, string> LoadKnownGameNames(string root, CancellationToken cancellationToken)
    {
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        AddSteamNames(root, names, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        AddEpicNames(root, names);
        cancellationToken.ThrowIfCancellationRequested();
        AddGogNames(root, names);
        return names;
    }

    static void AddSteamNames(string root, Dictionary<string, string> names, CancellationToken cancellationToken)
    {
        foreach (var manifest in SafeFiles(root, "appmanifest_*.acf", 7, cancellationToken).Take(2048))
        {
            string text;
            try { text = LocalData.ReadText(manifest, 1024 * 1024); }
            catch { continue; }

            var installDir = KeyValue(text, "installdir");
            var name = KeyValue(text, "name");
            var steamapps = Directory.GetParent(manifest)?.FullName;
            if (!RuntimeDiagnostics.SafeRelative(installDir) || name is null || steamapps is null) continue;
            var gameDir = Path.Combine(steamapps, "common", installDir!);
            if (Directory.Exists(gameDir)) names[Path.GetFullPath(gameDir)] = name;
        }
    }

    static void AddEpicNames(string root, Dictionary<string, string> names)
    {
        var manifestDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Epic", "EpicGamesLauncher", "Data", "Manifests");
        if (!Directory.Exists(manifestDir)) return;
        try { LocalPaths.RequireNoLinks(manifestDir); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return; }
        foreach (var file in Directory.EnumerateFiles(manifestDir, "*.item").Take(2048))
        {
            try
            {
                using var json = System.Text.Json.JsonDocument.Parse(LocalData.ReadText(file, 1024 * 1024));
                var install = json.RootElement.GetProperty("InstallLocation").GetString();
                var name = json.RootElement.GetProperty("DisplayName").GetString();
                if (install is not null) { install = LocalPaths.Normalize(install); LocalPaths.RequireNoLinks(install); }
                if (install is null || name is null || !Directory.Exists(install)) continue;
                if (!LocalPaths.IsInside(install, root)) continue;
                names[Path.GetFullPath(install)] = name;
            }
            catch { }
        }
    }

    static void AddGogNames(string root, Dictionary<string, string> names)
    {
        try
        {
            using var baseKey = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\GOG.com\Games");
            if (baseKey is null) return;
            foreach (var subkeyName in baseKey.GetSubKeyNames().Take(2048))
            {
                using var subkey = baseKey.OpenSubKey(subkeyName);
                var install = subkey?.GetValue("path") as string;
                var name = subkey?.GetValue("gameName") as string;
                if (install is not null) { install = LocalPaths.Normalize(install); LocalPaths.RequireNoLinks(install); }
                if (install is null || name is null || !Directory.Exists(install)) continue;
                if (!LocalPaths.IsInside(install, root)) continue;
                names[Path.GetFullPath(install)] = name;
            }
        }
        catch { }
    }

    static IEnumerable<string> SafeFiles(string root, string pattern, int maxDepth, CancellationToken cancellationToken)
    {
        var queue = new Queue<(string Dir, int Depth)>();
        queue.Enqueue((root, 0));
        var directories = 1;
        var entriesLeft = 20000;
        while (queue.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (dir, depth) = queue.Dequeue();
            FileSystemInfo[] entries;
            try { entries = new DirectoryInfo(dir).EnumerateFileSystemInfos().Take(entriesLeft + 1).ToArray(); }
            catch { continue; }

            foreach (var entry in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (entriesLeft-- <= 0) yield break;
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                if (entry is DirectoryInfo subdir)
                {
                    if (depth < maxDepth && directories < 1024 && !ShouldSkipDirectory(subdir, root))
                    { queue.Enqueue((subdir.FullName, depth + 1)); directories++; }
                }
                else if (entry is FileInfo file && FilePatternMatches(file.Name, pattern))
                {
                    yield return file.FullName;
                }
            }
        }
    }

    static bool FilePatternMatches(string fileName, string pattern)
    {
        var regex = "^" + Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".") + "$";
        return Regex.IsMatch(fileName, regex, RegexOptions.IgnoreCase);
    }

    static string GetDisplayName(string gameRoot, string exePath, Dictionary<string, string> knownNames)
    {
        var fullRoot = Path.GetFullPath(gameRoot);
        if (knownNames.TryGetValue(fullRoot, out var known)) return known;

        foreach (var item in knownNames)
        {
            if (Path.GetFullPath(exePath).StartsWith(item.Key.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))
                return item.Value;
        }

        var versionName = VersionName(exePath);
        if (!string.IsNullOrWhiteSpace(versionName)) return versionName;

        return CleanName(Path.GetFileName(gameRoot));
    }

    static string? VersionName(string exePath)
    {
        try
        {
            var info = System.Diagnostics.FileVersionInfo.GetVersionInfo(exePath);
            foreach (var value in new[] { info.ProductName, info.FileDescription, info.InternalName, info.OriginalFilename })
            {
                if (string.IsNullOrWhiteSpace(value)) continue;
                var cleaned = CleanName(value);
                if (cleaned.Length >= 3 && !NotUsefulVersionName().IsMatch(cleaned))
                    return cleaned;
            }
        }
        catch { }

        return null;
    }

    static string CleanName(string value)
    {
        var name = Path.GetFileNameWithoutExtension(value).Replace('_', ' ').Replace('.', ' ').Trim();
        name = Regex.Replace(name, @"\s+", " ");
        name = Regex.Replace(name, @"\s*[-_ ]?(win64|win32|x64|x86|shipping|final|retail|launcher|binary|binaries)\s*$", "", RegexOptions.IgnoreCase).Trim();
        return name;
    }

    static string? KeyValue(string text, string key)
    {
        var match = Regex.Match(text, "\"" + Regex.Escape(key) + "\"\\s+\"([^\"]+)\"", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value : null;
    }

    [GeneratedRegex("^(gameoverlay|.*overlay\\.runtime|qtwebengineprocess|galaxyclient|vortex|start_protected_game|unins|setup|install|vcredist|vc_redist|dxsetup|dxwebsetup|oalinst|uninstall|unitycrashhandler|crashreport|crashreportclient|crashhandler|crashpad|crashpad_handler|crash_report_sender|.*crash.*report|easyanticheat|eac|battleye|be_service|launcher|.*launcher.*|activation|patch|.*patch.*|update|updater|.*update.*|dotnetfx|touchup|autorun|autoplay|helper|service|cleanup|dgvoodoocpl|dgvoodoo|reshade_setup|reshade|dlss5compatapp|dlss5[\\s_-]*swapper|steam|steamservice|steamwebhelper|streaming_client|ffmpeg|ffprobe|gldriverquery|vulkandriverquery.*|fossilize-replay.*|adapter_info|cefclient|cefsubprocess|unrealcefsubprocess|epicwebhelper|ueprereq|ue4prereq|dnspy|sb3utility|addonmanager|zfgamebrowser|vconsole.*|worldedit|worlded|staredit|.*compiler|.*viewer|.*editor|.*uploader|.*builder|.*tool|.*workload.*|.*\\.extprotocol)", RegexOptions.IgnoreCase)]
    private static partial Regex NotGameExe();

    [GeneratedRegex("^(application|game|launcher|bootstrapper|setup|installer|client|helper|service|unreal engine|unity|unityplayer|microsoft|nvidia|epic games launcher)$", RegexOptions.IgnoreCase)]
    private static partial Regex NotUsefulVersionName();

    [GeneratedRegex("(^|[^a-z])(launcher|updater?|patcher?|prepatch|bootstrapper|installer|uninstaller|config|configuration|settings|setup|server|dedicated server|crash|reporter|editor|world\\s*ed|star\\s*edit|map\\s*ed|script\\s*maker|viewer|compiler|builder|uploader|workload|video test|diagnostic|benchmark workload|helper|service)($|[^a-z])", RegexOptions.IgnoreCase)]
    private static partial Regex NonGameVersionMetadata();

    [GeneratedRegex("^(dxinfo|dxr_info|slinfo|glview|cefsimple|fossilize-replay.*|vulkandriverquery.*|gldriverquery.*|adapter_info|ffmpeg|ffprobe|world\\s*ed|worlded|worldedit|staredit|instcc|.*world\\s*editor.*|.*launcher.*|.*updater?.*|.*patcher?.*|.*prepatch.*|.*scr(ipts?)?maker.*|.*map(ed|editor)?|.*modtool.*|.*dedicated.*server.*|.*workload.*|.*crash.*)$", RegexOptions.IgnoreCase)]
    private static partial Regex CommonToolExe();

    [GeneratedRegex("^(tools?|support|redist|prereq|prerequisites|installers?|patch(es)?|updates?|mpqview|sdk|steamworks shared|maped[0-9]*|mapeditor|editors?|cef)$", RegexOptions.IgnoreCase)]
    private static partial Regex UtilityPathSegment();

    [GeneratedRegex("^(bnupdate|lodpatch|mpqview|scrmaker|civ2map|instcc|.*config.*|.*cfg|.*setup.*|.*helper.*|.*service.*)$", RegexOptions.IgnoreCase)]
    private static partial Regex TinyHelperName();

    sealed record ApiDetection(GraphicsApi Api, string Via);
}
