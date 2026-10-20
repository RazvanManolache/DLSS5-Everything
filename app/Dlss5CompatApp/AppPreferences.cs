using System.Text.Json;
using System.Text.Json.Serialization;

namespace Dlss5CompatApp;

sealed class AppPreferences
{
    public const int MaximumRecents = 100;
    public const int MaximumFileBytes = 128 * 1024;
    public int SchemaVersion { get; set; } = 1;
    public long Revision { get; set; }
    public bool CloseToTray { get; set; }
    public bool ScanOnStartup { get; set; }
    public bool CompactNavigation { get; set; }
    public bool UseTiles { get; set; }
    public bool AutomaticArtwork { get; set; } = true;
    public bool RememberRecents { get; set; } = true;
    public int RecentLimit { get; set; } = 20;
    public string Language { get; set; } = "en";
    public string RecentQuery { get; set; } = "";
    public bool OnlyAvailableRecents { get; set; }
    public List<RecentGame> Recents { get; set; } = [];
    [JsonIgnore] public static string DefaultPath => Path.Combine(LocalData.DirectoryPath, "preferences.json");

    public static AppPreferences Load(string? filePath = null)
    {
        var path = LocalPaths.Normalize(filePath ?? DefaultPath);
        using var gate = LocalData.Lock(path);
        return Read(path);
    }

    static AppPreferences Read(string path)
    {
        if (!File.Exists(path)) return new();
        var value = JsonSerializer.Deserialize<AppPreferences>(LocalData.ReadText(path, MaximumFileBytes), LocalData.Json)
            ?? throw new InvalidDataException("Preferences are empty.");
        value.Validate();
        return value;
    }

    public AppPreferences Copy() => JsonSerializer.Deserialize<AppPreferences>(JsonSerializer.Serialize(this, LocalData.Json), LocalData.Json)!;

    public void Save(string? filePath = null)
    {
        Validate();
        var path = LocalPaths.Normalize(filePath ?? DefaultPath);
        using var gate = LocalData.Lock(path);
        if (Read(path).Revision != Revision)
            throw new InvalidOperationException("Preferences changed in another window. Reopen the app before saving.");
        var copy = Copy();
        copy.Revision = checked(Revision + 1);
        var json = JsonSerializer.Serialize(copy, LocalData.Json);
        if (System.Text.Encoding.UTF8.GetByteCount(json) > MaximumFileBytes)
            throw new InvalidDataException("Preferences exceed the size limit.");
        LocalData.WriteAtomic(path, json);
        Revision = copy.Revision;
    }

    public void Validate()
    {
        if (SchemaVersion != 1 || Revision < 0) throw new InvalidDataException("Unsupported preferences version.");
        if (RecentLimit is < 1 or > MaximumRecents) throw new InvalidDataException("Recent limit must be between 1 and 100.");
        if (!UiLocalization.Supports(Language)) throw new InvalidDataException("Unsupported language.");
        if (RecentQuery is null || RecentQuery.Length > 256 || RecentQuery.Any(char.IsControl))
            throw new InvalidDataException("Invalid recent games filter.");
        if (Recents is null || Recents.Count > MaximumRecents) throw new InvalidDataException("Invalid recent games.");
        var normalized = new List<RecentGame>();
        foreach (var entry in Recents)
        {
            if (entry is null || entry.Name is null || entry.Name.Length > 256 || entry.Name.Any(char.IsControl)
                || entry.ExePath is null || entry.ExePath.Length > 1024 || entry.ViewedAt == default)
                throw new InvalidDataException("Invalid recent game.");
            var path = LocalPaths.Normalize(entry.ExePath);
            if (!Path.GetExtension(path).Equals(".exe", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Recent games must name executables.");
            normalized.Add(entry with { ExePath = path });
        }
        Recents = RememberRecents ? normalized.OrderByDescending(r => r.ViewedAt)
            .DistinctBy(r => r.ExePath, StringComparer.OrdinalIgnoreCase).Take(RecentLimit).ToList() : [];
    }

    public bool RecordViewed(string exePath, string name, DateTimeOffset? viewedAt = null)
    {
        if (!RememberRecents) return false;
        var path = LocalPaths.Normalize(exePath);
        var safeName = new string(name.Where(c => !char.IsControl(c)).Take(256).ToArray());
        var updated = Copy();
        updated.Recents.RemoveAll(r => r.ExePath.Equals(path, StringComparison.OrdinalIgnoreCase));
        updated.Recents.Insert(0, new(path, safeName, viewedAt ?? DateTimeOffset.UtcNow));
        updated.Recents = updated.Recents.Take(RecentLimit).ToList();
        updated.Validate();
        Recents = updated.Recents;
        return true;
    }
}

sealed record RecentGame(string ExePath, string Name, DateTimeOffset ViewedAt);
