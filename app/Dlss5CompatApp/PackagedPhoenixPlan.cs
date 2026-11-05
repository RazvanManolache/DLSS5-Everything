namespace Dlss5CompatApp;

/// <summary>Architecture/API rules for the self-contained Phoenix graphics bridge.
/// Older payloads retain their explicit feeder route; no executable/title allowlist is used.</summary>
sealed record PackagedPhoenixPlan(string Addon, string Proxy, bool NativeGameInput, InstallRoute Translation = InstallRoute.Unsupported)
{
    public const string DgVoodooD3D9Library = "PhoenixDgVoodooD3D9.dll";
    public bool UsesDgVoodooD3D9ProxyChain { get; init; }
    public bool SoftwareDeviceSupport { get; init; }
    public string? ReShadeOverride { get; init; }
    public bool UsesImportedD3D9Bootstrap { get; init; }
    public IReadOnlyList<InstallRoute> CompanionTranslations { get; init; } = [];
    public string InputProvider => NativeGameInput ? PhoenixConfiguration.GameInputProvider : PhoenixConfiguration.FeederInputProvider;
    public bool SupportsInProcessBackbuffer => (PayloadScanner.IsPeDll(Addon, CpuArch.X64) ||
        PayloadScanner.IsPeDll(Addon, CpuArch.X86)) &&
        PayloadScanner.HasExports(Addon, "CnrPHOENIXInProcessBackbufferVersion");
    public bool SupportsNativeBackbuffer(GameCandidate game) => game.Arch == CpuArch.X64 &&
        PayloadScanner.IsPeDll(Addon, CpuArch.X64) &&
        (game.Api == GraphicsApi.DirectX12 && SupportsInProcessBackbuffer ||
         (game.Api is GraphicsApi.DirectX9 or GraphicsApi.DirectX10 or GraphicsApi.DirectX10_1 or GraphicsApi.DirectX11 or GraphicsApi.Dxgi or GraphicsApi.OpenGl or GraphicsApi.Vulkan ||
          Translation == InstallRoute.X64DirectXLegacyViaDgVoodoo) &&
         PayloadScanner.HasExports(Addon, "CnrPHOENIXInProcessGraphicsV2"));
    public bool RequiresHelperDefault(PayloadInfo payload, GameCandidate game, PhoenixSettings settings) =>
        !SupportsInProcessBackbuffer && settings.CompatibilityExecutionMode == "auto" &&
        !PhoenixRuntimePackage.HasCompatibleOptionalRuntime(game, payload, settings);
    public IReadOnlyList<(string? Source, string Target)> TranslationFiles(PayloadInfo payload) =>
        new[] { Translation }.Concat(CompanionTranslations)
            .SelectMany(route => FilesForTranslation(payload, route))
            .Select(file => (file.Source, Target: UsesDgVoodooD3D9ProxyChain &&
                file.Target.Equals("D3D9.dll", StringComparison.OrdinalIgnoreCase) ? DgVoodooD3D9Library : file.Target))
            .DistinctBy(file => file.Target, StringComparer.OrdinalIgnoreCase).ToArray();

    static IReadOnlyList<(string? Source, string Target)> FilesForTranslation(PayloadInfo payload, InstallRoute translation) => translation switch
    {
        InstallRoute.X86DirectXLegacyViaDgVoodoo => [(payload.DgVoodooDDraw, "DDraw.dll"), (payload.DgVoodooD3DImm, "D3DImm.dll")],
        InstallRoute.X64DirectXLegacyViaDgVoodoo => [(payload.DgVoodooDDraw64, "DDraw.dll"), (payload.DgVoodooD3DImm64, "D3DImm.dll")],
        InstallRoute.X86Dx8ViaD3D8To9 => [(payload.D3D8To9D3D8, "D3D8.dll")],
        InstallRoute.X86Dx8ViaDgVoodoo => [(payload.DgVoodooD3D8, "D3D8.dll")],
        InstallRoute.X86Dx9ViaDgVoodoo => [(payload.DgVoodooD3D9, "D3D9.dll")],
        InstallRoute.X86Glide211ViaDgVoodoo => [(payload.DgVoodooGlide, "Glide.dll")],
        InstallRoute.X86Glide245ViaDgVoodoo => [(payload.DgVoodooGlide2x, "Glide2x.dll")],
        InstallRoute.X86Glide31ViaDgVoodoo => [(payload.DgVoodooGlide3x, "Glide3x.dll")],
        InstallRoute.X86Glide31NapalmViaDgVoodoo => [(payload.DgVoodooGlide3xNapalm, "Glide3x.dll")],
        _ => []
    };

