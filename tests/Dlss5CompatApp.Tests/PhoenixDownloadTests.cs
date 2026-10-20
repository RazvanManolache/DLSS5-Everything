using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Dlss5CompatApp.Tests;

public sealed class PhoenixDownloadTests : IDisposable
{
    static readonly byte[] Pe = MakePe();
    readonly TestWorkspace workspace = new();
    string Root => workspace.Root;
    string Target => workspace.FilePath("phoenix.addon64");
    string Provenance => workspace.FilePath(PhoenixReleaseDownloader.SourceManifestFileName);

    public PhoenixDownloadTests() => File.WriteAllText(Target, "previous payload");

    byte[] PortableRuntimeArchive()
    {
        PhoenixRuntimeTests.Portable(workspace, "optional", CpuArch.X64);
        var root = workspace.FilePath("optional");
        return Zip(Directory.EnumerateFiles(Path.Combine(root, "PhoenixRuntime"), "*", SearchOption.AllDirectories)
            .Select(path => (Path.GetRelativePath(root, path).Replace('\\', '/'), File.ReadAllBytes(path))).ToArray());
    }

    [Fact]
    public async Task OptionalRuntimeDownloadCacheAndRepairUseTheSelectedRelease()
    {
        using var handler = new FakeHttp();
        handler.Add("phoenix.addon64", Pe, "sha256:" + Convert.ToHexString(SHA256.HashData(Pe)));
        var runtime = PortableRuntimeArchive();
        const string name = "DLSS5-Phoenix-v1-Portable-Runtimes.zip";
        handler.Add(name, runtime, "sha256:" + Convert.ToHexString(SHA256.HashData(runtime)));
        var first = await Download(handler);
        Assert.NotNull(first.RuntimePath);
        var files = PhoenixRuntimePackage.For(PayloadScanner.Scan(Root), CpuArch.X64);
        Assert.Contains(files, file => file.RelativePath.EndsWith("vulkan/phoenix_vulkan.dll"));
        handler.Requests.Clear();
        await Download(handler);
        Assert.Single(handler.Requests);
        File.Delete(files.First(file => file.RelativePath.EndsWith(".spv")).SourcePath);
        var repaired = await Download(handler);
        Assert.NotEqual(first.RuntimePath, repaired.RuntimePath);
        Assert.True(PhoenixRuntimePackage.VerifyCached(Root, repaired));
        Assert.True(Directory.Exists(Path.Combine(Root, first.RuntimePath!)));
        Assert.Contains(handler.Requests, url => url.EndsWith('/' + name));
    }

    [Fact]
    public async Task RuntimeChecksumFailurePreservesPreviousAddonAndProvenance()
    {
        var imported = workspace.FilePath("local.addon64");
        File.WriteAllBytes(imported, Pe);
        await PhoenixReleaseDownloader.ImportLocalAsync(imported, Root, default);
        var before = File.ReadAllBytes(Provenance);
        using var handler = new FakeHttp();
        handler.Add("phoenix.addon64", Pe);
        handler.Add("DLSS5-Phoenix-v1-Portable-Runtimes.zip", PortableRuntimeArchive(), "sha256:" + new string('0', 64));
        using var http = new HttpClient(handler);
        await Assert.ThrowsAsync<InvalidDataException>(() => PhoenixReleaseDownloader.DownloadLatestAsync(http, Root, default, replaceLocalDevelopment: true));
        Assert.Equal(before, File.ReadAllBytes(Provenance));
        AssertInstalled();
    }

