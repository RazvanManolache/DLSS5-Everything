using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;

namespace Dlss5CompatApp;

enum AppUpdateStatus { UpToDate, UpdateAvailable, NoRelease, Failed, Unverified, NeedsRepair }
sealed record AppUpdateResult(AppUpdateStatus Status, string CurrentVersion, string? LatestVersion,
    Uri ReleasesUrl, string? Error = null)
{
    public PhoenixReleaseAsset[] Assets { get; init; } = [];
    public bool Newer => Status == AppUpdateStatus.UpdateAvailable;
    public bool CheckSucceeded => Status is AppUpdateStatus.UpToDate or AppUpdateStatus.UpdateAvailable;
}

static class AppUpdateService
{
    // This checkout's origin, not the Swapper upstream or its payload repository.
    public const string Repository = "RazvanManolache/DLSS5-Everything";
    public static Uri ReleasesUrl => new($"https://github.com/{Repository}/releases");
    public static Uri CheckUrl => new($"https://api.github.com/repos/{Repository}/releases?per_page=30");
    public static string CurrentVersion => (typeof(AppUpdateService).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(AppUpdateService).Assembly.GetName().Version?.ToString() ?? "0.0.0").Split('+')[0];
    static readonly HttpClient Client = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan };

    // Only public metadata is read. Include previews because Everything ships preview releases.
    public static Task<AppUpdateResult> CheckAsync(string currentVersion, HttpClient? client = null,
        CancellationToken cancellationToken = default) => CheckRepositoryAsync(Repository, currentVersion, client, cancellationToken);

    internal static async Task<AppUpdateResult> CheckRepositoryAsync(string repository, string currentVersion,
        HttpClient? client = null, CancellationToken cancellationToken = default, bool stableOnly = false)
    {
        if (repository is not (Repository or "RazvanManolache/dosbox-enhanced" or "RazvanManolache/dlss5-phoenix-video-player"))
            throw new ArgumentException("Unknown update repository.", nameof(repository));
        var releasesUrl = new Uri($"https://github.com/{repository}/releases");
        var checkUrl = $"https://api.github.com/repos/{repository}/releases?per_page=30";
        cancellationToken.ThrowIfCancellationRequested();
        AppUpdateResult Failed(string message) => new(AppUpdateStatus.Failed, currentVersion, null, releasesUrl, message);
        if (!ReleaseVersion.TryParse(currentVersion, out var current)) return Failed("The running version cannot be compared to releases.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        ReleaseVersion? newest = null;
        string? latestTag = null;
        PhoenixReleaseAsset[] latestAssets = [];
        try
        {
            for (int page = 1; page <= 10; page++)
            {
                // Build pagination locally rather than following a URL from remote metadata.
                using var request = new HttpRequestMessage(HttpMethod.Get, checkUrl + (page == 1 ? "" : "&page=" + page));
                request.Headers.UserAgent.ParseAdd("DLSS5-Everything-update-notice");
                request.Headers.Accept.ParseAdd("application/vnd.github+json");
                using var response = await (client ?? Client).SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
                if (response.StatusCode == HttpStatusCode.NotFound)
                    return new(AppUpdateStatus.NoRelease, currentVersion, null, releasesUrl, "No published release was found.");
                if (!response.IsSuccessStatusCode) return Failed($"Update check returned HTTP {(int)response.StatusCode}.");
                const int limit = 512 * 1024;
                if (response.Content.Headers.ContentLength > limit) return Failed("Release response exceeds the read limit.");
                using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
                var bytes = new byte[limit + 1];
                var count = 0;
                while (count < bytes.Length)
                {
                    var read = await stream.ReadAsync(bytes.AsMemory(count), timeout.Token).ConfigureAwait(false);
                    if (read == 0) break;
                    count += read;
                }
                if (count > limit) return Failed("Release response exceeds the read limit.");
                using var json = JsonDocument.Parse(bytes.AsMemory(0, count), new JsonDocumentOptions { MaxDepth = 24 });
                if (json.RootElement.ValueKind != JsonValueKind.Array) return Failed("Release metadata is not a release list.");
                foreach (var release in json.RootElement.EnumerateArray())
                {
                    if (release.ValueKind != JsonValueKind.Object
                        || !release.TryGetProperty("draft", out var draft) || draft.ValueKind != JsonValueKind.False
                        || !release.TryGetProperty("published_at", out var published) || published.ValueKind != JsonValueKind.String
                        || !published.TryGetDateTimeOffset(out _)
                        || !release.TryGetProperty("tag_name", out var tag) || tag.ValueKind != JsonValueKind.String
                        || !ReleaseVersion.TryParse(tag.GetString(), out var candidate)) continue;
                    if (stableOnly && (candidate!.Pre.Length > 0 ||
                        release.TryGetProperty("prerelease", out var preview) && preview.ValueKind == JsonValueKind.True)) continue;
                    if (newest is null || candidate!.CompareTo(newest) > 0)
                    {
                        newest = candidate; latestTag = tag.GetString();
                        latestAssets = release.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array
                            ? assets.EnumerateArray().Where(a => a.ValueKind == JsonValueKind.Object).Select(a => new PhoenixReleaseAsset(
                                Text(a, "name") ?? "", Text(a, "browser_download_url") ?? "",
                                a.TryGetProperty("size", out var size) && size.TryGetInt64(out long bytes) ? bytes : -1,
                                Text(a, "digest"))).ToArray() : [];
                    }
                }
                if (response.Headers.TryGetValues("Link", out var links)
                    && links.Any(link => link.Contains("rel=\"next\"", StringComparison.Ordinal))) continue;
                if (newest is null)
                    return new(AppUpdateStatus.NoRelease, currentVersion, null, releasesUrl, "No comparable published release was found.");
                return new(newest.CompareTo(current!) > 0 ? AppUpdateStatus.UpdateAvailable : AppUpdateStatus.UpToDate,
                    currentVersion, latestTag, new Uri($"https://github.com/{repository}/releases/tag/{Uri.EscapeDataString(latestTag!)}")) { Assets = latestAssets };
            }
            return Failed("Release listing exceeds its pagination limit.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return Failed("Update check timed out."); }
        catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or InvalidOperationException)
        { return Failed("Update check failed: " + ex.Message); }
    }

    internal static bool IsVersion(string? value) => ReleaseVersion.TryParse(value, out _);
    internal static int? CompareVersions(string? a, string? b) => ReleaseVersion.TryParse(a, out var first)
        && ReleaseVersion.TryParse(b, out var second) ? first!.CompareTo(second) : null;
    static string? Text(JsonElement value, string key) => value.TryGetProperty(key, out var text) && text.ValueKind == JsonValueKind.String ? text.GetString() : null;

    internal static async Task<AppUpdateResult> CheckInstalledAppAsync(string currentVersion, CancellationToken token = default,
        HttpClient? client = null, string? directory = null)
    {
        var result = await CheckAsync(currentVersion, client, token);
        if (!result.CheckSucceeded || result.Newer) return result;
        return await ReleaseFileVerifier.VerifyAsync(Repository, result, directory ?? AppContext.BaseDirectory,
            "Dlss5CompatApp.dll", token, client);
    }

    sealed record ReleaseVersion(int[] Numbers, string[] Pre) : IComparable<ReleaseVersion>
    {
        public static bool TryParse(string? text, out ReleaseVersion? version)
        {
            version = null;
            if (string.IsNullOrWhiteSpace(text) || text.Length > 128) return false;
            text = text.Trim().TrimStart('v', 'V');
            var build = text.Split('+');
            if (build.Length > 2 || build.Length == 2 && !Identifiers(build[1])) return false;
            var parts = build[0].Split('-', 2);
            var digits = parts[0].Split('.');
            if (digits.Length is < 2 or > 4) return false;
            var numbers = new int[4];
            for (var i = 0; i < digits.Length; i++)
            {
                if (digits[i].Length == 0 || !digits[i].All(char.IsAsciiDigit) || !int.TryParse(digits[i], out numbers[i])) return false;
            }
            if (parts.Length == 2 && !Identifiers(parts[1])) return false;
            version = new(numbers, parts.Length == 2 ? parts[1].Split('.') : []);
            return true;
        }
        static bool Identifiers(string value) => value.Split('.').All(p => p.Length > 0 && p.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'));
        public int CompareTo(ReleaseVersion? other)
        {
            if (other is null) return 1;
            for (var i = 0; i < 4; i++) { var cmp = Numbers[i].CompareTo(other.Numbers[i]); if (cmp != 0) return cmp; }
            if (Pre.Length == 0 || other.Pre.Length == 0) return (Pre.Length == 0 ? 1 : 0).CompareTo(other.Pre.Length == 0 ? 1 : 0);
            for (var i = 0; i < Math.Min(Pre.Length, other.Pre.Length); i++)
            {
                var a = Pre[i]; var b = other.Pre[i];
                var an = a.All(char.IsAsciiDigit); var bn = b.All(char.IsAsciiDigit);
                var cmp = an && bn ? CompareDigits(a, b) : an != bn ? (an ? -1 : 1) : string.CompareOrdinal(a, b);
                if (cmp != 0) return cmp;
            }
            return Pre.Length.CompareTo(other.Pre.Length);
        }
        static int CompareDigits(string a, string b)
        {
            a = a.TrimStart('0'); b = b.TrimStart('0');
            return a.Length == b.Length ? string.CompareOrdinal(a, b) : a.Length.CompareTo(b.Length);
        }
    }
}