    internal static IReadOnlyList<InstallRoute> RequiredCompanions(GameCandidate game, InstallRoute primary)
    {
        // The output hook alone does not capture a legacy engine's input
        // interfaces. Menus can use DirectDraw while gameplay selects Glide.
        // Keep both when the executable proves that combination. Never add
        // these wrappers to a selected native D3D/OpenGL/Vulkan route.
        if (game.Arch != CpuArch.X86 || primary is not (InstallRoute.X86DirectXLegacyViaDgVoodoo or
            InstallRoute.X86Glide211ViaDgVoodoo or InstallRoute.X86Glide245ViaDgVoodoo or
            InstallRoute.X86Glide31ViaDgVoodoo or InstallRoute.X86Glide31NapalmViaDgVoodoo) ||
            !game.DetectedInputApis.Contains(GraphicsApi.DirectX7OrOlder)) return [];
        var glide = game.DetectedInputApis.Select(api => api switch
        {
            GraphicsApi.Glide211 => InstallRoute.X86Glide211ViaDgVoodoo,
            GraphicsApi.Glide245 => InstallRoute.X86Glide245ViaDgVoodoo,
            GraphicsApi.Glide31 or GraphicsApi.Glide31Napalm => InstallRoute.X86Glide31ViaDgVoodoo,
            _ => InstallRoute.Unsupported
        }).Where(route => route != InstallRoute.Unsupported).ToArray();
        if (glide.Length == 0) return [];
        return glide.Append(InstallRoute.X86DirectXLegacyViaDgVoodoo).Where(route => route != primary &&
            !(primary == InstallRoute.X86Glide31NapalmViaDgVoodoo && route == InstallRoute.X86Glide31ViaDgVoodoo))
            .Distinct().ToArray();
    }
    public static IReadOnlyList<string> RetiredComponents(GameCandidate game, string activeProxy)
    {
        var directory = Path.GetDirectoryName(game.ExePath)!;
        var activeFiles = Directory.EnumerateFiles(directory).Where(path =>
        {
            var name = Path.GetFileName(path);
            if (name.Equals(activeProxy, StringComparison.OrdinalIgnoreCase)) return false;
            var proxy = new[] { "dxgi.dll", "d3d9.dll", "d3d10.dll", "d3d10core.dll", "d3d11.dll", "d3d12.dll", "opengl32.dll", InputBootstrap.ProxyName, InputBootstrap.LegacyProxyName }
                .Contains(name, StringComparer.OrdinalIgnoreCase);
            var addon = name.EndsWith(".addon32", StringComparison.OrdinalIgnoreCase)
                || name.EndsWith(".addon64", StringComparison.OrdinalIgnoreCase);
            return proxy && IsReShade(path) || addon && IsPreviousNeuralAddon(path)
                && !name.Equals(game.Arch == CpuArch.X86 ? "Phoenix.addon32" : "Phoenix.addon64", StringComparison.OrdinalIgnoreCase);
        });
        var legacyAddons = new[] { Path.Combine(directory, "host64"), Path.Combine(directory, "_DLSS5_OpenXR", "host64") }
            .Where(Directory.Exists).SelectMany(Directory.EnumerateFiles)
            .Where(path => (Path.GetExtension(path).Equals(".addon32", StringComparison.OrdinalIgnoreCase) ||
                Path.GetExtension(path).Equals(".addon64", StringComparison.OrdinalIgnoreCase)) && IsPreviousNeuralAddon(path));
        var retired = activeFiles.Concat(legacyAddons).ToList();
        // Retire a feeder's settings with its verified binary. A matching
        // filename by itself is not proof that arbitrary user data is obsolete.
        foreach (var folder in retired.Where(path => Path.GetFileNameWithoutExtension(path)
            .Equals("dlss5-feed", StringComparison.OrdinalIgnoreCase)).Select(Path.GetDirectoryName).Distinct().ToArray())
        {
            var settings = Path.Combine(folder!, "dlss5-feed.cfg");
            if (File.Exists(settings)) retired.Add(settings);
        }
        return retired;
    }

