namespace Dlss5CompatApp.Tests;

public sealed class MissingDependencyDiagnosticsTests
{
    [Fact]
    public void LoaderFailureReportsUnresolvedRequiredImportsWithoutFlaggingContractsOrLocalFiles()
    {
        using var workspace = new TestWorkspace();
        var exe = workspace.WritePe("game.exe", CpuArch.X86, "missing-runtime-test-only.dll", "local.dll",
            "kernel32.dll", "api-ms-win-test-contract.dll");
        File.WriteAllText(workspace.FilePath("local.dll"), "local dependency");
        var message = StartupDiagnostics.DescribeExit(unchecked((int)0xc0000135), exe);
        Assert.Contains("0xC0000135", message);
        Assert.Contains("missing-runtime-test-only.dll", message);
        Assert.DoesNotContain("local.dll", message);
        Assert.DoesNotContain("kernel32.dll", message);
        Assert.DoesNotContain("api-ms-win-test-contract.dll", message);
        Assert.Contains("Other loader search locations may apply", message);
    }

    [Fact]
    public void OtherFailuresDoNotGuessAtDependencies()
    {
        Assert.Equal("Game exited with code -1.", StartupDiagnostics.DescribeExit(-1, "not-an-existing-game.exe"));
    }

    [Fact]
    public void RequiredImportScanExcludesDelayImports()
    {
        using var workspace = new TestWorkspace();
        var exe = workspace.WritePe("game.exe", CpuArch.X86, "required.dll");
        var bytes = File.ReadAllBytes(exe);
        void Write32(int offset, uint value) => System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset), value);
        // PE fixture: delay descriptors at RVA 0x1800, file offset 0xc00.
        Write32(0x98 + 96 + 13 * 8, 0x1800);
        Write32(0x98 + 96 + 13 * 8 + 4, 64);
        Write32(0xc04, 0x1900);
        System.Text.Encoding.ASCII.GetBytes("optional-delay.dll\0").CopyTo(bytes, 0xd00);
        File.WriteAllBytes(exe, bytes);
        Assert.Contains("optional-delay.dll", PeReader.GetImports(exe));
        Assert.Equal(new[] { "required.dll" }, PeReader.GetImports(exe, includeDelayed: false));
    }
}
