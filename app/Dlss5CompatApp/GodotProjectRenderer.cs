using System.Text;

namespace Dlss5CompatApp;

/// <summary>
/// Reads only bounded Godot 4 project metadata. Engine capability strings are
/// not the exported project's renderer choice. No resources are extracted and
/// encrypted/unknown pack formats are left to ordinary detection/user selection.
/// Format references: Godot core/io/file_access_pack.cpp and
/// core/config/project_settings.cpp, branch 4.2 (MIT).
/// </summary>
static class GodotProjectRenderer
{
    const uint PackMagic = 0x43504447; // GDPC, little endian
    const uint SettingsMagic = 0x47464345; // ECFG
    const int MaxDirectoryBytes = 8 * 1024 * 1024;
    const int MaxSettingsBytes = 1024 * 1024;
    const string RendererKey = "rendering/renderer/rendering_method";

    public static bool UsesOpenGl(string executable)
    {
        try
        {
            var external = Path.ChangeExtension(executable, ".pck");
            var path = File.Exists(external) ? external : executable;
            LocalPaths.RequireNoLinks(path);
            using var file = File.OpenRead(path);
            using var reader = new BinaryReader(file, Encoding.UTF8, leaveOpen: true);
            if (file.Length < 100) return false;
            long header = 0, dataEnd = file.Length;
            if (reader.ReadUInt32() != PackMagic)
            {
                file.Position = file.Length - 12;
                var packBytes = reader.ReadUInt64();
                if (reader.ReadUInt32() != PackMagic || packBytes > (ulong)(file.Length - 12)) return false;
                header = file.Length - 12 - (long)packBytes;
                dataEnd -= 12;
            }
            file.Position = header;
            if (reader.ReadUInt32() != PackMagic || reader.ReadUInt32() != 2 || reader.ReadUInt32() != 4) return false;
            file.Position = checked(header + 20);
            if (reader.ReadUInt32() != 0) return false; // Encrypted/unknown flags.
            var dataBase = reader.ReadUInt64();
            file.Position = checked(header + 96);
            var count = reader.ReadUInt32();
            if (count > 65536 || dataBase > (ulong)dataEnd) return false;
            for (uint entry = 0; entry < count; entry++)
            {
                if (file.Position - header > MaxDirectoryBytes) return false;
                var name = ReadString(reader, 4096).TrimEnd('\0');
                var relative = reader.ReadUInt64();
                var bytes = reader.ReadUInt64();
                file.Position = checked(file.Position + 16); // Stored content digest.
                var flags = reader.ReadUInt32();
                if (name != "res://project.binary") continue;
                if (flags != 0 || bytes > MaxSettingsBytes || relative > ulong.MaxValue - dataBase) return false;
                var offset = dataBase + relative;
                if (offset < (ulong)(header + 100) || offset > (ulong)dataEnd || bytes > (ulong)dataEnd - offset) return false;
                file.Position = (long)offset;
                var settings = reader.ReadBytes((int)bytes);
                return settings.Length == (int)bytes && ReadRenderer(settings) == "gl_compatibility";
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or OverflowException)
        { /* Optional metadata must not make scanning an otherwise valid EXE fail. */ }
        return false;
    }

    static string ReadString(BinaryReader reader, int maximum)
    {
        var length = reader.ReadUInt32();
        if (length > maximum || length > reader.BaseStream.Length - reader.BaseStream.Position)
            throw new InvalidDataException("Invalid Godot metadata string extent.");
        return Encoding.UTF8.GetString(reader.ReadBytes((int)length));
    }

    static string? ReadRenderer(byte[] settings)
    {
        using var file = new MemoryStream(settings, writable: false);
        using var reader = new BinaryReader(file);
        if (reader.ReadUInt32() != SettingsMagic) return null;
        var count = reader.ReadUInt32();
        if (count > 4096) return null;
        string? generic = null, windows = null;
        for (uint entry = 0; entry < count; entry++)
        {
            var key = ReadString(reader, 4096);
            var length = reader.ReadUInt32();
            if (length > MaxSettingsBytes || length > file.Length - file.Position) return null;
            var end = file.Position + length;
            if (key == RendererKey || key == RendererKey + ".windows")
            {
                // Decode only the String variant. Never decode objects/scripts
                // or search arbitrary asset bytes for a coincidental marker.
                if (length < 8 || reader.ReadUInt32() != 4) return null;
                var value = ReadString(reader, 256);
                if (file.Position > end) return null;
                if (key == RendererKey) generic = value; else windows = value;
            }
            file.Position = end;
        }
        return windows ?? generic;
    }
}
