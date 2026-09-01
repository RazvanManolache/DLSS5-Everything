using System.Security.Cryptography;

namespace Dlss5CompatApp;

enum CpuArch
{
    Unknown,
    X86,
    X64
}

enum GraphicsApi
{
    Unknown,
    DirectX7OrOlder,
    DirectX8,
    DirectX9,
    DirectX11,
    DirectX12,
    Dxgi,
    Vulkan,
    OpenGl
}

enum InstallRoute
{
    Unsupported,
    X86Dx9NativeFeeder,
    X86Dx9ViaDgVoodoo,
    X86DxgiFeeder,
    X64DxgiFeeder,
    X64DirectRenoDx
}

sealed record GameCandidate(
    string Root,
    string ExePath,
    string DisplayName,
    string Name,
    CpuArch Arch,
    GraphicsApi Api,
    string Detection,
    long Size)
{
    public string DisplayApi => Api switch
    {
        GraphicsApi.DirectX7OrOlder => "DX7/DirectDraw or older",
        GraphicsApi.DirectX8 => "DirectX 8",
        GraphicsApi.DirectX9 => "DirectX 9",
        GraphicsApi.DirectX11 => "DirectX 11",
        GraphicsApi.DirectX12 => "DirectX 12",
        GraphicsApi.Dxgi => "DXGI",
        GraphicsApi.Vulkan => "Vulkan",
        GraphicsApi.OpenGl => "OpenGL",
        _ => "Unknown"
    };

    public InstallRoute Route => (Arch, Api) switch
    {
        (CpuArch.X86, GraphicsApi.DirectX9) => InstallRoute.X86Dx9NativeFeeder,
        (CpuArch.X86, GraphicsApi.DirectX11) => InstallRoute.X86DxgiFeeder,
        (CpuArch.X86, GraphicsApi.Dxgi) => InstallRoute.X86DxgiFeeder,
        (CpuArch.X64, GraphicsApi.DirectX11) => InstallRoute.X64DxgiFeeder,
        (CpuArch.X64, GraphicsApi.DirectX12) => InstallRoute.X64DirectRenoDx,
        (CpuArch.X64, GraphicsApi.Dxgi) => InstallRoute.X64DxgiFeeder,
        _ => InstallRoute.Unsupported
    };

    public string DisplayRoute => Route switch
    {
        InstallRoute.X86Dx9NativeFeeder => "x86 native DX9 ReShade -> x86 feeder -> host64",
        InstallRoute.X86Dx9ViaDgVoodoo => "x86 DX9 -> dgVoodoo2 -> x86 feeder -> host64",
        InstallRoute.X86DxgiFeeder => "x86 DXGI/D3D11 -> x86 feeder -> host64",
        InstallRoute.X64DxgiFeeder => "x64 DXGI/D3D11 -> x64 feeder -> host64",
        InstallRoute.X64DirectRenoDx => "x64 direct ReShade + RenoDX",
        _ when Api is GraphicsApi.DirectX8 or GraphicsApi.DirectX7OrOlder => "Unsupported: DX8 or older",
        _ => "Unsupported"
    };
}

