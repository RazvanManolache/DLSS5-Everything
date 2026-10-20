using System.IO.Compression;

namespace Dlss5CompatApp.Tests;

public sealed class DirectDraw64PayloadTests
{
    [Fact]
    public void SelectsOnlyNormalX64AbiAndRejectsOtherArchitecture()
    {
        using var workspace = new TestWorkspace();
        var correct = workspace.WritePe("correct.dll", CpuArch.X64);
        MarkDll(correct);
        var wrong = workspace.WritePe("wrong.dll", CpuArch.X86);
        MarkDll(wrong);
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, true))
        {
            foreach (var name in new[] { "DDraw.dll", "D3DImm.dll" })
            {
                using (var stream = archive.CreateEntry("Release/x64/" + name).Open())
                    stream.Write(File.ReadAllBytes(correct));
                using (var stream = archive.CreateEntry("Spec Release/x64/" + name).Open())
                    stream.Write(File.ReadAllBytes(wrong));
                using (var stream = archive.CreateEntry("Release/arm64/" + name).Open())
                    stream.Write(File.ReadAllBytes(wrong));
            }
        }
        buffer.Position = 0;
        using var zip = new ZipArchive(buffer, ZipArchiveMode.Read);
        PayloadBootstrapper.ExtractDgVoodoo64Files(zip, workspace.FilePath("payload"));
        Assert.Equal(File.ReadAllBytes(correct), File.ReadAllBytes(workspace.FilePath("payload/MS/x64/DDraw.dll")));
        Assert.Equal(CpuArch.X64, PeReader.GetArchitecture(workspace.FilePath("payload/MS/x64/D3DImm.dll")));
    }

    [Fact]
    public void PayloadScannerNeverTreatsX86DllAsX64Translator()
    {
        using var workspace = new TestWorkspace();
        foreach (var name in new[] { "DDraw.dll", "D3DImm.dll" })
        {
            var path = workspace.WritePe("payload/MS/x64/" + name, CpuArch.X86);
            MarkDll(path);
        }
        var payload = PayloadScanner.Scan(workspace.FilePath("payload"));
        Assert.Null(payload.DgVoodooDDraw64);
        Assert.Null(payload.DgVoodooD3DImm64);
        Assert.Contains("Release/x64/DDraw.dll", payload.MissingFor(InstallRoute.X64DirectXLegacyViaDgVoodoo));
    }

    static void MarkDll(string path)
    {
        var bytes = File.ReadAllBytes(path);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(0x96), 0x2022);
        File.WriteAllBytes(path, bytes);
    }
}
