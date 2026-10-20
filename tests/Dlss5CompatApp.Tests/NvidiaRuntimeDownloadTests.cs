using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Dlss5CompatApp.Tests;

public sealed class NvidiaRuntimeDownloadTests : IDisposable
{
    readonly TestWorkspace workspace = new();
    readonly ProgressSink progress = new();
    // Small ZIP fixtures exercise the common verifier; the probe uses the real pinned RAR.
    static readonly PayloadBootstrapper.NvidiaSource Fixture = new("fixture", "fixture/runtime", "nvidia.zip");
    static readonly byte[] Dll = MakeDll(CpuArch.X64);
    string Root => workspace.Root;
    public void Dispose() => workspace.Dispose();

    [Fact]
    public async Task DownloadsOnlyRuntimeAndNoticesAndReusesAuthenticatedCache()
    {
        var archive = Zip(("nvidia/nvngx_dlssnr.dll", Dll), ("nvidia/sl.common.dll", Dll),
            ("nvidia/nvngx_dlss.license.txt", Encoding.UTF8.GetBytes("license")),
            ("nvidia/nis.license.txt", Encoding.UTF8.GetBytes("NIS license")),
            ("nvidia/reflex.license.txt", Encoding.UTF8.GetBytes("Reflex license")),
            ("renodx.addon64", Dll), ("launcher.exe", Dll));
        using var handler = new PackageHttp(archive); using var http = new HttpClient(handler);
        var first = await Download(http);
        Assert.Equal("dlss5-nvidia-package/1", first.Schema);
        Assert.Equal(5, first.Files.Length);
        Assert.Contains(first.Files, file => file.Name == "nvngx_dlssnr.dll" && file.Sha256 == Digest(Dll));
        Assert.DoesNotContain(Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories), path => path.EndsWith("launcher.exe") || path.EndsWith("renodx.addon64"));
        var second = await Download(http);
        Assert.Equal(first.ArchiveSha256, second.ArchiveSha256);
        Assert.Equal(1, handler.AssetRequests);
        Assert.Empty(Directory.EnumerateDirectories(Path.Combine(Root, ".download-cache")));
        Assert.True(PayloadScanner.Scan(Root).HasCoreDlss);
    }

    [Fact]
    public async Task ExplicitImportWinsAndStagingCannotBecomeSelected()
    {
        var imported = workspace.WritePe("nvngx_dlssnr.dll", CpuArch.X64);
        await File.WriteAllBytesAsync(imported, Dll);
        using var handler = new PackageHttp(Zip(("nvngx_dlssnr.dll", Dll))); using var http = new HttpClient(handler);
        await Download(http);
        workspace.WritePe(".download-cache/nvidia-unpublished/nvngx_dlssnr.dll", CpuArch.X64);
        var payload = PayloadScanner.Scan(Root);
        Assert.Equal(imported, Assert.Single(payload.NvidiaDlls));
        File.Delete(imported);
        Assert.Contains(Path.DirectorySeparatorChar + "Nvidia" + Path.DirectorySeparatorChar,
            Assert.Single(PayloadScanner.Scan(Root).NvidiaDlls));
    }

    [Theory]
    [InlineData("hash")]
    [InlineData("size")]
    [InlineData("missing-model")]
    [InlineData("x86")]
    [InlineData("duplicate")]
    [InlineData("traversal")]
    public async Task InvalidUpdateNeverPublishesOrOverwritesWorkingPackage(string fault)
    {
        using var handler = new PackageHttp(Zip(("nvngx_dlssnr.dll", Dll))); using var http = new HttpClient(handler);
        await Download(http);
        var installed = Assert.Single(PayloadScanner.Scan(Root).NvidiaDlls);
        var good = await File.ReadAllBytesAsync(installed);
        handler.Tag = "v2";
        handler.Bytes = fault switch {
            "missing-model" => Zip(("nvngx_dlss.dll", Dll)),
            "x86" => Zip(("nvngx_dlssnr.dll", MakeDll(CpuArch.X86))),
            "duplicate" => Zip(("a/nvngx_dlssnr.dll", Dll), ("b/NVNGX_DLSSNR.DLL", Dll)),
            "traversal" => Zip(("../nvngx_dlssnr.dll", Dll)),
            _ => Zip(("nvngx_dlssnr.dll", Dll))
        };
        handler.BadHash = fault == "hash"; handler.BadSize = fault == "size";
        await Assert.ThrowsAsync<InvalidDataException>(() => Download(http));
        Assert.Equal(good, await File.ReadAllBytesAsync(installed));
        Assert.Single(Directory.EnumerateDirectories(Path.Combine(Root, "Nvidia", "fixture")));
        Assert.Empty(Directory.EnumerateDirectories(Path.Combine(Root, ".download-cache")));
    }

    [Fact]
    public async Task CompanionSelectionUsesOnePublishedBundleRatherThanIndividualFileTimestamps()
    {
        using var handler = new PackageHttp(Zip(("nvngx_dlssnr.dll", Dll), ("sl.common.dll", Dll))); using var http = new HttpClient(handler);
        await Download(http);
        var first = Directory.GetDirectories(Path.Combine(Root, "Nvidia", "fixture")).Single();
        handler.Tag = "v2";
        await Download(http);
        var second = Directory.GetDirectories(Path.Combine(Root, "Nvidia", "fixture")).Single(folder => folder != first);
        Directory.SetCreationTimeUtc(first, DateTime.UtcNow.AddDays(-1));
        Directory.SetCreationTimeUtc(second, DateTime.UtcNow);
        File.SetLastWriteTimeUtc(Path.Combine(first, "sl.common.dll"), DateTime.UtcNow.AddDays(1));
        var payload = PayloadScanner.Scan(Root);
        Assert.All(payload.NvidiaDlls.Concat(payload.StreamlineDlls), file => Assert.Equal(second, Path.GetDirectoryName(file)));
    }

    [Fact]
    public async Task ChangedCachePublishesRepairAlongsideOriginal()
    {
        using var handler = new PackageHttp(Zip(("nvngx_dlssnr.dll", Dll))); using var http = new HttpClient(handler);
        await Download(http);
        var installed = Assert.Single(PayloadScanner.Scan(Root).NvidiaDlls);
        var corrupted = (byte[])Dll.Clone(); corrupted[^1] ^= 1;
        await File.WriteAllBytesAsync(installed, corrupted);
        await Download(http);
        Assert.Equal(2, handler.AssetRequests);
        Assert.Equal(2, Directory.EnumerateDirectories(Path.Combine(Root, "Nvidia", "fixture")).Count());
        Assert.Equal(corrupted, await File.ReadAllBytesAsync(installed));
        Assert.Equal(Dll, await File.ReadAllBytesAsync(Assert.Single(PayloadScanner.Scan(Root).NvidiaDlls)));
    }

    [Fact]
    public async Task MissingCompatibilitySourcePreservesNvidiaImportAndReportsUnavailable()
    {
        var imported = workspace.WritePe("nvngx_dlssnr.dll", CpuArch.X64);
        await File.WriteAllBytesAsync(imported, Dll);
        using var handler = new PackageHttp(Zip(("nvngx_dlssnr.dll", Dll))); using var http = new HttpClient(handler);
        await PayloadBootstrapper.EnsureNvidiaPackagesAsync(http, Root, progress, default);
        Assert.True(PayloadScanner.Scan(Root).HasCoreDlss);
        Assert.Equal(imported, Assert.Single(PayloadScanner.Scan(Root).NvidiaDlls));
        Assert.Contains(progress.Messages, message => message.Contains("NODIX-TECH/DLSS-5-MANAGER unavailable (HTTP 404)"));
        using var status = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(Root, "nvidia-downloads.json")));
        var sources = status.RootElement.GetProperty("sources").EnumerateArray().ToArray();
        Assert.Equal("unavailable", Assert.Single(sources).GetProperty("status").GetString());
        Assert.DoesNotContain(PayloadBootstrapper.NvidiaSources, source => source.Id is "ff7r" or "swapper");
    }

    [Fact]
    public async Task CompatibilityExtractionRetainsRuntimeAndNeverLaunchesManager()
    {
        var source = Fixture with { RarMember = "mod files/dlss 5/nvngx_dlssnr.dll" };
        using var handler = new PackageHttp(Encoding.UTF8.GetBytes("archive fixture"));
        using var http = new HttpClient(handler);
        var calls = 0;
        var receipt = await PayloadBootstrapper.EnsureNvidiaSourceAsync(http, Root, source, progress, default, async (archive, destination, token) => {
            calls++; Assert.Equal(handler.Bytes, await File.ReadAllBytesAsync(archive, token));
            await File.WriteAllBytesAsync(Path.Combine(destination, "nvngx_dlssnr.dll"), Dll, token);
            await File.WriteAllBytesAsync(Path.Combine(destination, "renodx.addon64"), Dll, token);
            await File.WriteAllBytesAsync(Path.Combine(destination, "manager.exe"), Dll, token);
        });
        Assert.Equal(1, calls);
        Assert.Equal("nvngx_dlssnr.dll", Assert.Single(receipt.Files).Name);
        Assert.Empty(Directory.EnumerateFiles(Root, "*.exe", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task CancellationDuringExtractionPreservesExistingImportAndRemovesStaging()
    {
        var imported = workspace.WritePe("nvngx_dlssnr.dll", CpuArch.X64);
        await File.WriteAllBytesAsync(imported, Dll);
        var source = Fixture with { RarMember = "mod files/dlss 5/nvngx_dlssnr.dll" };
        using var handler = new PackageHttp([1, 2, 3]);
        using var http = new HttpClient(handler); using var cancellation = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => PayloadBootstrapper.EnsureNvidiaSourceAsync(http, Root, source, progress, cancellation.Token,
            (archive, destination, token) => { cancellation.Cancel(); token.ThrowIfCancellationRequested(); return Task.CompletedTask; }));
        Assert.Equal(Dll, await File.ReadAllBytesAsync(imported));
        Assert.Empty(Directory.EnumerateDirectories(Path.Combine(Root, ".download-cache")));
        Assert.Empty(Directory.EnumerateDirectories(Path.Combine(Root, "Nvidia", "fixture")));
    }

    [Fact]
    public async Task ChangedPinnedReleaseOrArchiveIsRejectedBeforeDownload()
    {
        var source = Assert.Single(PayloadBootstrapper.NvidiaSources);
        using var handler = new PackageHttp([1, 2, 3]) { Repository = source.Repository, Tag = source.Release, Asset = source.Asset };
        using var http = new HttpClient(handler);
        await Assert.ThrowsAsync<InvalidDataException>(() => PayloadBootstrapper.EnsureNvidiaSourceAsync(http, Root, source, progress, default));
        Assert.Equal(0, handler.AssetRequests);
        handler.Tag = "different-release";
        await Assert.ThrowsAsync<InvalidDataException>(() => PayloadBootstrapper.EnsureNvidiaSourceAsync(http, Root, source, progress, default));
        Assert.Equal(0, handler.AssetRequests);
    }

    [Fact]
    public async Task PinnedDllIdentityIsEnforcedForExtractionAndSelfConsistentCacheTampering()
    {
        var source = Fixture with { RequiredNr = new("nvngx_dlssnr.dll", Digest(Dll), Dll.Length, "unknown") };
        using var handler = new PackageHttp(Zip(("nvngx_dlssnr.dll", Dll))); using var http = new HttpClient(handler);
        await PayloadBootstrapper.EnsureNvidiaSourceAsync(http, Root, source, progress, default);
        var installed = Assert.Single(PayloadScanner.Scan(Root).NvidiaDlls);
        var mutated = (byte[])Dll.Clone(); mutated[^1] ^= 1;
        await File.WriteAllBytesAsync(installed, mutated);
        var manifest = Path.Combine(Path.GetDirectoryName(installed)!, "package.json");
        var oldReceipt = JsonSerializer.Deserialize<PayloadBootstrapper.NvidiaReceipt>(await File.ReadAllTextAsync(manifest))!;
        var newReceipt = oldReceipt with { Files = [oldReceipt.Files.Single() with { Sha256 = Digest(mutated) }] };
        await File.WriteAllTextAsync(manifest, JsonSerializer.Serialize(newReceipt));
        await PayloadBootstrapper.EnsureNvidiaSourceAsync(http, Root, source, progress, default);
        Assert.Equal(2, handler.AssetRequests);
        Assert.Equal(Dll, await File.ReadAllBytesAsync(Assert.Single(PayloadScanner.Scan(Root).NvidiaDlls)));
        handler.Tag = "v2"; handler.Bytes = Zip(("nvngx_dlssnr.dll", mutated));
        await Assert.ThrowsAsync<InvalidDataException>(() => PayloadBootstrapper.EnsureNvidiaSourceAsync(http, Root, source, progress, default));
    }

    Task<PayloadBootstrapper.NvidiaReceipt> Download(HttpClient http) => PayloadBootstrapper.EnsureNvidiaSourceAsync(http, Root, Fixture, progress, default);
    static byte[] MakeDll(CpuArch arch)
    {
        var bytes = TestWorkspace.PeBytes(arch, []);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(0x96), 0x2022);
        return bytes;
    }
    static string Digest(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    static byte[] Zip(params (string Name, byte[] Bytes)[] files)
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, true))
            foreach (var file in files) { using var entry = zip.CreateEntry(file.Name).Open(); entry.Write(file.Bytes); }
        return stream.ToArray();
    }
    sealed class ProgressSink : IProgress<BootstrapProgress>
    {
        public List<string> Messages { get; } = [];
        public void Report(BootstrapProgress value) => Messages.Add(value.Message);
    }
    sealed class PackageHttp(byte[] bytes) : HttpMessageHandler
    {
        public byte[] Bytes { get; set; } = bytes;
        public string Tag { get; set; } = "v1";
        public string Repository { get; set; } = Fixture.Repository;
        public string Asset { get; set; } = "nvidia.zip";
        public bool BadHash { get; set; }
        public bool BadSize { get; set; }
        public int AssetRequests { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var url = request.RequestUri!.AbsoluteUri;
            if (url.StartsWith("https://api.github.com/repos/" + Repository + "/releases/"))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new {
                    tag_name = Tag, assets = new[] { new { name = Asset, size = Bytes.Length + (BadSize ? 1 : 0),
                    browser_download_url = "https://github.com/" + Repository + "/releases/download/" + Tag + "/" + Asset,
                    digest = "sha256:" + (BadHash ? new string('0', 64) : Digest(Bytes)) } }
                })) });
            if (url.StartsWith("https://github.com/" + Repository + "/releases/download/"))
            { AssetRequests++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Bytes) }); }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