    internal static bool IsReShade(string path) => PeReader.GetArchitecture(path) != CpuArch.Unknown &&
        PayloadScanner.HasExports(path, "ReShadeRegisterAddon", "ReShadeUnregisterAddon");

    internal static bool IsExistingDgVoodoo(string path, CpuArch architecture)
    {
        if (PeReader.GetArchitecture(path) != architecture) return false;
        var product = System.Diagnostics.FileVersionInfo.GetVersionInfo(path).ProductName;
        if (!string.Equals(product, "dgVoodoo", StringComparison.OrdinalIgnoreCase)) return false;
        return HasDgVoodooInterface(path);
    }

    internal static bool HasDgVoodooInterface(string path) => Path.GetFileName(path).ToLowerInvariant() switch
        {
            "d3d8.dll" => PayloadScanner.HasExports(path, "Direct3DCreate8"),
            "d3d9.dll" => PayloadScanner.HasExports(path, "Direct3DCreate9"),
            "ddraw.dll" => PayloadScanner.HasExports(path, "DirectDrawCreate"),
            "d3dimm.dll" => PayloadScanner.HasExports(path, "GetD3DInterface", "GetD3DVersion"),
            "glide.dll" or "glide2x.dll" or "glide3x.dll" =>
                PayloadScanner.HasExports(path, "_grGlideInit@0", "_grBufferSwap@4") ||
                PayloadScanner.HasExports(path, "grGlideInit", "grBufferSwap"),
            _ => false
        };

    internal static bool IsPreviousNeuralAddon(string path) =>
        PayloadScanner.HasExports(path, "CnrPHOENIXSummary") ||
        PayloadScanner.HasExports(path, "CnrPHOENIXCompatibilityStatus") ||
        // Earlier project builds used this ABI prefix before the Phoenix rename.
        // Identify the complete export set, independent of the installed filename.
        PayloadScanner.HasExports(path, "CnrY41Summary", "CnrY41Register", "CnrY41Unregister", "AddonInit", "AddonUninit") ||
        PeReader.GetArchitecture(path) != CpuArch.Unknown &&
        Path.GetFileName(path).StartsWith("renodx-dlss", StringComparison.OrdinalIgnoreCase) &&
        (PeReader.FindMarkers(path, ["RenoDX DLSS controller"]).Contains("RenoDX DLSS controller") ||
         PayloadScanner.HasExports(path, "NAME", "DESCRIPTION") &&
         PeReader.FindMarkers(path, ["RenoDX.DLSS5", "Generic experimental DLSS Neural Rendering post-pass"])
            .Count == 2) ||
        PeReader.GetArchitecture(path) != CpuArch.Unknown &&
        new[] { "dlss5-feed", "phoenix-vulkan-feed", "dlss5-bridge" }.Contains(Path.GetFileNameWithoutExtension(path), StringComparer.OrdinalIgnoreCase) &&
        PeReader.FindMarkers(path, ["DLSS5"]).Contains("DLSS5");

