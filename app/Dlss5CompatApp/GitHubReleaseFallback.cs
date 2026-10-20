using System.Text.RegularExpressions;

namespace Dlss5CompatApp;

/// <summary>Official public release metadata when the anonymous API quota is exhausted.</summary>
static class GitHubReleaseFallback
{
    internal static async Task<(string Tag, PhoenixReleaseAsset[] Assets)> ReadAsync(
        HttpClient http, string repository, string tagOrLatest, CancellationToken token)
    {
        if (!Regex.IsMatch(repository, @"\A[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+\z"))
            throw new ArgumentException("Invalid GitHub repository", nameof(repository));
        var root = "https://github.com/" + repository;
        var tag = tagOrLatest;
        if (tagOrLatest.Equals("latest", StringComparison.OrdinalIgnoreCase))
        {
            // GitHub's own latest redirect selects a published stable release.
            using var page = await PhoenixReleaseCatalog.GetAsync(http, root + "/releases/latest", token);
            page.EnsureSuccessStatusCode();
            var uri = page.RequestMessage?.RequestUri;
            var prefix = "/" + repository + "/releases/tag/";
            if (uri is null || !PhoenixReleaseCatalog.IsGitHubUri(uri) ||
                !uri.AbsolutePath.StartsWith(prefix, StringComparison.Ordinal))
                throw new InvalidDataException("GitHub latest release did not resolve to the requested repository.");
            tag = Uri.UnescapeDataString(uri.AbsolutePath[prefix.Length..]);
        }
        PhoenixReleaseCatalog.ValidateTag(tag);
        using var response = await PhoenixReleaseCatalog.GetAsync(http,
            root + "/releases/expanded_assets/" + Uri.EscapeDataString(tag), token);
        response.EnsureSuccessStatusCode();
        // The parser requires exact repository/tag links and a GitHub SHA256
        // for every asset. Never replace a rate-limit error with unchecked URLs.
        var assets = PhoenixReleaseCatalog.ParsePublicAssets(
            await PhoenixReleaseCatalog.ReadBoundedAsync(response, token), tag, repository);
        return (tag, assets);
    }
}
