using System.Reflection;

namespace Dlss5CompatApp.Tests;

public sealed class DgVoodooConfigurationTests
{
    [Theory]
    [InlineData("")]
    [InlineData("false")]
    [InlineData("2560x1080")]
    public void NativeDesktopDefinitionIsAnEmptyResolutionString(string previous)
    {
        using var workspace = new TestWorkspace();
        var config = workspace.FilePath("dgVoodoo.conf");
        File.WriteAllText(config, $"Version = 0x287\n[GeneralExt]\nDesktopResolution = {previous}\n" +
            "[DirectX]\ndgVoodooWatermark = true\nVRAM = 1024\nResolution = 1920x1080\n" +
            "[Glide]\n3DfxWatermark = false\ndgVoodooWatermark = true\n");
        typeof(InstallerEngine).GetMethod("ConfigureDgVoodoo", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [config]);
        Assert.Equal("", IniEditor.GetValue(config, "GeneralExt", "DesktopResolution"));
        Assert.Equal("false", IniEditor.GetValue(config, "DirectX", "dgVoodooWatermark"));
        Assert.Null(IniEditor.GetValue(config, "Glide", "dgVoodooWatermark"));
        Assert.Equal("false", IniEditor.GetValue(config, "Glide", "3DfxWatermark"));
        Assert.Equal("1024", IniEditor.GetValue(config, "DirectX", "VRAM"));
        Assert.Equal("1920x1080", IniEditor.GetValue(config, "DirectX", "Resolution"));
    }

    [Fact]
    public void InvalidKeyRemovalIsConfinedToItsSectionAndRemovesDuplicates()
    {
        using var workspace = new TestWorkspace();
        var config = workspace.FilePath("dgVoodoo.conf");
        File.WriteAllText(config, "Version = 0x287\n[DirectX]\ndgVoodooWatermark=true\n" +
            "[Glide]\n; dgVoodooWatermark=comment\n dgVoodooWatermark = true\nDGVOODOOWATERMARK=false\n" +
            "3DfxWatermark=false\n[General]\nOutputAPI=bestavailable\n");
        IniEditor.RemoveValue(config, "Glide", "dgVoodooWatermark");
        Assert.Null(IniEditor.GetValue(config, "Glide", "dgVoodooWatermark"));
        Assert.Equal("true", IniEditor.GetValue(config, "DirectX", "dgVoodooWatermark"));
        Assert.Equal("false", IniEditor.GetValue(config, "Glide", "3DfxWatermark"));
        Assert.Contains("; dgVoodooWatermark=comment", File.ReadAllText(config));
        Assert.Equal("bestavailable", IniEditor.GetValue(config, "General", "OutputAPI"));
    }

    [Fact]
    public void MissingKeyRemovalDoesNotCreateOrRewriteFiles()
    {
        using var workspace = new TestWorkspace();
        var config = workspace.FilePath("dgVoodoo.conf");
        IniEditor.RemoveValue(config, "Glide", "dgVoodooWatermark");
        Assert.False(File.Exists(config));
        var original = System.Text.Encoding.UTF8.GetBytes("[DirectX]\r\ndgVoodooWatermark=true\r\n");
        File.WriteAllBytes(config, original);
        IniEditor.RemoveValue(config, "Glide", "dgVoodooWatermark");
        Assert.Equal(original, File.ReadAllBytes(config));
        IniEditor.RemoveValue(config, "DirectX", "unknown");
        Assert.Equal(original, File.ReadAllBytes(config));
    }
}
