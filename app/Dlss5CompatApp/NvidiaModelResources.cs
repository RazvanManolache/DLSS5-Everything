using System.Buffers.Binary;
using System.Reflection.PortableExecutable;
using System.Text;

namespace Dlss5CompatApp;

/// <summary>Inspect model resources without loading NVIDIA code or reading its large weight blobs.</summary>
static class NvidiaModelResources
{
    internal static string ResourceName(string model) => model switch
    {
        "original" => "WEIGHTS_HT",
        "small-fp8" => "WEIGHTS_CC_SMALL_FP8",
        _ => throw new ArgumentException("Unknown neural model: " + model)
    };

    public static bool Contains(string path, string model)
    {
        var wanted = ResourceName(model);
        try
        {
            using var input = File.OpenRead(path);
            using var pe = new PEReader(input, PEStreamOptions.LeaveOpen);
            if (pe.PEHeaders.CoffHeader.Machine != Machine.Amd64 ||
                (pe.PEHeaders.CoffHeader.Characteristics & Characteristics.Dll) == 0 ||
                pe.PEHeaders.PEHeader is not { } header) return false;
            var resource = header.ResourceTableDirectory;
            if (resource.RelativeVirtualAddress <= 0 || resource.Size < 16) return false;

            // PEReader.GetSectionData can materialize a DLL-sized .rsrc section.
            // Translate RVAs and read just bounded directory/name/data entries.
            long FileOffset(long rva, int bytes)
            {
                foreach (var section in pe.PEHeaders.SectionHeaders)
                {
                    var delta = rva - section.VirtualAddress;
                    if (delta < 0 || delta + bytes > section.SizeOfRawData) continue;
                    var offset = section.PointerToRawData + delta;
                    if (offset < 0 || offset + bytes > input.Length) break;
                    return offset;
                }
                throw new BadImageFormatException("Resource lies outside the DLL.");
            }
            byte[] Read(uint relative, int bytes)
            {
                if (relative > resource.Size || bytes < 0 || (long)relative + bytes > resource.Size)
                    throw new BadImageFormatException("Invalid resource directory.");
                input.Position = FileOffset((long)resource.RelativeVirtualAddress + relative, bytes);
                var value = new byte[bytes];
                input.ReadExactly(value);
                return value;
            }
            IEnumerable<(uint Name, uint Target)> Children(uint directory)
            {
                var node = Read(directory, 16);
                var count = BinaryPrimitives.ReadUInt16LittleEndian(node.AsSpan(12)) +
                            BinaryPrimitives.ReadUInt16LittleEndian(node.AsSpan(14));
                if (count > 4096) throw new BadImageFormatException("Oversized resource directory.");
                var entries = Read(checked(directory + 16), checked(count * 8));
                for (var i = 0; i < count; i++)
                    yield return (BinaryPrimitives.ReadUInt32LittleEndian(entries.AsSpan(i * 8)),
                                  BinaryPrimitives.ReadUInt32LittleEndian(entries.AsSpan(i * 8 + 4)));
            }
            foreach (var type in Children(0).Where(entry => entry.Name == 10 && (entry.Target & 0x80000000) != 0))
            foreach (var name in Children(type.Target & 0x7fffffff))
            {
                if ((name.Name & 0x80000000) == 0 || (name.Target & 0x80000000) == 0) continue;
                var offset = name.Name & 0x7fffffff;
                var chars = BinaryPrimitives.ReadUInt16LittleEndian(Read(offset, 2));
                if (chars > 128) continue;
                var text = Encoding.Unicode.GetString(Read(checked(offset + 2), chars * 2));
                if (text != wanted) continue;
                foreach (var language in Children(name.Target & 0x7fffffff))
                {
                    if ((language.Name & 0x80000000) != 0 || (language.Target & 0x80000000) != 0) continue;
                    var data = Read(language.Target, 16);
                    var rva = BinaryPrimitives.ReadUInt32LittleEndian(data);
                    var bytes = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(4));
                    if (bytes == 0 || bytes > int.MaxValue) continue;
                    _ = FileOffset(rva, (int)bytes); // Validate extent, never read weights here.
                    return true;
                }
            }
            return false;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or
            BadImageFormatException or ArgumentException or OverflowException) { return false; }
    }
}
