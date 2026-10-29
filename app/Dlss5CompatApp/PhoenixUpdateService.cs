using System.Security.Cryptography;

namespace Dlss5CompatApp;

enum PhoenixUpdateState { Current, UpdateAvailable, LocalBuild, Missing, NeedsRepair, Unavailable }

sealed record PhoenixUpdateResult(PhoenixUpdateState State, string Message, string? LatestVersion = null,
    string? CachedVersion = null, string? CachedSha256 = null, string? Companion32Sha256 = null)
{
    public bool NeedsAttention => State != PhoenixUpdateState.Current;
}

static class PhoenixUpdateService
{
    public static async Task<PhoenixUpdateResult> CheckAsync(string root, PhoenixReleaseChannel channel,
        CancellationToken token = default, HttpClient? client = null)
    {
        using var ownedClient = client is null ? new HttpClient() : null;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(25));
        PhoenixReleasePayload? cached = null;
        try
        {
            cached = await PhoenixReleaseCache.ReadAsync(root, timeout.Token);
            var release = await PhoenixReleaseCatalog.GetLatestAsync(client ?? ownedClient!, channel, null, timeout.Token);
            return await AssessAsync(root, cached, release, timeout.Token);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            return new(PhoenixUpdateState.Unavailable, "Could not check Phoenix updates (timed out). Retry when online.", CachedVersion: cached?.Version);
        }
        catch (Exception error) when (error is HttpRequestException or IOException or UnauthorizedAccessException or
            System.Text.Json.JsonException or InvalidOperationException or System.Xml.XmlException)
        {
            return new(PhoenixUpdateState.Unavailable, "Could not check Phoenix updates. " + error.Message, CachedVersion: cached?.Version);
        }
    }

    internal static async Task<PhoenixUpdateResult> AssessAsync(string root, PhoenixReleasePayload? cached,
        PhoenixPublishedRelease release, CancellationToken token)
    {
        PhoenixUpdateResult Result(PhoenixUpdateState state, string message) => new(state, message, release.Tag,
            cached?.Version, cached?.Sha256, cached?.Companion32Sha256);
        if (cached is null)
            return Result(PhoenixUpdateState.Missing, $"Phoenix release not installed or unrecognized. Latest: {release.Tag}. Choose Update Phoenix.");
        if (cached.IsDevelopment)
            return Result(PhoenixUpdateState.LocalBuild, $"Imported local Phoenix build; latest published: {release.Tag}. Local build is not verified as current. Choose Update Phoenix to replace it.");
        if (!MatchesRelease(cached, release))
            return Result(PhoenixUpdateState.UpdateAvailable, $"Phoenix update available: {release.Tag}. Cached: {cached.Version}. Updating components does not update installed games.");
        if ((PhoenixReleaseDownloader.RuntimeAssets(release.Assets).Any() && cached.RuntimePath is null) ||
            !await PhoenixReleaseCache.VerifyAddonAsync(root, cached, token,
                release.Assets.Single(a => a.Url == cached.Source).Name.Equals(PhoenixReleaseDownloader.AddonFileName, StringComparison.OrdinalIgnoreCase)
                    ? cached.ReleaseAssetDigest : null) ||
            !await PhoenixReleaseCache.VerifyNoticesAsync(root, cached, token))
            return Result(PhoenixUpdateState.NeedsRepair, $"Phoenix {release.Tag} files need repair. Choose Update Phoenix.");
        return Result(PhoenixUpdateState.Current, $"Phoenix {release.Tag} is current. Installed games are checked separately.");
    }

    internal static bool MatchesRelease(PhoenixReleasePayload cached, PhoenixPublishedRelease release)
    {
        // Compare publication identity and assets, not numerical tag ordering:
        // a release can replace binaries without changing its version number.
        if (cached.IsDevelopment || cached.ReleaseAssetsFingerprint != PhoenixReleaseCache.AssetsFingerprint(release.Assets)) return false;
        var assets = release.Assets.Where(a => a.Name.Equals(PhoenixReleaseDownloader.AddonFileName, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (assets.Length == 0) assets = release.Assets.Where(a => a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) &&
            !PhoenixReleaseDownloader.ExcludedArchive(a.Name)).ToArray();
        var selected = assets.SingleOrDefault(a => a.Url == cached.Source && cached.Version == release.Tag + ":" + a.Name);
        return selected is not null && PhoenixReleaseDownloader.ArchiveRank(selected.Name) >= assets.Max(a => PhoenixReleaseDownloader.ArchiveRank(a.Name)) &&
            !string.IsNullOrWhiteSpace(selected.Digest) && string.Equals(cached.ReleaseAssetDigest, selected.Digest, StringComparison.OrdinalIgnoreCase) &&
            (selected.Size <= 0 || cached.ReleaseAssetSize == selected.Size);
    }

    public static async Task<string> InstalledStatusAsync(string addon, CpuArch arch, PhoenixUpdateResult? latest, CancellationToken token)
    {
        if (!File.Exists(addon)) return "Phoenix addon not found beside this executable; inspect components for host/legacy routes.";
        if (latest is null || latest.State == PhoenixUpdateState.Unavailable)
            return "Installed Phoenix version not checked against the latest release.";
        var expected = arch == CpuArch.X86 ? latest.Companion32Sha256 : latest.CachedSha256;
        if (string.IsNullOrWhiteSpace(expected)) return "Installed Phoenix version unknown. Update components, then reinstall this game.";
        try
        {
            await using var input = File.OpenRead(addon);
            if (input.Length > 512L * 1024 * 1024) return "Installed Phoenix file is unexpectedly large; inspect components.";
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(input, token));
            if (!hash.Equals(expected, StringComparison.OrdinalIgnoreCase))
                return "Installed Phoenix differs from the component library. Reinstall this game to use the selected release.";
            return latest.State == PhoenixUpdateState.Current ? $"Installed Phoenix matches current release {latest.LatestVersion}." :
                "Installed Phoenix matches the cached build, which is not verified as the latest release. Update Phoenix, then reinstall this game.";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { return "Installed Phoenix could not be checked: " + error.Message; }
    }
}
