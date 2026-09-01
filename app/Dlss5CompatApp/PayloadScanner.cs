using System.Text.RegularExpressions;

namespace Dlss5CompatApp;

static partial class PayloadScanner
{
    public static PayloadInfo Scan(string root)
    {
        if (!Directory.Exists(root))
            return new PayloadInfo(root, null, null, null, null, [], [], []);

        var files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).ToArray();
        var reshade = files
            .Where(f => ReShadeSetup().IsMatch(Path.GetFileName(f)))
            .OrderByDescending(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();

        var addon = files.FirstOrDefault(f => Path.GetFileName(f).Equals("renodx-dlss5.addon64", StringComparison.OrdinalIgnoreCase))
            ?? files.FirstOrDefault(f => Path.GetExtension(f).Equals(".addon64", StringComparison.OrdinalIgnoreCase));

        var extraAddons = files
            .Where(f => Path.GetExtension(f).Equals(".addon64", StringComparison.OrdinalIgnoreCase))
            .Where(f => addon is null || !Path.GetFullPath(f).Equals(Path.GetFullPath(addon), StringComparison.OrdinalIgnoreCase))
            .ToArray();

        var nvidia = files
            .Where(f => NvidiaDll().IsMatch(Path.GetFileName(f)))
            .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var streamline = files
            .Where(f => StreamlineDll().IsMatch(Path.GetFileName(f)))
            .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var dgD3D9 = files.FirstOrDefault(f =>
            Path.GetFileName(f).Equals("D3D9.dll", StringComparison.OrdinalIgnoreCase) &&
            f.Replace('/', '\\').Contains("\\MS\\x86\\", StringComparison.OrdinalIgnoreCase));

        dgD3D9 ??= files.FirstOrDefault(f =>
            Path.GetFileName(f).Equals("D3D9.dll", StringComparison.OrdinalIgnoreCase) &&
            PeReader.GetArchitecture(f) == CpuArch.X86);

        var dgCpl = files.FirstOrDefault(f => Path.GetFileName(f).Equals("dgVoodooCpl.exe", StringComparison.OrdinalIgnoreCase));

        return new PayloadInfo(root, reshade, addon, dgD3D9, dgCpl, nvidia, streamline, extraAddons);
    }

    [GeneratedRegex("^ReShade_Setup_.*_Addon\\.exe$", RegexOptions.IgnoreCase)]
    private static partial Regex ReShadeSetup();

    [GeneratedRegex("^nvngx_[a-z_]+\\.dll$", RegexOptions.IgnoreCase)]
    private static partial Regex NvidiaDll();

    [GeneratedRegex("^sl\\.[a-z_]+\\.dll$", RegexOptions.IgnoreCase)]
    private static partial Regex StreamlineDll();
}
