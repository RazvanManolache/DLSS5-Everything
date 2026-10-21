using System.Diagnostics;
using System.Security.Cryptography;

namespace Dlss5CompatApp;

sealed record ModComponent(string Name, string Kind, CpuArch Architecture, string Sha256, string Detail);
sealed record ModLoadingPlan(string ExePath, string? SuggestedProxy, IReadOnlyList<ModComponent> Components,
    IReadOnlyList<string> Steps, bool ManualReview);

/// <summary>Read-only inventory. Installation retains its fresh ownership and conflict checks.</summary>
static class ModCoexistencePlanner
{
    static readonly string[] Names = ["dxgi.dll", "d3d11.dll", "d3d12.dll", "d3d9.dll", "opengl32.dll",
        "version.dll", "winmm.dll", "dinput8.dll", "ReShade.asi", "d3d11.asi", "OptiScaler.dll",
        "OptiScaler.ini", "ReShade.ini", "ReShade64.ini", "ReShade32.ini", "global.ini"];

    internal static ModLoadingPlan Inspect(GameCandidate game)
    {
        var executable = Path.GetFullPath(game.ExePath);
        LocalPaths.RequireNoLinks(executable);
        var directory = Path.GetDirectoryName(executable)!;
        var components = new List<ModComponent>(); var steps = new List<string>();
        var occupied = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in Names)
        {
            var path = Path.Combine(directory, name);
            if (!File.Exists(path) && !Directory.Exists(path)) continue;
            occupied.Add(name);
            try
            {
                LocalPaths.RequireNoLinks(path);
                var info = new FileInfo(path);
                if (!info.Exists || info.Length > 128L * 1024 * 1024) throw new IOException("Not a bounded regular file.");
                var binary = name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".asi", StringComparison.OrdinalIgnoreCase);
                var markers = binary ? PeReader.FindMarkers(path, ["ReShade", "OptiScaler", "Special K", "Ultimate ASI Loader"]) : new HashSet<string>();
                var kind = markers.Contains("OptiScaler") || name.StartsWith("OptiScaler", StringComparison.OrdinalIgnoreCase) ? "OptiScaler"
                    : markers.Contains("ReShade") ? "ReShade" : markers.Contains("Special K") ? "Special K"
                    : markers.Contains("Ultimate ASI Loader") ? "ASI loader" : binary ? "Unidentified graphics or loader component" : "Configuration";
                using var stream = File.OpenRead(path);
                var digest = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
                var detail = binary ? FileVersionInfo.GetVersionInfo(path).FileVersion ?? "Version unavailable" : "Preserve existing configuration";
                components.Add(new(name, kind, binary ? PeReader.GetArchitecture(path) : CpuArch.Unknown, digest, detail));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
            { components.Add(new(name, "Unavailable — preserve", CpuArch.Unknown, "", error.Message)); }
        }
        // A free name is only a loading candidate. d3d11.dll cannot intercept D3D12-only games.
        string[] candidates = game.Api == GraphicsApi.DirectX11 ? ["dxgi.dll", "d3d11.dll"]
            : game.Api is GraphicsApi.DirectX12 or GraphicsApi.Dxgi ? ["dxgi.dll"] : [];
        var proxy = candidates.FirstOrDefault(name => !occupied.Contains(name));
        foreach (var component in components)
            steps.Add($"Keep {component.Name}: {component.Kind}." +
                (component.Architecture != CpuArch.Unknown && component.Architecture != game.Arch ? " Architecture differs from this executable." : ""));
        if (proxy is not null)
            steps.Add($"Use the currently unoccupied {proxy} slot for Phoenix's ReShade host. Verify after installation; coexistence remains untested.");
        else if (candidates.Length > 0)
            steps.Add("Compatible proxy slots are occupied. Review the existing loader's documented chain-loading setup before installation. This planner never renames or replaces files.");
        else steps.Add("This renderer uses a specialized route. The installer preflight must validate its loading plan.");
        if (components.Any(c => c.Kind == "OptiScaler"))
            steps.Add("OptiScaler integration is deferred. The current installer can reject an active conflicting component.");
        steps.Add("Installation rechecks locks, ownership and conflicts before writing. This inventory grants no ownership of existing files.");
        return new(executable, proxy, components.AsReadOnly(), steps.AsReadOnly(), components.Count > 0);
    }
}
