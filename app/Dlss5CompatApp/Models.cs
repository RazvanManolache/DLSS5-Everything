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
    DirectX8DgVoodoo,
    DirectX9,
    DirectX11,
    DirectX12,
    Dxgi,
    OpenXr,
    OpenVr,
    Vulkan,
    OpenGl,
    Glide211,
    Glide245,
    Glide31,
    Glide31Napalm
}

enum InstallRoute
{
    Unsupported,
    X86DirectXLegacyViaDgVoodoo,
    X86Dx8ViaD3D8To9,
    X86Dx8ViaDgVoodoo,
    X86Dx9NativeFeeder,
    X86Dx9ViaDgVoodoo,
    X86DxgiFeeder,
    X86OpenGlFeeder,
    X86Glide211ViaDgVoodoo,
    X86Glide245ViaDgVoodoo,
    X86Glide31ViaDgVoodoo,
    X86Glide31NapalmViaDgVoodoo,
    X64DxgiFeeder,
    X64OpenXrLayer,
    X64OpenVrFeeder,
    X64OpenGlFeeder,
    X64VulkanFeeder,
    X64NativeThenFeeder,
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
    long Size,
    IReadOnlyList<GraphicsApi>? PossibleApis = null,
    string SuggestedArguments = "",
    IReadOnlyDictionary<GraphicsApi, string>? SuggestedArgumentsByApi = null)
{
    public IReadOnlyList<GraphicsApi> AllApis => PossibleApis is { Count: > 0 } ? PossibleApis : [Api];

    public string DisplayApi => Api switch
    {
        GraphicsApi.DirectX7OrOlder => "DirectX 1-7 / DirectDraw",
        GraphicsApi.DirectX8 => "DirectX 8",
        GraphicsApi.DirectX8DgVoodoo => "DirectX 8 (dgVoodoo2)",
        GraphicsApi.DirectX9 => "DirectX 9",
        GraphicsApi.DirectX11 => "DirectX 11",
        GraphicsApi.DirectX12 => "DirectX 12",
        GraphicsApi.Dxgi => "DXGI",
        GraphicsApi.OpenXr => "OpenXR",
        GraphicsApi.OpenVr => "OpenVR",
        GraphicsApi.Vulkan => "Vulkan",
        GraphicsApi.OpenGl => "OpenGL",
        GraphicsApi.Glide211 => "Glide 2.11",
        GraphicsApi.Glide245 => "Glide 2.45",
        GraphicsApi.Glide31 => "Glide 3.1",
        GraphicsApi.Glide31Napalm => "Glide 3.1 Napalm",
        _ => "Unknown"
    };

    public string DisplayPossibleApis => string.Join(", ", AllApis.Select(DisplayApiName).Distinct(StringComparer.OrdinalIgnoreCase));

    public GameCandidate WithApi(GraphicsApi api)
    {
        var arguments = SuggestedArgumentsByApi is not null && SuggestedArgumentsByApi.TryGetValue(api, out var value)
            ? value
            : api == Api ? SuggestedArguments : "";
        return this with { Api = api, SuggestedArguments = arguments };
    }

    public InstallRoute Route => (Arch, Api) switch
    {
        (CpuArch.X86, GraphicsApi.DirectX7OrOlder) => InstallRoute.X86DirectXLegacyViaDgVoodoo,
        (CpuArch.X86, GraphicsApi.DirectX8) => InstallRoute.X86Dx8ViaD3D8To9,
        (CpuArch.X86, GraphicsApi.DirectX8DgVoodoo) => InstallRoute.X86Dx8ViaDgVoodoo,
        (CpuArch.X86, GraphicsApi.DirectX9) => PreferDgVoodooForX86Dx9 ? InstallRoute.X86Dx9ViaDgVoodoo : InstallRoute.X86Dx9NativeFeeder,
        (CpuArch.X86, GraphicsApi.DirectX11) => InstallRoute.X86DxgiFeeder,
        (CpuArch.X86, GraphicsApi.Dxgi) => InstallRoute.X86DxgiFeeder,
        (CpuArch.X86, GraphicsApi.OpenGl) => InstallRoute.X86OpenGlFeeder,
        (CpuArch.X86, GraphicsApi.Glide211) => InstallRoute.X86Glide211ViaDgVoodoo,
        (CpuArch.X86, GraphicsApi.Glide245) => InstallRoute.X86Glide245ViaDgVoodoo,
        (CpuArch.X86, GraphicsApi.Glide31) => InstallRoute.X86Glide31ViaDgVoodoo,
        (CpuArch.X86, GraphicsApi.Glide31Napalm) => InstallRoute.X86Glide31NapalmViaDgVoodoo,
        (CpuArch.X64, GraphicsApi.DirectX11) => InstallRoute.X64DxgiFeeder,
        (CpuArch.X64, GraphicsApi.DirectX12) => InstallRoute.X64NativeThenFeeder,
        (CpuArch.X64, GraphicsApi.Dxgi) => InstallRoute.X64DxgiFeeder,
        (CpuArch.X64, GraphicsApi.OpenXr) => InstallRoute.X64OpenXrLayer,
        (CpuArch.X64, GraphicsApi.OpenVr) => InstallRoute.X64OpenVrFeeder,
        (CpuArch.X64, GraphicsApi.OpenGl) => InstallRoute.X64OpenGlFeeder,
        (CpuArch.X64, GraphicsApi.Vulkan) => InstallRoute.X64VulkanFeeder,
        _ => InstallRoute.Unsupported
    };

    public string DisplayRoute => Route switch
    {
        InstallRoute.X86DirectXLegacyViaDgVoodoo => "x86 DX1-7/DirectDraw -> dgVoodoo2 -> x86 feeder -> host64",
        InstallRoute.X86Dx8ViaD3D8To9 => "x86 DX8 -> d3d8to9 -> native DX9 ReShade -> x86 feeder -> host64",
        InstallRoute.X86Dx8ViaDgVoodoo => "x86 DX8 -> dgVoodoo2 -> x86 feeder -> host64",
        InstallRoute.X86Dx9NativeFeeder => "x86 native DX9 ReShade -> x86 feeder -> host64",
        InstallRoute.X86Dx9ViaDgVoodoo => "x86 DX9 -> dgVoodoo2 -> x86 feeder -> host64",
        InstallRoute.X86DxgiFeeder => "x86 DXGI/D3D11 -> x86 feeder -> host64",
        InstallRoute.X86OpenGlFeeder => "x86 OpenGL -> x86 feeder CPU bridge -> host64",
        InstallRoute.X86Glide211ViaDgVoodoo => "x86 Glide 2.11 -> dgVoodoo2 -> x86 feeder -> host64",
        InstallRoute.X86Glide245ViaDgVoodoo => "x86 Glide 2.45 -> dgVoodoo2 -> x86 feeder -> host64",
        InstallRoute.X86Glide31ViaDgVoodoo => "x86 Glide 3.1 -> dgVoodoo2 -> x86 feeder -> host64",
        InstallRoute.X86Glide31NapalmViaDgVoodoo => "x86 Glide 3.1 Napalm -> dgVoodoo2 -> x86 feeder -> host64",
        InstallRoute.X64DxgiFeeder => "x64 DXGI/D3D11 -> x64 feeder -> host64",
        InstallRoute.X64OpenXrLayer => "x64 OpenXR -> OpenXR API layer -> host64",
        InstallRoute.X64OpenVrFeeder => "x64 OpenVR -> OpenVR shim -> x64 feeder -> host64",
        InstallRoute.X64OpenGlFeeder => "x64 OpenGL -> x64 feeder CPU bridge -> host64",
        InstallRoute.X64VulkanFeeder => "x64 Vulkan -> ReShade Vulkan layer -> DLSS5 Bridge + RenoDX",
        InstallRoute.X64NativeThenFeeder => "x64 native RenoDX -> feeder fallback",
        InstallRoute.X64DirectRenoDx => "x64 direct ReShade + RenoDX",
        _ when Api is GraphicsApi.Vulkan => "Unsupported: x86 Vulkan feeder backend not implemented yet",
        _ when Api is GraphicsApi.DirectX7OrOlder => "Unsupported: x64 DirectX 1-7 / DirectDraw is not handled",
        _ when Api is GraphicsApi.DirectX8 => "Unsupported: x64 DX8 is not handled",
        _ => "Unsupported"
    };

    bool PreferDgVoodooForX86Dx9
    {
        get
        {
            if (File.Exists(Path.Combine(Root, "dgVoodoo.conf")))
                return true;

            var backup = Path.Combine(Root, "_DLSS5_Compat_Backup");
            return Directory.Exists(backup) &&
                   Directory.EnumerateFiles(backup, "manifest.json*", SearchOption.TopDirectoryOnly)
                       .Any(ManifestMentionsDgVoodoo);
        }
    }

    static bool ManifestMentionsDgVoodoo(string path)
    {
        try
        {
            return File.ReadAllText(path).Contains("X86Dx9ViaDgVoodoo", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    static string DisplayApiName(GraphicsApi api) => api switch
    {
        GraphicsApi.DirectX7OrOlder => "DirectX 1-7 / DirectDraw",
        GraphicsApi.DirectX8 => "DirectX 8",
        GraphicsApi.DirectX8DgVoodoo => "DirectX 8 (dgVoodoo2)",
        GraphicsApi.DirectX9 => "DirectX 9",
        GraphicsApi.DirectX11 => "DirectX 11",
        GraphicsApi.DirectX12 => "DirectX 12",
        GraphicsApi.Dxgi => "DXGI",
        GraphicsApi.OpenXr => "OpenXR",
        GraphicsApi.OpenVr => "OpenVR",
        GraphicsApi.Vulkan => "Vulkan",
        GraphicsApi.OpenGl => "OpenGL",
        GraphicsApi.Glide211 => "Glide 2.11",
        GraphicsApi.Glide245 => "Glide 2.45",
        GraphicsApi.Glide31 => "Glide 3.1",
        GraphicsApi.Glide31Napalm => "Glide 3.1 Napalm",
        _ => "Unknown"
    };
}

sealed record PayloadInfo(
    string Root,
    string? ReShadeSetup,
    string? ReShade32Dll,
    string? ReShade64Dll,
    string? RenoDxDlss5Addon,
    string? Dlss5BridgeAddon,
    string? D3D8To9D3D8,
    string? DgVoodooD3D8,
    string? DgVoodooD3D9,
    string? DgVoodooD3DImm,
    string? DgVoodooDDraw,
    string? DgVoodooGlide,
    string? DgVoodooGlide2x,
    string? DgVoodooGlide3x,
    string? DgVoodooGlide3xNapalm,
    string? DgVoodooCpl,
    string? DosBoxExe,
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
            InstallRoute.X86DirectXLegacyViaDgVoodoo => HasReShade32 && HasReShade64 && DgVoodooDDraw is not null && DgVoodooD3DImm is not null,
            InstallRoute.X86Dx8ViaD3D8To9 => HasReShade32 && HasReShade64 && D3D8To9D3D8 is not null,
            InstallRoute.X86Dx8ViaDgVoodoo => HasReShade32 && HasReShade64 && DgVoodooD3D8 is not null,
            InstallRoute.X86Dx9NativeFeeder => HasReShade32 && HasReShade64,
            InstallRoute.X86Dx9ViaDgVoodoo => HasReShade32 && HasReShade64 && DgVoodooD3D9 is not null,
            InstallRoute.X86DxgiFeeder => HasReShade32 && HasReShade64,
            InstallRoute.X86OpenGlFeeder => HasReShade32 && HasReShade64,
            InstallRoute.X86Glide211ViaDgVoodoo => HasReShade32 && HasReShade64 && DgVoodooGlide is not null,
            InstallRoute.X86Glide245ViaDgVoodoo => HasReShade32 && HasReShade64 && DgVoodooGlide2x is not null,
            InstallRoute.X86Glide31ViaDgVoodoo => HasReShade32 && HasReShade64 && DgVoodooGlide3x is not null,
            InstallRoute.X86Glide31NapalmViaDgVoodoo => HasReShade32 && HasReShade64 && DgVoodooGlide3xNapalm is not null,
            InstallRoute.X64DxgiFeeder => HasReShade64,
            InstallRoute.X64OpenXrLayer => HasReShade64,
            InstallRoute.X64OpenVrFeeder => HasReShade64,
            InstallRoute.X64OpenGlFeeder => HasReShade64,
            InstallRoute.X64VulkanFeeder => ReShadeSetup is not null && HasReShade64 && Dlss5BridgeAddon is not null,
            InstallRoute.X64NativeThenFeeder => HasReShade64,
            InstallRoute.X64DirectRenoDx => HasReShade64,
            _ => false
        };
    }

    public string MissingFor(InstallRoute route)
    {
        var missing = new List<string>();
        if (RenoDxDlss5Addon is null) missing.Add("renodx-dlss5.addon64");
        else if (!HasKnownGoodRenoDxAddon) missing.Add("known-good renodx-dlss5.addon64 (0.2026.0828.0517 / 245C0613...)");
        if (route == InstallRoute.X64VulkanFeeder && Dlss5BridgeAddon is null)
            missing.Add("dlss5-bridge.addon64");
        var hasDlss = NvidiaDlls.Any(p => Path.GetFileName(p).Equals("nvngx_dlss.dll", StringComparison.OrdinalIgnoreCase));
        var hasDlssNr = NvidiaDlls.Any(p => Path.GetFileName(p).Equals("nvngx_dlssnr.dll", StringComparison.OrdinalIgnoreCase));
        if (!hasDlss) missing.Add("nvngx_dlss.dll");
        if (!hasDlssNr) missing.Add("nvngx_dlssnr.dll");
        else if (!HasKnownGoodDlssNr) missing.Add("known-good nvngx_dlssnr.dll (310.8.0.0 / E16BCF15...)");
        if (hasDlss && hasDlssNr &&
            DlssVersion is not null && DlssNrVersion is not null &&
            !DlssVersion.Equals(DlssNrVersion, StringComparison.OrdinalIgnoreCase))
            missing.Add($"matching nvngx_dlss.dll/nvngx_dlssnr.dll versions (DLSS {DlssVersion ?? "unknown"}, DLSSNR {DlssNrVersion ?? "unknown"})");
        if (route is InstallRoute.X86DirectXLegacyViaDgVoodoo or InstallRoute.X86Dx8ViaD3D8To9 or InstallRoute.X86Dx8ViaDgVoodoo or InstallRoute.X86Dx9NativeFeeder or InstallRoute.X86Dx9ViaDgVoodoo or InstallRoute.X86DxgiFeeder or InstallRoute.X86OpenGlFeeder or InstallRoute.X86Glide211ViaDgVoodoo or InstallRoute.X86Glide245ViaDgVoodoo or InstallRoute.X86Glide31ViaDgVoodoo or InstallRoute.X86Glide31NapalmViaDgVoodoo)
        {
            if (!HasReShade32) missing.Add("ReShade32.dll");
            if (!HasReShade64) missing.Add("ReShade64.dll");
        }
        else if ((route is InstallRoute.X64DxgiFeeder or InstallRoute.X64OpenXrLayer or InstallRoute.X64OpenVrFeeder or InstallRoute.X64OpenGlFeeder or InstallRoute.X64VulkanFeeder or InstallRoute.X64NativeThenFeeder or InstallRoute.X64DirectRenoDx) && !HasReShade64)
        {
            missing.Add("ReShade64.dll");
        }
        if (route == InstallRoute.X64VulkanFeeder && ReShadeSetup is null)
            missing.Add("ReShade_Setup_*_Addon.exe for Vulkan layer setup");

        if (route == InstallRoute.X86Dx9ViaDgVoodoo && DgVoodooD3D9 is null)
            missing.Add("dgVoodoo2 MS/x86/D3D9.dll");
        if (route == InstallRoute.X86Dx8ViaDgVoodoo && DgVoodooD3D8 is null)
            missing.Add("dgVoodoo2 MS/x86/D3D8.dll");
        if (route == InstallRoute.X86DirectXLegacyViaDgVoodoo)
        {
            if (DgVoodooDDraw is null) missing.Add("dgVoodoo2 MS/x86/DDraw.dll");
            if (DgVoodooD3DImm is null) missing.Add("dgVoodoo2 MS/x86/D3DImm.dll");
        }
        if (route == InstallRoute.X86Glide211ViaDgVoodoo && DgVoodooGlide is null)
            missing.Add("dgVoodoo2 3Dfx/x86/Glide.dll");
        if (route == InstallRoute.X86Glide245ViaDgVoodoo && DgVoodooGlide2x is null)
            missing.Add("dgVoodoo2 3Dfx/x86/Glide2x.dll");
        if (route == InstallRoute.X86Glide31ViaDgVoodoo && DgVoodooGlide3x is null)
            missing.Add("dgVoodoo2 3Dfx/x86/Glide3x.dll");
        if (route == InstallRoute.X86Glide31NapalmViaDgVoodoo && DgVoodooGlide3xNapalm is null)
            missing.Add("dgVoodoo2 3Dfx/x86/Napalm/Glide3x.dll");
        if (route == InstallRoute.X86Dx8ViaD3D8To9 && D3D8To9D3D8 is null)
            missing.Add("d3d8to9 d3d8.dll");

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
            parts.Add(Dlss5BridgeAddon is null ? "DLSS5 Bridge missing" : "DLSS5 Bridge found");
            parts.Add(HasCoreDlss
                ? $"DLSS/DLSSNR {DlssVersion} verified"
                : CoreDlssStatus());
            parts.Add(D3D8To9D3D8 is null ? "d3d8to9 missing" : "d3d8to9 found");
            parts.Add(HasDgVoodooDirectX ? "dgVoodoo2 DirectX found" : "dgVoodoo2 DirectX incomplete");
            parts.Add(HasDgVoodooGlide ? "dgVoodoo2 Glide found" : "dgVoodoo2 Glide incomplete");
            parts.Add(DosBoxExe is null ? "DOSBox Staging missing" : "DOSBox Staging found");
            if (ExtraAddons.Count > 0) parts.Add($"{ExtraAddons.Count} extra add-on(s)");
            return string.Join("; ", parts);
        }
    }

    bool HasDgVoodooDirectX => DgVoodooD3D8 is not null && DgVoodooD3D9 is not null && DgVoodooD3DImm is not null && DgVoodooDDraw is not null;
    bool HasDgVoodooGlide => DgVoodooGlide is not null && DgVoodooGlide2x is not null && DgVoodooGlide3x is not null && DgVoodooGlide3xNapalm is not null;

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
