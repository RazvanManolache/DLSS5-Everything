using System.Net;

namespace Dlss5CompatApp.Tests;

public sealed class GitHubReleaseFallbackTests
{
    [Fact]
    public async Task ResolvesOfficialStableRedirectAndKeepsAssetDigest()
    {
        var requests = new List<string>();
        using var http = new HttpClient(new Handler(request =>
        {
            requests.Add(request.RequestUri!.AbsoluteUri);
            if (request.RequestUri.AbsolutePath.EndsWith("/latest"))
                return new(HttpStatusCode.OK) { RequestMessage = new(HttpMethod.Get, "https://github.com/owner/project/releases/tag/v1") };
            return new(HttpStatusCode.OK) { Content = new StringContent(
                "<li><a href=\"/owner/project/releases/download/v1/runtime.zip\">runtime</a><span>sha256:" + new string('a', 64) + "</span></li>") };
        }));
        var result = await GitHubReleaseFallback.ReadAsync(http, "owner/project", "latest", default);
        Assert.Equal("v1", result.Tag);
        Assert.Equal("sha256:" + new string('a', 64), Assert.Single(result.Assets).Digest);
        Assert.Equal(new[] { "https://github.com/owner/project/releases/latest",
            "https://github.com/owner/project/releases/expanded_assets/v1" }, requests);
    }

    [Theory]
    [InlineData("https://example.com/owner/project/releases/tag/v1")]
    [InlineData("https://github.com/other/project/releases/tag/v1")]
    [InlineData("https://github.com/owner/project/releases/latest")]
    public async Task RejectsUntrustedOrUnresolvedLatestRedirect(string finalUrl)
    {
        using var http = new HttpClient(new Handler(_ => new(HttpStatusCode.OK) { RequestMessage = new(HttpMethod.Get, finalUrl) }));
        await Assert.ThrowsAsync<InvalidDataException>(() => GitHubReleaseFallback.ReadAsync(http, "owner/project", "latest", default));
    }

    [Theory]
    [InlineData("/owner/project/releases/download/v1/runtime.zip", "")]
    [InlineData("/other/project/releases/download/v1/runtime.zip", "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public void RejectsMissingDigestOrWrongRepository(string href, string digest)
    {
        Assert.Throws<InvalidDataException>(() => PhoenixReleaseCatalog.ParsePublicAssets(
            "<li><a href=\"" + href + "\">runtime</a>" + digest + "</li>", "v1", "owner/project"));
    }

    sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            Task.FromResult(respond(request));
    }
}
