using System.Security.Cryptography;
using System.Text.Json;

namespace Dlss5CompatApp.Tests;

public sealed class DosGraphicsSetupTests
{
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
    public void PowerShellReceivesModelAndRecipeAsSeparateLiteralArguments()
    {
        var info = DosGraphicsSetup.StartInfo("C:\\Fixture with spaces", "C:\\Model's folder\\model.dll");
        Assert.False(info.UseShellExecute);
        Assert.Contains("C:\\Model's folder\\model.dll", info.ArgumentList);
        Assert.Contains("C:\\Fixture with spaces\\setup-graphics.ps1", info.ArgumentList);
        Assert.DoesNotContain("-Command", info.ArgumentList);
    }
}
