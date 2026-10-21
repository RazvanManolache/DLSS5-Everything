using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Dlss5CompatApp;

sealed class LibraryState
{
    public int SchemaVersion { get; set; } = 1;
    public long Revision { get; set; }
    public List<string> ScanRoots { get; set; } = [];
    public List<string> ManualPaths { get; set; } = [];
    public List<string> CachedGamePaths { get; set; } = [];
    public List<string> HiddenPaths { get; set; } = [];
    public List<string> FavouritePaths { get; set; } = [];
    public Dictionary<string, GameProfile> Profiles { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public string Theme { get; set; } = "system";
    public bool GroupByStore { get; set; } = true;
    public string PhoenixChannel { get; set; } = "LatestPublished";
    public List<string> OptionalModelDownloads { get; set; } = [];
    public bool DownloadTinyUpscaler { get; set; }
    public GameInstallDefaults InstallationDefaults { get; set; } = new();
    public Dictionary<string, string> GameStores { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public static string DefaultPath => Path.Combine(LocalData.DirectoryPath, "library.json");

    public static LibraryState Load(string? filePath = null)
    {
        var path = LocalPaths.Normalize(filePath ?? DefaultPath);
        using var gate = LocalData.Lock(path);
        return Read(path);
    }

    public static void Save(LibraryState state, string? filePath = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        state.Validate();
        var path = LocalPaths.Normalize(filePath ?? DefaultPath);
        using var gate = LocalData.Lock(path);
        var current = Read(path);
        if (current.Revision != state.Revision)
            throw new InvalidOperationException("Library settings changed. Reload before saving.");
        var next = checked(state.Revision + 1);
        var copy = JsonSerializer.Deserialize<LibraryState>(JsonSerializer.Serialize(state, LocalData.Json), LocalData.Json)!;
        copy.Revision = next;
        LocalData.WriteAtomic(path, JsonSerializer.Serialize(copy, LocalData.Json));
        state.Revision = next;
    }

    public void Save(string? filePath = null) => Save(this, filePath);

    static LibraryState Read(string path)
    {
        if (!File.Exists(path)) return new();
        var state = JsonSerializer.Deserialize<LibraryState>(LocalData.ReadText(path), LocalData.Json)
            ?? throw new InvalidDataException("Library settings are empty.");
        state.Validate();
        return state;
    }

    void Validate()
    {
        if (SchemaVersion != 1 || Revision < 0) throw new InvalidDataException("Unsupported library settings version.");
        if (Theme is not ("light" or "dark" or "system")) throw new InvalidDataException("Theme must be light, dark or system.");
        if (PhoenixChannel is not ("LatestPublished" or "StableOnly")) throw new InvalidDataException("Invalid Phoenix DLSS5 release channel.");
        if (OptionalModelDownloads is null || OptionalModelDownloads.Count > OptionalModelLibrary.Catalog.Count ||
            OptionalModelDownloads.Any(id => !OptionalModelLibrary.Catalog.Any(p => p.Id == id)) ||
            OptionalModelDownloads.Distinct().Count() != OptionalModelDownloads.Count)
            throw new InvalidDataException("Invalid optional native model selection.");
        if (InstallationDefaults is null) throw new InvalidDataException("Missing game installation defaults.");
        InstallationDefaults.Validate();
        if (GameStores is null || GameStores.Count > 10000) throw new InvalidDataException("Invalid store metadata.");
        GameStores = GameStores.ToDictionary(p => LocalPaths.Normalize(p.Key), p =>
            p.Value is "Steam" or "Epic" or "GOG" or "Xbox" or "Ubisoft" or "Manual" ? p.Value : "Manual", StringComparer.OrdinalIgnoreCase);
        ScanRoots = NormalizeList(ScanRoots);
        ManualPaths = NormalizeList(ManualPaths);
        CachedGamePaths = NormalizeList(CachedGamePaths);
        if (ManualPaths.Concat(CachedGamePaths).Any(p => !Path.GetExtension(p).Equals(".exe", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("Manual and cached paths must name executables.");
        HiddenPaths = NormalizeList(HiddenPaths);
        FavouritePaths = NormalizeList(FavouritePaths);
        if (Profiles is null || Profiles.Count > 10000) throw new InvalidDataException("Invalid game profiles.");
        var profiles = new Dictionary<string, GameProfile>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, profile) in Profiles)
        {
            if (profile is null || profile.ApiOverride is { } api && (!Enum.IsDefined(api) || api == GraphicsApi.Unknown))
                throw new InvalidDataException("Invalid API override. Use null for automatic detection.");
            if (profile.LaunchArguments is { } args && (args.Length > 32767 || args.IndexOfAny(['\0', '\r', '\n']) >= 0))
                throw new InvalidDataException("Invalid launch arguments.");
            if (profile.FrameIterations is < 1 or > 10) throw new InvalidDataException("Frame iterations must be between 1 and 10.");
            if (profile.ArtworkPath is not null) profile.ArtworkPath = LocalPaths.Normalize(profile.ArtworkPath);
            profiles[LocalPaths.Normalize(key)] = profile;
        }
        Profiles = profiles;
    }

    static List<string> NormalizeList(List<string>? paths)
    {
        if (paths is null || paths.Count > 10000) throw new InvalidDataException("Invalid path list.");
        return paths.Select(LocalPaths.Normalize).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    public GameProfile ProfileFor(string exePath) => Profiles.FirstOrDefault(p => LocalPaths.Same(p.Key, exePath)).Value ?? new();

    public IReadOnlyList<LibraryEntry> Project(IEnumerable<LibraryGame> games, LibraryFilter filter = LibraryFilter.All,
        string? query = null, bool includeHidden = false)
    {
        if (!Enum.IsDefined(filter)) throw new ArgumentOutOfRangeException(nameof(filter));
        var hidden = HiddenPaths.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var favourites = FavouritePaths.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return games.GroupBy(g => LocalPaths.Normalize(g.DetectedGame.ExePath), StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderBy(x => x.Store == "Manual").First())
            .Select(g => new LibraryEntry(g.DetectedGame, ProfileFor(g.DetectedGame.ExePath).Apply(g.DetectedGame),
                g.Store, g.Installed, favourites.Contains(LocalPaths.Normalize(g.DetectedGame.ExePath)),
                hidden.Contains(LocalPaths.Normalize(g.DetectedGame.ExePath)) || hidden.Contains(LocalPaths.Normalize(g.DetectedGame.Root))))
            .Where(g => includeHidden || !g.Hidden)
            .Where(g => filter != LibraryFilter.Installed || g.Installed)
            .Where(g => filter != LibraryFilter.Favourites || g.Favourite)
            .Where(g => string.IsNullOrWhiteSpace(query) || g.DetectedGame.DisplayName.Contains(query.Trim(), StringComparison.OrdinalIgnoreCase)
                || g.Store.Contains(query.Trim(), StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(g => g.Favourite).ThenBy(g => g.DetectedGame.DisplayName, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public IReadOnlyList<LibraryGroup> Group(IEnumerable<LibraryEntry> entries) => entries
        .GroupBy(g => GroupByStore ? g.Store : "All games", StringComparer.OrdinalIgnoreCase)
        .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase).Select(g => new LibraryGroup(g.Key, g.ToArray())).ToArray();
}

static class LocalPaths
{
    public static string Normalize(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || path.StartsWith(@"\\", StringComparison.Ordinal)
            || path.StartsWith("//", StringComparison.Ordinal) || path.IndexOfAny(['\0', '\r', '\n']) >= 0
            || path.Skip(2).Contains(':'))
            throw new ArgumentException("An absolute local path is required.", nameof(path));
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var drive = new DriveInfo(Path.GetPathRoot(full)!);
        if (drive.DriveType == DriveType.Network) throw new ArgumentException("Network paths are not supported.", nameof(path));
        return full;
    }

    public static bool Same(string left, string right) => Normalize(left).Equals(Normalize(right), StringComparison.OrdinalIgnoreCase);
    public static bool IsInside(string path, string root)
    {
        var parent = Normalize(root);
        var candidate = Normalize(path);
        return candidate.Equals(parent, StringComparison.OrdinalIgnoreCase) || candidate.StartsWith(
            Path.EndsInDirectorySeparator(parent) ? parent : parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    public static void RequireNoLinks(string path)
    {
        for (var current = Normalize(path); current is not null; current = Path.GetDirectoryName(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Reparse points are excluded: " + current);
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }
}

static class LocalData
{
    public static string DirectoryPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DLSS5-Everything");
    public static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true, Converters = { new JsonStringEnumConverter() }, MaxDepth = 32
    };
    public static string ReadText(string path, int limit = 4 * 1024 * 1024)
    {
        LocalPaths.RequireNoLinks(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length > limit) throw new InvalidDataException("File exceeds the read limit: " + path);
        var bytes = new byte[limit + 1];
        var count = 0;
        while (count < bytes.Length)
        {
            var read = stream.Read(bytes, count, bytes.Length - count);
            if (read == 0) break;
            count += read;
        }
        if (count > limit) throw new InvalidDataException("File exceeds the read limit: " + path);
        return Encoding.UTF8.GetString(bytes, 0, count);
    }

    public static void WriteAtomic(string path, string text)
    {
        LocalPaths.RequireNoLinks(path);
        var bytes = Encoding.UTF8.GetBytes(text);
        if (bytes.Length > 4 * 1024 * 1024) throw new InvalidDataException("Local data exceeds the write limit.");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { stream.Write(bytes); stream.Flush(true); }
            File.Move(temp, path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    public static IDisposable Lock(string path)
    {
        LocalPaths.RequireNoLinks(path);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path.ToUpperInvariant())));
        var mutex = new Mutex(false, "Local\\DLSS5-Everything-" + hash);
        try
        {
            try { if (!mutex.WaitOne(TimeSpan.FromSeconds(5))) throw new IOException("Local data is busy."); }
            catch (AbandonedMutexException) { }
            return new Lease(mutex);
        }
        catch { mutex.Dispose(); throw; }
    }
    sealed class Lease(Mutex mutex) : IDisposable
    {
        public void Dispose() { mutex.ReleaseMutex(); mutex.Dispose(); }
    }
}