    public static PackagedPhoenixPlan? For(GameCandidate game, PayloadInfo? payload)
    {
        if (payload is null || game.OverrideValidationError is not null) return null;
        var translation = game.Route is InstallRoute.X86DirectXLegacyViaDgVoodoo or InstallRoute.X64DirectXLegacyViaDgVoodoo or InstallRoute.X86Dx8ViaD3D8To9
            or InstallRoute.X86Dx8ViaDgVoodoo or InstallRoute.X86Dx9ViaDgVoodoo or InstallRoute.X86Glide211ViaDgVoodoo
            or InstallRoute.X86Glide245ViaDgVoodoo or InstallRoute.X86Glide31ViaDgVoodoo or InstallRoute.X86Glide31NapalmViaDgVoodoo
            ? game.Route : InstallRoute.Unsupported;
        // Reinstallation restores the outgoing manifest before copying files.
        // A detected wrapper output still needs its original translator after
        // that restore; DXGI output alone must not erase the legacy input chain.
        if (translation == InstallRoute.Unsupported && game.Arch == CpuArch.X86 &&
            game.Api == GraphicsApi.DirectX9 &&
            game.TranslatorKinds.Contains("D3D8ToD3D9", StringComparer.OrdinalIgnoreCase))
            translation = InstallRoute.X86Dx8ViaD3D8To9;
        if (translation == InstallRoute.Unsupported && game.Arch == CpuArch.X64 &&
            game.Api is (GraphicsApi.Dxgi or GraphicsApi.DirectX11 or GraphicsApi.DirectX12) &&
            game.TranslatorKinds.Contains("dgVoodoo", StringComparer.OrdinalIgnoreCase) &&
            game.DetectedInputApis.Contains(GraphicsApi.DirectX7OrOlder))
            translation = InstallRoute.X64DirectXLegacyViaDgVoodoo;
        if (translation == InstallRoute.Unsupported && game.Arch == CpuArch.X86 &&
            game.Api is (GraphicsApi.Dxgi or GraphicsApi.DirectX11 or GraphicsApi.DirectX12) &&
            game.TranslatorKinds.Contains("dgVoodoo", StringComparer.OrdinalIgnoreCase))
            translation = game.DetectedInputApis.Select(api => api switch
            {
                GraphicsApi.DirectX7OrOlder => InstallRoute.X86DirectXLegacyViaDgVoodoo,
                GraphicsApi.DirectX8 or GraphicsApi.DirectX8DgVoodoo => InstallRoute.X86Dx8ViaDgVoodoo,
                GraphicsApi.DirectX9 => InstallRoute.X86Dx9ViaDgVoodoo,
                GraphicsApi.Glide211 => InstallRoute.X86Glide211ViaDgVoodoo,
                GraphicsApi.Glide245 => InstallRoute.X86Glide245ViaDgVoodoo,
                GraphicsApi.Glide31 => InstallRoute.X86Glide31ViaDgVoodoo,
                GraphicsApi.Glide31Napalm => InstallRoute.X86Glide31NapalmViaDgVoodoo,
                _ => InstallRoute.Unsupported
            }).FirstOrDefault(route => route != InstallRoute.Unsupported, InstallRoute.Unsupported);
        // Legacy input still needs its translator, followed by the same packaged addon.
        if (translation == InstallRoute.Unsupported && game.Api is not (GraphicsApi.DirectX9 or GraphicsApi.DirectX11 or GraphicsApi.DirectX12
            or GraphicsApi.DirectX10 or GraphicsApi.DirectX10_1 or GraphicsApi.Dxgi or GraphicsApi.OpenGl or GraphicsApi.Vulkan)) return null;
        var addon = game.Arch == CpuArch.X86 ? payload.PhoenixAddon32 : payload.PhoenixAddon;
        if (!PayloadScanner.HasPackagedPhoenixBridge(addon, game.Arch)) return null;
        if (game.Arch == CpuArch.X64 && game.Api == GraphicsApi.DirectX12 &&
            game.Route != InstallRoute.X64DirectPhoenix && !PayloadScanner.HasStandaloneD3D12Input(addon!)) return null;
        var importedD3D9Bootstrap = PrefersImportedD3D9Bootstrap(game, translation);
        var earlyInputBootstrap = translation != InstallRoute.Unsupported ? InputBootstrap.AutomaticProxy(game) : null;
        // A translator can load DXGI/D3D11 by absolute system path, bypassing a
        // side-by-side proxy. With no ordinary input anchor, use the executable's
        // selected D3D9 entry point and chain the unchanged translator via ReShade.
        // Explicit D3D9 translation also covers dynamic imports; do not require
        // a static PE import when the chosen source API already proves D3D9.
        var dgVoodooD3D9Chain = game.ProxyName is null && earlyInputBootstrap is null &&
            game.Arch == CpuArch.X86 && translation == InstallRoute.X86Dx9ViaDgVoodoo &&
            (game.Api == GraphicsApi.DirectX9 ||
             game.StartupGraphicsApis.Contains(GraphicsApi.DirectX9));
        var proxy = game.ProxyName ?? earlyInputBootstrap ?? (dgVoodooD3D9Chain || importedD3D9Bootstrap ? "d3d9.dll" : translation != InstallRoute.Unsupported
            ? translation == InstallRoute.X86Dx8ViaD3D8To9 ? "d3d9.dll" : "dxgi.dll" : game.Api switch
        {
            GraphicsApi.DirectX9 => "d3d9.dll",
            GraphicsApi.OpenGl => "opengl32.dll",
            GraphicsApi.Vulkan => "",
            _ => "dxgi.dll"
        });
        var software = SoftwarePresentation.RequiresHook(game);
        // A DLSS DLL/import proves neither native-resolution guides nor a
        // compatible engine buffer layout. Modern automatic installs use the
        // backbuffer and keep the fast in-process RTX execution route available.
        var nativeGameInput = game.Route == InstallRoute.X64DirectPhoenix &&
            PayloadScanner.HasExports(addon!, "CnrPHOENIXSummary", "CnrPHOENIXRegister") &&
            (game.PreferredRoute == GameRoutePreference.Native || !PayloadScanner.HasStandaloneD3D12Input(addon!));
        return new(addon!, proxy, nativeGameInput, translation)
        {
            UsesDgVoodooD3D9ProxyChain = dgVoodooD3D9Chain,
            SoftwareDeviceSupport = software,
            UsesImportedD3D9Bootstrap = importedD3D9Bootstrap,
            CompanionTranslations = RequiredCompanions(game, translation),
            ReShadeOverride = software ? game.Arch == CpuArch.X86 ? payload.SoftwareReShade32 : payload.SoftwareReShade64 : null
        };
    }

