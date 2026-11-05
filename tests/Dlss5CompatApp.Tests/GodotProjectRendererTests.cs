using System.Text;
using System.Buffers.Binary;

namespace Dlss5CompatApp.Tests;

public sealed class GodotProjectRendererTests
{
    const string Key = "rendering/renderer/rendering_method";

    [Theory]
    [InlineData((int)CpuArch.X64)]
    [InlineData((int)CpuArch.X86)]
    public void EmbeddedProjectBeatsEngineCapabilitiesAndAllowsExplicitOverride(int architecture)
    {
        var arch = (CpuArch)architecture;
        using var workspace = new TestWorkspace();
        var exe = workspace.WritePe("game/arbitrary.exe", arch);
        var image = TestWorkspace.PeBytes(arch, [], "D3D12CreateDevice D3D11CreateDevice wglCreateContext");
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(0x96), 0x22);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(0xa8), 0x1000);
        File.WriteAllBytes(exe, image);
        WritePack(exe, embedded: true, [(Key, "gl_compatibility")]);
        var game = GameScanner.ScanSingleExe(exe)!;
        Assert.Equal(GraphicsApi.OpenGl, game.Api);
        Assert.Contains("project:Godot4/PCK", game.Detection);
        Assert.Contains(GraphicsApi.DirectX12, game.AllApis);
        Assert.Equal(GraphicsApi.DirectX12, GameScanner.ScanSingleExe(exe, GraphicsApi.DirectX12)!.Api);
    }

    [Theory]
    [InlineData("gl_compatibility", "forward_plus", false)]
    [InlineData("forward_plus", "gl_compatibility", true)]
    public void WindowsFeatureSettingOverridesGeneric(string generic, string windows, bool expected)
    {
        using var workspace = new TestWorkspace();
        var exe = workspace.WritePe("game/arbitrary.exe", CpuArch.X64);
        WritePack(Path.ChangeExtension(exe, ".pck"), false, [(Key, generic), (Key + ".windows", windows)]);
        Assert.Equal(expected, GodotProjectRenderer.UsesOpenGl(exe));
    }

    [Fact]
    public void UnrelatedStringsAndMobileChoiceCannotSelectDesktopRenderer()
    {
        using var workspace = new TestWorkspace();
        var exe = workspace.WritePe("game/arbitrary.exe", CpuArch.X64);
        WritePack(exe, true, [("application/config/description", "gl_compatibility"), (Key + ".mobile", "gl_compatibility")]);
        Assert.False(GodotProjectRenderer.UsesOpenGl(exe));
    }

    [Theory]
    [InlineData(1u, 2u, false, false)] // Encrypted directory, no decoding/bypass.
    [InlineData(0u, 3u, false, false)] // Unknown PCK version.
    [InlineData(0u, 2u, true, false)] // Encrypted project file.
    [InlineData(0u, 2u, false, true)] // Overflow/out-of-file data address.
    public void UnsupportedOrCorruptMetadataDoesNotClaimRenderer(uint flags, uint version, bool encrypted, bool badOffset)
    {
        using var workspace = new TestWorkspace();
        var exe = workspace.WritePe("game/arbitrary.exe", CpuArch.X64);
        WritePack(exe, true, [(Key, "gl_compatibility")], flags, version, encrypted, badOffset);
        Assert.False(GodotProjectRenderer.UsesOpenGl(exe));
    }

    [Fact]
    public void InvalidExternalPackDoesNotFallBackToDifferentEmbeddedProject()
    {
        using var workspace = new TestWorkspace();
        var exe = workspace.WritePe("game/arbitrary.exe", CpuArch.X64);
        WritePack(exe, true, [(Key, "gl_compatibility")]);
        File.WriteAllBytes(Path.ChangeExtension(exe, ".pck"), new byte[100]);
        Assert.False(GodotProjectRenderer.UsesOpenGl(exe));
    }

    static void WritePack(string path, bool embedded, (string Key, string Value)[] values,
        uint flags = 0, uint version = 2, bool encrypted = false, bool badOffset = false)
    {
        var prefix = embedded ? File.ReadAllBytes(path) : [];
        using var config = new MemoryStream();
        using (var settings = new BinaryWriter(config, Encoding.UTF8, leaveOpen: true))
        {
            settings.Write(0x47464345u); settings.Write((uint)values.Length);
            foreach (var (key, value) in values)
            {
                Text(settings, key);
                using var variant = new MemoryStream();
                using var writer = new BinaryWriter(variant, Encoding.UTF8, leaveOpen: true);
                writer.Write(4u); Text(writer, value);
                settings.Write((uint)variant.Length); settings.Write(variant.ToArray());
            }
        }
        using var file = File.Create(path);
        using var pack = new BinaryWriter(file);
        pack.Write(prefix);
        pack.Write(0x43504447u); pack.Write(version); pack.Write(4u); pack.Write(2u); pack.Write(0u); pack.Write(flags);
        var name = Encoding.UTF8.GetBytes("res://project.binary");
        var dataBase = (ulong)(prefix.Length + 100 + 4 + name.Length + 8 + 8 + 16 + 4);
        pack.Write(dataBase); pack.Write(new byte[64]); pack.Write(1u);
        Text(pack, "res://project.binary"); pack.Write(badOffset ? ulong.MaxValue : 0ul);
        pack.Write((ulong)config.Length); pack.Write(new byte[16]); pack.Write(encrypted ? 1u : 0u);
        pack.Write(config.ToArray());
        if (embedded) { pack.Write((ulong)(file.Position - prefix.Length)); pack.Write(0x43504447u); }
    }

    static void Text(BinaryWriter writer, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value); writer.Write((uint)bytes.Length); writer.Write(bytes);
    }
}
