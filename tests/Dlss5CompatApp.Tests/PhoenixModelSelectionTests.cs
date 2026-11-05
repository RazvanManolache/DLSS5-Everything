using System.Buffers.Binary;

namespace Dlss5CompatApp.Tests;

public sealed class PhoenixModelSelectionTests
{
    static string Dll(TestWorkspace workspace, string name, params string[] resources)
    {
        var path = workspace.WriteDll(name, CpuArch.X64, []);
        TestWorkspace.SetModelResources(path, resources);
        return Path.GetFullPath(path);
    }

    [Theory]
    [InlineData("original", "WEIGHTS_HT")]
    [InlineData("small-fp8", "WEIGHTS_CC_SMALL_FP8")]
    public void ReadsActualNamedResourceNotEmbeddedText(string model, string resource)
    {
        using var w = new TestWorkspace();
        var dll = Dll(w, "test.dll", resource);
        Assert.True(NvidiaModelResources.Contains(dll, model));
        TestWorkspace.SetModelResources(dll);
        using (var stream = new FileStream(dll, FileMode.Open, FileAccess.Write))
        { stream.Position = 0x10000; stream.Write(System.Text.Encoding.Unicode.GetBytes(resource)); }
        Assert.False(NvidiaModelResources.Contains(dll, model));
    }

    [Fact]
    public void ScannerRetainsCompatibleManagedAlternativeToLegacyLocalDll()
    {
        using var w = new TestWorkspace();
        var old = Dll(w, "payload/nvngx_dlssnr.dll", "WEIGHTS_HT");
        var newer = Dll(w, "payload/Nvidia/nodix-compat/1.3.7-fixture/nvngx_dlssnr.dll", "WEIGHTS_HT", "WEIGHTS_CC_SMALL_FP8");
        var payload = PayloadScanner.Scan(w.FilePath("payload"));
        Assert.Equal(old, Assert.Single(payload.NvidiaDlls));
        Assert.Equal(2, payload.NrModelCandidates.Count);
        Assert.Equal(newer, PhoenixModelSelection.Resolve(payload, new() { ModelId = "small-fp8" }));
        Assert.Equal(old, PhoenixModelSelection.Resolve(payload, new() { ModelId = "original" }));
        Assert.Equal(newer, PhoenixModelSelection.Resolve(payload, new() { ModelId = "small-fp8", Runner = "nvidia" }));
        Assert.Equal(newer, PhoenixModelSelection.Resolve(payload, new() { ModelId = "small-fp8", Comparison = 1, SourceA = 1 }));
    }

    [Fact]
    public void ExplicitIncompatibleSelectionDoesNotSilentlySwitchModelsOrDlls()
    {
        using var w = new TestWorkspace();
        var old = Dll(w, "payload/nvngx_dlssnr.dll", "WEIGHTS_HT");
        Dll(w, "payload/Nvidia/nodix-compat/1.3.7-fixture/nvngx_dlssnr.dll", "WEIGHTS_CC_SMALL_FP8");
        var error = Assert.Throws<InvalidOperationException>(() => PhoenixModelSelection.Resolve(
            PayloadScanner.Scan(w.FilePath("payload")), new() { ModelId = "small-fp8" }, old));
        Assert.Contains("WEIGHTS_CC_SMALL_FP8", error.Message);
    }

    [Fact]
    public void NoCompatibleSourceGivesActionableError()
    {
        using var w = new TestWorkspace();
        Dll(w, "payload/nvngx_dlssnr.dll", "WEIGHTS_HT");
        var error = Assert.Throws<InvalidOperationException>(() => PhoenixModelSelection.Resolve(
            PayloadScanner.Scan(w.FilePath("payload")), new() { ModelId = "small-fp8" }));
        Assert.Contains("selected model has not been changed", error.Message);
    }

    [Fact]
    public void VerifiedExportPreferredForPhoenixButNotNvidia()
    {
        using var w = new TestWorkspace();
        var dll = Dll(w, "payload/nvngx_dlssnr.dll", "WEIGHTS_HT");
        var library = OptionalModelLibraryTests.PublishFixture(w.FilePath("payload"));
        var payload = PayloadScanner.Scan(w.FilePath("payload"));
        var folder = Path.Combine(library, "models", "fixture");
        Assert.Equal(folder, PhoenixModelSelection.Resolve(payload, new()));
        Assert.Equal(dll, PhoenixModelSelection.Resolve(payload, new() { Runner = "nvidia" }));
        // Same-length corruption must not pass selection just because an index exists.
        File.WriteAllBytes(Path.Combine(folder, "weights.bin"), [9, 9, 9, 9]);
        Assert.Equal(dll, PhoenixModelSelection.Resolve(payload, new()));
    }

    [Theory]
    [InlineData(0)] // Missing resource table.
    [InlineData(1)] // Out-of-range child directory.
    [InlineData(2)] // Truncated resource payload.
    [InlineData(3)] // Architecture mismatch.
    [InlineData(4)] // Executable, not a DLL.
    public void MalformedOrWrongArchitectureDllIsRejected(int variation)
    {
        using var w = new TestWorkspace();
        var path = Dll(w, "model.dll", "WEIGHTS_CC_SMALL_FP8");
        var bytes = File.ReadAllBytes(path);
        if (variation == 0) BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0x118), 0);
        if (variation == 1) BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0x2414), 0xfffffff0);
        if (variation == 2) Array.Resize(ref bytes, 0x2440);
        if (variation == 3) BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(0x84), 0x14c);
        if (variation == 4) BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(0x96), 0x22);
        File.WriteAllBytes(path, bytes);
        Assert.False(NvidiaModelResources.Contains(path, "small-fp8"));
    }
}
