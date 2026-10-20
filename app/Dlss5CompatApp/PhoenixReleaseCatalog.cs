using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace Dlss5CompatApp;

enum PhoenixReleaseChannel { LatestPublished, StableOnly }

sealed record PhoenixReleaseAsset(string Name, string Url, long Size, string? Digest, bool IsRawNotice = false);
sealed record PhoenixPublishedRelease(string Tag, bool IsPreview, DateTimeOffset PublishedAt, PhoenixReleaseAsset[] Assets);

static class PhoenixReleaseCatalog
{
    public const string ReleasesUrl = "https://api.github.com/repos/" + PhoenixReleaseDownloader.Repository + "/releases?per_page=100";
    public const string PublicRoot = "https://github.com/" + PhoenixReleaseDownloader.Repository;
    static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(2);

    public static async Task<PhoenixPublishedRelease> GetLatestAsync(HttpClient http, PhoenixReleaseChannel channel,
        Action<PhoenixDownloadProgress>? progress, CancellationToken token)
    {
        if (!Enum.IsDefined(channel)) throw new ArgumentOutOfRangeException(nameof(channel));
        try
        {
            var releases = new List<PhoenixPublishedRelease>();
            for (var page = 1; page <= 20; page++)
            {
                using var response = await GetAsync(http, ReleasesUrl + (page == 1 ? "" : "&page=" + page), token);
                if (response.StatusCode == HttpStatusCode.NotFound)
                    throw new PhoenixReleaseUnavailableException(
                        "Phoenix DLSS5 published releases returned HTTP 404; no public release is available or the repository is inaccessible. " +
                        "Select a local development addon explicitly with localPhoenixAddonPath. No RenoDX fallback is used.");
                ThrowApiFailure(response);
                using var document = JsonDocument.Parse(await ReadBoundedAsync(response, token));
                releases.AddRange(document.RootElement.EnumerateArray().Select(ParseRelease).OfType<PhoenixPublishedRelease>());
                if (!response.Headers.TryGetValues("Link", out var links) || !links.Any(link => link.Contains("rel=\"next\"", StringComparison.Ordinal)))
                    return Select(releases, channel);
            }
            throw new InvalidDataException("Phoenix DLSS5 release listing exceeded its pagination limit; latest publication cannot be established.");
        }
        catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
        {
            var status = $"GitHub API HTTP {(int)ex.StatusCode.Value}: rate-limited or access denied";
            progress?.Invoke(new PhoenixDownloadProgress(status + "; checking public GitHub release metadata...", 0, null));
            try { return await GetPublicLatestAsync(http, channel, token); }
            catch (Exception fallback) when (fallback is HttpRequestException or InvalidDataException or XmlException)
            {
                throw new HttpRequestException(status + ". Auth-free public release lookup failed: " + fallback.Message, fallback, ex.StatusCode);
            }
        }
    }

    static PhoenixPublishedRelease Select(IEnumerable<PhoenixPublishedRelease> releases, PhoenixReleaseChannel channel) =>
        releases.Where(r => channel == PhoenixReleaseChannel.LatestPublished || !r.IsPreview)
            .OrderByDescending(r => r.PublishedAt).ThenBy(r => r.Tag, StringComparer.Ordinal).FirstOrDefault()
        ?? throw new PhoenixReleaseUnavailableException(channel == PhoenixReleaseChannel.StableOnly
            ? "No published stable Phoenix DLSS5 release is available. StableOnly excludes previews; select LatestPublished to include them."
            : "No published Phoenix DLSS5 release is available. Explicitly select a local development addon with localPhoenixAddonPath.");

