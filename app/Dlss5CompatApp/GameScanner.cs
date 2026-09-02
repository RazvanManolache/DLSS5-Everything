using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Dlss5CompatApp;

static partial class GameScanner
{
    static readonly string[] ApiMarkers =
    [
        "D3D12CreateDevice",
        "D3D11CreateDevice",
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
        "vkCreateInstance",
        "wglCreateContext",
        "glBindTexture",
        "Initializing OpenGL display"
    ];

    static readonly HashSet<string> SkipDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        "_dlss5_backup", "_dlss5_compat_backup", "reshade-shaders", "node_modules", ".git",
        "_redist", "prerequisites", "directx", "redist", "_commonredist", "dotnet",
        "installer", "installers", "support", "tools", "[modding] tools", "modding tools", "vcredist", "_support", "directx_redist",
        "easyanticheat", "battleye", "backup", "backups", "_backup", "bak", "old", "original", "originals",
        "_repos", "payload", "addons", "vendor", "dist", "publish", "runtime", "host64",
        "source", "src", "obj", "docs", "screenshots", "streamline", "dgvoodoo2",
        "reshade", "rtx_remix_downloads", "dxwrapper_downloads", "local-release"
    };

    public static async Task<IReadOnlyList<GameCandidate>> ScanAsync(string root, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        return await Task.Run(() =>
        {
            var candidates = new List<GameCandidate>();
            var knownNames = LoadKnownGameNames(root);
            foreach (var file in EnumerateExecutables(root, 10, cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var name = Path.GetFileName(file);
                if (NotGameExe().IsMatch(name)) continue;

                long size;
                try { size = new FileInfo(file).Length; }
                catch { continue; }
                var smallGameLauncher = IsSmallGameLauncherCandidate(file);
                if (size < 128 * 1024 && !smallGameLauncher) continue;
                if (!smallGameLauncher && LooksLikeNonGameExecutable(file, size)) continue;

                var arch = PeReader.GetArchitecture(file);
                if (arch == CpuArch.Unknown) continue;

                var imports = PeReader.GetImports(file);
                var detections = DetectApis(file, imports);
                var detected = ChoosePreferredApi(detections, arch);
                if (detected.Api == GraphicsApi.Unknown && !LooksLikeGameExecutable(file)) continue;

                var gameRoot = GuessGameRoot(root, file);
                var displayName = GetDisplayName(gameRoot, file, knownNames);
                var suggestedArgumentsByApi = SuggestedArgumentsByApi(file, detections, arch);
                candidates.Add(new GameCandidate(gameRoot, file, displayName, name, arch, detected.Api, DetectionSummary(detections, detected), size, PossibleApis(detections, arch), SuggestedArguments(detected.Api, suggestedArgumentsByApi), suggestedArgumentsByApi));
                if (candidates.Count % 10 == 0) progress?.Report($"Found {candidates.Count} compatible candidate(s)...");
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

    public static GameCandidate? ScanSingleExe(string exePath)
    {
        if (!File.Exists(exePath)) return null;
        if (NotGameExe().IsMatch(Path.GetFileName(exePath))) return null;
        long size;
        try { size = new FileInfo(exePath).Length; }
        catch { return null; }
        var smallGameLauncher = IsSmallGameLauncherCandidate(exePath);
        if ((size < 128 * 1024 && !smallGameLauncher) || (!smallGameLauncher && LooksLikeNonGameExecutable(exePath, size))) return null;
        var arch = PeReader.GetArchitecture(exePath);
        var imports = PeReader.GetImports(exePath);
        var detections = DetectApis(exePath, imports);
        var detected = ChoosePreferredApi(detections, arch);
        if (arch == CpuArch.Unknown) return null;
        if (detected.Api == GraphicsApi.Unknown && !LooksLikeGameExecutable(exePath)) return null;
        var gameRoot = Path.GetDirectoryName(exePath)!;
        var suggestedArgumentsByApi = SuggestedArgumentsByApi(exePath, detections, arch);
        return new GameCandidate(gameRoot, exePath, GetDisplayName(gameRoot, exePath, LoadKnownGameNames(gameRoot)), Path.GetFileName(exePath), arch, detected.Api, DetectionSummary(detections, detected), size, PossibleApis(detections, arch), SuggestedArguments(detected.Api, suggestedArgumentsByApi), suggestedArgumentsByApi);
    }

    static IEnumerable<string> EnumerateExecutables(string root, int maxDepth, CancellationToken cancellationToken)
    {
        var queue = new Queue<(string Dir, int Depth)>();
        queue.Enqueue((root, 0));

        while (queue.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (dir, depth) = queue.Dequeue();
            IEnumerable<FileSystemInfo> entries;
            try { entries = new DirectoryInfo(dir).EnumerateFileSystemInfos(); }
            catch { continue; }

            foreach (var entry in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (entry is DirectoryInfo subdir)
                {
                    if (depth < maxDepth && !ShouldSkipDirectory(subdir))
                        queue.Enqueue((subdir.FullName, depth + 1));
                }
                else if (entry is FileInfo file && file.Extension.Equals(".exe", StringComparison.OrdinalIgnoreCase))
                {
                    yield return file.FullName;
                }
            }
        }
    }

    static (GraphicsApi Api, string Via) ChoosePreferredApi(IReadOnlyList<ApiDetection> detections, CpuArch arch)
    {
        var apis = detections.Select(x => x.Api).ToHashSet();
        var preferredApis = arch == CpuArch.X86
            ? new[]
            {
                GraphicsApi.DirectX11,
                GraphicsApi.Dxgi,
                GraphicsApi.DirectX9,
                GraphicsApi.OpenGl,
                GraphicsApi.Glide245,
                GraphicsApi.Glide31,
                GraphicsApi.Glide211,
                GraphicsApi.Glide31Napalm,
                GraphicsApi.DirectX8,
                GraphicsApi.DirectX8DgVoodoo,
                GraphicsApi.DirectX12,
                GraphicsApi.Vulkan,
                GraphicsApi.DirectX7OrOlder
            }
            : new[]
            {
                GraphicsApi.DirectX12,
                GraphicsApi.DirectX11,
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

        return (GraphicsApi.Unknown, "none");
    }

    static (GraphicsApi Api, string Via) FirstDetection(IReadOnlyList<ApiDetection> detections, GraphicsApi api)
    {
        var item = detections.First(x => x.Api == api);
        return (item.Api, item.Via);
    }

    static IReadOnlyList<GraphicsApi> PossibleApis(IReadOnlyList<ApiDetection> detections, CpuArch arch)
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
            .ToArray();
    }

    static int ApiSortKey(GraphicsApi api) => api switch
    {
        GraphicsApi.DirectX12 => 0,
        GraphicsApi.DirectX11 => 1,
        GraphicsApi.Dxgi => 2,
        GraphicsApi.DirectX9 => 3,
        GraphicsApi.OpenGl => 4,
        GraphicsApi.Vulkan => 5,
        GraphicsApi.Glide245 => 6,
        GraphicsApi.Glide31 => 7,
        GraphicsApi.Glide31Napalm => 8,
        GraphicsApi.Glide211 => 9,
        GraphicsApi.DirectX8 => 10,
        GraphicsApi.DirectX8DgVoodoo => 11,
        GraphicsApi.DirectX7OrOlder => 12,
        _ => 99
    };

    static string DetectionSummary(IReadOnlyList<ApiDetection> detections, (GraphicsApi Api, string Via) chosen)
    {
        var sources = detections
            .Where(x => x.Api != GraphicsApi.Unknown)
            .Select(x => x.Via)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(5)
            .ToArray();
        return sources.Length == 0 ? chosen.Via : string.Join(", ", sources);
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

    static string SuggestedArguments(string file, GraphicsApi api, IReadOnlyList<ApiDetection> detections)
    {
        if (api == GraphicsApi.OpenGl)
        {
            var hasOlderDirectX = detections.Any(x => x.Api is GraphicsApi.DirectX8 or GraphicsApi.DirectX7OrOlder);
            if (!hasOlderDirectX)
                return "";

            if (HasRendererMarker(file, "-opengl"))
                return "-opengl";

            return Path.GetFileName(file).Equals("war3.exe", StringComparison.OrdinalIgnoreCase) ? "-opengl" : "";
        }

        if (api is not (GraphicsApi.Glide211 or GraphicsApi.Glide245 or GraphicsApi.Glide31 or GraphicsApi.Glide31Napalm))
            return "";

        if (HasRendererMarker(file, "-3dfx", "VIDEO\\3DFX", "VIDEO/3DFX", "3DFX"))
            return "-3dfx -w";

        return "";
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

        var inspectImportedSiblings = !detections.Any(x => x.Api is GraphicsApi.DirectX12 or GraphicsApi.DirectX11 or GraphicsApi.Dxgi or GraphicsApi.DirectX9 or GraphicsApi.OpenGl or GraphicsApi.Vulkan or GraphicsApi.DirectX8);
        if (inspectImportedSiblings)
        {
            foreach (var import in imports.Take(80))
            {
                if (IsGraphicsProxyOrAppDll(import))
                    continue;
                var sibling = Path.Combine(dir, import);
                if (!File.Exists(sibling)) continue;
                foreach (var item in DetectApis(sibling, PeReader.GetImports(sibling), inspectModules: false))
                    detections.Add(item with { Via = "module:" + import + "/" + item.Via });
            }
        }

        return detections
            .GroupBy(x => new { x.Api, x.Via })
            .Select(g => g.First())
            .ToArray();
    }

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
        var hasNewerImport =
            importSet.Contains("d3d12.dll") ||
            importSet.Contains("d3d11.dll") ||
            importSet.Contains("dxgi.dll") ||
            importSet.Contains("d3d9.dll") ||
            importSet.Contains("opengl32.dll") ||
            importSet.Contains("vulkan-1.dll") ||
            importSet.Contains("glide.dll") ||
            importSet.Contains("glide2x.dll") ||
            importSet.Contains("glide3x.dll") ||
            importSet.Contains("d3d8.dll") ||
            importSet.Contains("d3d8thk.dll");
        if (importSet.Contains("d3d12.dll")) detections.Add(new ApiDetection(GraphicsApi.DirectX12, via));
        if (importSet.Contains("d3d11.dll")) detections.Add(new ApiDetection(GraphicsApi.DirectX11, via));
        if (importSet.Contains("dxgi.dll")) detections.Add(new ApiDetection(GraphicsApi.Dxgi, via));
        if (importSet.Contains("d3d9.dll")) detections.Add(new ApiDetection(GraphicsApi.DirectX9, via));
        if (importSet.Contains("opengl32.dll")) detections.Add(new ApiDetection(GraphicsApi.OpenGl, via));
        if (importSet.Contains("vulkan-1.dll")) detections.Add(new ApiDetection(GraphicsApi.Vulkan, via));
        if (importSet.Contains("glide.dll")) detections.Add(new ApiDetection(GraphicsApi.Glide211, via));
        if (importSet.Contains("glide2x.dll")) detections.Add(new ApiDetection(GraphicsApi.Glide245, via));
        if (importSet.Contains("glide3x.dll")) detections.Add(new ApiDetection(GraphicsApi.Glide31, via));
        if (importSet.Contains("d3d8.dll") || importSet.Contains("d3d8thk.dll")) detections.Add(new ApiDetection(GraphicsApi.DirectX8, via));
        if (!hasNewerImport && (importSet.Contains("ddraw.dll") ||
            importSet.Contains("d3dim.dll") ||
            importSet.Contains("d3dim700.dll") ||
            importSet.Contains("d3drm.dll")))
            detections.Add(new ApiDetection(GraphicsApi.DirectX7OrOlder, via));

        var markers = PeReader.FindMarkers(file, ApiMarkers);
        var hasNewerMarker =
            markers.Contains("D3D12CreateDevice") ||
            markers.Contains("D3D11CreateDevice") ||
            markers.Contains("CreateDXGIFactory") ||
            markers.Contains("Direct3DCreate9") ||
            markers.Contains("Direct3DCreate8") ||
            markers.Contains("vkCreateInstance") ||
            markers.Contains("wglCreateContext") ||
            markers.Contains("glBindTexture") ||
            markers.Contains("Initializing OpenGL display") ||
            markers.Contains("glide.dll") ||
            markers.Contains("glide2x.dll") ||
            markers.Contains("glide3x.dll") ||
            markers.Contains("grGlideInit") ||
            markers.Contains("grSstWinOpen");
        if (markers.Contains("D3D12CreateDevice")) detections.Add(new ApiDetection(GraphicsApi.DirectX12, "strings"));
        if (markers.Contains("D3D11CreateDevice")) detections.Add(new ApiDetection(GraphicsApi.DirectX11, "strings"));
        if (markers.Contains("CreateDXGIFactory")) detections.Add(new ApiDetection(GraphicsApi.Dxgi, "strings"));
        if (markers.Contains("Direct3DCreate9")) detections.Add(new ApiDetection(GraphicsApi.DirectX9, "strings"));
        if (markers.Contains("Direct3DCreate8")) detections.Add(new ApiDetection(GraphicsApi.DirectX8, "strings"));
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
            "renderer_opengl2_x86.dll"
        })
        {
            if (self.Equals(name, StringComparison.OrdinalIgnoreCase)) continue;
            var path = Path.Combine(dir, name);
            if (File.Exists(path)) yield return path;
        }
    }

    static bool ShouldSkipDirectory(DirectoryInfo dir)
    {
        if (SkipDirectories.Contains(dir.Name)) return true;
        if (dir.Name.StartsWith(".", StringComparison.Ordinal) && !dir.Name.Equals(".steam", StringComparison.OrdinalIgnoreCase)) return true;
        if (dir.Name.Contains("backup", StringComparison.OrdinalIgnoreCase)) return true;
        if (dir.Name.Contains("swapper", StringComparison.OrdinalIgnoreCase)) return true;
        if (dir.Name.StartsWith("DLSS5-Feeder-clean", StringComparison.OrdinalIgnoreCase)) return true;
        if (dir.FullName.Contains(@"\AppData\", StringComparison.OrdinalIgnoreCase)) return true;
        if (dir.FullName.Contains(@"\Windows\", StringComparison.OrdinalIgnoreCase)) return true;
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

        if (HasLocalRendererModuleEvidence(file, dir))
            return true;

        return IsDiabloLikeLayout(file, dir);
    }

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
        if (PeReader.FindMarkers(file, markers).Count > 0)
            return true;

        var dir = Path.GetDirectoryName(file);
        if (dir is null) return false;

        foreach (var module in KnownRendererModules(file, dir))
        {
            if (PeReader.FindMarkers(module, markers).Count > 0)
                return true;
        }

        return false;
    }

    static bool IsDiabloLikeLayout(string file, string dir)
    {
        var name = Path.GetFileName(file);
        if (!name.Equals("Game.exe", StringComparison.OrdinalIgnoreCase) &&
            !name.Equals("D2MultiResGame.exe", StringComparison.OrdinalIgnoreCase) &&
            !name.Equals("Diablo II.exe", StringComparison.OrdinalIgnoreCase))
            return false;

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

    static Dictionary<string, string> LoadKnownGameNames(string root)
    {
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        AddSteamNames(root, names);
        AddEpicNames(root, names);
        AddGogNames(root, names);
        return names;
    }

    static void AddSteamNames(string root, Dictionary<string, string> names)
    {
        foreach (var manifest in SafeFiles(root, "appmanifest_*.acf", 7))
        {
            string text;
            try { text = File.ReadAllText(manifest); }
            catch { continue; }

            var installDir = KeyValue(text, "installdir");
            var name = KeyValue(text, "name");
            var steamapps = Directory.GetParent(manifest)?.FullName;
            if (installDir is null || name is null || steamapps is null) continue;
            var gameDir = Path.Combine(steamapps, "common", installDir);
            if (Directory.Exists(gameDir)) names[Path.GetFullPath(gameDir)] = name;
        }
    }

    static void AddEpicNames(string root, Dictionary<string, string> names)
    {
        var manifestDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Epic", "EpicGamesLauncher", "Data", "Manifests");
        if (!Directory.Exists(manifestDir)) return;
        foreach (var file in Directory.EnumerateFiles(manifestDir, "*.item"))
        {
            try
            {
                var text = File.ReadAllText(file);
                var install = JsonValue(text, "InstallLocation");
                var name = JsonValue(text, "DisplayName");
                if (install is null || name is null || !Directory.Exists(install)) continue;
                if (!Path.GetFullPath(install).StartsWith(Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase)) continue;
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
            foreach (var subkeyName in baseKey.GetSubKeyNames())
            {
                using var subkey = baseKey.OpenSubKey(subkeyName);
                var install = subkey?.GetValue("path") as string;
                var name = subkey?.GetValue("gameName") as string;
                if (install is null || name is null || !Directory.Exists(install)) continue;
                if (!Path.GetFullPath(install).StartsWith(Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase)) continue;
                names[Path.GetFullPath(install)] = name;
            }
        }
        catch { }
    }

    static IEnumerable<string> SafeFiles(string root, string pattern, int maxDepth)
    {
        var queue = new Queue<(string Dir, int Depth)>();
        queue.Enqueue((root, 0));
        while (queue.Count > 0)
        {
            var (dir, depth) = queue.Dequeue();
            IEnumerable<FileSystemInfo> entries;
            try { entries = new DirectoryInfo(dir).EnumerateFileSystemInfos(); }
            catch { continue; }

            foreach (var entry in entries)
            {
                if (entry is DirectoryInfo subdir)
                {
                    if (depth < maxDepth && !ShouldSkipDirectory(subdir))
                        queue.Enqueue((subdir.FullName, depth + 1));
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

    static string? JsonValue(string text, string key)
    {
        var match = Regex.Match(text, "\"" + Regex.Escape(key) + "\"\\s*:\\s*\"([^\"]+)\"", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value.Replace(@"\\", @"\") : null;
    }

    [GeneratedRegex("^(unins|setup|install|vcredist|vc_redist|dxsetup|dxwebsetup|oalinst|uninstall|unitycrashhandler|crashreport|crashreportclient|crashhandler|crashpad|crashpad_handler|crash_report_sender|.*crash.*report|easyanticheat|eac|battleye|be_service|launcher|.*launcher.*|activation|patch|.*patch.*|update|updater|.*update.*|dotnetfx|touchup|autorun|autoplay|helper|service|cleanup|dgvoodoocpl|dgvoodoo|reshade_setup|reshade|dlss5compatapp|dlss5[\\s_-]*swapper|steam|steamservice|steamwebhelper|streaming_client|ffmpeg|ffprobe|gldriverquery|vulkandriverquery.*|fossilize-replay.*|adapter_info|cefclient|cefsubprocess|unrealcefsubprocess|epicwebhelper|ueprereq|ue4prereq|dnspy|sb3utility|addonmanager|zfgamebrowser|vconsole.*|worldedit|worlded|staredit|.*compiler|.*viewer|.*editor|.*uploader|.*builder|.*tool|.*workload.*|.*\\.extprotocol)", RegexOptions.IgnoreCase)]
    private static partial Regex NotGameExe();

    [GeneratedRegex("^(application|game|launcher|bootstrapper|setup|installer|client|helper|service|unreal engine|unity|unityplayer|microsoft|nvidia|epic games launcher)$", RegexOptions.IgnoreCase)]
    private static partial Regex NotUsefulVersionName();

    [GeneratedRegex("(^|[^a-z])(launcher|updater?|patcher?|prepatch|bootstrapper|installer|uninstaller|config|configuration|settings|setup|server|dedicated server|crash|reporter|editor|world\\s*ed|star\\s*edit|map\\s*ed|script\\s*maker|viewer|compiler|builder|uploader|workload|video test|diagnostic|benchmark workload|helper|service)($|[^a-z])", RegexOptions.IgnoreCase)]
    private static partial Regex NonGameVersionMetadata();

    [GeneratedRegex("^(fossilize-replay.*|vulkandriverquery.*|gldriverquery.*|adapter_info|ffmpeg|ffprobe|world\\s*ed|worlded|worldedit|staredit|instcc|.*world\\s*editor.*|.*launcher.*|.*updater?.*|.*patcher?.*|.*prepatch.*|.*scr(ipts?)?maker.*|.*map(ed|editor)?|.*modtool.*|.*dedicated.*server.*|.*workload.*|.*crash.*)$", RegexOptions.IgnoreCase)]
    private static partial Regex CommonToolExe();

    [GeneratedRegex("^(tools?|support|redist|prereq|prerequisites|installers?|patch(es)?|updates?|mpqview|sdk|bin64|steamworks shared)$", RegexOptions.IgnoreCase)]
    private static partial Regex UtilityPathSegment();

    [GeneratedRegex("^(bnupdate|lodpatch|mpqview|scrmaker|civ2map|instcc|.*config.*|.*setup.*|.*helper.*|.*service.*)$", RegexOptions.IgnoreCase)]
    private static partial Regex TinyHelperName();

    sealed record ApiDetection(GraphicsApi Api, string Via);
}
