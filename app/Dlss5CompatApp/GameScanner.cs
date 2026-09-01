using System.Text.RegularExpressions;

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
        "easyanticheat", "battleye", "backup", "backups", "_backup", "bak", "old", "original", "originals"
    };

    public static async Task<IReadOnlyList<GameCandidate>> ScanAsync(string root, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        return await Task.Run(() =>
        {
            var candidates = new List<GameCandidate>();
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
                candidates.Add(new GameCandidate(gameRoot, file, name, arch, detected.Api, detected.Via, size));
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
        return new GameCandidate(Path.GetDirectoryName(exePath)!, exePath, Path.GetFileName(exePath), arch, detected.Api, detected.Via, new FileInfo(exePath).Length);
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
                    if (depth < maxDepth && !SkipDirectories.Contains(subdir.Name))
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

    [GeneratedRegex("^(unins|setup|install|vcredist|vc_redist|dxsetup|dxwebsetup|oalinst|uninstall|crashreport|crashhandler|easyanticheat|eac|battleye|be_service|launcher|activation|patch|update|dotnetfx|touchup|autorun|autoplay|helper|service|cleanup)", RegexOptions.IgnoreCase)]
    private static partial Regex NotGameExe();
}
