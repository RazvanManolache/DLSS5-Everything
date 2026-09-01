using System.Buffers.Binary;
using System.Text;

namespace Dlss5CompatApp;

static class PeReader
{
    public static CpuArch GetArchitecture(string file)
    {
        try
        {
            using var fs = File.OpenRead(file);
            var headers = ReadHeaders(fs);
            if (headers is null) return CpuArch.Unknown;
            return headers.Value.Machine switch
            {
                0x014c => CpuArch.X86,
                0x8664 => CpuArch.X64,
                _ => CpuArch.Unknown
            };
        }
        catch
        {
            return CpuArch.Unknown;
        }
    }

    public static IReadOnlyList<string> GetImports(string file)
    {
        try
        {
            using var fs = File.OpenRead(file);
            var headers = ReadHeaders(fs);
            if (headers is null) return [];

            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            ReadImportTable(fs, headers.Value, 1, 20, 12, names);
            ReadImportTable(fs, headers.Value, 13, 32, 4, names);
            return names.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
        }
        catch
        {
            return [];
        }
    }

    public static IReadOnlySet<string> FindMarkers(string file, IEnumerable<string> markers)
    {
        var needles = markers.Select(m => (Text: m, Bytes: Encoding.ASCII.GetBytes(m))).ToArray();
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (needles.Length == 0) return found;

        try
        {
            using var fs = File.OpenRead(file);
            var longest = needles.Max(n => n.Bytes.Length);
            var buffer = new byte[(4 * 1024 * 1024) + longest];
            var carry = 0;

            while (fs.Position < fs.Length)
            {
                var read = fs.Read(buffer, carry, buffer.Length - carry);
                if (read <= 0) break;
                var span = buffer.AsSpan(0, carry + read);

                foreach (var needle in needles)
                {
                    if (!found.Contains(needle.Text) && span.IndexOf(needle.Bytes) >= 0)
                        found.Add(needle.Text);
                }

                if (found.Count == needles.Length) break;
                carry = Math.Min(longest, span.Length);
                span[^carry..].CopyTo(buffer);
            }
        }
        catch
        {
            // Static scanning should fail closed, not guess.
        }

        return found;
    }

    static void ReadImportTable(FileStream fs, PeHeaders headers, int directoryIndex, int stride, int nameFieldOffset, HashSet<string> names)
    {
        if (headers.DataDirectories.Count <= directoryIndex) return;
        var dir = headers.DataDirectories[directoryIndex];
        if (dir.Rva == 0) return;

        var tableOffset = RvaToOffset(headers, dir.Rva);
        if (tableOffset is null) return;

        var max = (int)Math.Min(dir.Size == 0 ? 4096 : dir.Size, 64 * 1024);
        var table = ReadAt(fs, max, tableOffset.Value);
        for (var offset = 0; offset + stride <= table.Length; offset += stride)
        {
            var nameRva = BinaryPrimitives.ReadUInt32LittleEndian(table.AsSpan(offset + nameFieldOffset, 4));
            if (nameRva == 0) break;
            var nameOffset = RvaToOffset(headers, nameRva);
            if (nameOffset is null) continue;
            var name = ReadCString(fs, nameOffset.Value);
            if (!string.IsNullOrWhiteSpace(name)) names.Add(name.ToLowerInvariant());
        }
    }

    static PeHeaders? ReadHeaders(FileStream fs)
    {
        var dos = ReadAt(fs, 0x40, 0);
        if (dos.Length < 0x40 || BinaryPrimitives.ReadUInt16LittleEndian(dos) != 0x5a4d) return null;

        var peOffset = BinaryPrimitives.ReadUInt32LittleEndian(dos.AsSpan(0x3c, 4));
        var coff = ReadAt(fs, 24, peOffset);
        if (coff.Length < 24 || BinaryPrimitives.ReadUInt32LittleEndian(coff) != 0x00004550) return null;

        var machine = BinaryPrimitives.ReadUInt16LittleEndian(coff.AsSpan(4, 2));
        var sections = BinaryPrimitives.ReadUInt16LittleEndian(coff.AsSpan(6, 2));
        var optionalSize = BinaryPrimitives.ReadUInt16LittleEndian(coff.AsSpan(20, 2));
        var optionalOffset = peOffset + 24;
        var optional = ReadAt(fs, optionalSize, optionalOffset);
        if (optional.Length < 2) return null;

        var magic = BinaryPrimitives.ReadUInt16LittleEndian(optional);
        var dataDirectoryOffset = magic == 0x20b ? 112 : 96;
        var directories = new List<DataDirectory>();
        for (var i = 0; i < 16; i++)
        {
            var offset = dataDirectoryOffset + (i * 8);
            if (offset + 8 > optional.Length) break;
            directories.Add(new DataDirectory(
                BinaryPrimitives.ReadUInt32LittleEndian(optional.AsSpan(offset, 4)),
                BinaryPrimitives.ReadUInt32LittleEndian(optional.AsSpan(offset + 4, 4))));
        }

        var sectionTable = ReadAt(fs, sections * 40, optionalOffset + optionalSize);
        var sectionList = new List<SectionHeader>();
        for (var i = 0; i < sections; i++)
        {
            var offset = i * 40;
            if (offset + 40 > sectionTable.Length) break;
            sectionList.Add(new SectionHeader(
                BinaryPrimitives.ReadUInt32LittleEndian(sectionTable.AsSpan(offset + 8, 4)),
                BinaryPrimitives.ReadUInt32LittleEndian(sectionTable.AsSpan(offset + 12, 4)),
                BinaryPrimitives.ReadUInt32LittleEndian(sectionTable.AsSpan(offset + 16, 4)),
                BinaryPrimitives.ReadUInt32LittleEndian(sectionTable.AsSpan(offset + 20, 4))));
        }

        return new PeHeaders(machine, directories, sectionList);
    }

    static long? RvaToOffset(PeHeaders headers, uint rva)
    {
        foreach (var section in headers.Sections)
        {
            var span = Math.Max(section.VirtualSize, section.RawSize);
            if (rva >= section.VirtualAddress && rva < section.VirtualAddress + span)
                return section.RawOffset + (rva - section.VirtualAddress);
        }

        return null;
    }

    static byte[] ReadAt(FileStream fs, long size, long position)
    {
        if (position < 0 || position >= fs.Length) return [];
        fs.Position = position;
        var buffer = new byte[Math.Min(size, fs.Length - position)];
        var read = fs.Read(buffer, 0, buffer.Length);
        if (read == buffer.Length) return buffer;
        Array.Resize(ref buffer, read);
        return buffer;
    }

    static string ReadCString(FileStream fs, long position)
    {
        var buffer = ReadAt(fs, 256, position);
        var end = Array.IndexOf(buffer, (byte)0);
        if (end < 0) end = buffer.Length;
        return Encoding.Latin1.GetString(buffer, 0, end);
    }

    readonly record struct PeHeaders(ushort Machine, IReadOnlyList<DataDirectory> DataDirectories, IReadOnlyList<SectionHeader> Sections);
    readonly record struct DataDirectory(uint Rva, uint Size);
    readonly record struct SectionHeader(uint VirtualSize, uint VirtualAddress, uint RawSize, uint RawOffset);
}
