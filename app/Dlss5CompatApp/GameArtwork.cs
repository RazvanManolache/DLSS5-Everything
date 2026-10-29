using System.Drawing.Imaging;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Dlss5CompatApp;

sealed record GameArtworkResult(string Path, string Source, uint? SteamAppId);

// Uses the same public Steam search and CDN as Swapper, without credentials or game uploads.
sealed partial class GameArtwork : IDisposable
{
    internal const int MaximumImageBytes = 8 * 1024 * 1024;
    readonly HttpClient _http;
    readonly bool _ownsClient;
    readonly string _cache;
    readonly string[] _steamRoots;
    readonly SemaphoreSlim _requests = new(1, 1);
    readonly TimeSpan _interval;
    DateTimeOffset _lastRequest;

    public GameArtwork(string? cache = null, HttpClient? http = null, IEnumerable<string>? steamRoots = null,
        TimeSpan? requestInterval = null)
    {
        _cache = LocalPaths.Normalize(cache ?? System.IO.Path.Combine(LocalData.DirectoryPath, "artwork", "steam"));
        _ownsClient = http is null;
        _http = http ?? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(12) };
        _steamRoots = (steamRoots ?? DefaultSteamRoots()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        _interval = requestInterval ?? TimeSpan.FromMilliseconds(350);
    }

    public async Task<GameArtworkResult?> GetAsync(GameCandidate game, CancellationToken cancellationToken = default, bool retryMissing = false)
    {
        var title = CleanTitle(game.DisplayName);
        if (title.Length is < 2 or > 256 || title.StartsWith("Example ", StringComparison.OrdinalIgnoreCase)) return null;
        var local = await Task.Run(() => FindLocal(game), cancellationToken);
        var key = local.Id is { } appId ? "app-" + appId : "title-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Normalize(title))));
        var destination = System.IO.Path.Combine(_cache, key + ".jpg");
        LocalPaths.RequireNoLinks(destination);
        if (await Task.Run(() => IsImage(destination), cancellationToken)) return new(destination, "Steam artwork cache", local.Id);
        if (local.Artwork is { } artwork)
        {
            var bytes = await File.ReadAllBytesAsync(artwork, cancellationToken);
            await Task.Run(() => SaveImage(bytes, destination), cancellationToken);
            return new(destination, "Steam local library", local.Id);
        }
        var miss = System.IO.Path.Combine(_cache, key + ".miss");
        LocalPaths.RequireNoLinks(miss);
        if (!retryMissing && File.Exists(miss) && DateTime.UtcNow - File.GetLastWriteTimeUtc(miss) < TimeSpan.FromHours(24)) return null;
        var id = local.Id;
        if (id is null)
        {
            var query = new Uri("https://store.steampowered.com/api/storesearch/?term=" + Uri.EscapeDataString(title) + "&cc=us&l=en");
            var json = await ReadAsync(query, 512 * 1024, cancellationToken);
            if (json is null) return null;
            using var document = JsonDocument.Parse(json);
            id = PickAppId(document.RootElement, title);
        }
        if (id is null) { LocalData.WriteAtomic(miss, "No confident Steam title match."); return null; }
        foreach (var filename in new[] { "library_600x900.jpg", "header.jpg" })
        {
            var bytes = await ReadAsync(AssetUri(id.Value, filename), MaximumImageBytes, cancellationToken);
            if (bytes is null) continue;
            try
            {
                await Task.Run(() => SaveImage(bytes, destination), cancellationToken);
                return new(destination, "Steam public artwork CDN", id);
            }
            catch (ArgumentException) { }
            catch (InvalidDataException) { }
        }
        LocalData.WriteAtomic(miss, "Steam artwork unavailable.");
        return null;
    }

    internal static Uri AssetUri(uint id, string filename)
    {
        if (id == 0 || filename is not ("library_600x900.jpg" or "header.jpg")) throw new ArgumentException("Invalid Steam asset.");
        return new($"https://shared.cloudflare.steamstatic.com/store_item_assets/steam/apps/{id}/{filename}");
    }

