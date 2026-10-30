using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

namespace Dlss5CompatApp;

enum CompanionProject { DosBoxEnhanced, VideoPlayer }
enum CompanionUpdateState { Current, UpdateAvailable, Missing, UnknownVersion, Unavailable, NeedsRepair, Unverified }
sealed record CompanionUpdateResult(CompanionProject Project, CompanionUpdateState State, string Message,
    string? InstalledVersion, string? LatestVersion, string? BinaryPath)
{
    public bool NeedsUpdate => State is CompanionUpdateState.UpdateAvailable or CompanionUpdateState.NeedsRepair;
}

static class CompanionUpdateService
{
    internal static string Repository(CompanionProject project) => project switch
    {
        CompanionProject.DosBoxEnhanced => "RazvanManolache/dosbox-enhanced",
        CompanionProject.VideoPlayer => "RazvanManolache/dlss5-phoenix-video-player",
        _ => throw new ArgumentOutOfRangeException(nameof(project))
    };
    internal static string Name(CompanionProject project) => project == CompanionProject.DosBoxEnhanced ? "DOSBox Enhanced" : "Video Player";
    internal static Uri ReleasesUrl(CompanionProject project) => new($"https://github.com/{Repository(project)}/releases");

    // Read metadata only. Never load an installed library or launch a player to ask its version.
    internal static async Task<CompanionUpdateResult> CheckAsync(CompanionProject project, string? binary,
        PhoenixReleaseChannel channel, CancellationToken token = default, HttpClient? client = null)
    {
        string? version = null;
        bool exists = binary is not null && File.Exists(binary);
        try
        {
            if (exists) version = await ReadVersionAsync(project, binary!, token);
            var latest = await AppUpdateService.CheckRepositoryAsync(Repository(project), version ?? "0.0.0", client,
                token, stableOnly: channel == PhoenixReleaseChannel.StableOnly);
            var name = Name(project);
            if (!latest.CheckSucceeded)
                return new(project, CompanionUpdateState.Unavailable, $"{name}: couldn't check GitHub; installed version: {version ?? (exists ? "unknown" : "not installed")}.", version, null, binary);
            if (!exists)
                return new(project, CompanionUpdateState.Missing, $"{name}: not installed. Available: {latest.LatestVersion}.", null, latest.LatestVersion, binary);
            if (version is null)
                return new(project, CompanionUpdateState.UnknownVersion, $"{name}: installed version could not be verified. Available: {latest.LatestVersion}.", null, latest.LatestVersion, binary);
            if (!latest.Newer)
            {
                var root = project == CompanionProject.DosBoxEnhanced ? DosGameLauncher.PackageRoot(binary!) : Path.GetDirectoryName(binary!)!;
                var verified = await ReleaseFileVerifier.VerifyAsync(Repository(project), latest, root,
                    project == CompanionProject.DosBoxEnhanced ? "app/DosboxEnhanced.exe" : "PhoenixVideoPlayer.exe", token, client);
                var state = verified.Status == AppUpdateStatus.UpToDate ? CompanionUpdateState.Current
                    : verified.Status == AppUpdateStatus.NeedsRepair ? CompanionUpdateState.NeedsRepair : CompanionUpdateState.Unverified;
                return new(project, state, $"{name}: installed {version}; published {latest.LatestVersion}. {verified.Error}", version, latest.LatestVersion, binary);
            }
            return new(project, latest.Newer ? CompanionUpdateState.UpdateAvailable : CompanionUpdateState.Current,
                latest.Newer ? $"{name}: update available — {version} → {latest.LatestVersion}." : $"{name}: {version}; no newer release found.",
                version, latest.LatestVersion, binary);
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or InvalidOperationException or KeyNotFoundException or System.ComponentModel.Win32Exception)
        {
            return new(project, CompanionUpdateState.Unavailable, $"{Name(project)}: version check unavailable. {error.Message}", version, null, binary);
        }
    }

    internal static async Task<string?> ReadVersionAsync(CompanionProject project, string binary, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        LocalPaths.RequireNoLinks(binary);
        if (project == CompanionProject.VideoPlayer)
        {
            var manifest = Path.Combine(Path.GetDirectoryName(binary)!, "package-manifest.json");
            if (File.Exists(manifest))
            {
                using var json = JsonDocument.Parse(LocalData.ReadText(manifest, 1024 * 1024));
                var root = json.RootElement;
                // A release tag is useful only when the package receipt matches this executable.
                if (root.GetProperty("schema").GetString() != "phoenix-player-package/1"
                    || !root.GetProperty("files").TryGetProperty(Path.GetFileName(binary), out var hash)
                    || hash.ValueKind != JsonValueKind.String) return null;
                await using var input = File.OpenRead(binary);
                if (input.Length > 256L * 1024 * 1024) return null;
                if (!Convert.ToHexString(await SHA256.HashDataAsync(input, token)).Equals(hash.GetString(), StringComparison.OrdinalIgnoreCase)) return null;
                var version = root.TryGetProperty("release_tag", out var tag) && tag.ValueKind == JsonValueKind.String
                    ? tag.GetString() : root.GetProperty("version").GetString();
                return AppUpdateService.IsVersion(version) ? version : null;
            }
        }
        return EmbeddedVersion(binary);
    }

    internal static string? EmbeddedVersion(string binary)
    {
        var info = FileVersionInfo.GetVersionInfo(binary);
        // ProductVersion preserves prerelease identifiers; FileVersion is usually numeric only.
        var version = info.ProductVersion?.Split('+')[0].Trim();
        return AppUpdateService.IsVersion(version) ? version : null;
    }
}
