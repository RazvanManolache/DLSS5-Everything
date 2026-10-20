using System.Text;
using System.Text.Json;

namespace Dlss5CompatApp;

enum DiagnosticFileStatus { Missing, Empty, Available, Unreadable }
sealed record DiagnosticSource(string Label, string Path, DiagnosticFileStatus Status, long Bytes,
    DateTimeOffset? Modified, bool Truncated, string Text, string? Error = null);
sealed record RuntimeDiagnosticReport(DateTimeOffset Generated, IReadOnlyList<DiagnosticSource> Sources, string Text)
{
    // Presence of logs or installed files is not proof of a successful evaluation.
    public bool RuntimeVerified => false;
    public bool HasLogs => Sources.Any(s => s.Label != "Install manifest" && s.Status == DiagnosticFileStatus.Available);
}

static class RuntimeDiagnostics
{
    public const int TailBytes = 256 * 1024;
    static readonly string[] Logs = ["dlss5-feed.log", "dlss5-bridge.log", "ReShade.log", "dlss5-feed-crash.dmp.txt",
        "host64/dlss5-feed-host.log", "host64/ReShade.log", "phoenix-vulkan-feed.log",
        "PhoenixOutput/phoenix.log", "host64/PhoenixOutput/phoenix.log",
        "_DLSS5_OpenXR/host64/dlss5-feed-host.log", "_DLSS5_OpenXR/host64/ReShade.log"];

    public static RuntimeDiagnosticReport Collect(GameCandidate game, string? appDataDirectory = null,
        IReadOnlyDictionary<string, string>? facts = null, CancellationToken cancellationToken = default)
    {
        var root = LocalPaths.Normalize(game.Root);
        var exe = LocalPaths.Normalize(game.ExePath);
        if (!LocalPaths.IsInside(exe, root)) throw new ArgumentException("Executable must be inside the game root.");
        var sources = new List<DiagnosticSource>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string label, string file)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (seen.Add(file)) sources.Add(Read(label, file));
        }
        foreach (var dir in new[] { Path.GetDirectoryName(exe)!, root }.Distinct(StringComparer.OrdinalIgnoreCase))
            foreach (var name in Logs) Add(name, Path.Combine(dir, name.Replace('/', Path.DirectorySeparatorChar)));
        Add("Install manifest", Path.Combine(root, "_DLSS5_Compat_Backup", "manifest.json"));
        if (appDataDirectory is not null)
        {
            var data = LocalPaths.Normalize(appDataDirectory);
            Add("App log", Path.Combine(data, "app.log"));
            Add("App crash log", Path.Combine(data, "crash.log"));
        }
        var now = DateTimeOffset.UtcNow;
        var text = new StringBuilder($"DLSS5 Everything diagnostics\nGenerated: {now:O}\nRuntime: not verified (log evidence only)\n");
        if (facts is not null) foreach (var (key, value) in facts) text.AppendLine($"{key}: {value}");
        foreach (var source in sources)
        {
            text.AppendLine($"\n=== {source.Label} ===\nPath: {source.Path}\nStatus: {source.Status}; bytes: {source.Bytes}; modified: {source.Modified:O}");
            if (source.Truncated) text.AppendLine("[Only the final 256 KiB are included.]");
            text.AppendLine(source.Error ?? source.Text);
        }
        return new(now, sources, text.ToString());
    }

    static DiagnosticSource Read(string label, string file)
    {
        try
        {
            LocalPaths.RequireNoLinks(file);
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var size = stream.Length;
            var bytes = new byte[(int)Math.Min(size, TailBytes)];
            stream.Seek(Math.Max(0, size - TailBytes), SeekOrigin.Begin);
            var count = 0;
            while (count < bytes.Length)
            {
                var read = stream.Read(bytes, count, bytes.Length - count);
                if (read == 0) break;
                count += read;
            }
            return new(label, file, count == 0 ? DiagnosticFileStatus.Empty : DiagnosticFileStatus.Available,
                size, File.GetLastWriteTimeUtc(file), size > TailBytes, Encoding.UTF8.GetString(bytes, 0, count));
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        { return new(label, file, DiagnosticFileStatus.Missing, 0, null, false, ""); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        { return new(label, file, DiagnosticFileStatus.Unreadable, 0, null, false, "", ex.Message); }
    }

    // Static install evidence for the Installed filter, never runtime health.
    public static bool HasInstallManifest(GameCandidate game)
    {
        try
        {
            var root = LocalPaths.Normalize(game.Root);
            var path = Path.Combine(root, "_DLSS5_Compat_Backup", "manifest.json");
            if (!File.Exists(path)) return false;
            using var json = JsonDocument.Parse(LocalData.ReadText(path, 1024 * 1024));
            if (json.RootElement.ValueKind != JsonValueKind.Object || new[] { "Version", "Date", "GameRoot", "ExeRelativePath", "Route", "Added", "Replaced" }
                .Any(key => !json.RootElement.TryGetProperty(key, out _))) return false;
            var manifest = json.RootElement.Deserialize<InstallManifest>(LocalData.Json);
            if (manifest is null || manifest.Version is not (1 or 2) || manifest.Date == default || string.IsNullOrWhiteSpace(manifest.Route)
                || !LocalPaths.Same(manifest.GameRoot, root) || !SafeRelative(manifest.ExeRelativePath)
                || !LocalPaths.Same(Path.Combine(root, manifest.ExeRelativePath), game.ExePath)
                || manifest.Added is null || manifest.Replaced is null || manifest.FileFailures is null ||
                manifest.FileFailures.Count != 0 || !File.Exists(game.ExePath)) return false;
            var files = manifest.Added.Concat(manifest.Replaced.Select(f => f.RelativePath)).ToArray();
            return files.Length > 0 && files.All(relative =>
            {
                if (!SafeRelative(relative)) return false;
                var full = Path.GetFullPath(Path.Combine(root, relative));
                LocalPaths.RequireNoLinks(full);
                return LocalPaths.IsInside(full, root) && (File.Exists(full) || Directory.Exists(full));
            });
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or JsonException or NullReferenceException)
        { return false; }
    }

    internal static bool SafeRelative(string? value) => !string.IsNullOrWhiteSpace(value) && !Path.IsPathRooted(value)
        && value.IndexOfAny([':', '\0', '\r', '\n']) < 0 && value.Split(['/', '\\']).All(p => p is not (".." or "." or ""));
}
