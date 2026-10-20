using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Dlss5CompatApp;

static class PhoenixReleaseCache
{
    public static async Task<PhoenixReleasePayload?> ReadAsync(string root, CancellationToken token)
    {
        var path = Path.Combine(root, PhoenixReleaseDownloader.SourceManifestFileName);
        try
        {
            if (!File.Exists(path) || IsLink(path) || new FileInfo(path).Length > 1024 * 1024) return null;
            return JsonSerializer.Deserialize<PhoenixReleasePayload>(await File.ReadAllTextAsync(path, token));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    public static string AssetsFingerprint(PhoenixReleaseAsset[] assets)
    {
        // Public HTML supplies digests but rounded sizes; API/HTML checks must share one identity.
        var identity = assets.OrderBy(a => a.Name, StringComparer.Ordinal).ThenBy(a => a.Url, StringComparer.Ordinal)
            .Select(a => new { a.Name, a.Url, Digest = a.Digest?.ToLowerInvariant() });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(identity))));
    }

    public static async Task<bool> VerifyAddonAsync(string root, PhoenixReleasePayload cached, CancellationToken token, string? upstreamDigest = null)
    {
        var path = Path.Combine(root, PhoenixReleaseDownloader.AddonFileName);
        try
        {
            if (!File.Exists(path) || IsLink(path) || new FileInfo(path).Length != cached.Size || !IsSha256(cached.Sha256)) return false;
            PhoenixReleaseDownloader.ValidateX64Pe(path);
            if (!PayloadScanner.HasPhoenixFeederApi(path) || !PayloadScanner.HasEmbeddedPhoenixRuntime(path)) return false;
            if (cached.Companion32Path is not null && PhoenixCompanion32.Resolve(root, cached) is null) return false;
            if (!PhoenixRuntimePackage.VerifyCached(root, cached)) return false;
            var sha256 = await HashAsync(path, token);
            if (!sha256.Equals(cached.Sha256, StringComparison.OrdinalIgnoreCase)) return false;
            // A direct release asset's bytes must match upstream, not just editable local provenance.
            if (string.IsNullOrWhiteSpace(upstreamDigest)) return true;
            var parts = upstreamDigest.Split(':', 2);
            if (parts.Length != 2) return false;
            if (parts[0].Equals("sha256", StringComparison.OrdinalIgnoreCase))
                return IsSha256(parts[1]) && sha256.Equals(parts[1], StringComparison.OrdinalIgnoreCase);
            if (!parts[0].Equals("sha512", StringComparison.OrdinalIgnoreCase) || parts[1].Length != 128 || !parts[1].All(Uri.IsHexDigit))
                return false;
            await using var input = File.OpenRead(path);
            return Convert.ToHexString(await SHA512.HashDataAsync(input, token)).Equals(parts[1], StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException) { return false; }
    }

    public static async Task<Dictionary<string, string>> HashNoticesAsync(string directory, CancellationToken token)
    {
        var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var path in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).OrderBy(p => p, StringComparer.Ordinal))
        {
            token.ThrowIfCancellationRequested();
            hashes[Path.GetRelativePath(directory, path).Replace('\\', '/')] = await HashAsync(path, token);
        }
        return hashes;
    }

    public static async Task<bool> VerifyNoticesAsync(string root, PhoenixReleasePayload cached, CancellationToken token)
    {
        if (cached.NoticeSha256 is not { Count: > 0 } hashes || string.IsNullOrWhiteSpace(cached.NoticesPath) ||
            Path.IsPathRooted(cached.NoticesPath)) return false;
        try
        {
            var noticesRoot = Path.GetFullPath(Path.Combine(root, "phoenix-notices"));
            var directory = Path.GetFullPath(Path.Combine(root, cached.NoticesPath));
            if (!IsWithin(directory, noticesRoot) || !string.Equals(Path.GetDirectoryName(directory), noticesRoot,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) ||
                !Directory.Exists(directory) || IsLink(noticesRoot) || IsLink(directory)) return false;
            var pending = new Stack<string>();
            pending.Push(directory);
            var found = new HashSet<string>(StringComparer.Ordinal);
            long totalBytes = 0;
            while (pending.TryPop(out var current))
            {
                token.ThrowIfCancellationRequested();
                foreach (var entry in Directory.EnumerateFileSystemEntries(current))
                {
                    if (IsLink(entry)) return false;
                    if (Directory.Exists(entry)) { pending.Push(entry); continue; }
                    var size = new FileInfo(entry).Length;
                    if (size > 4 * 1024 * 1024 || (totalBytes += size) > 16 * 1024 * 1024) return false;
                    var relative = Path.GetRelativePath(directory, entry).Replace('\\', '/');
                    if (!hashes.TryGetValue(relative, out var expected) || !IsSha256(expected) ||
                        !(await HashAsync(entry, token)).Equals(expected, StringComparison.OrdinalIgnoreCase)) return false;
                    found.Add(relative);
                }
            }
            return found.Count == hashes.Count && new[] { "LICENSE", "NOTICE", "THIRD_PARTY" }.All(stem =>
                found.Any(name => !name.Contains('/') && Path.GetFileNameWithoutExtension(name).Equals(stem, StringComparison.OrdinalIgnoreCase)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return false; }
    }

    static bool IsWithin(string path, string parent) => path.StartsWith(parent.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    static bool IsLink(string path) => (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
    static bool IsSha256(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);

    static async Task<string> HashAsync(string path, CancellationToken token)
    {
        await using var input = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(input, token));
    }
}
