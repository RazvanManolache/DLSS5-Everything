using System.Buffers.Binary;
using System.Text;

namespace Dlss5CompatApp.Tests;

public sealed class LegacyRendererBootstrapTests
{
    [Fact]
    public void ReferencedGameExecutableIsResolvedFromSmallLauncher()
    {
        using var workspace = new TestWorkspace();
        var launcher = workspace.FilePath("arbitrary.exe");
        var bytes = TestWorkspace.PeBytes(CpuArch.X86, [])[..(96 * 1024)];
        Encoding.ASCII.GetBytes("Game.exe\0").CopyTo(bytes, 0x5000);
        File.WriteAllBytes(launcher, bytes);
        var game = workspace.WritePe("Game.exe", CpuArch.X86, "d3d9.dll");
        bytes = File.ReadAllBytes(game);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(0x96), 0x22);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(0x98 + 16), 0x1000);
        File.WriteAllBytes(game, bytes);
        Assert.True(LocalPaths.Same(game, GameScanner.ScanSingleExe(launcher)!.ExePath));
    }

    [Theory]
    [InlineData(true, true, false, false, true)]
    [InlineData(false, true, false, false, false)]
    [InlineData(true, false, false, false, false)]
    [InlineData(true, true, true, false, false)]
    [InlineData(true, true, false, true, false)]
    public void ExplicitSmallBootstrapOnlyResolvesItsReferencedCompatibleRenderer(
        bool referenced, bool graphics, bool library, bool wrongArchitecture, bool expected)
    {
        using var workspace = new TestWorkspace();
        var launcher = workspace.FilePath("arbitrary.exe");
        var small = TestWorkspace.PeBytes(CpuArch.X86, [])[..(48 * 1024)];
        if (referenced) Encoding.ASCII.GetBytes("\\ARBITRARY.BIN\0").CopyTo(small, 0x5000);
        File.WriteAllBytes(launcher, small);
        var renderer = workspace.WritePe("arbitrary.bin", wrongArchitecture ? CpuArch.X64 : CpuArch.X86,
            graphics ? ["ddraw.dll"] : []);
        var bytes = File.ReadAllBytes(renderer);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(0x96), library ? (ushort)0x2022 : (ushort)0x22);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(0x98 + 16), 0x1000);
        File.WriteAllBytes(renderer, bytes);
        var result = GameScanner.ScanSingleExe(launcher);
        if (expected)
        {
            Assert.NotNull(result);
            Assert.True(LocalPaths.Same(renderer, result.ExePath));
            Assert.Contains("renderer referenced by arbitrary.exe", result.Detection);
        }
        else Assert.True(result is null || !LocalPaths.Same(renderer, result.ExePath));
    }
}
