using System.Buffers.Binary;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;

namespace Dlss5CompatApp.Tests;

public sealed class PhoenixUpdateTests
{
    [Fact]
    public async Task CurrentRequiresVerifiedBytesAndNotices()
    {
        using var fixture = new Fixture();
        var current = await PhoenixUpdateService.AssessAsync(fixture.Root, fixture.Cached, fixture.Release, default);
        Assert.Equal(PhoenixUpdateState.Current, current.State);
        File.AppendAllText(fixture.Addon, "modified");
        var damaged = await PhoenixUpdateService.AssessAsync(fixture.Root, fixture.Cached, fixture.Release, default);
        Assert.Equal(PhoenixUpdateState.NeedsRepair, damaged.State);
    }

    [Fact]
    public async Task MissingNoticesNeverReportsCurrent()
    {
        using var fixture = new Fixture();
        File.Delete(Path.Combine(fixture.Root, "phoenix-notices/test/NOTICE"));
        Assert.Equal(PhoenixUpdateState.NeedsRepair,
            (await PhoenixUpdateService.AssessAsync(fixture.Root, fixture.Cached, fixture.Release, default)).State);
    }

    [Fact]
    public async Task ImportedDevelopmentBuildIsVisibleEvenWhenItsBytesMatchPublished()
    {
        using var fixture = new Fixture();
        var result = await PhoenixUpdateService.AssessAsync(fixture.Root, fixture.Cached with { IsDevelopment = true, Version = "local-development" }, fixture.Release, default);
        Assert.Equal(PhoenixUpdateState.LocalBuild, result.State);
        Assert.Contains("not verified as current", result.Message);
        Assert.Contains("0.18", result.Message);
    }

