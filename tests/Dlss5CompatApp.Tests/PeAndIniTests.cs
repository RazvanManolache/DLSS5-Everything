namespace Dlss5CompatApp.Tests;

public class PeAndIniTests
{
    [Fact]
    public void ReadsSpacedWrapperConfigurationWithoutTreatingCommentsAsSettings()
    {
        using var workspace = new TestWorkspace();
        var path = workspace.FilePath("dgVoodoo.conf");
        File.WriteAllText(path, "; OutputAPI = d3d12\n[General]\n;OutputAPI=d3d12\n OutputAPI = d3d11_fl11_0 \n");
        Assert.Equal("d3d11_fl11_0", IniEditor.GetValue(path, "General", "OutputAPI"));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void ReadsArchitectureAndImports(int architecture)
    {
        var arch = (CpuArch)architecture;
        using var workspace = new TestWorkspace();
        var file = workspace.WritePe("game.exe", arch, "d3d11.dll", "DXGI.dll");
        Assert.Equal(arch, PeReader.GetArchitecture(file));
        Assert.Equal(["d3d11.dll", "dxgi.dll"], PeReader.GetImports(file));
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(63)] [InlineData(64)] [InlineData(200)]
    public void TruncatedExecutableCannotBecomeCompatible(int length)
    {
        using var workspace = new TestWorkspace();
        var path = workspace.FilePath("broken.exe");
        File.WriteAllBytes(path, new byte[length]);
        Assert.Equal(CpuArch.Unknown, PeReader.GetArchitecture(path));
        Assert.Empty(PeReader.GetImports(path));
    }

    [Fact]
    public void FindsMarkerAcrossReadBoundary()
    {
        using var workspace = new TestWorkspace();
        var bytes = new byte[4 * 1024 * 1024 + 1024];
        "D3D12CreateDevice"u8.CopyTo(bytes.AsSpan(4 * 1024 * 1024 - 5));
        var file = workspace.FilePath("large.exe");
        File.WriteAllBytes(file, bytes);
        Assert.Contains("D3D12CreateDevice", PeReader.FindMarkers(file, ["D3D12CreateDevice", "Vulkan"]));
    }

    [Fact]
    public void IniUpdatesDoNotClobberOtherSectionsAndAreIdempotent()
    {
        using var workspace = new TestWorkspace();
        var file = workspace.FilePath("ReShade.ini");
        File.WriteAllText(file, "; Keep comment\n[GENERAL]\nCustom=keep\n[OTHER]\nCustom=other\n");
        IniEditor.SetValue(file, "GENERAL", "TutorialProgress", "4");
        IniEditor.SetValue(file, "GENERAL", "TutorialProgress", "4");
        var result = File.ReadAllText(file);
        Assert.Contains("Custom=keep", result);
        Assert.Contains("Custom=other", result);
        Assert.Contains("; Keep comment", result);
        Assert.Single(File.ReadAllLines(file), x => x == "TutorialProgress=4");
    }

    [Fact]
    public void IniCsvDefinitionsReplaceOnlyMatchingDefinition()
    {
        using var workspace = new TestWorkspace();
        var file = workspace.FilePath("preset.ini");
        IniEditor.SetValue(file, "GENERAL", "Definitions", "DEPTH=0,KEEP=3");
        IniEditor.SetCsvDefinition(file, "GENERAL", "Definitions", "DEPTH=1");
        IniEditor.SetCsvDefinition(file, "GENERAL", "Definitions", "DEPTH=1");
        Assert.Contains("Definitions=KEEP=3,DEPTH=1", File.ReadAllText(file));
    }
}
