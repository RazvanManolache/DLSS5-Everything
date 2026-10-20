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
    Glide31Napalm,
    // Append values: existing profiles may serialize enum values numerically.
    DirectX10,
    DirectX10_1
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
    X64DirectPhoenix,
    X64Dx9NativeFeeder,
    X86PackagedBridge
}

enum GameRoutePreference { Native, Feeder }

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
    IReadOnlyDictionary<GraphicsApi, string>? SuggestedArgumentsByApi = null,
    bool HasNativeDlssSignal = false)
{
    public GameRoutePreference? PreferredRoute { get; init; }
    public string? ProxyName { get; init; }
    // Keep measured defaults distinct from editable overrides. Returning to
    // Automatic must discard a manual override while retaining runtime evidence.
    public GameRoutePreference? VerifiedInputRoute { get; init; }
    public string? VerifiedProxyName { get; init; }
    public IReadOnlyList<GraphicsApi> DetectedInputApis { get; init; } = [];
    public IReadOnlyList<string> TranslatorKinds { get; init; } = [];
    public bool RuntimeSelectionRequired { get; init; }
    public bool HasVerifiedRenderer { get; init; }
    public IReadOnlyList<string> CompatibilityWarnings { get; init; } = [];

    public static string? OverrideValueValidationError(GameRoutePreference? preferredRoute, string? proxyName)
    {
        if (preferredRoute is { } route && !Enum.IsDefined(route))
            return "Invalid route override. Use null for automatic selection.";
        if (proxyName is not null && !proxyName.Equals("dxgi.dll", StringComparison.OrdinalIgnoreCase) &&
            !proxyName.Equals("d3d11.dll", StringComparison.OrdinalIgnoreCase))
            return "Proxy override must be dxgi.dll or d3d11.dll. Use null for automatic selection.";
        return null;
    }

    public string? OverrideValidationError
    {
        get
        {
            if (OverrideValueValidationError(PreferredRoute, ProxyName) is { } error) return error;
            if (PreferredRoute == GameRoutePreference.Native && (Arch != CpuArch.X64 || Api != GraphicsApi.DirectX12))
                return "Native Phoenix DLSS5 requires an x64 DirectX 12 executable.";
            if (PreferredRoute == GameRoutePreference.Feeder && AutomaticRoute == InstallRoute.Unsupported)
                return "No feeder route supports this executable/API combination.";
            if (ProxyName is not null)
            {
                if (Arch is not (CpuArch.X86 or CpuArch.X64) ||
                    Api is not (GraphicsApi.DirectX10 or GraphicsApi.DirectX10_1 or GraphicsApi.DirectX11 or GraphicsApi.DirectX12 or GraphicsApi.Dxgi) ||
                    AutomaticRoute == InstallRoute.Unsupported)
                    return "A DXGI proxy override requires a supported DirectX 11, DirectX 12 or DXGI route.";
                if (ProxyName.Equals("d3d11.dll", StringComparison.OrdinalIgnoreCase) && Api != GraphicsApi.DirectX11)
                    return "The d3d11.dll proxy requires DirectX 11. Select dxgi.dll for DirectX 12 or unspecified DXGI.";
            }
            return null;
        }
    }

    public void ValidateOverrides()
    {
        if (OverrideValidationError is { } error) throw new InvalidOperationException(error);
        if (Route == InstallRoute.Unsupported) throw new InvalidOperationException("This executable/API combination is not supported.");
    }

    public IReadOnlyList<GraphicsApi> AllApis => PossibleApis is { Count: > 0 } ? PossibleApis : [Api];

    public string DisplayApi => Api switch
    {
        GraphicsApi.DirectX7OrOlder => "DirectX 1-7 / DirectDraw",
        GraphicsApi.DirectX8 => "DirectX 8",
        GraphicsApi.DirectX8DgVoodoo => "DirectX 8 (dgVoodoo2)",
        GraphicsApi.DirectX9 => "DirectX 9",
        GraphicsApi.DirectX10 => "DirectX 10",
        GraphicsApi.DirectX10_1 => "DirectX 10.1",
        GraphicsApi.DirectX11 => "DirectX 11",
        GraphicsApi.DirectX12 => "DirectX 12",
        GraphicsApi.Dxgi when TranslatorKinds.Contains("ANGLE") => "ANGLE / DXGI (runtime backend)",
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

    public InstallRoute Route => OverrideValidationError is not null ? InstallRoute.Unsupported : PreferredRoute switch
    {
        GameRoutePreference.Native => InstallRoute.X64DirectPhoenix,
        GameRoutePreference.Feeder when AutomaticRoute == InstallRoute.X64DirectPhoenix => InstallRoute.X64DxgiFeeder,
        _ => AutomaticRoute
    };

    InstallRoute AutomaticRoute => (Arch, Api) switch
    {
        (CpuArch.X86, GraphicsApi.DirectX7OrOlder) => InstallRoute.X86DirectXLegacyViaDgVoodoo,
        (CpuArch.X86, GraphicsApi.DirectX8) => InstallRoute.X86Dx8ViaD3D8To9,
        (CpuArch.X86, GraphicsApi.DirectX8DgVoodoo) => InstallRoute.X86Dx8ViaDgVoodoo,
        (CpuArch.X86, GraphicsApi.DirectX9) => PreferDgVoodooForX86Dx9 ? InstallRoute.X86Dx9ViaDgVoodoo : InstallRoute.X86Dx9NativeFeeder,
        (CpuArch.X86, GraphicsApi.DirectX11) => InstallRoute.X86DxgiFeeder,
        (CpuArch.X86, GraphicsApi.DirectX10 or GraphicsApi.DirectX10_1) => InstallRoute.X86DxgiFeeder,
        (CpuArch.X86, GraphicsApi.Dxgi) => InstallRoute.X86DxgiFeeder,
        (CpuArch.X86, GraphicsApi.OpenGl) => InstallRoute.X86OpenGlFeeder,
        (CpuArch.X86, GraphicsApi.Glide211) => InstallRoute.X86Glide211ViaDgVoodoo,
        (CpuArch.X86, GraphicsApi.Glide245) => InstallRoute.X86Glide245ViaDgVoodoo,
        (CpuArch.X86, GraphicsApi.Glide31) => InstallRoute.X86Glide31ViaDgVoodoo,
        (CpuArch.X86, GraphicsApi.Glide31Napalm) => InstallRoute.X86Glide31NapalmViaDgVoodoo,
        (CpuArch.X64, GraphicsApi.DirectX9) => InstallRoute.X64Dx9NativeFeeder,
        (CpuArch.X64, GraphicsApi.DirectX11) => InstallRoute.X64DxgiFeeder,
        (CpuArch.X64, GraphicsApi.DirectX10 or GraphicsApi.DirectX10_1) => InstallRoute.X64DxgiFeeder,
        (CpuArch.X64, GraphicsApi.DirectX12) => HasNativeDlssSignal ? InstallRoute.X64DirectPhoenix : InstallRoute.X64DxgiFeeder,
        (CpuArch.X64, GraphicsApi.Dxgi) => InstallRoute.X64DxgiFeeder,
        (CpuArch.X64, GraphicsApi.OpenXr) => InstallRoute.X64OpenXrLayer,
        (CpuArch.X64, GraphicsApi.OpenVr) => InstallRoute.X64OpenVrFeeder,
        (CpuArch.X64, GraphicsApi.OpenGl) => InstallRoute.X64OpenGlFeeder,
        (CpuArch.X64, GraphicsApi.Vulkan) => InstallRoute.X64VulkanFeeder,
        (CpuArch.X86, GraphicsApi.Vulkan) => InstallRoute.X86PackagedBridge,
        (CpuArch.X86, GraphicsApi.DirectX12) => InstallRoute.X86PackagedBridge,
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
        InstallRoute.X64Dx9NativeFeeder => "x64 native DX9 ReShade -> x64 feeder SDR CPU bridge -> Phoenix DLSS5 host64",
        InstallRoute.X64OpenXrLayer => "x64 OpenXR -> OpenXR API layer -> host64",
        InstallRoute.X64OpenVrFeeder => "x64 OpenVR -> OpenVR shim -> x64 feeder -> host64",
        InstallRoute.X64OpenGlFeeder => "x64 OpenGL -> x64 feeder CPU bridge -> host64",
        InstallRoute.X64VulkanFeeder => "x64 Vulkan -> ReShade Vulkan layer -> Vulkan CPU bridge -> Phoenix DLSS5 host64",
        InstallRoute.X86PackagedBridge => "x86 " + DisplayApi + " -> Phoenix DLSS5 packaged graphics bridge",
        InstallRoute.X64NativeThenFeeder => "x64 D3D12 -> feeder -> Phoenix DLSS5 host64",
        InstallRoute.X64DirectPhoenix => "x64 native D3D12 NGX SR/RR -> Phoenix DLSS5",
        _ when OverrideValidationError is { } error => "Unsupported: " + error,
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
        GraphicsApi.DirectX10 => "DirectX 10",
        GraphicsApi.DirectX10_1 => "DirectX 10.1",
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
    string? PhoenixAddon,
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
    string? DosBoxEnhancedExe,
    IReadOnlyList<string> NvidiaDlls,
    IReadOnlyList<string> StreamlineDlls,
    IReadOnlyList<string> ExtraAddons)
{
    public const string KnownGoodDlssNrSha256 = "8270B350CD82DE5CE89806872CDD6B6A9249B80836B91BBEB3573470744CC206";
    public const string LegacyKnownGoodDlssNrSha256 = "E16BCF15E16E13F527491CDF7845B2FE6521A738D8F7C9C721866A8496E1FC8E";

    public bool HasReShade32 => ReShade32Dll is not null;
    public string? PhoenixAddon32 { get; init; }
    public bool HasReShade64 => ReShade64Dll is not null;

    public string? DlssVersion => FileVersion(NvidiaDlls.FirstOrDefault(p => Path.GetFileName(p).Equals("nvngx_dlss.dll", StringComparison.OrdinalIgnoreCase)));
    public string? DlssNrVersion => FileVersion(NvidiaDlls.FirstOrDefault(p => Path.GetFileName(p).Equals("nvngx_dlssnr.dll", StringComparison.OrdinalIgnoreCase)));
    public string? DlssNrSha256 => FileSha256(NvidiaDlls.FirstOrDefault(p => Path.GetFileName(p).Equals("nvngx_dlssnr.dll", StringComparison.OrdinalIgnoreCase)));
    public string? PhoenixAddonSha256 => FileSha256(PhoenixAddon);
    public bool HasPhoenixAddon => PayloadScanner.IsPhoenixAddon(PhoenixAddon);
    public bool HasPhoenixFeederApi => PayloadScanner.HasPhoenixFeederApi(PhoenixAddon);
    public bool HasEmbeddedPhoenixRuntime => PayloadScanner.HasEmbeddedPhoenixRuntime(PhoenixAddon);
    public bool HasKnownGoodDlssNr => DlssNrSha256 is not null &&
        (DlssNrSha256.Equals(KnownGoodDlssNrSha256, StringComparison.OrdinalIgnoreCase) ||
         DlssNrSha256.Equals(LegacyKnownGoodDlssNrSha256, StringComparison.OrdinalIgnoreCase));

    public bool HasCoreDlss => new[] { "nvngx_dlssnr.dll" }.All(name =>
        NvidiaDlls.Any(p => Path.GetFileName(p).Equals(name, StringComparison.OrdinalIgnoreCase) && PayloadScanner.IsX64Dll(p)));
    public bool HasPhoenixModel => HasCoreDlss || OptionalModelLibrary.Installed(OptionalModelLibrary.Root(Root)).Count > 0;

    public bool IsReadyFor(GameCandidate game) => string.IsNullOrEmpty(MissingFor(game));

    public string MissingFor(GameCandidate game)
    {
        if (PackagedPhoenixPlan.For(game, this) is not { } plan) return MissingFor(game.Route);
        var missing = new List<string>();
        if (!HasPhoenixModel) missing.Add("NVIDIA DLSS5 NR model DLL or an installed native model export");
        if (!HasEmbeddedPhoenixRuntime) missing.Add("embedded Phoenix DLSS5 C++ runtime");
        foreach (var file in plan.TranslationFiles(this))
            if (file.Source is null) missing.Add("API translator " + file.Target);
        if (game.Arch == CpuArch.X86 ? !HasReShade32 : !HasReShade64)
            missing.Add(game.Arch == CpuArch.X86 ? "ReShade32.dll" : "ReShade64.dll");
        return string.Join(", ", missing);
    }

    public bool IsReadyFor(InstallRoute route)
    {
        if (!HasPhoenixAddon || !HasPhoenixModel) return false;
        if (route != InstallRoute.X64DirectPhoenix && !HasPhoenixFeederApi) return false;

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
            InstallRoute.X64Dx9NativeFeeder => HasReShade64,
            InstallRoute.X64OpenXrLayer => HasReShade64,
            InstallRoute.X64OpenVrFeeder => HasReShade64,
            InstallRoute.X64OpenGlFeeder => HasReShade64,
            InstallRoute.X64VulkanFeeder => HasReShade64,
            InstallRoute.X64NativeThenFeeder => HasReShade64,
            InstallRoute.X64DirectPhoenix => HasReShade64,
            InstallRoute.X86PackagedBridge => HasReShade32 && PayloadScanner.HasPackagedPhoenixBridge(PhoenixAddon32, CpuArch.X86),
            _ => false
        };
    }

    public string MissingFor(InstallRoute route)
    {
        var missing = new List<string>();
        if (route == InstallRoute.X86PackagedBridge)
        {
            if (!HasReShade32) missing.Add("ReShade32.dll");
            if (!PayloadScanner.HasPackagedPhoenixBridge(PhoenixAddon32, CpuArch.X86))
                missing.Add("Phoenix.addon32 packaged graphics bridge");
        }
        if (!HasPhoenixAddon) missing.Add("Phoenix.addon64 (valid x64 PE DLL with Phoenix DLSS5 exports; no Python imports)");
        else if (route != InstallRoute.X64DirectPhoenix && !HasPhoenixFeederApi) missing.Add(PayloadScanner.FeederApiRemediation);
        foreach (var name in new[] { "nvngx_dlssnr.dll" })
            if (!HasPhoenixModel)
                missing.Add(name + " (valid x64 PE DLL)");
        if (route is InstallRoute.X86DirectXLegacyViaDgVoodoo or InstallRoute.X86Dx8ViaD3D8To9 or InstallRoute.X86Dx8ViaDgVoodoo or InstallRoute.X86Dx9NativeFeeder or InstallRoute.X86Dx9ViaDgVoodoo or InstallRoute.X86DxgiFeeder or InstallRoute.X86OpenGlFeeder or InstallRoute.X86Glide211ViaDgVoodoo or InstallRoute.X86Glide245ViaDgVoodoo or InstallRoute.X86Glide31ViaDgVoodoo or InstallRoute.X86Glide31NapalmViaDgVoodoo)
        {
            if (!HasReShade32) missing.Add("ReShade32.dll");
            if (!HasReShade64) missing.Add("ReShade64.dll");
        }
        else if ((route is InstallRoute.X64DxgiFeeder or InstallRoute.X64Dx9NativeFeeder or InstallRoute.X64OpenXrLayer or InstallRoute.X64OpenVrFeeder or InstallRoute.X64OpenGlFeeder or InstallRoute.X64VulkanFeeder or InstallRoute.X64NativeThenFeeder or InstallRoute.X64DirectPhoenix) && !HasReShade64)
        {
            missing.Add("ReShade64.dll");
        }

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
            parts.Add(HasPhoenixAddon ? "Phoenix DLSS5 x64 PE found (inference unverified)" : "Phoenix DLSS5 x64 add-on missing or invalid");
            if (HasPhoenixAddon)
            {
                parts.Add(HasPhoenixFeederApi ? "Direct feeder API found" : "Incompatible: direct feeder API missing");
                parts.Add(HasEmbeddedPhoenixRuntime ? "Embedded C++ runtime found" : "Embedded C++ runtime missing");
            }
            parts.Add(HasCoreDlss
                ? $"DLSS/DLSSNR x64 PE found"
                : HasPhoenixModel ? "Shared native model export found" : CoreDlssStatus());
            parts.Add(D3D8To9D3D8 is null ? "d3d8to9 missing" : "d3d8to9 found");
            parts.Add(HasDgVoodooDirectX ? "dgVoodoo2 DirectX found" : "dgVoodoo2 DirectX incomplete");
            parts.Add(HasDgVoodooGlide ? "dgVoodoo2 Glide found" : "dgVoodoo2 Glide incomplete");
            parts.Add(DosBoxEnhancedExe is null ? "DOSBox Enhanced missing" : "DOSBox Enhanced found");
            if (ExtraAddons.Count > 0) parts.Add($"{ExtraAddons.Count} extra add-on(s)");
            return string.Join("; ", parts);
        }
    }

    bool HasDgVoodooDirectX => DgVoodooD3D8 is not null && DgVoodooD3D9 is not null && DgVoodooD3DImm is not null && DgVoodooDDraw is not null;
    bool HasDgVoodooGlide => DgVoodooGlide is not null && DgVoodooGlide2x is not null && DgVoodooGlide3x is not null && DgVoodooGlide3xNapalm is not null;

    string CoreDlssStatus() => "DLSS/DLSSNR x64 PE missing or invalid";

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
    public int Version { get; set; } = 2;
    public string TransactionId { get; set; } = Guid.NewGuid().ToString("N");
    public string Operation { get; set; } = "install";
    public string BackupHashEvidence { get; set; } = "captured-before-mutation";
    public DateTimeOffset Date { get; set; } = DateTimeOffset.Now;
    public string GameRoot { get; set; } = "";
    public string ExeRelativePath { get; set; } = "";
    public string Route { get; set; } = "";
    // Preserve the actual install choice; later UI overrides must not change what Verify checks.
    public GraphicsApi? Api { get; set; }
    public GameRoutePreference? PreferredRoute { get; set; }
    public string? ProxyName { get; set; }
    public string? LaunchArguments { get; set; }
    public List<ManifestFile> Replaced { get; set; } = [];
    public List<string> Added { get; set; } = [];
    public List<string> AddedDirectories { get; set; } = [];
    public List<InstallFileFailure> FileFailures { get; set; } = [];
    // Present only when this game transaction added a shared layer reference.
    public bool? VulkanLayerX86 { get; set; }
    public Dictionary<string, string> VulkanLaunchEnvironment { get; set; } = [];
}

sealed class ManifestFile
{
    public string RelativePath { get; set; } = "";
    public string BackupRelativePath { get; set; } = "";
    public string Sha256 { get; set; } = "";
}
