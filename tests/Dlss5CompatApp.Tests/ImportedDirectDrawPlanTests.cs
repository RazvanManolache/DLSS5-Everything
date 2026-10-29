namespace Dlss5CompatApp.Tests;

public class ImportedDirectDrawPlanTests
{
    [Theory]
    [InlineData("xdd.dll", true)]
    [InlineData("other-ddraw.DLL", true)]
    [InlineData("DDraw.dll", false)]
    [InlineData("../xdd.dll", false)]
    [InlineData("..\\xdd.dll", false)]
    [InlineData("C:xdd.dll", false)]
    public void OnlyLocalNonstandardDllNamesAreAliases(string name, bool expected) =>
        Assert.Equal(expected, ImportedDirectDrawPlan.IsAlias(name));

    [Fact]
    public void UnknownModsAreNotAuthorizedByExportsAlone()
    {
        Assert.True(ImportedDirectDrawPlan.IsKnownTranslator("DDrawCompat", false));
        Assert.False(ImportedDirectDrawPlan.IsKnownTranslator("Other mod", false));
        Assert.False(ImportedDirectDrawPlan.IsKnownTranslator(null, false));
        Assert.True(ImportedDirectDrawPlan.IsKnownTranslator(null, true));
    }

    [Fact]
    public void OwnedAliasStillRequiresLocalImportMatchingArchitectureAndDrawExports()
    {
        using var workspace = new TestWorkspace();
        var exe = workspace.WritePe("game.exe", CpuArch.X86, "xdd.dll");
        var alias = workspace.WritePe("xdd.dll", CpuArch.X86);
        MarkDll(alias);
        TestWorkspace.SetPhoenixExports(alias, "DirectDrawCreate", "DirectDrawCreateEx", "DirectDrawEnumerateA");
        Assert.Empty(ImportedDirectDrawPlan.Find(exe, CpuArch.X86));
        Assert.Equal(new[] { alias }, ImportedDirectDrawPlan.Find(exe, CpuArch.X86, _ => true));
        Assert.Empty(ImportedDirectDrawPlan.Find(exe, CpuArch.X64, _ => true));
        var unrelated = workspace.WritePe("unused.dll", CpuArch.X86);
        MarkDll(unrelated);
        TestWorkspace.SetPhoenixExports(unrelated, "DirectDrawCreate", "DirectDrawCreateEx", "DirectDrawEnumerateA");
        Assert.Single(ImportedDirectDrawPlan.Find(exe, CpuArch.X86, _ => true));
    }

    static void MarkDll(string path)
    {
        var bytes = File.ReadAllBytes(path);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(0x96), 0x2102);
        File.WriteAllBytes(path, bytes);
    }
}
