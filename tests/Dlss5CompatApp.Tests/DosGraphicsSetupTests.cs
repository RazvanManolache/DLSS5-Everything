using System.Security.Cryptography;
using System.Text.Json;
using System.IO.Compression;
using System.Net;
using System.Text;

namespace Dlss5CompatApp.Tests;

public sealed class DosGraphicsSetupTests
{
    [Fact]
    public void PreparationDownloadsRequireAnExplicitFlag()
    {
        string[] args = ["--prepare-dos", "game.exe", "--payload", "payload", "--output", "session.json"];
        Assert.False(DosGameLauncher.ParsePreparation(args));
        Assert.True(DosGameLauncher.ParsePreparation([..args, "--download-graphics"]));
        Assert.Throws<ArgumentException>(() => DosGameLauncher.ParsePreparation([..args, "--unknown"]));
        Assert.Throws<ArgumentException>(() => DosGameLauncher.ParsePreparation([..args, "--download-graphics", "--download-graphics"]));
    }

    static readonly string[] Notices = ["license.txt", "3rd-party-licenses.md", "nvngx_dlss.license.txt", "reflex.license.txt"];

    static PayloadInfo Payload(TestWorkspace workspace)
    {
        var executable = workspace.WritePe("dos/app/DosboxEnhanced.exe", CpuArch.X64);
        var model = workspace.WritePe("model/nvngx_dlssnr.dll", CpuArch.X64);
        var modelBytes = File.ReadAllBytes(model);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(modelBytes.AsSpan(0x96), 0x2022);
        File.WriteAllBytes(model, modelBytes);
        foreach (var name in new[] { "dosbox_nvngx.dll", "phoenix_native.dll" })
            workspace.WritePe("dos/runtime/" + name, CpuArch.X64);
        File.WriteAllText(workspace.FilePath("dos/setup-graphics.ps1"), "# Test recipe; never executed.");
        return PayloadScanner.Scan(workspace.Root) with { DosBoxEnhancedExe = executable, NvidiaDlls = [model] };
    }

    static void Lock(TestWorkspace workspace, string name = "sl.interposer.dll") =>
        File.WriteAllText(workspace.FilePath("dos/runtime.lock.json"), JsonSerializer.Serialize(new
        {
            nvidia = new { files = new[] { new { name, sha256 = Convert.ToHexString(SHA256.HashData("verified"u8)) } } }
        }));

    static void AssertClean(TestWorkspace workspace) =>
        Assert.Empty(Directory.GetDirectories(workspace.Root, ".dos-graphics-*"));

