using System.Text.RegularExpressions;
using System.Buffers.Binary;
using System.Reflection.PortableExecutable;
using System.Text.Json;

namespace Dlss5CompatApp;

static partial class PayloadScanner
{
    static readonly System.Collections.Concurrent.ConcurrentDictionary<(string Path, long Bytes, long Modified), bool> StandaloneInputCache = new();

    internal static bool HasStandaloneD3D12Input(string path)
    {
        var file = new FileInfo(path);
        if (StandaloneInputCache.Count > 64) StandaloneInputCache.Clear();
        return StandaloneInputCache.GetOrAdd((file.FullName, file.Length, file.LastWriteTimeUtc.Ticks),
            _ => PeReader.FindMarkers(path, ["StandaloneInput"]).Contains("StandaloneInput"));
    }
    public static PayloadInfo Scan(string root)
    {
        var files = Directory.Exists(root)
            ? Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).ToArray()
            : [];

        var reshade = files
            .Where(f => ReShadeSetup().IsMatch(Path.GetFileName(f)))
            .OrderByDescending(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();

        var reshade32 = FindReShadeDll(files, "ReShade32.dll") ?? AppOptionalFile("Runtime", "reshade", "ReShade32.dll");
        var reshade64 = FindReShadeDll(files, "ReShade64.dll") ?? AppOptionalFile("Runtime", "reshade", "ReShade64.dll");

        var addon = files
            .Where(f => Path.GetFileName(f).Equals("Phoenix.addon64", StringComparison.OrdinalIgnoreCase))
            .Where(IsPhoenixAddon)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .ThenBy(f => f, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();

        var bridgeAddon = files
            .Where(f => Path.GetFileName(f).Equals("dlss5-bridge.addon64", StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => PayloadFilePriority(root, f))
            .FirstOrDefault();

        var extraAddons = files
            .Where(f => Path.GetDirectoryName(Path.GetFullPath(f))!.Equals(Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase) ||
                Path.GetRelativePath(root, f).Replace('\\', '/').StartsWith("CustomAddons/", StringComparison.OrdinalIgnoreCase))
            .Where(f => Path.GetExtension(f).Equals(".addon64", StringComparison.OrdinalIgnoreCase) ||
                Path.GetExtension(f).Equals(".addon32", StringComparison.OrdinalIgnoreCase))
            .Where(f => addon is null || !Path.GetFullPath(f).Equals(Path.GetFullPath(addon), StringComparison.OrdinalIgnoreCase))
            .Where(f => !Path.GetFileName(f).StartsWith("renodx", StringComparison.OrdinalIgnoreCase))
            .Where(f => !Path.GetFileNameWithoutExtension(f).Equals("Phoenix", StringComparison.OrdinalIgnoreCase))
            .Where(f => !Path.GetFileNameWithoutExtension(f).Equals("phoenix-vulkan-feed", StringComparison.OrdinalIgnoreCase))
            .Where(f => !Path.GetFileName(f).StartsWith("dlss5-feed.", StringComparison.OrdinalIgnoreCase))
            .Where(f => !Path.GetFileNameWithoutExtension(f).Equals("dlss5-bridge", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        var nvidia = FindPayloadFiles(root, files, NvidiaDll());

        var streamline = FindPayloadFiles(root, files, StreamlineDll());

        var d3d8To9 = files
            .Where(f => Path.GetFileName(f).Equals("d3d8.dll", StringComparison.OrdinalIgnoreCase))
            .Where(f => f.Replace('/', '\\').Contains("\\d3d8to9\\", StringComparison.OrdinalIgnoreCase))
            .Where(f => PeReader.GetArchitecture(f) == CpuArch.X86)
            .OrderBy(f => PayloadFilePriority(root, f))
            .FirstOrDefault();

        d3d8To9 ??= AppOptionalFile("Runtime", "d3d8to9", "d3d8.dll");

        var dgD3D8 = FindDgVoodooFile(root, files, @"MS\x86", "D3D8.dll") ?? AppOptionalFile("Runtime", "dgvoodoo2", "x86", "D3D8.dll");
        var dgD3D9 = FindDgVoodooFile(root, files, @"MS\x86", "D3D9.dll") ?? AppOptionalFile("Runtime", "dgvoodoo2", "x86", "D3D9.dll");
        var dgD3DImm = FindDgVoodooFile(root, files, @"MS\x86", "D3DImm.dll") ?? AppOptionalFile("Runtime", "dgvoodoo2", "x86", "D3DImm.dll");
        var dgDDraw = FindDgVoodooFile(root, files, @"MS\x86", "DDraw.dll") ?? AppOptionalFile("Runtime", "dgvoodoo2", "x86", "DDraw.dll");
        var dgGlide = FindDgVoodooFile(root, files, @"3Dfx\x86", "Glide.dll");
        var dgGlide2x = FindDgVoodooFile(root, files, @"3Dfx\x86", "Glide2x.dll");
        var dgGlide3x = files
            .Where(f => Path.GetFileName(f).Equals("Glide3x.dll", StringComparison.OrdinalIgnoreCase))
            .Where(f => f.Replace('/', '\\').Contains("\\3Dfx\\x86\\", StringComparison.OrdinalIgnoreCase))
            .Where(f => !f.Replace('/', '\\').Contains("\\Napalm\\", StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => PayloadFilePriority(root, f))
            .FirstOrDefault();
        var dgGlide3xNapalm = files
            .Where(f => Path.GetFileName(f).Equals("Glide3x.dll", StringComparison.OrdinalIgnoreCase))
            .Where(f => f.Replace('/', '\\').Contains("\\3Dfx\\x86\\Napalm\\", StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => PayloadFilePriority(root, f))
            .FirstOrDefault();

        var dgCpl = files.FirstOrDefault(f => Path.GetFileName(f).Equals("dgVoodooCpl.exe", StringComparison.OrdinalIgnoreCase));
        var dosBoxExe = files
            .Where(f => Path.GetFileName(f).Equals("DosboxEnhanced.exe", StringComparison.OrdinalIgnoreCase))
            .Where(f => LocalPaths.Same(Path.GetDirectoryName(f)!, Path.Combine(root, "dosbox-enhanced", "app")))
            .Where(f => PeReader.GetArchitecture(f) == CpuArch.X64)
            .OrderBy(f => PayloadFilePriority(root, f))
            .FirstOrDefault();

        return new PayloadInfo(root, reshade, reshade32, reshade64, addon, bridgeAddon, d3d8To9, dgD3D8, dgD3D9, dgD3DImm, dgDDraw, dgGlide, dgGlide2x, dgGlide3x, dgGlide3xNapalm, dgCpl, dosBoxExe, nvidia, streamline, extraAddons)
        {
            DgVoodooDDraw64 = FindDgVoodooFile(root, files.Where(f => IsPeDll(f, CpuArch.X64)), @"MS\x64", "DDraw.dll"),
            DgVoodooD3DImm64 = FindDgVoodooFile(root, files.Where(f => IsPeDll(f, CpuArch.X64)), @"MS\x64", "D3DImm.dll"),
            SoftwareReShade32 = files.FirstOrDefault(f => Path.GetFileName(f).Equals("ReShadeSoftware32.dll", StringComparison.OrdinalIgnoreCase) && SoftwarePresentation.Compatible(f, CpuArch.X86))
                ?? AppOptionalFile("Runtime", "software-reshade", "ReShadeSoftware32.dll"),
            SoftwareReShade64 = files.FirstOrDefault(f => Path.GetFileName(f).Equals("ReShadeSoftware64.dll", StringComparison.OrdinalIgnoreCase) && SoftwarePresentation.Compatible(f, CpuArch.X64))
                ?? AppOptionalFile("Runtime", "software-reshade", "ReShadeSoftware64.dll"),
            PhoenixAddon32 = PhoenixCompanion32.Resolve(root) ?? files
                .Where(f => !Path.GetRelativePath(root, f).Replace('\\', '/').StartsWith("phoenix-companions/", StringComparison.OrdinalIgnoreCase))
                .Where(f => Path.GetFileName(f).Equals("Phoenix.addon32", StringComparison.OrdinalIgnoreCase))
                .Where(f => HasPackagedPhoenixBridge(f, CpuArch.X86))
                .OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault()
        };
    }

    static string? FindDgVoodooFile(string root, IEnumerable<string> files, string relativeFolder, string name)
    {
        var folderNeedle = "\\" + relativeFolder.Replace('/', '\\').Trim('\\') + "\\";
        return files
            .Where(f => Path.GetFileName(f).Equals(name, StringComparison.OrdinalIgnoreCase))
            .Where(f => f.Replace('/', '\\').Contains(folderNeedle, StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => PayloadFilePriority(root, f))
            .FirstOrDefault();
    }

    static string? FindReShadeDll(IEnumerable<string> files, string name)
    {
        return files.FirstOrDefault(f => Path.GetFileName(f).Equals(name, StringComparison.OrdinalIgnoreCase));
    }

    static string[] FindPayloadFiles(string root, IEnumerable<string> files, Regex regex)
    {
        return files
            .Where(f => regex.IsMatch(Path.GetFileName(f)))
            .Where(f => !Path.GetRelativePath(root, f).Replace('\\', '/').StartsWith(".download-cache/", StringComparison.OrdinalIgnoreCase))
            .Where(IsX64Dll)
            .GroupBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase)
            .Select(group => group
                // Explicit imports/legacy selections stay ahead of managed downloads.
                .OrderBy(f => IsManagedNvidia(root, f) ? 1 : 0)
                .ThenByDescending(f => IsManagedNvidia(root, f) ? NvidiaPackageVersion(f) : new Version())
                .ThenByDescending(f => IsManagedNvidia(root, f) ? Directory.GetCreationTimeUtc(Path.GetDirectoryName(f)!) : DateTime.MinValue)
                .ThenBy(f => IsManagedNvidia(root, f) ? Path.GetDirectoryName(f) : "", StringComparer.OrdinalIgnoreCase)
                .ThenByDescending(File.GetLastWriteTimeUtc)
                .ThenBy(f => PayloadFilePriority(root, f))
                .ThenBy(f => f, StringComparer.OrdinalIgnoreCase)
                .First())
            .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    static bool IsManagedNvidia(string root, string file) => Path.GetRelativePath(root, file)
        .Replace('\\', '/').StartsWith("Nvidia/", StringComparison.OrdinalIgnoreCase);

    static Version NvidiaPackageVersion(string file)
    {
        try
        {
            // Rank the bundle by its NR model, keeping its SR/FG/Streamline companions together.
            var model = Path.Combine(Path.GetDirectoryName(file)!, "nvngx_dlssnr.dll");
            return Version.TryParse(PayloadBootstrapper.NvidiaVersion(model), out var version) ? version : new Version();
        }
        catch (Exception error) when (error is IOException or ArgumentException) { return new Version(); }
    }

    static int PayloadFilePriority(string root, string file)
    {
        var relative = Path.GetRelativePath(root, file).Replace('/', '\\');
        if (!relative.Contains('\\', StringComparison.Ordinal)) return 0;
        if (relative.StartsWith(@"streamline\", StringComparison.OrdinalIgnoreCase)) return 1;
        if (relative.StartsWith(@".download-cache\", StringComparison.OrdinalIgnoreCase)) return 1000;
        return 10 + relative.Count(c => c == '\\');
    }

    public static bool IsX64Dll(string? path) => IsPeDll(path, CpuArch.X64);

    public static bool IsPeDll(string? path, CpuArch architecture)
    {
        try
        {
            using var file = File.OpenRead(path!);
            using var pe = new PEReader(file);
            var headers = pe.PEHeaders;
            if (architecture is not (CpuArch.X64 or CpuArch.X86) ||
                headers.CoffHeader.Machine != (architecture == CpuArch.X64 ? Machine.Amd64 : Machine.I386) ||
                headers.PEHeader?.Magic != (architecture == CpuArch.X64 ? PEMagic.PE32Plus : PEMagic.PE32) ||
                (headers.CoffHeader.Characteristics & Characteristics.Dll) == 0 ||
                (headers.CoffHeader.Characteristics & Characteristics.ExecutableImage) == 0 ||
                headers.SectionHeaders.Length == 0) return false;
            return headers.SectionHeaders.All(section => section.PointerToRawData >= 0 && section.SizeOfRawData >= 0 &&
                (long)section.PointerToRawData + section.SizeOfRawData <= file.Length);
        }
        catch { return false; }
    }

    public static bool IsPhoenixAddon(string? path)
    {
        if (!IsX64Dll(path)) return false;
        if (PeReader.GetImports(path!).Any(name => name.Contains("python", StringComparison.OrdinalIgnoreCase))) return false;
        return HasExports(path!, "CnrPHOENIXSummary", "CnrPHOENIXRegister");
    }

    public const string FeederApiRemediation = "Phoenix.addon64 needs CnrPHOENIXFeederVersion and CnrPHOENIXFeederEvaluate. Import a newly built direct-feeder-compatible Phoenix DLSS5 addon until a release includes these exports. The host requires ABI 1; SR/DLAA is not a fallback.";

    // Static PE inspection only. The runtime host separately checks FeederVersion() == 1.
    public static bool HasPhoenixFeederApi(string? path) => IsPhoenixAddon(path) &&
        HasExports(path!, "CnrPHOENIXFeederVersion", "CnrPHOENIXFeederEvaluate");

    public static bool HasPackagedPhoenixBridge(string? path, CpuArch architecture) =>
        architecture is CpuArch.X86 or CpuArch.X64 && IsPeDll(path, architecture) &&
        !PeReader.GetImports(path!).Any(name => name.Contains("python", StringComparison.OrdinalIgnoreCase)) &&
        HasExports(path!, "CnrPHOENIXCompatibilityStatus", "AddonInit", "AddonUninit");

    public static bool IsDirectPhoenixHost(string path) => PeReader.GetArchitecture(path) == CpuArch.X64 &&
        PeReader.FindMarkers(path, new[] { "phoenix-host/2" }).Contains("phoenix-host/2");

    internal static bool HasExports(string path, params string[] required)
    {
        try
        {
            using var file = File.OpenRead(path);
            using var pe = new PEReader(file);
            var directory = pe.PEHeaders.PEHeader!.ExportTableDirectory;
            if (directory.RelativeVirtualAddress == 0 || directory.Size < 40) return false;
            var exports = pe.GetSectionData(directory.RelativeVirtualAddress).GetContent(0, 40).ToArray();
            var count = BinaryPrimitives.ReadUInt32LittleEndian(exports.AsSpan(24));
            var names = BinaryPrimitives.ReadInt32LittleEndian(exports.AsSpan(32));
            if (count > 65536) return false;
            var table = pe.GetSectionData(names).GetContent(0, checked((int)count * 4)).ToArray();
            var found = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < count; ++i)
            {
                var rva = BinaryPrimitives.ReadInt32LittleEndian(table.AsSpan(i * 4));
                var reader = pe.GetSectionData(rva).GetReader();
                found.Add(reader.ReadUTF8(reader.IndexOf(0) is var length && length >= 0 ? length : throw new BadImageFormatException()));
            }
            return required.All(found.Contains);
        }
        catch { return false; }
    }

    public static bool HasEmbeddedPhoenixRuntime(string? path)
    {
        if (!IsPhoenixAddon(path)) return false;
        try
        {
            using var file = File.OpenRead(path!);
            using var pe = new PEReader(file);
            var directory = pe.PEHeaders.PEHeader!.ResourceTableDirectory;
            var rva = directory.RelativeVirtualAddress;
            if (rva == 0 || directory.Size < 40) return false;
            // Resource directory nodes are small; do not copy the entire embedded runtime into managed memory.
            var tree = pe.GetSectionData(rva).GetContent(0, Math.Min(directory.Size, 65536)).ToArray();
            int Child(int offset, uint? id)
            {
                var named = BinaryPrimitives.ReadUInt16LittleEndian(tree.AsSpan(offset + 12));
                var count = named + BinaryPrimitives.ReadUInt16LittleEndian(tree.AsSpan(offset + 14));
                for (var i = 0; i < count; ++i)
                {
                    var entry = offset + 16 + 8 * i;
                    var name = BinaryPrimitives.ReadUInt32LittleEndian(tree.AsSpan(entry));
                    if (id is null || name == id)
                        return checked((int)(BinaryPrimitives.ReadUInt32LittleEndian(tree.AsSpan(entry + 4)) & 0x7fffffff));
                }
                throw new BadImageFormatException();
            }
            (int Rva, int Bytes) Resource(uint id)
            {
                var data = Child(Child(Child(0, 10), id), null);
                var start = BinaryPrimitives.ReadInt32LittleEndian(tree.AsSpan(data));
                var count = BinaryPrimitives.ReadInt32LittleEndian(tree.AsSpan(data + 4));
                if (start < rva || count < 1 || (long)start + count > (long)rva + directory.Size ||
                    count > pe.GetSectionData(start).Length) throw new BadImageFormatException();
                return (start, count);
            }
            var (contentRva, bytes) = Resource(1001);
            if (bytes is < 1 or > 4194304) return false;
            using var manifest = JsonDocument.Parse(pe.GetSectionData(contentRva).GetContent(0, bytes).ToArray());
            var root = manifest.RootElement;
            if (root.GetProperty("schema").GetString() != "dlss5-phoenix-embedded-runtime/1" ||
                root.GetProperty("model_data_included").GetBoolean() || root.GetProperty("nvidia_dll_included").GetBoolean()) return false;
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var ids = new HashSet<uint>();
            var files = root.GetProperty("files");
            if (files.GetArrayLength() is < 2 or > 4096) return false;
            foreach (var entry in files.EnumerateArray())
            {
                var name = entry.GetProperty("path").GetString()!;
                var id = entry.GetProperty("resource").GetUInt32();
                var count = entry.GetProperty("bytes").GetInt64();
                var packed = entry.GetProperty("packed_bytes").GetInt64();
                var algorithm = entry.GetProperty("algorithm").GetString();
                var hash = entry.GetProperty("sha256").GetString();
                if (string.IsNullOrEmpty(name) || name.Contains('\\') || name.Contains(':') || name.Contains('\0') ||
                    name.Split('/').Any(part => part is "" or "." or "..") || !paths.Add(name) ||
                    name.EndsWith(".py", StringComparison.OrdinalIgnoreCase) || Path.GetFileName(name).Contains("python", StringComparison.OrdinalIgnoreCase) ||
                    id is <= 1001 or > 65535 || !ids.Add(id) || count is < 1 or > 536870912 || packed is < 1 or > 536870912 ||
                    algorithm is not ("raw" or "xpress_huff") || algorithm == "raw" && packed != count ||
                    hash is not { Length: 64 } || !hash.All(Uri.IsHexDigit) || Resource(id).Bytes != packed) return false;
            }
            return paths.Contains("runtime/phoenix_runtime_cpp.dll") && paths.Contains("runtime/phoenix_native_cpp.json");
        }
        catch { return false; }
    }

    static string? AppOptionalFile(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray());
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }

        return null;
    }

    [GeneratedRegex("^ReShade_Setup_.*_Addon\\.exe$", RegexOptions.IgnoreCase)]
    private static partial Regex ReShadeSetup();

    [GeneratedRegex("^nvngx_[a-z_]+\\.dll$", RegexOptions.IgnoreCase)]
    private static partial Regex NvidiaDll();

    [GeneratedRegex("^sl\\.[a-z_]+\\.dll$", RegexOptions.IgnoreCase)]
    private static partial Regex StreamlineDll();
}