    internal static bool PrefersImportedD3D9Bootstrap(GameCandidate game, InstallRoute translation)
    {
        // Some engines probe and unload DXGI before choosing D3D9. A DXGI-only
        // proxy disappears with that probe. An ordinary D3D9 import is a startup
        // entry point; a delayed D3D9 import can arrive after a DX12 renderer is
        // already running and must never displace its DXGI frontend.
        // Do not deploy two ReShade copies, infer imports from capability strings,
        // replace translator chains, or supersede an explicit proxy selection.
        return game.ProxyName is null && translation == InstallRoute.Unsupported &&
            game.TranslatorKinds.Count == 0 &&
            game.Api is (GraphicsApi.DirectX10 or GraphicsApi.DirectX10_1 or GraphicsApi.DirectX11 or GraphicsApi.DirectX12 or GraphicsApi.Dxgi) &&
            game.StartupGraphicsApis.Contains(GraphicsApi.DirectX9) &&
            game.ImportedGraphicsApis.Any(api => api is GraphicsApi.DirectX10 or GraphicsApi.DirectX10_1 or GraphicsApi.DirectX11 or GraphicsApi.DirectX12 or GraphicsApi.Dxgi);
    }

    public string Description => SoftwareDeviceSupport ? "Phoenix DLSS5 software presentation bridge (modified ReShade, explicit software-device opt-in)"
        : NativeGameInput ? "Phoenix DLSS5 native game DLSS input"
        : SupportsInProcessBackbuffer
            ? "Phoenix DLSS5 automatic graphics bridge (eligible in-process mechanisms, then helper fallback)"
            : "Phoenix DLSS5 packaged graphics bridge (private x64 inference)";
}