    async Task<byte[]?> ReadAsync(Uri uri, int limit, CancellationToken cancellationToken)
    {
        if (uri.Scheme != "https" || uri.Host is not ("store.steampowered.com" or "shared.cloudflare.steamstatic.com"))
            throw new InvalidDataException("Unexpected artwork host.");
        await _requests.WaitAsync(cancellationToken);
        try
        {
            var delay = _interval - (DateTimeOffset.UtcNow - _lastRequest);
            if (delay > TimeSpan.Zero) await Task.Delay(delay, cancellationToken);
            _lastRequest = DateTimeOffset.UtcNow;
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.AcceptLanguage.ParseAdd("en");
            request.Headers.UserAgent.ParseAdd("DLSS5-Everything/3.0");
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (response.StatusCode == HttpStatusCode.NotFound) return null;
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > limit) throw new InvalidDataException("Artwork response exceeds the size limit.");
            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var result = new MemoryStream();
            var buffer = new byte[16384];
            int read;
            while ((read = await source.ReadAsync(buffer, cancellationToken)) != 0)
            {
                if (result.Length + read > limit) throw new InvalidDataException("Artwork response exceeds the size limit.");
                result.Write(buffer, 0, read);
            }
            return result.ToArray();
        }
        finally { _requests.Release(); }
    }

    internal static string CleanTitle(string title) => Regex.Replace(Regex.Replace(title, @"\[[^\]]*\]|\b(?:build\s*\d+|v\d+(?:\.\d+)+)\b", " ", RegexOptions.IgnoreCase), @"[_\s]+", " ").Trim();
    static string Normalize(string title) => Regex.Replace(Regex.Replace(title.ToLowerInvariant(), @"\((?:19|20)\d{2}\)", " "), @"[^\p{L}\p{N}]+", " ").Trim();

    internal static uint? PickAppId(JsonElement root, string title)
    {
        if (!root.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array) return null;
        var query = Normalize(title);
        var matches = new List<(uint Id, double Score)>();
        foreach (var row in items.EnumerateArray().Take(100))
        {
            if (row.ValueKind != JsonValueKind.Object || !row.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.Number || !id.TryGetUInt32(out var value) || value == 0 ||
                !row.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String) continue;
            if (row.TryGetProperty("type", out var type) && (type.ValueKind != JsonValueKind.String || type.GetString() != "app")) continue;
            var normalized = Normalize(name.GetString()!);
            if (normalized == query) return value;
            var a = query.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
            var b = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
            var shared = a.Intersect(b).Count();
            var score = (double)shared / Math.Max(1, a.Union(b).Count());
            var ordered = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var offset = 0;
            foreach (var word in normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                if (offset < ordered.Length && ordered[offset] == word) offset++;
            if (offset != ordered.Length) score = 0;
            matches.Add((value, score));
        }
        var ranked = matches.OrderByDescending(m => m.Score).ToArray();
        return ranked.Length > 0 && ranked[0].Score >= .82 && (ranked.Length == 1 || ranked[0].Score - ranked[1].Score >= .15) ? ranked[0].Id : null;
    }

    (uint? Id, string? Artwork) FindLocal(GameCandidate game)
    {
        uint? id = null;
        string? library = null;
        var folder = new DirectoryInfo(System.IO.Path.GetDirectoryName(game.ExePath)!);
        for (var depth = 0; folder is not null && depth < 10; depth++, folder = folder.Parent)
        {
            if (folder.Parent?.Name.Equals("common", StringComparison.OrdinalIgnoreCase) != true ||
                folder.Parent.Parent?.Name.Equals("steamapps", StringComparison.OrdinalIgnoreCase) != true) continue;
            library = folder.Parent.Parent.Parent?.FullName;
            foreach (var manifest in Directory.EnumerateFiles(folder.Parent.Parent.FullName, "appmanifest_*.acf").Take(2048))
            {
                try
                {
                    var document = LibraryDiscovery.VdfNode.Parse(LocalData.ReadText(manifest, 1024 * 1024));
                    var state = document.Children.FirstOrDefault(p => p.Key.Equals("AppState", StringComparison.OrdinalIgnoreCase)).Value;
                    if (state?.Get("installdir")?.Equals(folder.Name, StringComparison.OrdinalIgnoreCase) == true &&
                        uint.TryParse(state.Get("appid"), out var parsed) && parsed > 0) { id = parsed; break; }
                }
                catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException) { }
            }
            break;
        }
        if (id is null) return (null, null);
        foreach (var root in new[] { library }.OfType<string>().Concat(_steamRoots).Distinct(StringComparer.OrdinalIgnoreCase))
        foreach (var file in new[] { $"{id}/library_600x900.jpg", $"{id}_library_600x900.jpg", $"{id}/header.jpg", $"{id}_header.jpg" })
        {
            var path = System.IO.Path.Combine(root, "appcache", "librarycache", file);
            if (IsImage(path)) return (id, path);
        }
        return (id, null);
    }

    static IEnumerable<string> DefaultSteamRoots()
    {
        var path = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string;
        return path is null ? [] : [path];
    }

    internal static bool IsImage(string path)
    {
        try
        {
            LocalPaths.RequireNoLinks(path);
            if (!File.Exists(path) || new FileInfo(path).Length is < 16 or > MaximumImageBytes) return false;
            using var image = Image.FromFile(path);
            return ValidDimensions(image);
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException or OutOfMemoryException) { return false; }
    }

    static bool ValidDimensions(Image image) => image.Width is >= 32 and <= 4096 && image.Height is >= 32 and <= 4096 && (long)image.Width * image.Height <= 16_000_000;

    static void SaveImage(byte[] bytes, string destination)
    {
        if (bytes.Length > MaximumImageBytes) throw new InvalidDataException("Artwork exceeds the size limit.");
        using var stream = new MemoryStream(bytes);
        using var image = Image.FromStream(stream, true, true);
        if (!ValidDimensions(image) || (image.RawFormat.Guid != ImageFormat.Jpeg.Guid && image.RawFormat.Guid != ImageFormat.Png.Guid))
            throw new InvalidDataException("Unsupported artwork image.");
        var scale = Math.Min(1, Math.Min(600d / image.Width, 900d / image.Height));
        using var resized = new Bitmap(image, new Size(Math.Max(1, (int)(image.Width * scale)), Math.Max(1, (int)(image.Height * scale))));
        LocalPaths.RequireNoLinks(destination);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(destination)!);
        var temp = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { resized.Save(temp, ImageFormat.Jpeg); File.Move(temp, destination, true); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    public void Dispose() { if (_ownsClient) _http.Dispose(); }
}
