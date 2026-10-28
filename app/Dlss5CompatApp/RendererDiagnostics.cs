using System.Text;

namespace Dlss5CompatApp;

/// <summary>Read-only software-renderer hints. Imports alone never prove which
/// adapter an application selects, and old logs must never become install blocks.</summary>
static class RendererDiagnostics
{
    static readonly string[] SoftwareMarkers =
    [
        "D3D_DRIVER_TYPE_WARP", "D3D10_DRIVER_TYPE_WARP", "D3D_DRIVER_TYPE_REFERENCE",
        "D3D10_DRIVER_TYPE_REFERENCE", "EnumWarpAdapter", "software_renderer", "WARP"
    ];

    public static IReadOnlyList<PreflightFinding> Inspect(string executable)
    {
        var findings = new List<PreflightFinding>();
        try
        {
            LocalPaths.RequireNoLinks(executable);
            var d3d9 = Path.Combine(Path.GetDirectoryName(executable)!, "d3d9.dll");
            if (File.Exists(d3d9))
            {
                LocalPaths.RequireNoLinks(d3d9);
                var version = System.Diagnostics.FileVersionInfo.GetVersionInfo(d3d9);
                if (version.ProductName?.Contains("bridge-remix", StringComparison.OrdinalIgnoreCase) == true &&
                    PayloadScanner.HasExports(d3d9, "Direct3DCreate9"))
                    findings.Add(new("remix-renderer-bridge", PreflightSeverity.Warning,
                        "An RTX Remix D3D9 bridge owns this game's front-end DLL. Preserve the bridge; Phoenix must target the bridge renderer process/API instead of replacing d3d9.dll.", d3d9));
            }
            var markers = PeReader.FindMarkers(executable, SoftwareMarkers);
            if (markers.Any(marker => marker.StartsWith("D3D_DRIVER_TYPE_", StringComparison.Ordinal) ||
                marker.StartsWith("D3D10_DRIVER_TYPE_", StringComparison.Ordinal) || marker == "EnumWarpAdapter") ||
                markers.Contains("software_renderer") && markers.Contains("WARP"))
                findings.Add(new("software-renderer-possible", PreflightSeverity.Warning,
                    "Software-renderer capability indicators found; the active adapter is not known from static scanning. " +
                    "Stock ReShade skips WARP/reference DX10/11 devices; software DX12 needs runtime verification and a compatible hardware inference route."));

            var log = Path.Combine(Path.GetDirectoryName(executable)!, "ReShade.log");
            var tail = ReadLogTail(log);
            if (tail.Contains("Skipping device because the driver type is", StringComparison.Ordinal) &&
                    (tail.Contains("D3D_DRIVER_TYPE_WARP", StringComparison.Ordinal) ||
                     tail.Contains("D3D_DRIVER_TYPE_REFERENCE", StringComparison.Ordinal)) ||
                tail.Contains("Skipping device because it uses the Microsoft Basic Render Driver", StringComparison.Ordinal))
                findings.Add(new("software-renderer-observed", PreflightSeverity.Warning,
                    "An existing ReShade log reports a skipped WARP/reference software device. " +
                    "This is previous-run evidence, not proof of the current executable/settings; choose hardware rendering and verify injection.", log));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // Missing, linked or locked diagnostics do not manufacture compatibility evidence.
        }
        return findings;
    }

    static string ReadLogTail(string path)
    {
        if (!File.Exists(path)) return "";
        LocalPaths.RequireNoLinks(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        const int limit = 256 * 1024;
        stream.Seek(Math.Max(0, stream.Length - limit), SeekOrigin.Begin);
        using var reader = new StreamReader(stream, Encoding.UTF8, true, 4096);
        // Bound the read even if a live process keeps appending to its log.
        var buffer = new char[limit];
        var length = reader.ReadBlock(buffer, 0, buffer.Length);
        return new string(buffer, 0, length);
    }
}
