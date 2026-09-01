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
        "vkCreateInstance",
        "wglCreateContext"
    ];

    static readonly HashSet<string> SkipDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        "_dlss5_backup", "_dlss5_compat_backup", "reshade-shaders", "node_modules", ".git",
        "_redist", "prerequisites", "directx", "redist", "_commonredist", "dotnet",
        "installer", "installers", "support", "vcredist", "_support", "directx_redist",
        "easyanticheat", "battleye", "backup", "backups", "_backup", "bak", "old", "original", "originals",
        "_repos", "payload", "addons", "vendor", "dist", "publish", "runtime", "host64",
        "source", "src", "obj", "bin", "docs", "screenshots", "streamline", "dgvoodoo2",
        "reshade", "rtx_remix_downloads", "dxwrapper_downloads", "local-release"
    };

    public static async Task<IReadOnlyList<GameCandidate>> ScanAsync(string root, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        return await Task.Run(() =>
        {
            var candidates = new List<GameCandidate>();
            var knownNames = LoadKnownGameNames(root);
            foreach (var file in EnumerateExecutables(root, 6, cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var name = Path.GetFileName(file);
                if (NotGameExe().IsMatch(name)) continue;

                long size;
                try { size = new FileInfo(file).Length; }
                catch { continue; }
                if (size < 256 * 1024) continue;

                var arch = PeReader.GetArchitecture(file);
                if (arch == CpuArch.Unknown) continue;

                var imports = PeReader.GetImports(file);
                var detected = DetectApi(file, imports);
                if (detected.Api is GraphicsApi.Unknown or GraphicsApi.Vulkan or GraphicsApi.OpenGl) continue;

                var gameRoot = GuessGameRoot(root, file);
                var displayName = GetDisplayName(gameRoot, file, knownNames);
                candidates.Add(new GameCandidate(gameRoot, file, displayName, name, arch, detected.Api, detected.Via, size));
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
        var arch = PeReader.GetArchitecture(exePath);
        var imports = PeReader.GetImports(exePath);
        var detected = DetectApi(exePath, imports);
        if (arch == CpuArch.Unknown || detected.Api == GraphicsApi.Unknown) return null;
        var gameRoot = Path.GetDirectoryName(exePath)!;
        return new GameCandidate(gameRoot, exePath, GetDisplayName(gameRoot, exePath, LoadKnownGameNames(gameRoot)), Path.GetFileName(exePath), arch, detected.Api, detected.Via, new FileInfo(exePath).Length);
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

    static (GraphicsApi Api, string Via) DetectApi(string file, IReadOnlyList<string> imports)
    {
        var importSet = imports.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (importSet.Contains("d3d12.dll")) return (GraphicsApi.DirectX12, "imports");
        if (importSet.Contains("d3d11.dll")) return (GraphicsApi.DirectX11, "imports");
        if (importSet.Contains("dxgi.dll")) return (GraphicsApi.Dxgi, "imports");
        if (importSet.Contains("d3d9.dll")) return (GraphicsApi.DirectX9, "imports");
        if (importSet.Contains("vulkan-1.dll")) return (GraphicsApi.Vulkan, "imports");
        if (importSet.Contains("opengl32.dll")) return (GraphicsApi.OpenGl, "imports");

        var markers = PeReader.FindMarkers(file, ApiMarkers);
        if (markers.Contains("D3D12CreateDevice")) return (GraphicsApi.DirectX12, "strings");
        if (markers.Contains("D3D11CreateDevice")) return (GraphicsApi.DirectX11, "strings");
        if (markers.Contains("CreateDXGIFactory")) return (GraphicsApi.Dxgi, "strings");
        if (markers.Contains("Direct3DCreate9")) return (GraphicsApi.DirectX9, "strings");
        if (markers.Contains("vkCreateInstance")) return (GraphicsApi.Vulkan, "strings");
        if (markers.Contains("wglCreateContext")) return (GraphicsApi.OpenGl, "strings");

        var dir = Path.GetDirectoryName(file);
        if (dir is null) return (GraphicsApi.Unknown, "none");

        foreach (var import in imports.Take(80))
        {
            var sibling = Path.Combine(dir, import);
            if (!File.Exists(sibling)) continue;
            var inner = DetectApi(sibling, PeReader.GetImports(sibling));
            if (inner.Api != GraphicsApi.Unknown)
                return (inner.Api, "module:" + import);
        }

        return (GraphicsApi.Unknown, "none");
    }

    static bool ShouldSkipDirectory(DirectoryInfo dir)
    {
        if (SkipDirectories.Contains(dir.Name)) return true;
        if (dir.Name.StartsWith(".", StringComparison.Ordinal) && !dir.Name.Equals(".steam", StringComparison.OrdinalIgnoreCase)) return true;
        if (dir.Name.StartsWith("_", StringComparison.Ordinal) && !dir.Name.StartsWith("_retail_", StringComparison.OrdinalIgnoreCase)) return true;
        if (dir.Name.Contains("backup", StringComparison.OrdinalIgnoreCase)) return true;
        if (dir.Name.Contains("swapper", StringComparison.OrdinalIgnoreCase)) return true;
        if (dir.Name.StartsWith("DLSS5-Feeder-clean", StringComparison.OrdinalIgnoreCase)) return true;
        if (dir.FullName.Contains(@"\AppData\", StringComparison.OrdinalIgnoreCase)) return true;
        if (dir.FullName.Contains(@"\Windows\", StringComparison.OrdinalIgnoreCase)) return true;
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
            foreach (var value in new[] { info.ProductName, info.FileDescription })
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
        var name = value.Replace('_', ' ').Replace('.', ' ').Trim();
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

    [GeneratedRegex("^(unins|setup|install|vcredist|vc_redist|dxsetup|dxwebsetup|oalinst|uninstall|crashreport|crashhandler|crashpad|easyanticheat|eac|battleye|be_service|launcher|activation|patch|update|dotnetfx|touchup|autorun|autoplay|helper|service|cleanup|dgvoodoocpl|dgvoodoo|reshade_setup|reshade|dlss5compatapp|dlss5[\\s_-]*swapper|steam|steamservice|steamwebhelper|streaming_client|gldriverquery|adapter_info|cefclient|cefsubprocess)", RegexOptions.IgnoreCase)]
    private static partial Regex NotGameExe();

    [GeneratedRegex("^(application|game|launcher|bootstrapper|setup|installer|client|helper|service)$", RegexOptions.IgnoreCase)]
    private static partial Regex NotUsefulVersionName();
}
