namespace Dlss5CompatApp;

/// <summary>Architecture/API rules for the self-contained Phoenix graphics bridge.
/// Older payloads retain their explicit feeder route; no executable/title allowlist is used.</summary>
sealed record PackagedPhoenixPlan(string Addon, string Proxy, bool NativeGameInput, InstallRoute Translation = InstallRoute.Unsupported)
{
    public bool SupportsInProcessBackbuffer => (PayloadScanner.IsPeDll(Addon, CpuArch.X64) ||
        PayloadScanner.IsPeDll(Addon, CpuArch.X86)) &&
        PayloadScanner.HasExports(Addon, "CnrPHOENIXInProcessBackbufferVersion");
    public IReadOnlyList<(string? Source, string Target)> TranslationFiles(PayloadInfo payload) => Translation switch
    {
        InstallRoute.X86DirectXLegacyViaDgVoodoo => [(payload.DgVoodooDDraw, "DDraw.dll"), (payload.DgVoodooD3DImm, "D3DImm.dll")],
        InstallRoute.X86Dx8ViaD3D8To9 => [(payload.D3D8To9D3D8, "D3D8.dll")],
        InstallRoute.X86Dx8ViaDgVoodoo => [(payload.DgVoodooD3D8, "D3D8.dll")],
        InstallRoute.X86Dx9ViaDgVoodoo => [(payload.DgVoodooD3D9, "D3D9.dll")],
        InstallRoute.X86Glide211ViaDgVoodoo => [(payload.DgVoodooGlide, "Glide.dll")],
        InstallRoute.X86Glide245ViaDgVoodoo => [(payload.DgVoodooGlide2x, "Glide2x.dll")],
        InstallRoute.X86Glide31ViaDgVoodoo => [(payload.DgVoodooGlide3x, "Glide3x.dll")],
        InstallRoute.X86Glide31NapalmViaDgVoodoo => [(payload.DgVoodooGlide3xNapalm, "Glide3x.dll")],
        _ => []
    };
    public static IReadOnlyList<string> RetiredComponents(GameCandidate game, string activeProxy)
    {
        var directory = Path.GetDirectoryName(game.ExePath)!;
        var activeFiles = Directory.EnumerateFiles(directory).Where(path =>
        {
            var name = Path.GetFileName(path);
            if (name.Equals(activeProxy, StringComparison.OrdinalIgnoreCase)) return false;
            var proxy = new[] { "dxgi.dll", "d3d9.dll", "d3d10.dll", "d3d10core.dll", "d3d11.dll", "d3d12.dll", "opengl32.dll" }
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
        return activeFiles.Concat(legacyAddons).ToArray();
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
        var translation = game.Route is InstallRoute.X86DirectXLegacyViaDgVoodoo or InstallRoute.X86Dx8ViaD3D8To9
            or InstallRoute.X86Dx8ViaDgVoodoo or InstallRoute.X86Dx9ViaDgVoodoo or InstallRoute.X86Glide211ViaDgVoodoo
            or InstallRoute.X86Glide245ViaDgVoodoo or InstallRoute.X86Glide31ViaDgVoodoo or InstallRoute.X86Glide31NapalmViaDgVoodoo
            ? game.Route : InstallRoute.Unsupported;
        // Reinstallation restores the outgoing manifest before copying files.
        // A detected wrapper output still needs its original translator after
        // that restore; DXGI output alone must not erase the legacy input chain.
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
        var proxy = game.ProxyName ?? (translation != InstallRoute.Unsupported
            ? translation == InstallRoute.X86Dx8ViaD3D8To9 ? "d3d9.dll" : "dxgi.dll" : game.Api switch
        {
            GraphicsApi.DirectX9 => "d3d9.dll",
            GraphicsApi.OpenGl => "opengl32.dll",
            GraphicsApi.Vulkan => "",
            _ => "dxgi.dll"
        });
        return new(addon!, proxy, game.Route == InstallRoute.X64DirectPhoenix, translation);
    }

    public string Description => NativeGameInput ? "Phoenix DLSS5 native game DLSS input"
        : SupportsInProcessBackbuffer && Proxy.Equals("dxgi.dll", StringComparison.OrdinalIgnoreCase)
            ? "Phoenix DLSS5 automatic graphics bridge (same-device DX12 when supported; helper fallback)"
            : "Phoenix DLSS5 packaged graphics bridge (private x64 inference)";
}