    [Fact]
    public async Task ReleaseArchivePreservesItsVerifiedX86Companion()
    {
        var file = workspace.WritePe("source32/Phoenix.addon32", CpuArch.X86);
        var bytes = File.ReadAllBytes(file);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(0x96), 0x2022);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0xf4), 16);
        File.WriteAllBytes(file, bytes);
        TestWorkspace.SetPhoenixExports(file, "CnrPHOENIXCompatibilityStatus", "AddonInit", "AddonUninit");
        using var handler = new FakeHttp();
        handler.Add("phoenix-windows-x64.zip", Zip(("Phoenix.addon64", Pe), ("Phoenix.addon32", File.ReadAllBytes(file))));
        var receipt = await Download(handler);
        var companion = PhoenixCompanion32.Resolve(Root, receipt);
        Assert.NotNull(companion);
        Assert.Equal(File.ReadAllBytes(file), File.ReadAllBytes(companion!));
        Assert.Equal(companion, PayloadScanner.Scan(Root).PhoenixAddon32);
    }

    [Fact]
    public async Task MissingReleaseReports404WithoutCachedFallback()
    {
        using var handler = new FakeHttp { ReleaseStatus = HttpStatusCode.NotFound };
        using var http = new HttpClient(handler);
        var ex = await Assert.ThrowsAsync<PhoenixReleaseUnavailableException>(() => PhoenixReleaseDownloader.DownloadLatestAsync(http, Root, default));
        Assert.Contains("HTTP 404", ex.Message);
        Assert.Contains("localPhoenixAddonPath", ex.Message);
        Assert.Single(handler.Requests);
        AssertUnchanged();
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task ApiFailureNeverFallsBackToExistingAddon(HttpStatusCode status)
    {
        using var handler = new FakeHttp { ReleaseStatus = status };
        await Assert.ThrowsAsync<HttpRequestException>(() => Download(handler));
        AssertUnchanged();
    }

    [Fact]
    public async Task DirectAddonPreferredOverZipAndDigestVerified()
    {
        using var handler = new FakeHttp();
        handler.Add("unrelated.zip", [1, 2, 3]);
        handler.Add("phoenix.addon64", Pe, "sha256:" + Convert.ToHexString(SHA256.HashData(Pe)));
        var result = await Download(handler);
        Assert.False(result.IsDevelopment);
        Assert.Equal("v1:phoenix.addon64", result.Version);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(Pe)), result.Sha256);
        Assert.Equal(2, handler.Requests.Count(url => !url.StartsWith("https://raw.githubusercontent.com/")));
        AssertInstalled();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SameVerifiedAssetAndNoticesReuseWithoutTransfer(bool archive)
    {
        using var handler = new FakeHttp();
        var name = archive ? "phoenix.zip" : "phoenix.addon64";
        var bytes = archive ? Zip(("phoenix.addon64", Pe)) : Pe;
        handler.Add(name, bytes, "sha256:" + Convert.ToHexString(SHA256.HashData(bytes)));
        var first = await Download(handler);
        var provenance = await File.ReadAllTextAsync(Provenance);
        handler.Requests.Clear();
        var reused = await Download(handler);
        Assert.Single(handler.Requests);
        Assert.Equal(PhoenixReleaseCatalog.ReleasesUrl, handler.Requests[0]);
        Assert.Equal(first.Sha256, reused.Sha256);
        Assert.Equal(first.NoticesPath, reused.NoticesPath);
        Assert.Equal(provenance, await File.ReadAllTextAsync(Provenance));
        AssertInstalled();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CorruptedCachedBytesRedownloadSameAsset(bool invalidPe)
    {
        using var handler = new FakeHttp();
        handler.Add("phoenix.zip", Zip(("phoenix.addon64", Pe)));
        await Download(handler);
        var corrupt = (byte[])Pe.Clone();
        corrupt[invalidPe ? 0 : corrupt.Length - 1] ^= 0xFF;
        await File.WriteAllBytesAsync(Target, corrupt);
        handler.Requests.Clear();
        await Download(handler);
        Assert.Contains(handler.Requests, url => url.EndsWith("/phoenix.zip"));
        AssertInstalled();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingOrChangedNoticesInvalidateCache(bool missing)
    {
        using var handler = new FakeHttp();
        handler.Add("phoenix.zip", Zip(("phoenix.addon64", Pe)));
        var first = await Download(handler);
        var notice = Path.Combine(Root, first.NoticesPath!, "NOTICE");
        if (missing) File.Delete(notice);
        else await File.WriteAllTextAsync(notice, "changed notice");
        handler.Requests.Clear();
        var repaired = await Download(handler);
        Assert.Contains(handler.Requests, url => url.EndsWith("/phoenix.zip"));
        Assert.NotEqual(first.NoticesPath, repaired.NoticesPath);
        Assert.True(await PhoenixReleaseCache.VerifyNoticesAsync(Root, repaired, default));
        AssertInstalled();
    }

    [Theory]
    [InlineData("sha256")]
    [InlineData("sha512")]
    public async Task DirectAssetCacheChecksUpstreamDigestEvenIfLocalManifestIsEdited(string algorithm)
    {
        using var handler = new FakeHttp();
        var digest = algorithm == "sha256" ? SHA256.HashData(Pe) : SHA512.HashData(Pe);
        handler.Add("phoenix.addon64", Pe, algorithm + ":" + Convert.ToHexString(digest));
        var first = await Download(handler);
        var replaced = (byte[])Pe.Clone();
        replaced[^1] ^= 0xFF;
        await File.WriteAllBytesAsync(Target, replaced);
        PhoenixReleaseDownloader.ValidateX64Pe(Target);
        await File.WriteAllTextAsync(Provenance, JsonSerializer.Serialize(first with
        {
            Sha256 = Convert.ToHexString(SHA256.HashData(replaced))
        }));
        handler.Requests.Clear();
        var repaired = await Download(handler);
        Assert.Contains(handler.Requests, url => url.EndsWith("/phoenix.addon64"));
        Assert.Equal(first.Sha256, repaired.Sha256);
        AssertInstalled();
    }

    [Fact]
    public async Task ChangedPublishedDigestForSameTagAndNameRedownloads()
    {
        using var first = new FakeHttp();
        first.Add("phoenix.addon64", Pe, "sha256:" + Convert.ToHexString(SHA256.HashData(Pe)));
        await Download(first);
        var changed = (byte[])Pe.Clone();
        changed[^1] ^= 0xFF;
        using var updated = new FakeHttp();
        updated.Add("phoenix.addon64", changed, "sha256:" + Convert.ToHexString(SHA256.HashData(changed)));
        var result = await Download(updated);
        Assert.Contains(updated.Requests, url => url.EndsWith("/phoenix.addon64"));
        Assert.Equal(Convert.ToHexString(SHA256.HashData(changed)), result.Sha256);
        Assert.Equal(changed, await File.ReadAllBytesAsync(Target));
    }

    [Fact]
    public async Task ExplicitLocalDevelopmentSurvivesStartupReleaseCheck()
    {
        var source = workspace.FilePath("development.addon64");
        await File.WriteAllBytesAsync(source, Pe);
        var local = await PhoenixReleaseDownloader.ImportLocalAsync(source, Root, default);
        var provenance = await File.ReadAllTextAsync(Provenance);
        using var handler = new FakeHttp();
        handler.Add("phoenix.addon64", Pe);
        var selected = await Download(handler);
        Assert.True(selected.IsDevelopment);
        Assert.Equal(local.Source, selected.Source);
        Assert.Equal("local-development", selected.Version);
        Assert.Equal(provenance, await File.ReadAllTextAsync(Provenance));
        Assert.Single(handler.Requests);
        AssertInstalled();
    }

    [Fact]
    public async Task ExplicitUpdateCanReplaceLocalDevelopmentBuild()
    {
        var source = workspace.FilePath("development.addon64");
        await File.WriteAllBytesAsync(source, Pe);
        await PhoenixReleaseDownloader.ImportLocalAsync(source, Root, default);
        using var handler = new FakeHttp();
        handler.Add("phoenix.addon64", Pe);
        using var http = new HttpClient(handler);
        var selected = await PhoenixReleaseDownloader.DownloadLatestAsync(http, Root, default, replaceLocalDevelopment: true);
        Assert.False(selected.IsDevelopment);
        Assert.Equal("v1:phoenix.addon64", selected.Version);
        Assert.Contains(handler.Requests, url => url.EndsWith("/phoenix.addon64"));
        AssertInstalled();
    }

    [Fact]
    public async Task ChangedLocalDevelopmentBuildRequiresExplicitDecision()
    {
        var source = workspace.FilePath("development.addon64");
        await File.WriteAllBytesAsync(source, Pe);
        await PhoenixReleaseDownloader.ImportLocalAsync(source, Root, default);
        var corrupt = (byte[])Pe.Clone();
        corrupt[^1] ^= 0xFF;
        await File.WriteAllBytesAsync(Target, corrupt);
        var provenance = await File.ReadAllTextAsync(Provenance);
        using var handler = new FakeHttp();
        handler.Add("phoenix.addon64", Pe);
        var ex = await Assert.ThrowsAsync<InvalidDataException>(() => Download(handler));
        Assert.Contains("Reimport", ex.Message);
        Assert.Single(handler.Requests);
        Assert.Equal(corrupt, await File.ReadAllBytesAsync(Target));
        Assert.Equal(provenance, await File.ReadAllTextAsync(Provenance));
    }

    [Fact]
    public void ApiAndPublicMetadataShareCacheIdentity()
    {
        var url = PhoenixReleaseCatalog.PublicRoot + "/releases/download/0.8-beta/phoenix.addon64";
        var digest = "sha256:" + Convert.ToHexString(SHA256.HashData(Pe));
        Assert.Equal(PhoenixReleaseCache.AssetsFingerprint([new("phoenix.addon64", url, Pe.Length, digest)]),
            PhoenixReleaseCache.AssetsFingerprint([new("phoenix.addon64", url, -1, digest.ToUpperInvariant())]));
    }

    [Fact]
    public async Task NestedZipSelectedByContentsRatherThanFirstZip()
    {
        using var handler = new FakeHttp();
        handler.Add("a-docs.zip", Zip(("README.md", [1])));
        handler.Add("z-binaries.zip", Zip(("release/bin/phoenix.addon64", Pe)));
        await Download(handler);
        AssertInstalled();
        Assert.False(Directory.Exists(workspace.FilePath("release")));
    }

    [Fact]
    public async Task WindowsX64PreferredAndSourceDebugOtherPlatformsExcluded()
    {
        using var handler = new FakeHttp();
        handler.Add("phoenix-source.zip", [1]);
        handler.Add("phoenix-debug-x64.zip", [1]);
        handler.Add("phoenix-linux-x64.zip", [1]);
        handler.Add("phoenix-win-x64.zip", Zip(("phoenix.addon64", Pe)));
        handler.Add("phoenix.zip", Zip(("phoenix.addon64", Pe)));
        await Download(handler);
        Assert.Equal(2, handler.Requests.Count(url => !url.StartsWith("https://raw.githubusercontent.com/")));
        AssertInstalled();
    }

    [Fact]
    public async Task LatestPublishedIncludesPreviewsByDefault()
    {
        using var handler = new FakeHttp { IsPreview = true };
        handler.Add("phoenix.addon64", Pe);
        await Download(handler);
        AssertInstalled();
    }

    [Fact]
    public async Task StableOnlyRejectsPreviewOnlyListing()
    {
        using var handler = new FakeHttp { IsPreview = true };
        handler.Add("phoenix.addon64", Pe);
        using var http = new HttpClient(handler);
        var ex = await Assert.ThrowsAsync<PhoenixReleaseUnavailableException>(() =>
            PhoenixReleaseDownloader.DownloadLatestAsync(http, Root, default, channel: PhoenixReleaseChannel.StableOnly));
        Assert.Contains("stable", ex.Message);
        AssertUnchanged();
    }

    [Theory]
    [InlineData((int)PhoenixReleaseChannel.LatestPublished, "0.8-beta")]
    [InlineData((int)PhoenixReleaseChannel.StableOnly, "1.0")]
    public async Task ReleaseSelectionUsesPublicationTimeAndExcludesDrafts(int channelValue, string expected)
    {
        using var handler = new FakeHttp
        {
            ReleaseJson = JsonSerializer.Serialize(new[]
            {
                new { tag_name = "99.0", draft = false, prerelease = false, published_at = "2026-01-01T00:00:00Z", assets = Array.Empty<object>() },
                new { tag_name = "1.0", draft = false, prerelease = false, published_at = "2026-10-10T00:00:00Z", assets = Array.Empty<object>() },
                new { tag_name = "0.8-beta", draft = false, prerelease = true, published_at = "2026-10-11T00:00:00Z", assets = Array.Empty<object>() },
                new { tag_name = "100.0", draft = true, prerelease = false, published_at = "2026-10-12T00:00:00Z", assets = Array.Empty<object>() }
            })
        };
        using var http = new HttpClient(handler);
        var release = await PhoenixReleaseCatalog.GetLatestAsync(http, (PhoenixReleaseChannel)channelValue, null, default);
        Assert.Equal(expected, release.Tag);
    }

    [Fact]
    public async Task RateLimitFallbackUsesPublicExactTagAndChecksDigest()
    {
        using var handler = new FakeHttp { ReleaseStatus = HttpStatusCode.Forbidden };
        var assetUrl = PhoenixReleaseCatalog.PublicRoot + "/releases/download/0.8-beta/phoenix.addon64";
        SetupPublicRelease(handler, "0.8-beta", true, assetUrl, Convert.ToHexString(SHA256.HashData(Pe)));
        handler.Route(assetUrl, Pe);
        using var http = new HttpClient(handler);
        var reports = new List<PhoenixDownloadProgress>();
        var result = await PhoenixReleaseDownloader.DownloadLatestAsync(http, Root, default, reports.Add);
        Assert.Contains("0.8-beta", result.Version);
        Assert.Contains(reports, report => report.Message.Contains("HTTP 403") && report.Message.Contains("rate-limited"));
        Assert.Contains(handler.Requests, url => url.EndsWith("/releases/expanded_assets/0.8-beta"));
        AssertInstalled();
    }

    [Fact]
    public async Task RateLimitFallbackDoesNotBypassStableOnly()
    {
        using var handler = new FakeHttp { ReleaseStatus = HttpStatusCode.Forbidden };
        SetupPublicRelease(handler, "0.8-beta", true, PhoenixReleaseCatalog.PublicRoot + "/releases/download/0.8-beta/phoenix.addon64", new string('0', 64));
        using var http = new HttpClient(handler);
        var ex = await Assert.ThrowsAsync<PhoenixReleaseUnavailableException>(() =>
            PhoenixReleaseDownloader.DownloadLatestAsync(http, Root, default, channel: PhoenixReleaseChannel.StableOnly));
        Assert.Contains("stable", ex.Message);
        AssertUnchanged();
    }

    [Fact]
    public async Task FailedPublicFallbackReportsRateLimitRatherThanNoRelease()
    {
        using var handler = new FakeHttp { ReleaseStatus = HttpStatusCode.Forbidden };
        using var http = new HttpClient(handler);
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => PhoenixReleaseDownloader.DownloadLatestAsync(http, Root, default));
        Assert.Equal(HttpStatusCode.Forbidden, ex.StatusCode);
        Assert.Contains("HTTP 403", ex.Message);
        Assert.Contains("rate-limited", ex.Message);
        AssertUnchanged();
    }

    [Theory]
    [InlineData("https://evil.example/RazvanManolache/dlss5-phoenix-addon/releases/download/0.8-beta/phoenix.addon64")]
    [InlineData("https://github.com/another/repo/releases/download/0.8-beta/phoenix.addon64")]
    [InlineData("https://github.com/RazvanManolache/dlss5-phoenix-addon/releases/download/other-tag/phoenix.addon64")]
    [InlineData("https://github.com/RazvanManolache/dlss5-phoenix-addon/releases/download/0.8-beta/%2e%2e%2fescape")]
    public void ExpandedAssetsRejectsForeignOrUnsafeLinks(string href)
    {
        var html = $"<li><a href=\"{href}\">addon</a><span>sha256:{new string('0', 64)}</span></li>";
        Assert.Throws<InvalidDataException>(() => PhoenixReleaseCatalog.ParsePublicAssets(html, "0.8-beta"));
    }

    [Fact]
    public async Task BundledLicensesAndThirdPartyDocumentsPreserved()
    {
        using var handler = new FakeHttp();
        var bytes = Encoding.UTF8.GetBytes("Apache-2.0 and third-party notices");
        handler.Add("phoenix.zip", Zip(("package/phoenix.addon64", Pe), ("package/LICENSE", bytes),
            ("package/NOTICE", bytes), ("package/THIRD_PARTY.md", bytes),
            ("package/licenses/Intel-EULA.pdf", bytes), ("package/third_party/imgui/LICENSE.txt", bytes)));
        var result = await Download(handler);
        Assert.NotNull(result.NoticesPath);
        var notices = Path.Combine(Root, result.NoticesPath);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(Path.Combine(notices, "LICENSE")));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(Path.Combine(notices, "NOTICE")));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(Path.Combine(notices, "THIRD_PARTY.md")));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(Path.Combine(notices, "licenses/Intel-EULA.pdf")));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(Path.Combine(notices, "third_party/imgui/LICENSE.txt")));
        Assert.DoesNotContain(handler.Requests, url => url.StartsWith("https://raw.githubusercontent.com/"));
        AssertInstalled();
    }

    [Fact]
    public async Task MissingNoticesFetchedOnlyFromExactSelectedTag()
    {
        using var handler = new FakeHttp();
        handler.Add("phoenix.addon64", Pe);
        var result = await Download(handler);
        Assert.NotNull(result.NoticesPath);
        Assert.Equal(3, handler.Requests.Count(url => url.StartsWith("https://raw.githubusercontent.com/")));
        Assert.All(handler.Requests.Where(url => url.StartsWith("https://raw.githubusercontent.com/")),
            url => Assert.StartsWith("https://raw.githubusercontent.com/" + PhoenixReleaseDownloader.Repository + "/refs/tags/v1/", url));
    }

    [Theory]
    [InlineData("phoenix-windows-x86_64.zip")]
    [InlineData("phoenix-windows-x86-64.zip")]
    public async Task X86_64ArchiveIsRecognizedAsX64(string name)
    {
        using var handler = new FakeHttp();
        handler.Add(name, Zip(("phoenix.addon64", Pe)));
        await Download(handler);
        AssertInstalled();
    }

    [Fact]
    public async Task EquallySuitableArchivesRejected()
    {
        using var handler = new FakeHttp();
        handler.Add("phoenix-one.zip", Zip(("phoenix.addon64", Pe)));
        handler.Add("phoenix-two.zip", Zip(("phoenix.addon64", Pe)));
        await Assert.ThrowsAsync<InvalidDataException>(() => Download(handler));
        AssertUnchanged();
    }

    [Fact]
    public async Task DuplicateDirectAssetsRejected()
    {
        using var handler = new FakeHttp();
        handler.Add("phoenix.addon64", Pe);
        handler.Add("PHOENIX.ADDON64", Pe);
        await Assert.ThrowsAsync<InvalidDataException>(() => Download(handler));
        AssertUnchanged();
    }

    [Fact]
    public async Task SourceOnlyAndMissingAddonReleaseReportUnavailable()
    {
        using var handler = new FakeHttp();
        handler.Add("phoenix-source.zip", [1]);
        handler.Add("docs.zip", Zip(("README", [1])));
        var ex = await Assert.ThrowsAsync<PhoenixReleaseUnavailableException>(() => Download(handler));
        Assert.Contains("no usable", ex.Message);
        AssertUnchanged();
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("..\\escape")]
    [InlineData("/absolute")]
    [InlineData("C:/escape")]
    [InlineData("bin/file:stream")]
    [InlineData("bin/.. /escape")]
    [InlineData("\\\\server\\share\\escape")]
    public async Task UnsafeArchivePathsRejected(string unsafeName)
    {
        using var handler = new FakeHttp();
        handler.Add("phoenix.zip", Zip(("phoenix.addon64", Pe), (unsafeName, [1])));
        var ex = await Assert.ThrowsAsync<InvalidDataException>(() => Download(handler));
        Assert.Contains("Unsafe", ex.Message);
        AssertUnchanged();
        Assert.Single(Directory.GetFiles(Root), p => !Path.GetFileName(p).StartsWith(".phoenix-"));
    }

    [Fact]
    public async Task SymlinkEntriesRejected()
    {
        using var handler = new FakeHttp();
        handler.Add("phoenix.zip", ZipSymlink());
        await Assert.ThrowsAsync<InvalidDataException>(() => Download(handler));
        AssertUnchanged();
    }

    [Fact]
    public async Task DuplicateNestedAddonsRejected()
    {
        using var handler = new FakeHttp();
        handler.Add("phoenix.zip", Zip(("a/phoenix.addon64", Pe), ("b/PHOENIX.ADDON64", Pe)));
        await Assert.ThrowsAsync<InvalidDataException>(() => Download(handler));
        AssertUnchanged();
    }

    [Fact]
    public async Task AssetSha256SidecarVerified()
    {
        using var handler = new FakeHttp();
        handler.Add("phoenix.addon64", Pe);
        handler.Add("phoenix.addon64.sha256", Encoding.UTF8.GetBytes(Convert.ToHexString(SHA256.HashData(Pe))));
        await Download(handler);
        AssertInstalled();
    }

    [Fact]
    public async Task GnuArchiveSha256SumsVerifiedBeforeExtraction()
    {
        using var handler = new FakeHttp();
        var zip = Zip(("phoenix.addon64", Pe));
        handler.Add("phoenix.zip", zip);
        handler.Add("SHA256SUMS", Encoding.UTF8.GetBytes(Convert.ToHexString(SHA256.HashData(zip)) + " *phoenix.zip\n"));
        await Download(handler);
        AssertInstalled();
    }

    [Fact]
    public async Task BsdSha512ChecksumVerified()
    {
        using var handler = new FakeHttp();
        handler.Add("phoenix.addon64", Pe);
        handler.Add("checksums.txt", Encoding.UTF8.GetBytes("SHA512 (phoenix.addon64) = " + Convert.ToHexString(SHA512.HashData(Pe))));
        await Download(handler);
        AssertInstalled();
    }

    [Theory]
    [InlineData("sha256:0000")]
    [InlineData("sha256:" + "0000000000000000000000000000000000000000000000000000000000000000")]
    [InlineData("md5:00000000000000000000000000000000")]
    public async Task BadDigestPreservesDestination(string digest)
    {
        using var handler = new FakeHttp();
        handler.Add("phoenix.addon64", Pe, digest);
        await Assert.ThrowsAsync<InvalidDataException>(() => Download(handler));
        AssertUnchanged();
    }

    [Theory]
    [InlineData("garbage")]
    [InlineData("0000000000000000000000000000000000000000000000000000000000000000")]
    public async Task PublishedBadChecksumFailsEvenWithValidApiDigest(string checksum)
    {
        using var handler = new FakeHttp();
        handler.Add("phoenix.addon64", Pe, "sha256:" + Convert.ToHexString(SHA256.HashData(Pe)));
        handler.Add("phoenix.addon64.sha256", Encoding.UTF8.GetBytes(checksum));
        await Assert.ThrowsAsync<InvalidDataException>(() => Download(handler));
        AssertUnchanged();
    }

    [Fact]
    public async Task TruncatedDownloadRejected()
    {
        using var handler = new FakeHttp();
        handler.Add("phoenix.addon64", Pe, declaredSize: Pe.Length + 1);
        await Assert.ThrowsAsync<InvalidDataException>(() => Download(handler));
        AssertUnchanged();
    }

    [Fact]
    public async Task NetworkFailurePreservesDestination()
    {
        using var handler = new FakeHttp { FailAssets = true };
        handler.Add("phoenix.addon64", Pe);
        await Assert.ThrowsAsync<HttpRequestException>(() => Download(handler));
        AssertUnchanged();
    }

    [Fact]
    public async Task CancellationDuringTransferPreservesDestinationAndCleansStaging()
    {
        using var cancel = new CancellationTokenSource();
        using var handler = new FakeHttp { CancelTransfer = cancel };
        handler.Add("phoenix.addon64", Pe);
        using var http = new HttpClient(handler);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => PhoenixReleaseDownloader.DownloadLatestAsync(http, Root, cancel.Token));
        AssertUnchanged();
    }

    [Fact]
    public async Task ExplicitLocalDevelopmentImportRecordsProvenanceWithoutHttp()
    {
        var source = workspace.FilePath("selected-development.addon64");
        await File.WriteAllBytesAsync(source, Pe);
        var result = await PhoenixReleaseDownloader.ImportLocalAsync(source, Root, default);
        Assert.True(result.IsDevelopment);
        Assert.Equal("local-development", result.Version);
        Assert.Equal(new Uri(source).AbsoluteUri, result.Source);
        Assert.Equal(result, JsonSerializer.Deserialize<PhoenixReleasePayload>(await File.ReadAllTextAsync(Provenance)));
        AssertInstalled();
    }

    [Fact]
    public async Task Latest404PreservesExplicitImportedAddonAndProvenance()
    {
        var source = workspace.FilePath("selected-development.addon64");
        await File.WriteAllBytesAsync(source, Pe);
        await PhoenixReleaseDownloader.ImportLocalAsync(source, Root, default);
        var provenance = await File.ReadAllTextAsync(Provenance);
        using var handler = new FakeHttp { ReleaseStatus = HttpStatusCode.NotFound };
        await Assert.ThrowsAsync<PhoenixReleaseUnavailableException>(() => Download(handler));
        AssertInstalled();
        Assert.Equal(provenance, await File.ReadAllTextAsync(Provenance));
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task DownloadReportsActualBytesAndTotal()
    {
        using var handler = new FakeHttp();
        handler.Add("phoenix.addon64", Pe);
        using var http = new HttpClient(handler);
        var reports = new List<PhoenixDownloadProgress>();
        await PhoenixReleaseDownloader.DownloadLatestAsync(http, Root, default, reports.Add);
        Assert.Equal(0, reports[0].BytesDownloaded);
        Assert.Equal(Pe.Length, reports[^1].BytesDownloaded);
        Assert.All(reports, report => Assert.Equal(Pe.Length, report.TotalBytes));
        AssertInstalled();
    }

    [Fact]
    public async Task MissingPhoenixStillUpdatesOtherPrerequisitesAndReportsIncomplete()
    {
        var source = workspace.FilePath("selected-development.addon64");
        await File.WriteAllBytesAsync(source, Pe);
        var imported = await PhoenixReleaseDownloader.ImportLocalAsync(source, Root, default);
        var provenance = await File.ReadAllTextAsync(Provenance);
        var reshadeUrl = "https://reshade.me/downloads/ReShade_Setup_9.9.9_Addon.exe";
        await File.WriteAllTextAsync(workspace.FilePath("ReShade_Setup_9.9.9_Addon.exe"), "cached setup");
        await File.WriteAllBytesAsync(workspace.FilePath("ReShade32.dll"), Pe);
        await File.WriteAllBytesAsync(workspace.FilePath("ReShade64.dll"), Pe);
        var manifestPath = workspace.FilePath("dlss5-payload-manifest.json");
        await File.WriteAllTextAsync(manifestPath, JsonSerializer.Serialize(new
        {
            Items = new Dictionary<string, object>
            {
                ["reshade-addon-setup"] = new { Url = reshadeUrl, Version = "9.9.9", Size = 12 },
                ["phoenix-addon64"] = new { Url = imported.Source, Version = imported.Version, imported.Size, imported.Sha256, imported.IsDevelopment }
            }
        }));
        using var handler = new FakeHttp { ReleaseStatus = HttpStatusCode.NotFound };
        handler.Route("https://reshade.me/", Encoding.UTF8.GetBytes("Download ReShade 9.9.9 with full add-on support"));
        var d3d8 = "https://github.com/crosire/d3d8to9/releases/download/v1/d3d8.dll";
        handler.RouteRelease("crosire/d3d8to9", "d3d8.dll", d3d8, Pe);
        var dgZip = Zip(("MS/x86/D3D8.dll", Pe), ("MS/x86/D3D9.dll", Pe), ("MS/x86/DDraw.dll", Pe),
            ("MS/x86/D3DImm.dll", Pe), ("3Dfx/x86/Glide.dll", Pe), ("3Dfx/x86/Glide2x.dll", Pe),
            ("3Dfx/x86/Glide3x.dll", Pe), ("3Dfx/x86/Napalm/Glide3x.dll", Pe), ("dgVoodooCpl.exe", Pe));
        handler.RouteRelease("dege-diosg/dgVoodoo2", "dgVoodoo2_9.zip", "https://github.com/dege-diosg/dgVoodoo2/releases/download/v1/dgVoodoo2_9.zip", dgZip);
        var enhancedZip = Zip(("portable/app/DosboxEnhanced.exe", Pe), ("portable/runtime/dosbox_pure_libretro.dll", Pe));
        handler.RouteRelease("RazvanManolache/dosbox-enhanced", "DOSBox-Enhanced-1-win-x64.zip",
            "https://github.com/RazvanManolache/dosbox-enhanced/releases/download/v1/DOSBox-Enhanced-1-win-x64.zip", enhancedZip);

        using var http = new HttpClient(handler);
        var progress = new ImmediateProgress();
        var ex = await Assert.ThrowsAsync<PhoenixReleaseUnavailableException>(() =>
            PayloadBootstrapper.EnsureCurrentWithClientAsync(http, Root, progress, default));
        Assert.Contains("HTTP 404", ex.Message);
        Assert.Equal(Pe, await File.ReadAllBytesAsync(workspace.FilePath("dosbox-enhanced/app/DosboxEnhanced.exe")));
        Assert.Equal(Pe, await File.ReadAllBytesAsync(workspace.FilePath("dosbox-enhanced/runtime/dosbox_pure_libretro.dll")));
        Assert.DoesNotContain(handler.Requests, url => url.Contains("dosbox-staging", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(Pe, await File.ReadAllBytesAsync(workspace.FilePath("d3d8to9/d3d8.dll")));
        Assert.Equal(Pe, await File.ReadAllBytesAsync(workspace.FilePath("MS/x86/D3D9.dll")));
        Assert.False(File.Exists(workspace.FilePath("dlss5-bridge.addon64")));
        Assert.Contains("payload incomplete", progress.Items[^1].Message);
        using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(manifestPath));
        var phoenix = manifest.RootElement.GetProperty("Items").GetProperty("phoenix-addon64");
        Assert.True(phoenix.GetProperty("IsDevelopment").GetBoolean());
        Assert.Equal(imported.Source, phoenix.GetProperty("Url").GetString());
        Assert.Equal(provenance, await File.ReadAllTextAsync(Provenance));
        AssertInstalled();
        Assert.DoesNotContain(handler.Requests, url => url.Contains("Swapper", StringComparison.OrdinalIgnoreCase)
            || url.Contains("RenoDX", StringComparison.OrdinalIgnoreCase)
            || url.Contains("FF7R-DLSS5", StringComparison.OrdinalIgnoreCase)
            || url.Contains("NIGos", StringComparison.OrdinalIgnoreCase)
            || url.Contains("dlss5-bridge", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("pkg/../../outside.txt")]
    [InlineData("pkg/../outside.txt")]
    [InlineData("pkg\\..\\..\\outside.txt")]
    [InlineData("/absolute.txt")]
    [InlineData("\\absolute.txt")]
    [InlineData("//server/share/outside.txt")]
    [InlineData("C:/outside.txt")]
    [InlineData("C:outside.txt")]
    [InlineData("pkg/dosbox.exe:stream")]
    [InlineData("pkg/./outside.txt")]
    [InlineData("pkg//outside.txt")]
    [InlineData("pkg/folder./outside.txt")]
    [InlineData("pkg/folder /outside.txt")]
    [InlineData("pkg/CON.txt")]
    [InlineData("pkg/NUL")]
    [InlineData("pkg/COM1.dll")]
    [InlineData("pkg/bad?.txt")]
    [InlineData("pkg/bad\0.txt")]
    public void DosBoxRejectsUnsafeArchiveBeforeCreatingAnyFiles(string unsafeName)
    {
        var archive = workspace.FilePath("dosbox.zip");
        File.WriteAllBytes(archive, Zip(("pkg/dosbox.exe", Pe), (unsafeName, new byte[] { 1 })));
        var target = workspace.FilePath("payload/dosbox");
        var before = Directory.GetFileSystemEntries(Root, "*", SearchOption.AllDirectories).Order().ToArray();
        Assert.Throws<InvalidDataException>(() => PayloadBootstrapper.ExtractZipFlattenSingleRoot(archive, target));
        Assert.Equal(before, Directory.GetFileSystemEntries(Root, "*", SearchOption.AllDirectories).Order().ToArray());
        Assert.False(Directory.Exists(workspace.FilePath("payload")));
    }

    [Theory]
    [InlineData("pkg/dosbox.exe", "pkg/DOSBOX.EXE")]
    [InlineData("pkg/folder", "pkg/folder/file.txt")]
    [InlineData("pkg/folder/file.txt", "pkg/folder")]
    [InlineData("pkg/folder/", "pkg/FOLDER/")]
    [InlineData("pkg/", "PKG/")]
    public void DosBoxRejectsDuplicatePathsAndFileDirectoryConflictsBeforeMutation(string first, string second)
    {
        var archive = workspace.FilePath("dosbox.zip");
        File.WriteAllBytes(archive, Zip(("pkg/valid.txt", new byte[] { 1 }), (first, Array.Empty<byte>()), (second, Array.Empty<byte>())));
        var target = workspace.FilePath("dosbox");
        Assert.Throws<InvalidDataException>(() => PayloadBootstrapper.ExtractZipFlattenSingleRoot(archive, target));
        Assert.False(Directory.Exists(target));
    }

    [Theory]
    [InlineData(unchecked((int)0xA1FF0000))]
    [InlineData((int)FileAttributes.ReparsePoint)]
    [InlineData(unchecked((int)0x21FF0000))]
    public void DosBoxRejectsLinkAndSpecialEntriesBeforeMutation(int attributes)
    {
        var archive = workspace.FilePath("dosbox.zip");
        File.WriteAllBytes(archive, Zip(("pkg/valid.txt", new byte[] { 1 }), ("pkg/link", new byte[] { 1 })));
        using (var zip = ZipFile.Open(archive, ZipArchiveMode.Update)) zip.Entries[1].ExternalAttributes = attributes;
        var target = workspace.FilePath("dosbox");
        Assert.Throws<InvalidDataException>(() => PayloadBootstrapper.ExtractZipFlattenSingleRoot(archive, target));
        Assert.False(Directory.Exists(target));
    }

    [Fact]
    public void DosBoxUnsafeArchivePreservesExistingFiles()
    {
        var archive = workspace.FilePath("dosbox.zip");
        File.WriteAllBytes(archive, Zip(("pkg/dosbox.exe", Pe), ("pkg/../outside.txt", new byte[] { 1 })));
        var target = workspace.FilePath("dosbox");
        Directory.CreateDirectory(target);
        var installed = Path.Combine(target, "dosbox.exe");
        File.WriteAllText(installed, "user payload");
        Assert.Throws<InvalidDataException>(() => PayloadBootstrapper.ExtractZipFlattenSingleRoot(archive, target));
        Assert.Equal("user payload", File.ReadAllText(installed));
        Assert.Equal(new[] { installed }, Directory.GetFileSystemEntries(target));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DosBoxRejectsExistingReparseDestinationBeforeMutation(bool rootIsLink)
    {
        var archive = workspace.FilePath("dosbox.zip");
        File.WriteAllBytes(archive, Zip(("pkg/first.txt", new byte[] { 1 }), ("pkg/linked/file.txt", new byte[] { 1 })));
        var target = workspace.FilePath("dosbox");
        var outside = workspace.FilePath("outside");
        Directory.CreateDirectory(outside);
        var link = rootIsLink ? target : Path.Combine(target, "linked");
        if (!rootIsLink) Directory.CreateDirectory(target);
        SwapperTestLinks.CreateJunction(link, outside);
        try
        {
            Assert.Throws<InvalidDataException>(() => PayloadBootstrapper.ExtractZipFlattenSingleRoot(archive, target));
            Assert.Empty(Directory.GetFileSystemEntries(outside));
            Assert.False(File.Exists(Path.Combine(target, "first.txt")));
        }
        finally { Directory.Delete(link); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DosBoxRejectsFileAndTotalSizeLimitsBeforeMutation(bool totalLimit)
    {
        var archive = workspace.FilePath("dosbox.zip");
        var bytes = Zip(("pkg/valid.txt", new byte[] { 1 }), ("pkg/one", Array.Empty<byte>()),
            ("pkg/two", Array.Empty<byte>()), ("pkg/three", Array.Empty<byte>()));
        // Override declared sizes in central-directory records, without allocating huge fixtures.
        using (var stream = new MemoryStream(bytes))
        using (var reader = new BinaryReader(stream))
        using (var writer = new BinaryWriter(stream))
        {
            var offset = bytes.AsSpan().IndexOf(BitConverter.GetBytes(0x02014b50));
            for (var i = 0; i < 4; i++)
            {
                stream.Position = offset + 28;
                var nameLength = reader.ReadUInt16();
                var extraLength = reader.ReadUInt16();
                var commentLength = reader.ReadUInt16();
                if (i > 0)
                {
                    stream.Position = offset + 24;
                    writer.Write(totalLimit ? 400U * 1024 * 1024 : 513U * 1024 * 1024);
                }
                offset += 46 + nameLength + extraLength + commentLength;
            }
        }
        File.WriteAllBytes(archive, bytes);
        var target = workspace.FilePath("dosbox");
        Assert.Throws<InvalidDataException>(() => PayloadBootstrapper.ExtractZipFlattenSingleRoot(archive, target));
        Assert.False(Directory.Exists(target));
    }

    [Fact]
    public void DosBoxRejectsEntryCountLimitBeforeMutation()
    {
        var archive = workspace.FilePath("dosbox.zip");
        File.WriteAllBytes(archive, Zip(Enumerable.Range(0, 10001).Select(i => ($"pkg/{i}.txt", Array.Empty<byte>())).ToArray()));
        var target = workspace.FilePath("dosbox");
        Assert.Throws<InvalidDataException>(() => PayloadBootstrapper.ExtractZipFlattenSingleRoot(archive, target));
        Assert.False(Directory.Exists(target));
    }

    [Fact]
    public void DosBoxValidArchiveFlattensOneRootAndPreservesUnrelatedFiles()
    {
        var archive = workspace.FilePath("dosbox.zip");
        File.WriteAllBytes(archive, Zip(("pkg/", Array.Empty<byte>()), ("pkg/dosbox.exe", Pe),
            ("pkg/support/", Array.Empty<byte>()), ("pkg/support/runtime.dll", Pe)));
        var target = workspace.FilePath("dosbox");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "user.conf"), "keep");
        PayloadBootstrapper.ExtractZipFlattenSingleRoot(archive, target);
        Assert.Equal(Pe, File.ReadAllBytes(Path.Combine(target, "dosbox.exe")));
        Assert.Equal(Pe, File.ReadAllBytes(Path.Combine(target, "support/runtime.dll")));
        Assert.Equal("keep", File.ReadAllText(Path.Combine(target, "user.conf")));
        Assert.Empty(Directory.GetFiles(target, ".dosbox-*", SearchOption.AllDirectories));
    }

    [Fact]
    public void DosBoxPreCancelledExtractionDoesNotCreateDestination()
    {
        var archive = workspace.FilePath("dosbox.zip");
        File.WriteAllBytes(archive, Zip(("pkg/dosbox.exe", Pe)));
        var target = workspace.FilePath("dosbox");
        Assert.ThrowsAny<OperationCanceledException>(() => PayloadBootstrapper.ExtractZipFlattenSingleRoot(archive, target, new CancellationToken(true)));
        Assert.False(Directory.Exists(target));
    }

    [Fact]
    public async Task EmbeddedAddonOver100MiBIsCopiedWholeIncludingTrailingResources()
    {
        var source = workspace.FilePath("embedded-build.addon64");
        var tail = Encoding.UTF8.GetBytes("embedded Phoenix DLSS5 runtime resource tail");
        await using (var file = File.Create(source))
        {
            await file.WriteAsync(Pe);
            file.SetLength(128L * 1024 * 1024);
            file.Position = file.Length;
            await file.WriteAsync(tail);
        }
        var result = await PhoenixReleaseDownloader.ImportLocalAsync(source, Root, default);
        Assert.Equal(new FileInfo(source).Length, result.Size);
        Assert.Equal(result.Size, new FileInfo(Target).Length);
        using var original = File.OpenRead(source);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(original)), result.Sha256);
        using var installed = File.OpenRead(Target);
        Assert.Equal(result.Sha256, Convert.ToHexString(SHA256.HashData(installed)));
        installed.Position = installed.Length - tail.Length;
        var copiedTail = new byte[tail.Length];
        installed.ReadExactly(copiedTail);
        Assert.Equal(tail, copiedTail);
    }

    [Fact]
    public async Task LocalSourceCanBeExistingDestination()
    {
        await File.WriteAllBytesAsync(Target, Pe);
        await PhoenixReleaseDownloader.ImportLocalAsync(Target, Root, default);
        AssertInstalled();
    }

    [Fact]
    public async Task LockedDestinationIsNeverDeletedBeforeReplacement()
    {
        var source = workspace.FilePath("selected.addon64");
        await File.WriteAllBytesAsync(source, Pe);
        using (var locked = new FileStream(Target, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var ex = await Record.ExceptionAsync(() => PhoenixReleaseDownloader.ImportLocalAsync(source, Root, default));
            Assert.True(ex is IOException or UnauthorizedAccessException);
        }
        AssertUnchanged();
        Assert.False(File.Exists(Provenance));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public async Task BlankExplicitSelectionRejected(string path)
    {
        await Assert.ThrowsAsync<ArgumentException>(() => PhoenixReleaseDownloader.ImportLocalAsync(path, Root, default));
        AssertUnchanged();
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public async Task MetadataLockOrReadOnlyRollsBackImportAndUpdateWithExactCacheAndProvenance(bool development, bool update, bool readOnly)
    {
        var oldBytes = (byte[])Pe.Clone();
        oldBytes[^1] = 42;
        PhoenixReleasePayload previous;
        if (development)
        {
            var oldSource = workspace.FilePath("old-development.addon64");
            await File.WriteAllBytesAsync(oldSource, oldBytes);
            previous = await PhoenixReleaseDownloader.ImportLocalAsync(oldSource, Root, default);
        }
        else
        {
            using var oldRelease = new FakeHttp();
            oldRelease.Add("phoenix.addon64", oldBytes, "sha256:" + Convert.ToHexString(SHA256.HashData(oldBytes)));
            previous = await Download(oldRelease);
        }
        var newSource = workspace.FilePath("new-development.addon64");
        await File.WriteAllBytesAsync(newSource, Pe);
        var before = CaptureFiles(Root);
        var directoriesBefore = CaptureDirectories(Root);
        var attributes = File.GetAttributes(Provenance);
        var lastWrite = File.GetLastWriteTimeUtc(Target);
        using var metadataLock = readOnly ? null : new FileStream(Provenance, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            if (readOnly) File.SetAttributes(Provenance, attributes | FileAttributes.ReadOnly);
            Exception? failure;
            if (update)
            {
                using var release = new FakeHttp();
                release.Add("phoenix.addon64", Pe, "sha256:" + Convert.ToHexString(SHA256.HashData(Pe)));
                using var http = new HttpClient(release);
                failure = await Record.ExceptionAsync(() => PhoenixReleaseDownloader.DownloadLatestAsync(http, Root, default,
                    replaceLocalDevelopment: true));
            }
            else failure = await Record.ExceptionAsync(() => PhoenixReleaseDownloader.ImportLocalAsync(newSource, Root, default));
            Assert.True(failure is IOException or UnauthorizedAccessException, failure?.ToString() ?? "Expected metadata publication failure.");
            Assert.Equal(before.OrderBy(p => p.Key), CaptureFiles(Root).OrderBy(p => p.Key));
            Assert.Equal(directoriesBefore, CaptureDirectories(Root));
            Assert.Equal(lastWrite, File.GetLastWriteTimeUtc(Target));
            Assert.Equal(readOnly ? attributes | FileAttributes.ReadOnly : attributes, File.GetAttributes(Provenance));
            var cached = await PhoenixReleaseCache.ReadAsync(Root, default);
            Assert.NotNull(cached);
            Assert.Equal(previous.Source, cached.Source);
            Assert.Equal(previous.Version, cached.Version);
            Assert.Equal(previous.IsDevelopment, cached.IsDevelopment);
            Assert.Equal(previous.Sha256, cached.Sha256);
            Assert.Equal(previous.NoticesPath, cached.NoticesPath);
            Assert.True(await PhoenixReleaseCache.VerifyAddonAsync(Root, cached, default));
            if (!development) Assert.True(await PhoenixReleaseCache.VerifyNoticesAsync(Root, cached, default));
        }
        finally { if (readOnly) File.SetAttributes(Provenance, attributes); }
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, false)]
    public void FailureAfterBinaryPublicationRestoresExactPreviousFilesAndNotices(bool existingBinary, bool existingMetadata)
    {
        var cache = workspace.FilePath("cache");
        Directory.CreateDirectory(cache);
        var binary = Path.Combine(cache, "phoenix.addon64");
        var metadata = Path.Combine(cache, "phoenix-release.json");
        if (existingBinary) File.WriteAllText(binary, "previous binary");
        if (existingMetadata) File.WriteAllText(metadata, "{\"IsDevelopment\":true,\"Source\":\"original-local-selection\"}");
        var before = CaptureFiles(cache);
        var directoriesBefore = CaptureDirectories(cache);
        var stage = workspace.FilePath("staged-binary");
        var provenance = workspace.FilePath("staged-provenance");
        var notices = workspace.FilePath("staged-notices");
        Directory.CreateDirectory(notices);
        File.WriteAllText(Path.Combine(notices, "LICENSE"), "new license");
        File.WriteAllText(stage, "new binary");
        File.WriteAllText(provenance, "{\"IsDevelopment\":false}");
        var destination = Path.Combine(cache, "phoenix-notices", "new-release");
        var reachedBinary = false;
        Assert.Throws<IOException>(() => PhoenixReleaseDownloader.PublishStagedPayload(stage, provenance, binary, metadata,
            notices, destination, default, () =>
            {
                reachedBinary = true;
                Assert.Equal("new binary", File.ReadAllText(binary));
                Assert.True(Directory.Exists(destination));
                throw new IOException("Failure after binary publication, before provenance.");
            }));
        Assert.True(reachedBinary);
        Assert.Equal(before.OrderBy(p => p.Key), CaptureFiles(cache).OrderBy(p => p.Key));
        Assert.Equal(directoriesBefore, CaptureDirectories(cache));
        Assert.Equal("new license", File.ReadAllText(Path.Combine(notices, "LICENSE")));
    }

    [Fact]
    public async Task ModelMetadataFailureRestoresPreviousModelAndItsProvenance()
    {
        var selected = workspace.FilePath("selected");
        Directory.CreateDirectory(selected);
        var source = Path.Combine(selected, "nvngx_dlssnr.dll");
        await File.WriteAllBytesAsync(source, Pe);
        await PhoenixReleaseDownloader.ImportLocalModelAsync(source, Root, default);
        var next = (byte[])Pe.Clone();
        next[^1] = 72;
        await File.WriteAllBytesAsync(source, next);
        var before = CaptureFiles(Root);
        using (var metadataLock = new FileStream(workspace.FilePath("nvngx_dlssnr.dll.source.json"), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var failure = await Record.ExceptionAsync(() => PhoenixReleaseDownloader.ImportLocalModelAsync(source, Root, default));
            Assert.True(failure is IOException or UnauthorizedAccessException);
        }
        Assert.Equal(before.OrderBy(p => p.Key), CaptureFiles(Root).OrderBy(p => p.Key));
        AssertUnchanged();
    }

    static Dictionary<string, string> CaptureFiles(string root) => Directory.GetFiles(root, "*", SearchOption.AllDirectories)
        .ToDictionary(p => Path.GetRelativePath(root, p), p => Convert.ToBase64String(File.ReadAllBytes(p)));

    static string[] CaptureDirectories(string root) => Directory.GetDirectories(root, "*", SearchOption.AllDirectories)
        .Select(p => Path.GetRelativePath(root, p)).Order().ToArray();

    [Theory]
    [InlineData("x86")]
    [InlineData("text")]
    [InlineData("truncated")]
    [InlineData("not-dll")]
    [InlineData("bad-offset")]
    [InlineData("section-truncated")]
    public async Task InvalidLocalPeRejected(string kind)
    {
        var bytes = (byte[])Pe.Clone();
        if (kind == "x86") bytes[0x85] = 1;
        if (kind == "text") bytes[0] = 0;
        if (kind == "truncated") bytes = bytes[..100];
        if (kind == "not-dll") bytes[0x97] = 0;
        if (kind == "bad-offset") BitConverter.GetBytes(int.MaxValue).CopyTo(bytes, 0x3C);
        if (kind == "section-truncated") BitConverter.GetBytes(uint.MaxValue).CopyTo(bytes, 0x188 + 16);
        var source = workspace.FilePath("invalid.addon64");
        await File.WriteAllBytesAsync(source, bytes);
        await Assert.ThrowsAsync<InvalidDataException>(() => PhoenixReleaseDownloader.ImportLocalAsync(source, Root, default));
        AssertUnchanged();
    }

    [Fact]
    public async Task PreCancelledImportPreservesDestination()
    {
        var source = workspace.FilePath("selected.addon64");
        await File.WriteAllBytesAsync(source, Pe);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => PhoenixReleaseDownloader.ImportLocalAsync(source, Root, new CancellationToken(true)));
        AssertUnchanged();
    }

    [Theory]
    [InlineData("nvngx_dlssnr.dll")]
    [InlineData("nvngx_dlss.dll")]
    [InlineData("nvngx_dlssg.dll")]
    public async Task ExplicitModelDllImportIsSeparateFromAddon(string name)
    {
        var sourceDir = workspace.FilePath("selected");
        Directory.CreateDirectory(sourceDir);
        var source = Path.Combine(sourceDir, name);
        await File.WriteAllBytesAsync(source, Pe);
        var result = await PhoenixReleaseDownloader.ImportLocalModelAsync(source, Root, default);
        Assert.Equal("local-model", result.Version);
        Assert.Equal(new Uri(source).AbsoluteUri, result.Source);
        Assert.Equal(Pe, await File.ReadAllBytesAsync(workspace.FilePath(name)));
        AssertUnchanged();
    }

    [Fact]
    public async Task InvalidModelImportPreservesExistingDll()
    {
        var target = workspace.FilePath("nvngx_dlssnr.dll");
        await File.WriteAllTextAsync(target, "previous model");
        var sourceDir = workspace.FilePath("selected");
        Directory.CreateDirectory(sourceDir);
        var source = Path.Combine(sourceDir, "nvngx_dlssnr.dll");
        await File.WriteAllTextAsync(source, "not a DLL");
        await Assert.ThrowsAsync<InvalidDataException>(() => PhoenixReleaseDownloader.ImportLocalModelAsync(source, Root, default));
        Assert.Equal("previous model", await File.ReadAllTextAsync(target));
        AssertUnchanged();
    }

    async Task<PhoenixReleasePayload> Download(FakeHttp handler)
    {
        using var http = new HttpClient(handler, disposeHandler: false);
        return await PhoenixReleaseDownloader.DownloadLatestAsync(http, Root, default);
    }

    void AssertUnchanged() => Assert.Equal("previous payload", File.ReadAllText(Target));
    void AssertInstalled() => Assert.Equal(Pe, File.ReadAllBytes(Target));

    public void Dispose()
    {
        try { Assert.Empty(Directory.GetFileSystemEntries(Root, ".phoenix-*")); }
        finally { workspace.Dispose(); }
    }

    static byte[] MakePe()
    {
        using var fixture = new TestWorkspace();
        var bytes = TestWorkspace.PeBytes(CpuArch.X64, []);
        BitConverter.GetBytes((ushort)0x2002).CopyTo(bytes, 0x96);
        var path = fixture.FilePath("phoenix.addon64");
        File.WriteAllBytes(path, bytes);
        TestWorkspace.SetPhoenixFeederExports(path);
        TestWorkspace.SetEmbeddedPhoenixRuntime(path);
        return File.ReadAllBytes(path);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task IncompatibleReleaseNeverReplacesExistingPayload(bool archive, bool missingRuntime)
    {
        var bad = workspace.FilePath("legacy.addon64");
        await File.WriteAllBytesAsync(bad, Pe);
        if (missingRuntime)
        {
            var bytes = await File.ReadAllBytesAsync(bad);
            Array.Clear(bytes, 0x118, 8);
            await File.WriteAllBytesAsync(bad, bytes);
        }
        else TestWorkspace.SetPhoenixExports(bad, "CnrPHOENIXSummary", "CnrPHOENIXRegister");
        using var handler = new FakeHttp();
        var incompatible = await File.ReadAllBytesAsync(bad);
        handler.Add(archive ? "phoenix.zip" : "phoenix.addon64", archive ? Zip(("phoenix.addon64", incompatible)) : incompatible);
        var error = await Assert.ThrowsAsync<PhoenixReleaseUnavailableException>(() => Download(handler));
        Assert.Contains(missingRuntime ? "embedded C++ runtime" : "CnrPHOENIXFeederEvaluate", error.Message);
        AssertUnchanged(); Assert.False(File.Exists(Provenance));
    }

    [Fact]
    public async Task IncompatibleLocalImportPreservesCompatiblePayloadAndProvenance()
    {
        var good = workspace.FilePath("good.addon64");
        await File.WriteAllBytesAsync(good, Pe);
        await PhoenixReleaseDownloader.ImportLocalAsync(good, Root, default);
        var provenance = await File.ReadAllTextAsync(Provenance);
        var bad = workspace.FilePath("old.addon64");
        await File.WriteAllBytesAsync(bad, Pe);
        TestWorkspace.SetPhoenixExports(bad, "CnrPHOENIXSummary", "CnrPHOENIXRegister");
        await Assert.ThrowsAsync<PhoenixReleaseUnavailableException>(() => PhoenixReleaseDownloader.ImportLocalAsync(bad, Root, default));
        AssertInstalled(); Assert.Equal(provenance, await File.ReadAllTextAsync(Provenance));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("extent")]
    [InlineData("rva")]
    public async Task ManifestWithoutCompleteRuntimeResourcesCannotReplaceWorkingAddon(string fault)
    {
        var good = workspace.FilePath("good.addon64");
        await File.WriteAllBytesAsync(good, Pe);
        await PhoenixReleaseDownloader.ImportLocalAsync(good, Root, default);
        var provenance = await File.ReadAllTextAsync(Provenance);
        var bytes = (byte[])Pe.Clone();
        if (fault == "missing") BitConverter.GetBytes((uint)9999).CopyTo(bytes, 0x1438);
        if (fault == "extent") BitConverter.GetBytes((uint)7).CopyTo(bytes, 0x14d4);
        if (fault == "rva") BitConverter.GetBytes(uint.MaxValue).CopyTo(bytes, 0x14d0);
        using var handler = new FakeHttp(); handler.Add("phoenix.addon64", bytes);
        using var http = new HttpClient(handler);
        await Assert.ThrowsAsync<PhoenixReleaseUnavailableException>(() => PhoenixReleaseDownloader.DownloadLatestAsync(http, Root, default, replaceLocalDevelopment: true));
        AssertInstalled(); Assert.Equal(provenance, await File.ReadAllTextAsync(Provenance));
    }

    [Fact]
    public async Task ExplicitUpdateToIncompatibleReleasePreservesLocalBuild()
    {
        var good = workspace.FilePath("good.addon64");
        await File.WriteAllBytesAsync(good, Pe);
        await PhoenixReleaseDownloader.ImportLocalAsync(good, Root, default);
        var provenance = await File.ReadAllTextAsync(Provenance);
        var bad = workspace.FilePath("old.addon64");
        await File.WriteAllBytesAsync(bad, Pe);
        TestWorkspace.SetPhoenixExports(bad, "CnrPHOENIXSummary", "CnrPHOENIXRegister");
        using var handler = new FakeHttp(); handler.Add("phoenix.addon64", await File.ReadAllBytesAsync(bad));
        using var http = new HttpClient(handler);
        await Assert.ThrowsAsync<PhoenixReleaseUnavailableException>(() => PhoenixReleaseDownloader.DownloadLatestAsync(http, Root, default, replaceLocalDevelopment: true));
        AssertInstalled(); Assert.Equal(provenance, await File.ReadAllTextAsync(Provenance));
    }

    [Fact]
    public async Task CacheWithValidHashButMissingFeederApiIsNotReusable()
    {
        var legacy = workspace.FilePath("legacy.addon64");
        await File.WriteAllBytesAsync(legacy, Pe);
        TestWorkspace.SetPhoenixExports(legacy, "CnrPHOENIXSummary", "CnrPHOENIXRegister");
        var bytes = await File.ReadAllBytesAsync(legacy);
        await File.WriteAllBytesAsync(Target, bytes);
        var cached = new PhoenixReleasePayload("https://example.invalid", "old", bytes.Length,
            Convert.ToHexString(SHA256.HashData(bytes)), false);
        Assert.False(await PhoenixReleaseCache.VerifyAddonAsync(Root, cached, default));
        Assert.Contains("Incompatible: direct feeder API missing", PayloadScanner.Scan(Root).Summary);
    }

    static byte[] Zip(params (string Name, byte[] Bytes)[] files)
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, true))
            foreach (var file in files)
            {
                using var output = zip.CreateEntry(file.Name).Open();
                output.Write(file.Bytes);
            }
        return stream.ToArray();
    }

    static byte[] ZipSymlink()
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, true))
        {
            var entry = zip.CreateEntry("phoenix.addon64");
            entry.ExternalAttributes = unchecked((int)0xA1FF0000);
            using var output = entry.Open();
            output.Write(Pe);
        }
        return stream.ToArray();
    }

    static void SetupPublicRelease(FakeHttp handler, string tag, bool preview, string assetUrl, string digest)
    {
        var root = PhoenixReleaseCatalog.PublicRoot;
        handler.Route(root + "/releases.atom", Encoding.UTF8.GetBytes($"<feed xmlns=\"http://www.w3.org/2005/Atom\"><entry><link rel=\"alternate\" href=\"{root}/releases/tag/{tag}\"/></entry></feed>"));
        handler.Route(root + "/releases/tag/" + tag, Encoding.UTF8.GetBytes(
            (preview ? "<span class=\"Label Label--warning\">Pre-release</span>" : "") + "released this <relative-time datetime=\"2026-10-11T19:07:33Z\"></relative-time>"));
        handler.Route(root + "/releases/expanded_assets/" + tag, Encoding.UTF8.GetBytes($"<ul><li><a href=\"{assetUrl}\">phoenix.addon64</a><span>sha256:{digest}</span></li></ul>"));
    }

    sealed class FakeHttp : HttpMessageHandler
    {
        readonly List<object> assets = new();
        readonly Dictionary<string, byte[]> bodies = new();
        readonly Dictionary<string, byte[]> routes = new();
        public List<string> Requests { get; } = new();
        public HttpStatusCode ReleaseStatus { get; init; } = HttpStatusCode.OK;
        public bool FailAssets { get; init; }
        public CancellationTokenSource? CancelTransfer { get; init; }
        public string? ReleaseJson { get; init; }
        public bool IsPreview { get; init; }

        public void Route(string url, byte[] bytes) => routes[url] = bytes;

        public void RouteRelease(string repository, string name, string url, byte[] bytes)
        {
            Route("https://api.github.com/repos/" + repository + "/releases/latest", JsonSerializer.SerializeToUtf8Bytes(new
            {
                tag_name = "v1", assets = new[] { new { name, browser_download_url = url, size = bytes.Length, digest = "sha256:" + Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant() } }
            }));
            Route(url, bytes);
        }

        public void Add(string name, byte[] bytes, string? digest = null, long? declaredSize = null)
        {
            var url = "https://github.com/" + PhoenixReleaseDownloader.Repository + "/releases/download/v1/" + name;
            assets.Add(new { name, browser_download_url = url, size = declaredSize ?? bytes.Length, digest });
            bodies[url] = bytes;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var url = request.RequestUri!.AbsoluteUri;
            Requests.Add(url);
            if (routes.TryGetValue(url, out var route))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(route) });
            if (url == PhoenixReleaseDownloader.LatestReleaseUrl || url == PhoenixReleaseCatalog.ReleasesUrl)
            {
                Assert.NotEmpty(request.Headers.UserAgent);
                return Task.FromResult(new HttpResponseMessage(ReleaseStatus)
                {
                    Content = new StringContent(ReleaseJson ?? (url == PhoenixReleaseDownloader.LatestReleaseUrl
                        ? JsonSerializer.Serialize(new { tag_name = "v1", draft = false, prerelease = IsPreview, published_at = "2026-10-11T19:00:00Z", assets })
                        : JsonSerializer.Serialize(new[] { new { tag_name = "v1", draft = false, prerelease = IsPreview, published_at = "2026-10-11T19:00:00Z", assets } })))
                });
            }
            if (url.StartsWith("https://raw.githubusercontent.com/"))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("License notice from exact selected tag") });
            if (FailAssets) throw new HttpRequestException("Simulated network failure.");
            if (!bodies.ContainsKey(url)) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            HttpContent content = CancelTransfer is null
                ? new ByteArrayContent(bodies[url])
                : new StreamContent(new CancelStream(bodies[url], CancelTransfer));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }

    sealed class ImmediateProgress : IProgress<BootstrapProgress>
    {
        public List<BootstrapProgress> Items { get; } = new();
        public void Report(BootstrapProgress value) => Items.Add(value);
    }

    sealed class CancelStream(byte[] bytes, CancellationTokenSource cancel) : MemoryStream(bytes)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancel.Cancel();
            cancellationToken.ThrowIfCancellationRequested();
            return base.ReadAsync(buffer, cancellationToken);
        }
    }
}
