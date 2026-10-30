using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Dlss5CompatApp.Tests;

public sealed class ReleaseFileVerifierTests
{
    sealed class Handler(byte[] bytes) : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Calls++; Assert.Equal(HttpMethod.Get, request.Method);
            Assert.StartsWith("https://github.com/RazvanManolache/", request.RequestUri!.AbsoluteUri);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
        }
    }
    static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    static AppUpdateResult Release(string repository, byte[] inventory) => new(AppUpdateStatus.UpToDate, "1.0-beta", "v1.0-beta",
        new Uri($"https://github.com/{repository}/releases/tag/v1.0-beta"))
    {
        Assets = [new("FILES_SHA256.json", $"https://github.com/{repository}/releases/download/v1.0-beta/FILES_SHA256.json",
            inventory.Length, "sha256:" + Hash(inventory))]
    };

    [Theory]
    [InlineData(AppUpdateService.Repository, "Dlss5CompatApp.dll")]
    [InlineData("RazvanManolache/dosbox-enhanced", "app/DosboxEnhanced.exe")]
    [InlineData("RazvanManolache/dlss5-phoenix-video-player", "PhoenixVideoPlayer.exe")]
    public async Task CurrentRequiresAuthenticatedInventoryAndEveryListedFile(string repository, string binary)
    {
        using var workspace = new TestWorkspace();
        var bytes = new byte[] { 1, 2, 3 };
        var installed = workspace.FilePath(binary); Directory.CreateDirectory(Path.GetDirectoryName(installed)!); File.WriteAllBytes(installed, bytes);
        var helper = workspace.FilePath("helper.dll"); File.WriteAllBytes(helper, bytes);
        var inventory = JsonSerializer.SerializeToUtf8Bytes(new[] { new { path = binary, sha256 = Hash(bytes) }, new { path = "helper.dll", sha256 = Hash(bytes) } });
        var release = Release(repository, inventory);
        using var handler = new Handler(inventory); using var client = new HttpClient(handler);
        async Task<AppUpdateResult> Check() => await ReleaseFileVerifier.VerifyAsync(repository, release, workspace.Root, binary, default, client, workspace.FilePath("state"));
        Assert.Equal(AppUpdateStatus.UpToDate, (await Check()).Status);
        File.WriteAllBytes(helper, [9]);
        Assert.Equal(AppUpdateStatus.NeedsRepair, (await Check()).Status);
        File.Delete(helper);
        Assert.Equal(AppUpdateStatus.NeedsRepair, (await Check()).Status);
    }

    [Fact]
    public async Task SameTagAssetReplacementIsReportedAndInstalledFilesAreReverified()
    {
        using var workspace = new TestWorkspace(); byte[] bytes = [1]; File.WriteAllBytes(workspace.FilePath("app.exe"), bytes);
        var inventory = JsonSerializer.SerializeToUtf8Bytes(new[] { new { path = "app.exe", sha256 = Hash(bytes) } });
        var release = Release(AppUpdateService.Repository, inventory);
        using var handler = new Handler(inventory); using var client = new HttpClient(handler);
        Assert.Equal(AppUpdateStatus.UpToDate, (await ReleaseFileVerifier.VerifyAsync(AppUpdateService.Repository, release,
            workspace.Root, "app.exe", default, client, workspace.FilePath("state"))).Status);
        release = release with { Assets = [.. release.Assets, new("package.zip", "https://github.com/package.zip", 10, "sha256:" + new string('a', 64))] };
        var replaced = await ReleaseFileVerifier.VerifyAsync(AppUpdateService.Repository, release, workspace.Root, "app.exe", default, client, workspace.FilePath("state"));
        Assert.Equal(AppUpdateStatus.UpToDate, replaced.Status); Assert.Contains("same tag", replaced.Error);
        Assert.Contains("installed files still match", replaced.Error);
    }

    [Theory]
    [InlineData("digest")]
    [InlineData("url")]
    [InlineData("missing-primary")]
    [InlineData("traversal")]
    [InlineData("duplicate")]
    [InlineData("old-schema")]
    public async Task UntrustedOrIncompleteEvidenceNeverReportsCurrent(string defect)
    {
        using var workspace = new TestWorkspace();
        var inventory = Encoding.UTF8.GetBytes(defect switch
        {
            "traversal" => "[{\"path\":\"../escape.dll\",\"sha256\":\"" + new string('a', 64) + "\"}]",
            "duplicate" => "[{\"path\":\"a.dll\",\"sha256\":\"" + new string('a', 64) + "\"},{\"path\":\"a.dll\",\"sha256\":\"" + new string('a', 64) + "\"}]",
            "old-schema" => "{\"schema\":\"old\",\"passed\":true}",
            _ => "[]"
        });
        var release = Release(AppUpdateService.Repository, inventory);
        if (defect == "digest") release = release with { Assets = [release.Assets[0] with { Digest = "sha256:" + new string('0', 64) }] };
        if (defect == "url") release = release with { Assets = [release.Assets[0] with { Url = "https://evil.test/FILES_SHA256.json" }] };
        using var handler = new Handler(inventory); using var client = new HttpClient(handler);
        Assert.Equal(AppUpdateStatus.Unverified, (await ReleaseFileVerifier.VerifyAsync(AppUpdateService.Repository, release,
            workspace.Root, "app.exe", default, client, workspace.FilePath("state"))).Status);
        if (defect == "url") Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task ReplacedSameTagBinariesRequireRepairAndBecomeCurrentAfterInstallation()
    {
        using var workspace = new TestWorkspace(); byte[] old = [1], updated = [2];
        var binary = workspace.FilePath("app.exe"); File.WriteAllBytes(binary, old);
        var inventory = JsonSerializer.SerializeToUtf8Bytes(new[] { new { path = "app.exe", sha256 = Hash(updated) } });
        var release = Release(AppUpdateService.Repository, inventory);
        using var handler = new Handler(inventory); using var client = new HttpClient(handler);
        async Task<AppUpdateResult> Check() => await ReleaseFileVerifier.VerifyAsync(AppUpdateService.Repository, release,
            workspace.Root, "app.exe", default, client, workspace.FilePath("state"));
        Assert.Equal(AppUpdateStatus.NeedsRepair, (await Check()).Status);
        File.WriteAllBytes(binary, updated);
        Assert.Equal(AppUpdateStatus.UpToDate, (await Check()).Status);
    }

    [Fact]
    public async Task DosKernelAndGraphMustMatchTheAuthenticatedNestedManifest()
    {
        using var workspace = new TestWorkspace(); byte[] bytes = [1, 2];
        Directory.CreateDirectory(workspace.FilePath("runtime/native"));
        File.WriteAllBytes(workspace.FilePath("app.exe"), bytes);
        File.WriteAllBytes(workspace.FilePath("runtime/native/kernel.fatbin"), bytes);
        File.WriteAllBytes(workspace.FilePath("runtime/native/native_graph.json"), bytes);
        var manifest = JsonSerializer.SerializeToUtf8Bytes(new { native_graph_sha256 = Hash(bytes),
            kernels = new[] { new { binary = "kernel.fatbin", binary_sha256 = Hash(bytes) } } });
        File.WriteAllBytes(workspace.FilePath("runtime/native/native_manifest.json"), manifest);
        var inventory = JsonSerializer.SerializeToUtf8Bytes(new[] { new { path = "app.exe", sha256 = Hash(bytes) },
            new { path = "runtime/native/native_manifest.json", sha256 = Hash(manifest) } });
        var release = Release("RazvanManolache/dosbox-enhanced", inventory);
        using var handler = new Handler(inventory); using var client = new HttpClient(handler);
        async Task<AppUpdateResult> Check() => await ReleaseFileVerifier.VerifyAsync("RazvanManolache/dosbox-enhanced", release,
            workspace.Root, "app.exe", default, client, workspace.FilePath("state"));
        Assert.Equal(AppUpdateStatus.UpToDate, (await Check()).Status);
        File.AppendAllText(workspace.FilePath("runtime/native/kernel.fatbin"), "damage");
        Assert.Equal(AppUpdateStatus.NeedsRepair, (await Check()).Status);
        File.WriteAllBytes(workspace.FilePath("runtime/native/kernel.fatbin"), bytes);
        File.AppendAllText(workspace.FilePath("runtime/native/native_graph.json"), "damage");
        Assert.Equal(AppUpdateStatus.NeedsRepair, (await Check()).Status);
    }

    [Fact]
    public void CurrentDosAndVideoValidationFormatsExposeTheirPublishedFileHashes()
    {
        string digest = new('a', 64);
        using var dos = JsonDocument.Parse(JsonSerializer.Serialize(new { schema = "dosbox-release-validation/1", passed = true,
            runtime_sha256 = new Dictionary<string, string> { ["app/DosboxEnhanced.exe"] = digest } }));
        Assert.Equal(digest, ReleaseFileVerifier.Inventory(dos.RootElement)["app/DosboxEnhanced.exe"]);
        using var video = JsonDocument.Parse(JsonSerializer.Serialize(new { schema = "phoenix-player-ui-refresh/1", passed = true,
            binaries = new Dictionary<string, string> { ["PhoenixVideoPlayer.exe"] = digest },
            retained_processing_files_sha256 = new Dictionary<string, string> { ["runtime.dll"] = digest } }));
        Assert.Equal(2, ReleaseFileVerifier.Inventory(video.RootElement).Count);
    }
}