sealed record PayloadInfo(
    string Root,
    string? ReShadeSetup,
    string? ReShade32Dll,
    string? ReShade64Dll,
    string? RenoDxDlss5Addon,
    string? DgVoodooD3D9,
    string? DgVoodooCpl,
    IReadOnlyList<string> NvidiaDlls,
    IReadOnlyList<string> StreamlineDlls,
    IReadOnlyList<string> ExtraAddons)
{
    public const string KnownGoodDlssNrSha256 = "E16BCF15E16E13F527491CDF7845B2FE6521A738D8F7C9C721866A8496E1FC8E";
    public const string KnownGoodRenoDxAddonSha256 = "245C06137AD13B1CA03AFAAD5100C1E8F0DCE8C11FE50A9272EA562F33CEA601";

    public bool HasReShade32 => ReShade32Dll is not null || ReShadeSetup is not null;
    public bool HasReShade64 => ReShade64Dll is not null || ReShadeSetup is not null;

    public string? DlssVersion => FileVersion(NvidiaDlls.FirstOrDefault(p => Path.GetFileName(p).Equals("nvngx_dlss.dll", StringComparison.OrdinalIgnoreCase)));
    public string? DlssNrVersion => FileVersion(NvidiaDlls.FirstOrDefault(p => Path.GetFileName(p).Equals("nvngx_dlssnr.dll", StringComparison.OrdinalIgnoreCase)));
    public string? DlssNrSha256 => FileSha256(NvidiaDlls.FirstOrDefault(p => Path.GetFileName(p).Equals("nvngx_dlssnr.dll", StringComparison.OrdinalIgnoreCase)));
    public string? RenoDxAddonSha256 => FileSha256(RenoDxDlss5Addon);
    public bool HasKnownGoodDlssNr => DlssNrSha256?.Equals(KnownGoodDlssNrSha256, StringComparison.OrdinalIgnoreCase) == true;
    public bool HasKnownGoodRenoDxAddon => RenoDxAddonSha256?.Equals(KnownGoodRenoDxAddonSha256, StringComparison.OrdinalIgnoreCase) == true;

    public bool HasCoreDlss => NvidiaDlls.Any(p => Path.GetFileName(p).Equals("nvngx_dlss.dll", StringComparison.OrdinalIgnoreCase))
        && NvidiaDlls.Any(p => Path.GetFileName(p).Equals("nvngx_dlssnr.dll", StringComparison.OrdinalIgnoreCase))
        && DlssVersion is not null
        && DlssNrVersion is not null
        && DlssVersion.Equals(DlssNrVersion, StringComparison.OrdinalIgnoreCase)
        && HasKnownGoodDlssNr;

    public bool IsReadyFor(InstallRoute route)
    {
        if (RenoDxDlss5Addon is null || !HasKnownGoodRenoDxAddon || !HasCoreDlss) return false;

        return route switch
        {
            InstallRoute.X86Dx9NativeFeeder => HasReShade32 && HasReShade64,
            InstallRoute.X86Dx9ViaDgVoodoo => HasReShade32 && HasReShade64 && DgVoodooD3D9 is not null,
            InstallRoute.X86DxgiFeeder => HasReShade32 && HasReShade64,
            InstallRoute.X64DxgiFeeder => HasReShade64,
            InstallRoute.X64DirectRenoDx => HasReShade64,
            _ => false
        };
    }

    public string MissingFor(InstallRoute route)
    {
        var missing = new List<string>();
        if (RenoDxDlss5Addon is null) missing.Add("renodx-dlss5.addon64");
        else if (!HasKnownGoodRenoDxAddon) missing.Add("known-good renodx-dlss5.addon64 (0.2026.0828.0517 / 245C0613...)");
        var hasDlss = NvidiaDlls.Any(p => Path.GetFileName(p).Equals("nvngx_dlss.dll", StringComparison.OrdinalIgnoreCase));
        var hasDlssNr = NvidiaDlls.Any(p => Path.GetFileName(p).Equals("nvngx_dlssnr.dll", StringComparison.OrdinalIgnoreCase));
        if (!hasDlss) missing.Add("nvngx_dlss.dll");
        if (!hasDlssNr) missing.Add("nvngx_dlssnr.dll");
        else if (!HasKnownGoodDlssNr) missing.Add("known-good nvngx_dlssnr.dll (310.8.0.0 / E16BCF15...)");
        if (hasDlss && hasDlssNr &&
            DlssVersion is not null && DlssNrVersion is not null &&
            !DlssVersion.Equals(DlssNrVersion, StringComparison.OrdinalIgnoreCase))
            missing.Add($"matching nvngx_dlss.dll/nvngx_dlssnr.dll versions (DLSS {DlssVersion ?? "unknown"}, DLSSNR {DlssNrVersion ?? "unknown"})");
        if (route is InstallRoute.X86Dx9NativeFeeder or InstallRoute.X86Dx9ViaDgVoodoo or InstallRoute.X86DxgiFeeder)
        {
            if (!HasReShade32) missing.Add("ReShade32.dll");
            if (!HasReShade64) missing.Add("ReShade64.dll");
        }
        else if ((route is InstallRoute.X64DxgiFeeder or InstallRoute.X64DirectRenoDx) && !HasReShade64)
        {
            missing.Add("ReShade64.dll");
        }

        if (route == InstallRoute.X86Dx9ViaDgVoodoo && DgVoodooD3D9 is null)
            missing.Add("dgVoodoo2 MS/x86/D3D9.dll");

        return missing.Count == 0 ? "" : string.Join(", ", missing);
    }

    public string Summary
    {
        get
        {
            var parts = new List<string>();
            if (ReShadeSetup is not null)
                parts.Add("ReShade setup found");
            else
            {
                parts.Add(HasReShade32 ? "ReShade32 found" : "ReShade32 missing");
                parts.Add(HasReShade64 ? "ReShade64 found" : "ReShade64 missing");
            }
            parts.Add(RenoDxDlss5Addon is null ? "RenoDX DLSS5 missing" : HasKnownGoodRenoDxAddon ? "RenoDX DLSS5 verified" : "RenoDX DLSS5 wrong build");
            parts.Add(HasCoreDlss
                ? $"DLSS/DLSSNR {DlssVersion} verified"
                : CoreDlssStatus());
            parts.Add(DgVoodooD3D9 is null ? "dgVoodoo2 D3D9 missing" : "dgVoodoo2 D3D9 found");
            if (ExtraAddons.Count > 0) parts.Add($"{ExtraAddons.Count} extra add-on(s)");
            return string.Join("; ", parts);
        }
    }

    string CoreDlssStatus()
    {
        var hasDlss = NvidiaDlls.Any(p => Path.GetFileName(p).Equals("nvngx_dlss.dll", StringComparison.OrdinalIgnoreCase));
        var hasDlssNr = NvidiaDlls.Any(p => Path.GetFileName(p).Equals("nvngx_dlssnr.dll", StringComparison.OrdinalIgnoreCase));
        if (!hasDlss || !hasDlssNr) return "DLSS/DLSSNR missing";
        if (!HasKnownGoodDlssNr) return $"DLSSNR wrong build: {ShortHash(DlssNrSha256)}";
        return $"DLSS/DLSSNR version mismatch: DLSS {DlssVersion ?? "unknown"}, DLSSNR {DlssNrVersion ?? "unknown"}";
    }

    static string? FileVersion(string? path)
    {
        try
        {
            return path is not null && File.Exists(path)
                ? System.Diagnostics.FileVersionInfo.GetVersionInfo(path).FileVersion?.Replace(',', '.')
                : null;
        }
        catch
        {
            return null;
        }
    }

    static string? FileSha256(string? path)
    {
        try
        {
            if (path is null || !File.Exists(path)) return null;
            using var stream = File.OpenRead(path);
            return Convert.ToHexString(SHA256.HashData(stream));
        }
        catch
        {
            return null;
        }
    }

    static string ShortHash(string? hash) =>
        string.IsNullOrWhiteSpace(hash) || hash.Length < 8 ? "unknown" : hash[..8] + "...";
}

sealed class InstallManifest
{
    public int Version { get; set; } = 1;
    public DateTimeOffset Date { get; set; } = DateTimeOffset.Now;
    public string GameRoot { get; set; } = "";
    public string ExeRelativePath { get; set; } = "";
    public string Route { get; set; } = "";
    public List<ManifestFile> Replaced { get; set; } = [];
    public List<string> Added { get; set; } = [];
}

sealed class ManifestFile
{
    public string RelativePath { get; set; } = "";
}