    [Fact]
    public async Task VerifiedSetupIsCachedAndRetainsRequiredNotices()
    {
        using var workspace = new TestWorkspace();
        var payload = Payload(workspace); Lock(workspace);
        var calls = 0;
        Task Execute(string work, string model, CancellationToken token)
        {
            calls++;
            Assert.Equal(payload.NvidiaDlls[0], model);
            var vendor = Path.Combine(work, "runtime", "nvidia"); Directory.CreateDirectory(vendor);
            File.WriteAllBytes(Path.Combine(vendor, "sl.interposer.dll"), "verified"u8.ToArray());
            foreach (var name in Notices) File.WriteAllText(Path.Combine(vendor, name), "Notice");
            return Task.CompletedTask;
        }
        await DosGraphicsSetup.EnsureAsync(payload, null, default, Execute);
        await DosGraphicsSetup.EnsureAsync(payload, null, default, Execute);
        Assert.Equal(1, calls);
        Assert.Empty(DosGraphicsSetup.Missing(workspace.FilePath("dos")));
        foreach (var name in Notices) Assert.True(File.Exists(workspace.FilePath("dos/runtime/nvidia/" + name)));
        AssertClean(workspace);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedOrCancelledSetupLeavesNoTemporaryDownloads(bool cancel)
    {
        using var workspace = new TestWorkspace();
        var payload = Payload(workspace); Lock(workspace);
        await Assert.ThrowsAnyAsync<Exception>(() => DosGraphicsSetup.EnsureAsync(payload, null, default,
            (work, model, token) => throw (cancel ? new OperationCanceledException() : new IOException("Fixture failure"))));
        AssertClean(workspace);
        Assert.False(Directory.Exists(workspace.FilePath("dos/runtime/nvidia")));
    }

    [Fact]
    public async Task IncorrectOutputHashCannotBecomeAnInstalledDependency()
    {
        using var workspace = new TestWorkspace();
        var payload = Payload(workspace); Lock(workspace);
        await Assert.ThrowsAsync<InvalidDataException>(() => DosGraphicsSetup.EnsureAsync(payload, null, default,
            (work, model, token) =>
            {
                var vendor = Path.Combine(work, "runtime", "nvidia"); Directory.CreateDirectory(vendor);
                File.WriteAllText(Path.Combine(vendor, "sl.interposer.dll"), "incorrect");
                return Task.CompletedTask;
            }));
        AssertClean(workspace);
        Assert.False(Directory.Exists(workspace.FilePath("dos/runtime/nvidia")));
    }

    [Theory]
    [InlineData("../escape.dll")]
    [InlineData("stream:alternate.dll")]
    public void UnsafeDependencyNameIsRejected(string name)
    {
        using var workspace = new TestWorkspace(); Payload(workspace); Lock(workspace, name);
        Assert.Throws<InvalidDataException>(() => DosGraphicsSetup.Missing(workspace.FilePath("dos")).ToArray());
    }

    [Fact]
    public void InvalidVendorCandidateDoesNotCrashComponentDiscovery()
    {
        using var workspace = new TestWorkspace();
        workspace.WritePe("nvngx_dlssnr.dll", CpuArch.X86);
        Assert.Empty(PayloadScanner.Scan(workspace.Root).NvidiaDlls);
    }

    [Fact]
    public void UnsignedModelFailsNativeTrustVerification()
    {
        using var workspace = new TestWorkspace();
        var model = workspace.WritePe("model with spaces/model.dll", CpuArch.X64);
        Assert.Throws<InvalidDataException>(() => DosGraphicsDownload.VerifySignedFile(model));
    }

    [Theory]
    [InlineData("ok")]
    [InlineData("archive-hash")]
    [InlineData("dll-hash")]
    [InlineData("missing-notice")]
    [InlineData("bad-signature")]
    [InlineData("untrusted-url")]
    public async Task NativeDownloadVerifiesBeforePublishingAndCleansStaging(string scenario)
    {
        using var workspace = new TestWorkspace();
        var payload = Payload(workspace);
        using var memory = new MemoryStream();
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, true))
        {
            foreach (var name in new[] { "bin/x64/sl.interposer.dll", "license.txt", "3rd-party-licenses.md",
                "bin/x64/nvngx_dlss.license.txt", "bin/x64/reflex.license.txt" })
            {
                if (scenario == "missing-notice" && name == "license.txt") continue;
                using var entry = zip.CreateEntry(name).Open();
                entry.Write(Encoding.UTF8.GetBytes(name.EndsWith(".dll") ? "verified" : "Notice"));
            }
        }
        var bytes = memory.ToArray();
        File.WriteAllText(workspace.FilePath("dos/runtime.lock.json"), JsonSerializer.Serialize(new
        {
            nvidia = new { url = scenario == "untrusted-url" ? "https://example.invalid/sdk.zip" :
                "https://github.com/NVIDIA-RTX/Streamline/releases/download/fixture/sdk.zip",
                archiveSha256 = scenario == "archive-hash" ? new string('0', 64) : Convert.ToHexString(SHA256.HashData(bytes)),
                files = new[] { new { name = "sl.interposer.dll", sha256 = scenario == "dll-hash" ? new string('0', 64) :
                    Convert.ToHexString(SHA256.HashData("verified"u8)) } } }
        }));
        var handler = new ArchiveResponse(bytes);
        using var http = new HttpClient(handler);
        var signatures = new List<string>();
        Task Execute(string work, string model, CancellationToken token) =>
            DosGraphicsDownload.ProvisionAsync(work, model, token, http, path =>
            {
                signatures.Add(Path.GetFileName(path));
                if (scenario == "bad-signature" && path.EndsWith("sl.interposer.dll"))
                    throw new InvalidDataException("Fixture signature rejection");
            });
        if (scenario == "ok")
        {
            await DosGraphicsSetup.EnsureAsync(payload, null, default, Execute);
            Assert.Equal("verified", File.ReadAllText(workspace.FilePath("dos/runtime/nvidia/sl.interposer.dll")));
            Assert.Equal(["nvngx_dlssnr.dll", "sl.interposer.dll"], signatures);
            await DosGraphicsSetup.EnsureAsync(payload, null, default, Execute);
            Assert.Equal(1, handler.Requests);
        }
        else
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => DosGraphicsSetup.EnsureAsync(payload, null, default, Execute));
            Assert.False(Directory.Exists(workspace.FilePath("dos/runtime/nvidia")));
            if (scenario == "untrusted-url") Assert.Equal(0, handler.Requests);
        }
        AssertClean(workspace);
    }

    sealed class ArchiveResponse(byte[] bytes) : HttpMessageHandler
    {
        internal int Requests;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Requests++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
        }
    }
}
