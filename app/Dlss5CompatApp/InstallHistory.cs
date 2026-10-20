using System.Text.Json;

namespace Dlss5CompatApp;

enum HistoryAction { Install, Restore, Verify }
enum HistoryOutcome { Success, Failure }

sealed record InstallHistoryEntry
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
    public string GamePath { get; init; } = "";
    public string GameName { get; init; } = "";
    public HistoryAction Action { get; init; } = (HistoryAction)(-1);
    public HistoryOutcome Outcome { get; init; } = (HistoryOutcome)(-1);
    public string Message { get; init; } = "";
    public string? Route { get; init; }
    public string? LogPath { get; init; }
    public string Summary => $"{Timestamp.LocalDateTime:g} | {Action} | {Outcome} | {GameName} | {Message}";
}

static class InstallHistory
{
    public static string DefaultPath => Path.Combine(LocalData.DirectoryPath, "install-history.jsonl");
    const int MaxBytes = 16 * 1024 * 1024;
    public static IReadOnlyList<InstallHistoryEntry> Load(string? filePath = null, Action<string>? warning = null)
    {
        var path = LocalPaths.Normalize(filePath ?? DefaultPath);
        using var gate = LocalData.Lock(path);
        return Read(path, warning);
    }

    static IReadOnlyList<InstallHistoryEntry> Read(string path, Action<string>? warning)
    {
        if (!File.Exists(path)) return [];
        var text = LocalData.ReadText(path, MaxBytes);
        var entries = new Dictionary<Guid, InstallHistoryEntry>();
        var lineNumber = 0;
        foreach (var line in text.Split('\n'))
        {
            lineNumber++;
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                using var json = JsonDocument.Parse(line);
                if (json.RootElement.ValueKind != JsonValueKind.Object || new[] { "Id", "Timestamp", "Action", "Outcome", "GamePath", "GameName", "Message" }
                    .Any(key => !json.RootElement.TryGetProperty(key, out _))) throw new InvalidDataException("Incomplete history record.");
                var entry = JsonSerializer.Deserialize<InstallHistoryEntry>(line, LocalData.Json)
                    ?? throw new InvalidDataException("Empty record.");
                Validate(entry);
                entries[entry.Id] = entry;
            }
            catch (Exception ex) when (ex is JsonException or InvalidDataException or ArgumentException)
            { warning?.Invoke($"History line {lineNumber} could not be read: {ex.Message}"); }
        }
        return entries.Values.OrderByDescending(e => e.Timestamp).ToArray();
    }

    public static void Append(InstallHistoryEntry entry, string? filePath = null)
    {
        Validate(entry);
        var path = LocalPaths.Normalize(filePath ?? DefaultPath);
        using var gate = LocalData.Lock(path);
        LocalPaths.RequireNoLinks(path);
        if (Read(path, null).Any(e => e.Id == entry.Id)) return;
        var compact = new JsonSerializerOptions(LocalData.Json) { WriteIndented = false };
        var bytes = System.Text.Encoding.UTF8.GetBytes("\n" + JsonSerializer.Serialize(entry, compact) + "\n");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
        if (stream.Length + bytes.Length > MaxBytes) throw new IOException("History is full; archive the local history before adding records.");
        stream.Seek(0, SeekOrigin.End);
        stream.Write(bytes);
        stream.Flush(true);
    }

    static void Validate(InstallHistoryEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.Id == Guid.Empty || entry.Timestamp == default || !Enum.IsDefined(entry.Action) || !Enum.IsDefined(entry.Outcome)
            || string.IsNullOrWhiteSpace(entry.GameName) || string.IsNullOrWhiteSpace(entry.Message)
            || entry.Message.Length > 65536 || entry.GameName.Length > 1024)
            throw new InvalidDataException("History requires a date, game, action, outcome and readable message.");
        _ = LocalPaths.Normalize(entry.GamePath);
        if (entry.LogPath is not null) _ = LocalPaths.Normalize(entry.LogPath);
    }
}
