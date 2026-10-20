using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace Dlss5CompatApp;

sealed record EngineBootstrapPlan(string Bootstrap, string WorkingDirectory, string Arguments);

/// <summary>Reads the target and command line embedded in an engine bootstrap.
/// Resource-only mapping never executes its code. A candidate must name the
/// exact selected renderer executable; no game-title rules are involved.</summary>
static class EngineBootstrap
{
    public static EngineBootstrapPlan? Find(string executable, Func<string, int, string?>? readResource = null)
    {
        if (FindSourceContent(executable) is { } source) return source;
        var binaryDirectory = new DirectoryInfo(Path.GetDirectoryName(Path.GetFullPath(executable))!);
        var binaries = binaryDirectory.Parent;
        if (!(binaryDirectory.Name.Equals("Win64", StringComparison.OrdinalIgnoreCase) ||
                binaryDirectory.Name.Equals("Win32", StringComparison.OrdinalIgnoreCase)) ||
            binaries is null || !binaries.Name.Equals("Binaries", StringComparison.OrdinalIgnoreCase)) return null;
        readResource ??= ReadText;
        var directory = binaries.Parent;
        for (var level = 0; directory is not null && level < 2; ++level, directory = directory.Parent)
        {
            LocalPaths.RequireNoLinks(directory.FullName);
            foreach (var file in Directory.EnumerateFiles(directory.FullName, "*.exe").Take(128))
            {
                if (LocalPaths.Same(file, executable) || new FileInfo(file).Length > 4 * 1024 * 1024 ||
                    PeReader.GetArchitecture(file) == CpuArch.Unknown) continue;
                LocalPaths.RequireNoLinks(file);
                var target = readResource(file, 201);
                var arguments = readResource(file, 202);
                if (target is null || arguments is null || Path.IsPathFullyQualified(target) ||
                    target.IndexOfAny(['\0', '\r', '\n', ':']) >= 0 || arguments.IndexOfAny(['\0', '\r', '\n']) >= 0) continue;
                var resolved = Path.GetFullPath(target, directory.FullName);
                if (LocalPaths.IsInside(resolved, directory.FullName) && LocalPaths.Same(resolved, executable))
                    return new(file, directory.FullName, arguments);
            }
        }
        return null;
    }

    static EngineBootstrapPlan? FindSourceContent(string executable)
    {
        var root = Path.GetDirectoryName(Path.GetFullPath(executable))!;
        var architecture = PeReader.GetArchitecture(executable);
        var engine = Path.Combine(root, "bin", "engine.dll");
        if (architecture == CpuArch.Unknown || PeReader.GetArchitecture(engine) != architecture) return null;
        try
        {
            LocalPaths.RequireNoLinks(engine);
            var candidates = new List<(string Directory, string? AppId)>();
            foreach (var directory in Directory.EnumerateDirectories(root).Take(128))
            {
                var path = Path.Combine(directory, "gameinfo.txt");
                if (!File.Exists(path)) continue;
                LocalPaths.RequireNoLinks(path);
                if (new FileInfo(path).Length > 65536) continue;
                var name = Path.GetFileName(directory);
                if (name.IndexOfAny(['"', '\0', '\r', '\n']) >= 0) continue;
                var text = File.ReadAllText(path);
                if (!Regex.IsMatch(text, @"(?im)^\s*""?GameInfo""?\s*$")) continue;
                var appId = Regex.Match(text, @"(?im)^\s*""?SteamAppId""?\s+""?(?<id>[0-9]+)""?(?=\s|$)");
                candidates.Add((name, appId.Success ? appId.Groups["id"].Value : null));
            }
            // A shared engine can ship several content directories. Prefer an
            // exact installed Steam app ID; otherwise only a unique candidate
            // is safe to choose. Display names never participate.
            var installed = InstallerEngine.FindSteamAppId(root);
            var matching = candidates.Where(candidate => installed is not null && candidate.AppId == installed).ToArray();
            var selected = matching.Length == 1 ? matching[0].Directory : candidates.Count == 1 ? candidates[0].Directory : null;
            return selected is null ? null : new(executable, root, "-game \"" + selected + "\"");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException) { return null; }
    }

    static string? ReadText(string path, int id)
    {
        var module = LoadLibraryExW(path, 0, 0x40 | 0x20); // Data/image resources, never executable code.
        if (module == 0) return null;
        try
        {
            var resource = FindResourceW(module, (nint)id, 10); // RCDATA
            if (resource == 0) return null;
            var size = SizeofResource(module, resource);
            if (size is < 2 or > 16384 || size % 2 != 0) return null;
            var data = LockResource(LoadResource(module, resource));
            if (data == 0) return null;
            var bytes = new byte[size];
            Marshal.Copy(data, bytes, 0, bytes.Length);
            return new UnicodeEncoding(false, false, true).GetString(bytes).TrimEnd('\0');
        }
        catch (DecoderFallbackException) { return null; }
        finally { FreeLibrary(module); }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern nint LoadLibraryExW(string path, nint reserved, uint flags);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern nint FindResourceW(nint module, nint name, nint type);
    [DllImport("kernel32.dll")] static extern uint SizeofResource(nint module, nint resource);
    [DllImport("kernel32.dll")] static extern nint LoadResource(nint module, nint resource);
    [DllImport("kernel32.dll")] static extern nint LockResource(nint resource);
    [DllImport("kernel32.dll")] static extern bool FreeLibrary(nint module);
}
