using System.Net;
using System.Security.Cryptography;
using System.Text.Json;

namespace Dlss5CompatApp.Tests;

public sealed class CompanionUpdateTests
{
    sealed class Handler(string repository, string body, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Calls++; Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal($"https://api.github.com/repos/{repository}/releases?per_page=30", request.RequestUri!.AbsoluteUri);
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
        }
    }
    static string Releases(string tag = "v1.0-beta") => JsonSerializer.Serialize(new[] {
        new { tag_name = tag, draft = false, prerelease = true, published_at = "2026-10-30T12:00:00Z" } });

    [Theory]
    [InlineData((int)CompanionProject.DosBoxEnhanced)]
    [InlineData((int)CompanionProject.VideoPlayer)]
    public async Task MissingComponentsAreCheckedAgainstTheirOwnRepository(int projectId)
    {
        var project = (CompanionProject)projectId;
        using var handler = new Handler(CompanionUpdateService.Repository(project), Releases()); using var client = new HttpClient(handler);
        var result = await CompanionUpdateService.CheckAsync(project, null, PhoenixReleaseChannel.LatestPublished, client: client);
        Assert.Equal(CompanionUpdateState.Missing, result.State); Assert.Equal("v1.0-beta", result.LatestVersion);
        Assert.Null(result.InstalledVersion); Assert.False(result.NeedsUpdate); Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData("0.1.0", "v1.0-beta", true)]
    [InlineData("v1.0-beta", "v1.0-beta", false)]
    [InlineData("v1.0-beta", "v1.0-beta.2", true)]
    [InlineData("1.0.0", "v1.0-beta", false)]
    public async Task VideoReceiptIdentifiesTheActualExecutableAndBetaVersion(string installed, string published, bool update)
    {
        using var workspace = new TestWorkspace();
        var binary = workspace.WritePe("Media/PhoenixVideoPlayer.exe", CpuArch.X64);
        WriteReceipt(binary, installed);
        using var handler = new Handler(CompanionUpdateService.Repository(CompanionProject.VideoPlayer), Releases(published));
        using var client = new HttpClient(handler);
        var result = await CompanionUpdateService.CheckAsync(CompanionProject.VideoPlayer, binary, PhoenixReleaseChannel.LatestPublished, client: client);
        Assert.Equal(update, result.NeedsUpdate); Assert.Equal(installed, result.InstalledVersion); Assert.Equal(binary, result.BinaryPath);
        // Equal version numbers without a remote authenticated inventory are unverified.
        Assert.Equal(update ? CompanionUpdateState.UpdateAvailable : CompanionUpdateState.Unverified, result.State);
    }

    [Fact]
    public async Task MismatchedReceiptNeverClaimsAnInstalledVersionIsCurrent()
    {
        using var workspace = new TestWorkspace(); var binary = workspace.WritePe("PhoenixVideoPlayer.exe", CpuArch.X64);
        WriteReceipt(binary, "v1.0-beta"); File.AppendAllText(binary, "different build");
        using var handler = new Handler(CompanionUpdateService.Repository(CompanionProject.VideoPlayer), Releases()); using var client = new HttpClient(handler);
        var result = await CompanionUpdateService.CheckAsync(CompanionProject.VideoPlayer, binary, PhoenixReleaseChannel.LatestPublished, client: client);
        Assert.Equal(CompanionUpdateState.UnknownVersion, result.State); Assert.Null(result.InstalledVersion);
        Assert.Equal("v1.0-beta", result.LatestVersion);
    }

    [Fact]
    public async Task DosboxReadsInformationalVersionWithoutExecutingIt()
    {
        using var workspace = new TestWorkspace();
        var binary = workspace.FilePath("DosboxEnhanced.exe"); File.Copy(typeof(MainForm).Assembly.Location, binary);
        using var handler = new Handler(CompanionUpdateService.Repository(CompanionProject.DosBoxEnhanced), Releases("v9.0"));
        using var client = new HttpClient(handler);
        var result = await CompanionUpdateService.CheckAsync(CompanionProject.DosBoxEnhanced, binary, PhoenixReleaseChannel.LatestPublished, client: client);
        Assert.Equal(AppUpdateService.CurrentVersion, result.InstalledVersion); Assert.True(result.NeedsUpdate);
    }

    [Fact]
    public async Task StableChannelDoesNotOfferABetaAndOfflineIsNotSuccess()
    {
        using var handler = new Handler(CompanionUpdateService.Repository(CompanionProject.DosBoxEnhanced), Releases()); using var client = new HttpClient(handler);
        var stable = await CompanionUpdateService.CheckAsync(CompanionProject.DosBoxEnhanced, null, PhoenixReleaseChannel.StableOnly, client: client);
        Assert.Equal(CompanionUpdateState.Unavailable, stable.State); Assert.Null(stable.LatestVersion);
        using var offlineHandler = new Handler(CompanionUpdateService.Repository(CompanionProject.VideoPlayer), "{}", HttpStatusCode.ServiceUnavailable);
        using var offline = new HttpClient(offlineHandler);
        Assert.Equal(CompanionUpdateState.Unavailable, (await CompanionUpdateService.CheckAsync(CompanionProject.VideoPlayer, null,
            PhoenixReleaseChannel.LatestPublished, client: offline)).State);
    }

    [Fact]
    public async Task CancellationAndUnknownRepositoryDoNotPerformRequests()
    {
        using var handler = new Handler("unused", "[]"); using var client = new HttpClient(handler);
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CompanionUpdateService.CheckAsync(CompanionProject.DosBoxEnhanced,
            null, PhoenixReleaseChannel.LatestPublished, cancel.Token, client));
        await Assert.ThrowsAsync<ArgumentException>(() => AppUpdateService.CheckRepositoryAsync("evil/repo", "1.0", client));
        Assert.Equal(0, handler.Calls);
    }

    static void WriteReceipt(string binary, string tag) => File.WriteAllText(Path.Combine(Path.GetDirectoryName(binary)!, "package-manifest.json"),
        JsonSerializer.Serialize(new { schema = "phoenix-player-package/1", version = "1.0.0", release_tag = tag,
            files = new Dictionary<string, string> { [Path.GetFileName(binary)] = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(binary))) } }));
}
