using System.Net;
using System.Net.Http;
using System.Text.Json;

namespace Dlss5CompatApp;

enum AppUpdateStatus { UpToDate, UpdateAvailable, NoRelease, Failed }
sealed record AppUpdateResult(AppUpdateStatus Status, string CurrentVersion, string? LatestVersion,
    Uri ReleasesUrl, string? Error = null)
{
    public bool Newer => Status == AppUpdateStatus.UpdateAvailable;
    public bool CheckSucceeded => Status is AppUpdateStatus.UpToDate or AppUpdateStatus.UpdateAvailable;
}

static class AppUpdateService
{
    // This checkout's origin, not the Swapper upstream or its payload repository.
    public const string Repository = "RazvanManolache/DLSS5-Everything";
    public static Uri ReleasesUrl => new($"https://github.com/{Repository}/releases");
    public static Uri CheckUrl => new($"https://api.github.com/repos/{Repository}/releases/latest");
    static readonly HttpClient Client = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan };

    // Only an explicit call performs a GET. No assets are downloaded or executed.
    public static async Task<AppUpdateResult> CheckAsync(string currentVersion, HttpClient? client = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        AppUpdateResult Failed(string message) => new(AppUpdateStatus.Failed, currentVersion, null, ReleasesUrl, message);
        if (!ReleaseVersion.TryParse(currentVersion, out var current)) return Failed("The running version cannot be compared to releases.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, CheckUrl);
            request.Headers.UserAgent.ParseAdd("DLSS5-Everything/1.0");
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            using var response = await (client ?? Client).SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (response.StatusCode == HttpStatusCode.NotFound)
                return new(AppUpdateStatus.NoRelease, currentVersion, null, ReleasesUrl, "No published release was found.");
            if (!response.IsSuccessStatusCode) return Failed($"Update check returned HTTP {(int)response.StatusCode}.");
            const int limit = 128 * 1024;
            if (response.Content.Headers.ContentLength > limit) return Failed("Release response exceeds the read limit.");
            using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            var bytes = new byte[limit + 1];
            var count = 0;
            while (count < bytes.Length)
            {
                var read = await stream.ReadAsync(bytes.AsMemory(count), timeout.Token);
                if (read == 0) break;
                count += read;
            }
            if (count > limit) return Failed("Release response exceeds the read limit.");
            using var json = JsonDocument.Parse(bytes.AsMemory(0, count), new JsonDocumentOptions { MaxDepth = 24 });
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("tag_name", out var tag) || tag.ValueKind != JsonValueKind.String
                || !ReleaseVersion.TryParse(tag.GetString(), out var latest)) return Failed("Release has no comparable version tag.");
            if (root.TryGetProperty("draft", out var draft) && draft.ValueKind != JsonValueKind.False
                || root.TryGetProperty("prerelease", out var prerelease) && prerelease.ValueKind != JsonValueKind.False)
                return Failed("The latest release is not a published stable release.");
            var status = latest!.CompareTo(current!) > 0 ? AppUpdateStatus.UpdateAvailable : AppUpdateStatus.UpToDate;
            return new(status, currentVersion, tag.GetString(), new Uri($"https://github.com/{Repository}/releases/tag/{Uri.EscapeDataString(tag.GetString()!)}"));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return Failed("Update check timed out."); }
        catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or InvalidOperationException)
        { return Failed("Update check failed: " + ex.Message); }
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
