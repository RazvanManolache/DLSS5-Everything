using System.Text.RegularExpressions;
using System.Security.Cryptography;

namespace Dlss5CompatApp;

static partial class PayloadScanner
{
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
            .Where(f => Path.GetFileName(f).Equals("renodx-dlss5.addon64", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(IsKnownGoodPayloadFile)
            .ThenBy(f => PayloadFilePriority(root, f))
            .FirstOrDefault();

        var bridgeAddon = files
            .Where(f => Path.GetFileName(f).Equals("dlss5-bridge.addon64", StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => PayloadFilePriority(root, f))
            .FirstOrDefault();

        var extraAddons = files
            .Where(f => Path.GetExtension(f).Equals(".addon64", StringComparison.OrdinalIgnoreCase))
            .Where(f => addon is null || !Path.GetFullPath(f).Equals(Path.GetFullPath(addon), StringComparison.OrdinalIgnoreCase))
            .Where(f => !Path.GetFileName(f).StartsWith("renodx-dlss5", StringComparison.OrdinalIgnoreCase))
            .Where(f => !Path.GetFileName(f).Equals("dlss5-bridge.addon64", StringComparison.OrdinalIgnoreCase))
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

        return new PayloadInfo(root, reshade, reshade32, reshade64, addon, bridgeAddon, d3d8To9, dgD3D8, dgD3D9, dgD3DImm, dgDDraw, dgGlide, dgGlide2x, dgGlide3x, dgGlide3xNapalm, dgCpl, nvidia, streamline, extraAddons);
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
            .GroupBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase)
            .Select(group => group
                .OrderByDescending(IsKnownGoodPayloadFile)
                .ThenBy(f => PayloadFilePriority(root, f))
                .ThenBy(f => f, StringComparer.OrdinalIgnoreCase)
                .First())
            .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    static int PayloadFilePriority(string root, string file)
    {
        var relative = Path.GetRelativePath(root, file).Replace('/', '\\');
        if (!relative.Contains('\\', StringComparison.Ordinal)) return 0;
        if (relative.StartsWith(@"streamline\", StringComparison.OrdinalIgnoreCase)) return 1;
        if (relative.StartsWith(@".download-cache\", StringComparison.OrdinalIgnoreCase)) return 1000;
        return 10 + relative.Count(c => c == '\\');
    }

    static bool IsKnownGoodPayloadFile(string file)
    {
        var name = Path.GetFileName(file);
        var wanted = name.Equals("nvngx_dlssnr.dll", StringComparison.OrdinalIgnoreCase)
            ? PayloadInfo.KnownGoodDlssNrSha256
            : name.Equals("renodx-dlss5.addon64", StringComparison.OrdinalIgnoreCase)
                ? PayloadInfo.KnownGoodRenoDxAddonSha256
                : null;

        if (wanted is null) return false;
        try
        {
            using var stream = File.OpenRead(file);
            return Convert.ToHexString(SHA256.HashData(stream)).Equals(wanted, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
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
