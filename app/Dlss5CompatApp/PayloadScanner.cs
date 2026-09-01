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
            .FirstOrDefault()
            ?? files
                .Where(f => Path.GetExtension(f).Equals(".addon64", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(IsKnownGoodPayloadFile)
                .ThenBy(f => PayloadFilePriority(root, f))
                .FirstOrDefault();

        var extraAddons = files
            .Where(f => Path.GetExtension(f).Equals(".addon64", StringComparison.OrdinalIgnoreCase))
            .Where(f => addon is null || !Path.GetFullPath(f).Equals(Path.GetFullPath(addon), StringComparison.OrdinalIgnoreCase))
            .Where(f => !Path.GetFileName(f).StartsWith("renodx-dlss5", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        var nvidia = FindPayloadFiles(root, files, NvidiaDll());

        var streamline = FindPayloadFiles(root, files, StreamlineDll());

        var dgD3D9 = files.FirstOrDefault(f =>
            Path.GetFileName(f).Equals("D3D9.dll", StringComparison.OrdinalIgnoreCase) &&
            f.Replace('/', '\\').Contains("\\MS\\x86\\", StringComparison.OrdinalIgnoreCase));

        dgD3D9 ??= files.FirstOrDefault(f =>
            Path.GetFileName(f).Equals("D3D9.dll", StringComparison.OrdinalIgnoreCase) &&
            PeReader.GetArchitecture(f) == CpuArch.X86);

        var dgCpl = files.FirstOrDefault(f => Path.GetFileName(f).Equals("dgVoodooCpl.exe", StringComparison.OrdinalIgnoreCase));

        return new PayloadInfo(root, reshade, reshade32, reshade64, addon, dgD3D9, dgCpl, nvidia, streamline, extraAddons);
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