    static PhoenixPublishedRelease? ParseRelease(JsonElement root)
    {
        if (root.GetProperty("draft").GetBoolean()) return null;
        if (!root.TryGetProperty("published_at", out var published) || !published.TryGetDateTimeOffset(out var date)) return null;
        var tag = root.GetProperty("tag_name").GetString() ?? "";
        ValidateTag(tag);
        var assets = root.GetProperty("assets").EnumerateArray().Select(a => new PhoenixReleaseAsset(
            a.GetProperty("name").GetString() ?? "", a.GetProperty("browser_download_url").GetString() ?? "",
            a.GetProperty("size").GetInt64(), a.TryGetProperty("digest", out var digest) ? digest.GetString() : null)).ToArray();
        return new PhoenixPublishedRelease(tag, root.GetProperty("prerelease").GetBoolean(), date, assets);
    }

    static async Task<PhoenixPublishedRelease> GetPublicLatestAsync(HttpClient http, PhoenixReleaseChannel channel, CancellationToken token)
    {
        using var response = await GetAsync(http, PublicRoot + "/releases.atom", token);
        response.EnsureSuccessStatusCode();
        var xml = await ReadBoundedAsync(response, token);
        using var reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 4 * 1024 * 1024
        });
        var feed = XDocument.Load(reader);
        XNamespace atom = "http://www.w3.org/2005/Atom";
        var releases = new List<PhoenixPublishedRelease>();
        foreach (var entry in feed.Root?.Elements(atom + "entry").Take(30) ?? [])
        {
            token.ThrowIfCancellationRequested();
            var href = entry.Elements(atom + "link").FirstOrDefault(e => (string?)e.Attribute("rel") == "alternate")?.Attribute("href")?.Value;
            if (!Uri.TryCreate(href, UriKind.Absolute, out var uri) || !IsGitHubUri(uri) ||
                !uri.AbsolutePath.StartsWith("/" + PhoenixReleaseDownloader.Repository + "/releases/tag/", StringComparison.Ordinal))
                throw new InvalidDataException("Public Phoenix DLSS5 release feed contains an untrusted tag link.");
            var tag = Uri.UnescapeDataString(uri.AbsolutePath[("/" + PhoenixReleaseDownloader.Repository + "/releases/tag/").Length..]);
            ValidateTag(tag);
            using var pageResponse = await GetAsync(http, PublicRoot + "/releases/tag/" + Uri.EscapeDataString(tag), token);
            pageResponse.EnsureSuccessStatusCode();
            var html = await ReadBoundedAsync(pageResponse, token);
            var published = Regex.Match(html, "released this[\\s\\S]{0,2000}?<relative-time\\b[^>]*datetime=[\"']([^\"']+)", RegexOptions.IgnoreCase, RegexTimeout);
            if (!published.Success || !DateTimeOffset.TryParse(published.Groups[1].Value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date))
                throw new InvalidDataException("Public Phoenix DLSS5 release page has no verifiable publication time.");
            if (Regex.IsMatch(html, "<span\\b[^>]*class=[\"'][^\"']*\\bLabel\\b[^\"']*[\"'][^>]*>\\s*Draft\\s*</span>", RegexOptions.IgnoreCase, RegexTimeout)) continue;
            var preview = Regex.IsMatch(html, "<span\\b[^>]*class=[\"'][^\"']*\\bLabel\\b[^\"']*[\"'][^>]*>\\s*Pre-release\\s*</span>", RegexOptions.IgnoreCase, RegexTimeout);
            releases.Add(new PhoenixPublishedRelease(tag, preview, date, []));
        }
        var selected = Select(releases, channel);
        using var assetResponse = await GetAsync(http, PublicRoot + "/releases/expanded_assets/" + Uri.EscapeDataString(selected.Tag), token);
        assetResponse.EnsureSuccessStatusCode();
        return selected with { Assets = ParsePublicAssets(await ReadBoundedAsync(assetResponse, token), selected.Tag) };
    }

    internal static PhoenixReleaseAsset[] ParsePublicAssets(string html, string tag)
    {
        ValidateTag(tag);
        var prefix = "/" + PhoenixReleaseDownloader.Repository + "/releases/download/" + Uri.EscapeDataString(tag) + "/";
        var assets = new List<PhoenixReleaseAsset>();
        foreach (Match row in Regex.Matches(html, @"<li\b[^>]*>([\s\S]*?)</li>", RegexOptions.IgnoreCase, RegexTimeout))
        {
            foreach (Match anchor in Regex.Matches(row.Groups[1].Value, "<a\\b[^>]*\\bhref\\s*=\\s*[\"']([^\"']+)[\"']", RegexOptions.IgnoreCase, RegexTimeout))
            {
                var href = WebUtility.HtmlDecode(anchor.Groups[1].Value);
                if (!Uri.TryCreate(new Uri("https://github.com"), href, out var uri) || !IsGitHubUri(uri) || !uri.AbsolutePath.StartsWith(prefix, StringComparison.Ordinal)) continue;
                var name = Uri.UnescapeDataString(uri.AbsolutePath[prefix.Length..]);
                if (string.IsNullOrWhiteSpace(name) || name.IndexOfAny(['/', '\\', ':']) >= 0 || name is "." or "..")
                    throw new InvalidDataException("Public Phoenix DLSS5 asset link has an unsafe filename.");
                var digest = Regex.Match(row.Groups[1].Value, @"\bsha256:([0-9a-fA-F]{64})\b", RegexOptions.IgnoreCase, RegexTimeout);
                if (!digest.Success) throw new InvalidDataException("Public Phoenix DLSS5 asset has no GitHub SHA256 digest: " + name);
                assets.Add(new PhoenixReleaseAsset(name, uri.AbsoluteUri, -1, "sha256:" + digest.Groups[1].Value));
            }
        }
        if (assets.Count == 0) throw new InvalidDataException("Public Phoenix DLSS5 release has no verified uploaded asset links.");
        return assets.ToArray();
    }

    internal static void ValidateTag(string tag)
    {
        if (string.IsNullOrWhiteSpace(tag) || tag.Length > 200 || tag.Any(c => char.IsControl(c) || c is '\\' or ':' or '?' or '#') ||
            tag.Split('/').Any(segment => segment is "" or "." or "..")) throw new InvalidDataException("Unsafe Phoenix DLSS5 release tag.");
    }

    static bool IsGitHubUri(Uri uri) => uri.Scheme == "https" && uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) &&
        uri.IsDefaultPort && uri.UserInfo.Length == 0 && uri.Query.Length == 0 && uri.Fragment.Length == 0;

    static void ThrowApiFailure(HttpResponseMessage response)
    {
        if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
            throw new HttpRequestException($"GitHub API HTTP {(int)response.StatusCode}: rate-limited or access denied.", null, response.StatusCode);
        response.EnsureSuccessStatusCode();
    }

    static async Task<HttpResponseMessage> GetAsync(HttpClient http, string url, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd("Dlss5CompatApp/1.0");
        request.Headers.Accept.ParseAdd(url.StartsWith("https://api.github.com/", StringComparison.Ordinal) ? "application/vnd.github+json" : "application/xml,text/html;q=0.9");
        return await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
    }

    static async Task<string> ReadBoundedAsync(HttpResponseMessage response, CancellationToken token)
    {
        if (response.Content.Headers.ContentLength > 4 * 1024 * 1024) throw new InvalidDataException("Phoenix DLSS5 release metadata is too large.");
        await using var input = await response.Content.ReadAsStreamAsync(token);
        using var memory = new MemoryStream();
        var buffer = new byte[32768];
        int read;
        while ((read = await input.ReadAsync(buffer, token)) > 0)
        {
            if (memory.Length + read > 4 * 1024 * 1024) throw new InvalidDataException("Phoenix DLSS5 release metadata is too large.");
            memory.Write(buffer, 0, read);
        }
        return System.Text.Encoding.UTF8.GetString(memory.ToArray());
    }
}
