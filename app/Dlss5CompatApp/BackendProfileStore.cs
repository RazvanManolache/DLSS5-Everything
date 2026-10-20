using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Dlss5CompatApp;

public sealed record BackendProfileInfo(string Route, string Path, int FileCount, long ConfigurationBytes);

/// <summary>Bounded route settings, separate from the installation's game-original backup journal.</summary>
public sealed class BackendProfileStore
{
    public const int MaxFileBytes = 256 * 1024;
    public const int MaxProfileBytes = 4 * 1024 * 1024;
    public const int MaxProfiles = 32;
    public const long MaxStoreBytes = 32 * 1024 * 1024;
    static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true, MaxDepth = 12, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };
    static readonly string[] ConfigNames = ["ReShade.ini", "ReShadeVR.ini", "ReShadePreset.ini", "Phoenix.json", "dlss5-feed.cfg"];
    readonly string root;
    readonly string executable;
    readonly HashSet<string> allowed;
    public string DirectoryPath { get; }
    public IReadOnlyList<string> ConfigurationPaths { get; }

    public BackendProfileStore(string gameRoot, string exePath)
    {
        root = LocalPaths.Normalize(gameRoot);
        var exe = LocalPaths.Normalize(exePath);
        if (LocalPaths.Same(root, Path.GetPathRoot(root)!) || !Directory.Exists(root) || !File.Exists(exe) || !LocalPaths.IsInside(exe, root) || LocalPaths.Same(exe, root))
            throw new ArgumentException("An existing executable inside the game root is required.");
        LocalPaths.RequireNoLinks(exe);
        executable = Canonical(Path.GetRelativePath(root, exe));
        var dir = Path.GetDirectoryName(exe)!;
        var paths = new List<string>();
        foreach (var folder in new[] { dir, Path.Combine(dir, "host64"), Path.Combine(dir, "_DLSS5_OpenXR", "host64") })
            foreach (var name in ConfigNames) paths.Add(Canonical(Path.GetRelativePath(root, Path.Combine(folder, name))));
        ConfigurationPaths = paths.AsReadOnly();
        allowed = new(paths, StringComparer.OrdinalIgnoreCase);
        DirectoryPath = BoundedInstallPaths.Resolve(root, "_DLSS5_Compat_Backup/backend-profiles");
    }

    public string ProfilePath(string route)
    {
        ValidateRoute(route);
        // Hash both identities: no caller-controlled route or executable segment becomes a filename.
        var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(executable.ToUpperInvariant() + "|" + route.ToUpperInvariant())));
        var path = Path.Combine(DirectoryPath, id + ".json");
        LocalPaths.RequireNoLinks(path);
        return path;
    }

    public BackendProfileInfo Snapshot(string route)
    {
        var path = ProfilePath(route);
        using var lease = LocalData.Lock(DirectoryPath);
        var profile = new Profile { GameRoot = root, Executable = executable, Route = route };
        foreach (var relative in ConfigurationPaths)
        {
            var file = BoundedInstallPaths.Resolve(root, relative);
            if (Directory.Exists(file)) throw new InvalidDataException("Configuration path is a directory: " + relative);
            if (!File.Exists(file)) continue;
            var bytes = ReadBounded(file, MaxFileBytes);
            profile.Files.Add(new Entry { RelativePath = relative, Data = bytes, Sha256 = Convert.ToHexString(SHA256.HashData(bytes)) });
        }
        var serialized = JsonSerializer.SerializeToUtf8Bytes(profile, Json);
        if (serialized.Length > MaxProfileBytes) throw new InvalidDataException("Backend profile exceeds its size limit.");
        ValidateStoreBudget(path, serialized.Length);
        WriteAtomic(path, serialized);
        return new(route, path, profile.Files.Count, profile.Files.Sum(f => (long)f.Data.Length));
    }

    /// <summary>Validates every entry, identity, size and checksum before returning any configuration bytes.
    /// A missing profile returns an empty dictionary; a corrupt profile fails explicitly.</summary>
    public IReadOnlyDictionary<string, byte[]> Load(string route)
    {
        using var lease = LocalData.Lock(DirectoryPath);
        return LoadCore(route);
    }

    public IReadOnlyList<string> Restore(string route, IEnumerable<string> managedFiles)
    {
        ArgumentNullException.ThrowIfNull(managedFiles);
        using var lease = LocalData.Lock(DirectoryPath);
        var files = LoadCore(route);
        var owned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var count = 0;
        foreach (var relative in managedFiles)
        {
            if (++count > 4096) throw new InvalidDataException("Too many managed paths.");
            owned.Add(Canonical(Path.GetRelativePath(root, BoundedInstallPaths.Resolve(root, relative))));
        }
        var targets = new List<(string Relative, string Target, byte[] Data)>();
        foreach (var (relative, data) in files)
        {
            if (!owned.Contains(relative)) throw new InvalidDataException("Profile destination is not owned by the current install: " + relative);
            var target = BoundedInstallPaths.Resolve(root, relative);
            if (Directory.Exists(target)) throw new InvalidDataException("Profile destination is a directory: " + relative);
            for (var parent = Path.GetDirectoryName(target); parent is not null && LocalPaths.IsInside(parent, root); parent = Path.GetDirectoryName(parent))
                if (File.Exists(parent)) throw new InvalidDataException("Profile destination parent is a file: " + relative);
            targets.Add((relative, target, data));
        }
        // Check every destination before changing any of them. The installer journal remains responsible
        // for crash recovery and rollback if a later filesystem operation fails during the commit loop.
        var locks = new List<FileStream>();
        var staged = new List<(string Target, string Temp)>();
        try
        {
            foreach (var item in targets)
                if (File.Exists(item.Target)) locks.Add(new FileStream(item.Target, FileMode.Open, FileAccess.ReadWrite, FileShare.None));
            foreach (var item in targets)
            {
                LocalPaths.RequireNoLinks(item.Target);
                Directory.CreateDirectory(Path.GetDirectoryName(item.Target)!);
                var temp = item.Target + "." + Guid.NewGuid().ToString("N") + ".tmp";
                staged.Add((item.Target, temp));
                using var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                stream.Write(item.Data);
                stream.Flush(true);
            }
            foreach (var stream in locks) stream.Dispose();
            locks.Clear();
            foreach (var item in staged)
            {
                LocalPaths.RequireNoLinks(item.Target);
                LocalPaths.RequireNoLinks(item.Temp);
                File.Move(item.Temp, item.Target, true);
            }
            return targets.Select(t => t.Relative).ToArray();
        }
        finally
        {
            foreach (var stream in locks) stream.Dispose();
            foreach (var item in staged) if (File.Exists(item.Temp)) File.Delete(item.Temp);
        }
    }

    Dictionary<string, byte[]> LoadCore(string route)
    {
        var path = ProfilePath(route);
        var result = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        if (Directory.Exists(path)) throw new InvalidDataException("Backend profile is a directory.");
        if (!File.Exists(path)) return result;
        var bytes = ReadBounded(path, MaxProfileBytes);
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 12 });
        RejectDuplicateProperties(document.RootElement);
        var profile = JsonSerializer.Deserialize<Profile>(bytes, Json) ?? throw new InvalidDataException("Invalid backend profile.");
        if (profile.Version != 1 || !string.Equals(profile.GameRoot, root, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(profile.Executable, executable, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(profile.Route, route, StringComparison.OrdinalIgnoreCase)
            || profile.Files is null || profile.Files.Count > allowed.Count)
            throw new InvalidDataException("Backend profile identity or schema is invalid.");
        foreach (var entry in profile.Files)
        {
            if (entry is null || string.IsNullOrWhiteSpace(entry.RelativePath) || entry.Data is null || entry.Data.Length > MaxFileBytes)
                throw new InvalidDataException("Invalid backend profile entry.");
            var target = BoundedInstallPaths.Resolve(root, entry.RelativePath);
            var relative = Canonical(Path.GetRelativePath(root, target));
            if (!allowed.Contains(relative) || !result.TryAdd(relative, entry.Data)
                || !string.Equals(entry.Sha256, Convert.ToHexString(SHA256.HashData(entry.Data)), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Unapproved, duplicated, or corrupt backend profile entry: " + entry.RelativePath);
        }
        return result;
    }

    void ValidateStoreBudget(string replacement, int size)
    {
        LocalPaths.RequireNoLinks(DirectoryPath);
        if (!Directory.Exists(DirectoryPath)) return;
        long bytes = size;
        var count = 1;
        foreach (var file in Directory.EnumerateFileSystemEntries(DirectoryPath))
        {
            LocalPaths.RequireNoLinks(file);
            if (Directory.Exists(file)) throw new InvalidDataException("Unexpected directory in backend profile store.");
            if (file.Equals(replacement, StringComparison.OrdinalIgnoreCase)) continue;
            if (++count > MaxProfiles || (bytes += new FileInfo(file).Length) > MaxStoreBytes)
                throw new InvalidDataException("Backend profile store limit reached. Review saved profiles before creating more.");
        }
    }

    static byte[] ReadBounded(string path, int limit)
    {
        LocalPaths.RequireNoLinks(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > limit) throw new InvalidDataException("File exceeds the backend profile limit: " + path);
        var bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        if (stream.ReadByte() != -1) throw new InvalidDataException("File grew beyond the backend profile read bound.");
        return bytes;
    }

    static void WriteAtomic(string path, byte[] data)
    {
        LocalPaths.RequireNoLinks(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { stream.Write(data); stream.Flush(true); }
            LocalPaths.RequireNoLinks(path);
            File.Move(temp, path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    static void RejectDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new InvalidDataException("Duplicate backend profile property.");
                RejectDuplicateProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var child in element.EnumerateArray()) RejectDuplicateProperties(child);
    }

    static string Canonical(string path) => path.Replace('\\', '/');
    static void ValidateRoute(string route)
    {
        if (string.IsNullOrWhiteSpace(route) || route.Length > 64 || !char.IsAsciiLetter(route[0])
            || route.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '_' and not '-'))
            throw new ArgumentException("Use a bounded backend route identifier, such as X64DirectPhoenix.", nameof(route));
    }

    sealed class Profile
    {
        [JsonRequired] public int Version { get; set; } = 1;
        [JsonRequired] public string GameRoot { get; set; } = "";
        [JsonRequired] public string Executable { get; set; } = "";
        [JsonRequired] public string Route { get; set; } = "";
        [JsonRequired] public List<Entry> Files { get; set; } = [];
    }
    sealed class Entry
    {
        [JsonRequired] public string RelativePath { get; set; } = "";
        [JsonRequired] public byte[] Data { get; set; } = [];
        [JsonRequired] public string Sha256 { get; set; } = "";
    }
}