    [Fact]
    public async Task ReplacedAssetsWithSameVersionRequireUpdate()
    {
        using var fixture = new Fixture();
        var changed = fixture.Release with { Assets = [fixture.Release.Assets[0] with { Digest = "sha256:" + new string('a', 64) }] };
        Assert.Equal(PhoenixUpdateState.UpdateAvailable,
            (await PhoenixUpdateService.AssessAsync(fixture.Root, fixture.Cached, changed, default)).State);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task LatestCheckUsesMetadataOnlyAndHonorsReleaseChannel(bool stableOnly)
    {
        using var fixture = new Fixture();
        var requests = new List<string>();
        using var http = new HttpClient(new Handler(request =>
        {
            requests.Add(request.RequestUri!.AbsoluteUri);
            return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new[] {
                ReleaseJson("0.18", false, "2026-10-28T10:00:00Z", fixture.Release.Assets),
                ReleaseJson("0.19-preview", true, "2026-10-29T10:00:00Z", fixture.Release.Assets)
            })) };
        }));
        await File.WriteAllTextAsync(Path.Combine(fixture.Root, "phoenix-release.json"), JsonSerializer.Serialize(fixture.Cached));
        var result = await PhoenixUpdateService.CheckAsync(fixture.Root,
            stableOnly ? PhoenixReleaseChannel.StableOnly : PhoenixReleaseChannel.LatestPublished, client: http);
        Assert.Equal(stableOnly ? "0.18" : "0.19-preview", result.LatestVersion);
        Assert.Equal(stableOnly ? PhoenixUpdateState.Current : PhoenixUpdateState.UpdateAvailable, result.State);
        Assert.All(requests, url => Assert.StartsWith("https://api.github.com/", url));
    }

    [Fact]
    public async Task OfflineIsUnknownRatherThanCurrent()
    {
        using var fixture = new Fixture();
        using var http = new HttpClient(new Handler(_ => throw new HttpRequestException("Offline fixture")));
        var result = await PhoenixUpdateService.CheckAsync(fixture.Root, PhoenixReleaseChannel.LatestPublished, client: http);
        Assert.Equal(PhoenixUpdateState.Unavailable, result.State);
        Assert.Contains("Could not check", result.Message);
    }

    [Fact]
    public async Task CallerCancellationIsPreserved()
    {
        using var fixture = new Fixture(); using var cancel = new CancellationTokenSource(); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => PhoenixUpdateService.CheckAsync(fixture.Root, PhoenixReleaseChannel.LatestPublished, cancel.Token));
    }

    [Fact]
    public async Task MissingComponentShowsPublishedVersion()
    {
        using var fixture = new Fixture();
        var result = await PhoenixUpdateService.AssessAsync(fixture.Root, null, fixture.Release, default);
        Assert.Equal(PhoenixUpdateState.Missing, result.State);
        Assert.Contains("0.18", result.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PerGameStatusUsesTheMatchingArchitecture(bool x86)
    {
        using var fixture = new Fixture();
        var old = fixture.Workspace.FilePath("old.addon64"); File.WriteAllText(old, "old build");
        var expected = new PhoenixUpdateResult(PhoenixUpdateState.Current, "Current", "0.18", "0.18", fixture.Cached.Sha256, fixture.Cached.Sha256);
        var arch = x86 ? CpuArch.X86 : CpuArch.X64;
        Assert.Contains("Reinstall", await PhoenixUpdateService.InstalledStatusAsync(old, arch, expected, default));
        Assert.Contains("matches current release 0.18", await PhoenixUpdateService.InstalledStatusAsync(fixture.Addon, arch, expected, default));
        Assert.Contains("not verified as the latest", await PhoenixUpdateService.InstalledStatusAsync(fixture.Addon, arch,
            expected with { State = PhoenixUpdateState.UpdateAvailable }, default));
        Assert.Contains("not checked", await PhoenixUpdateService.InstalledStatusAsync(fixture.Addon, arch, null, default));
        Assert.Contains("unknown", await PhoenixUpdateService.InstalledStatusAsync(fixture.Addon, CpuArch.X86,
            expected with { Companion32Sha256 = null }, default));
    }

    static object ReleaseJson(string tag, bool preview, string published, PhoenixReleaseAsset[] assets) => new {
        tag_name = tag, draft = false, prerelease = preview, published_at = published,
        assets = assets.Select(a => new { name = a.Name, browser_download_url = a.Url, size = a.Size, digest = a.Digest }) };

    sealed class Fixture : IDisposable
    {
        public TestWorkspace Workspace { get; } = new();
        public string Root => Workspace.Root;
        public string Addon { get; }
        public PhoenixReleasePayload Cached { get; }
        public PhoenixPublishedRelease Release { get; }
        public Fixture()
        {
            Addon = Workspace.WritePe("phoenix.addon64", CpuArch.X64);
            var bytes = File.ReadAllBytes(Addon); BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(0x96), 0x2022);
            File.WriteAllBytes(Addon, bytes); TestWorkspace.SetPhoenixFeederExports(Addon); TestWorkspace.SetEmbeddedPhoenixRuntime(Addon);
            var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Addon)));
            var notices = "phoenix-notices/test"; Directory.CreateDirectory(Workspace.FilePath(notices));
            foreach (var file in new[] { "LICENSE", "NOTICE", "THIRD_PARTY.md" }) File.WriteAllText(Workspace.FilePath(notices + "/" + file), "Fixture notice");
            var hashes = Directory.GetFiles(Workspace.FilePath(notices)).ToDictionary(p => Path.GetFileName(p), p => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p))));
            var url = "https://github.com/RazvanManolache/dlss5-phoenix-addon/releases/download/0.18/phoenix.addon64";
            Release = new("0.18", false, DateTimeOffset.UtcNow, [new("phoenix.addon64", url, new FileInfo(Addon).Length, "sha256:" + hash)]);
            Cached = new(url, "0.18:phoenix.addon64", new FileInfo(Addon).Length, hash, false, notices,
                "sha256:" + hash, new FileInfo(Addon).Length, PhoenixReleaseCache.AssetsFingerprint(Release.Assets), hashes);
        }
        public void Dispose() => Workspace.Dispose();
    }
    sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(respond(request));
    }
}
