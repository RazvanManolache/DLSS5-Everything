using System.Security.Cryptography;
using System.Text.Json;

namespace Dlss5CompatApp;

/// <summary>The x86 addon is published immutably before the release receipt points to it.
/// A failed x64 publication therefore cannot select an uncommitted companion.</summary>
static class PhoenixCompanion32
{
    public static async Task<PhoenixReleasePayload> PublishAsync(string staged, string root,
        PhoenixReleasePayload release, CancellationToken token)
    {
        if (new FileInfo(staged).Length is < 64 or > 512L * 1024 * 1024 ||
            !PayloadScanner.HasPackagedPhoenixBridge(staged, CpuArch.X86))
            throw new InvalidDataException("Phoenix DLSS5 x86 companion is not a packaged graphics bridge.");
        string digest;
        await using (var input = File.OpenRead(staged))
            digest = Convert.ToHexString(await SHA256.HashDataAsync(input, token));
        var relative = $"phoenix-companions/{digest.ToLowerInvariant()}/Phoenix.addon32";
        var destination = BoundedInstallPaths.Resolve(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        if (File.Exists(destination))
        {
            using var existing = File.OpenRead(destination);
            if (Convert.ToHexString(SHA256.HashData(existing)) != digest)
                throw new InvalidDataException("Cached x86 companion was modified.");
        }
        else File.Move(staged, destination);
        return release with { Companion32Path = relative, Companion32Sha256 = digest,
            Companion32Size = new FileInfo(destination).Length };
    }

    public static string? Resolve(string root, PhoenixReleasePayload release)
    {
        if (release.Companion32Sha256 is not { Length: 64 } digest || !digest.All(Uri.IsHexDigit) ||
            release.Companion32Path != $"phoenix-companions/{digest.ToLowerInvariant()}/Phoenix.addon32") return null;
        try
        {
            var path = BoundedInstallPaths.Resolve(root, release.Companion32Path);
            if (!File.Exists(path) || new FileInfo(path).Length != release.Companion32Size ||
                !PayloadScanner.HasPackagedPhoenixBridge(path, CpuArch.X86)) return null;
            using var input = File.OpenRead(path);
            return Convert.ToHexString(SHA256.HashData(input)).Equals(digest, StringComparison.OrdinalIgnoreCase) ? path : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException) { return null; }
    }

    public static string? Resolve(string root)
    {
        try
        {
            var path = Path.Combine(root, PhoenixReleaseDownloader.SourceManifestFileName);
            LocalPaths.RequireNoLinks(path);
            if (!File.Exists(path) || new FileInfo(path).Length > 1024 * 1024) return null;
            var release = JsonSerializer.Deserialize<PhoenixReleasePayload>(File.ReadAllText(path));
            return release is null ? null : Resolve(root, release);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }
}
