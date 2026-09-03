using System.Diagnostics;
using System.Text.Json;
using System.Text;
using System.Text.RegularExpressions;

namespace Dlss5CompatApp;

sealed class InstallerEngine
{
    const string BackupDirectoryName = "_DLSS5_Compat_Backup";
    const string OpenVrOriginalDllName = "openvr_api.dlss5-original.dll";
    const string OpenVrShimDllName = "dlss5-openvr-shim64.dll";
    const string VulkanDisablePatchCommand = "--patch-reshade-vulkan-disable";

    readonly Action<string> _log;
    int _frameIterations = 1;

    enum DgVoodooMode
    {
        None,
        DirectXLegacy,
        DirectX8,
        DirectX9,
        Glide211,
        Glide245,
        Glide31,
        Glide31Napalm
    }

    public InstallerEngine(Action<string> log)
    {
        _log = log;
    }

    public static bool IsVulkanDisablePatchCommand(string[] args) =>
        args.Length >= 3 && args[0].Equals(VulkanDisablePatchCommand, StringComparison.OrdinalIgnoreCase);

    public static int RunVulkanDisablePatchCommand(string[] args)
    {
        try
        {
            var json = args[1];
            var replacementName = args[2];
            const string original = "\"DISABLE_VK_LAYER_reshade_1\"";
            var replacement = "\"" + replacementName + "\"";
            if (!File.Exists(json))
                return 2;

            var backup = json + ".dlss5compat.bak";
            if (!File.Exists(backup))
                File.Copy(json, backup);

            var text = File.ReadAllText(json);
            if (!text.Contains(original, StringComparison.Ordinal))
                return text.Contains(replacement, StringComparison.Ordinal) ? 0 : 3;

            File.WriteAllText(json, text.Replace(original, replacement, StringComparison.Ordinal));
            return 0;
        }
        catch
        {
            return 1;
        }
    }

    public async Task InstallAsync(GameCandidate game, PayloadInfo payload, bool forceVrEyeSplit = false, int frameIterations = 1, CancellationToken cancellationToken = default)
    {
        _frameIterations = Math.Clamp(frameIterations, 1, 10);

        if (game.Route == InstallRoute.Unsupported)
            throw new InvalidOperationException("This executable/API combination is not supported.");

        var missing = payload.MissingFor(game.Route);
        if (!string.IsNullOrWhiteSpace(missing))
            throw new InvalidOperationException("Payload is missing: " + missing);

        var gameDir = Path.GetDirectoryName(game.ExePath)!;
        EnsureWritable(gameDir);

        if (File.Exists(ManifestPath(game.Root)))
        {
            _log("Restoring previous DLSS5 compatibility install before applying the selected route.");
            await RestoreAsync(game.Root, cancellationToken);
        }

        var manifest = new InstallManifest
        {
            GameRoot = game.Root,
            ExeRelativePath = Path.GetRelativePath(game.Root, game.ExePath),
            Route = game.Route.ToString()
        };

        Directory.CreateDirectory(BackupRoot(game.Root));

        switch (game.Route)
        {
            case InstallRoute.X86DirectXLegacyViaDgVoodoo:
                await InstallX86Async(game, payload, manifest, gameReShadeDllName: "dxgi.dll", gameReShadeApi: "d3d10", dgVoodooMode: DgVoodooMode.DirectXLegacy, installD3D8To9: false, cancellationToken);
                break;
            case InstallRoute.X86Dx8ViaD3D8To9:
                await InstallX86Async(game, payload, manifest, gameReShadeDllName: "D3D9.dll", gameReShadeApi: "d3d9", dgVoodooMode: DgVoodooMode.None, installD3D8To9: true, cancellationToken);
                break;
            case InstallRoute.X86Dx8ViaDgVoodoo:
                await InstallX86Async(game, payload, manifest, gameReShadeDllName: "dxgi.dll", gameReShadeApi: "d3d10", dgVoodooMode: DgVoodooMode.DirectX8, installD3D8To9: false, cancellationToken);
                break;
            case InstallRoute.X86Dx9NativeFeeder:
                await InstallX86Async(game, payload, manifest, gameReShadeDllName: "D3D9.dll", gameReShadeApi: "d3d9", dgVoodooMode: DgVoodooMode.None, installD3D8To9: false, cancellationToken);
                break;
            case InstallRoute.X86Dx9ViaDgVoodoo:
                await InstallX86Async(game, payload, manifest, gameReShadeDllName: "dxgi.dll", gameReShadeApi: "d3d10", dgVoodooMode: DgVoodooMode.DirectX9, installD3D8To9: false, cancellationToken);
                break;
            case InstallRoute.X86DxgiFeeder:
                await InstallX86Async(game, payload, manifest, gameReShadeDllName: "dxgi.dll", gameReShadeApi: "d3d10", dgVoodooMode: DgVoodooMode.None, installD3D8To9: false, cancellationToken);
                break;
            case InstallRoute.X86OpenGlFeeder:
                await InstallX86Async(game, payload, manifest, gameReShadeDllName: "opengl32.dll", gameReShadeApi: "opengl", dgVoodooMode: DgVoodooMode.None, installD3D8To9: false, cancellationToken);
                break;
            case InstallRoute.X86Glide211ViaDgVoodoo:
                await InstallX86Async(game, payload, manifest, gameReShadeDllName: "dxgi.dll", gameReShadeApi: "d3d10", dgVoodooMode: DgVoodooMode.Glide211, installD3D8To9: false, cancellationToken);
                break;
            case InstallRoute.X86Glide245ViaDgVoodoo:
                await InstallX86Async(game, payload, manifest, gameReShadeDllName: "dxgi.dll", gameReShadeApi: "d3d10", dgVoodooMode: DgVoodooMode.Glide245, installD3D8To9: false, cancellationToken);
                break;
            case InstallRoute.X86Glide31ViaDgVoodoo:
                await InstallX86Async(game, payload, manifest, gameReShadeDllName: "dxgi.dll", gameReShadeApi: "d3d10", dgVoodooMode: DgVoodooMode.Glide31, installD3D8To9: false, cancellationToken);
                break;
            case InstallRoute.X86Glide31NapalmViaDgVoodoo:
                await InstallX86Async(game, payload, manifest, gameReShadeDllName: "dxgi.dll", gameReShadeApi: "d3d10", dgVoodooMode: DgVoodooMode.Glide31Napalm, installD3D8To9: false, cancellationToken);
                break;
            case InstallRoute.X64DxgiFeeder:
                await InstallX64FeederAsync(game, payload, manifest, forceVrEyeSplit, cancellationToken);
                break;
            case InstallRoute.X64OpenXrLayer:
                await InstallX64OpenXrLayerAsync(game, payload, manifest, cancellationToken);
                break;
            case InstallRoute.X64OpenVrFeeder:
                await InstallX64FeederAsync(game, payload, manifest, forceVrEyeSplit, cancellationToken);
                break;
            case InstallRoute.X64OpenGlFeeder:
                await InstallX64FeederAsync(game, payload, manifest, forceVrEyeSplit, cancellationToken);
                break;
            case InstallRoute.X64VulkanFeeder:
                await InstallX64VulkanFeederAsync(game, payload, manifest, forceVrEyeSplit, cancellationToken);
                break;
            case InstallRoute.X64NativeThenFeeder:
                await InstallX64NativeThenFeederAsync(game, payload, manifest, forceVrEyeSplit, cancellationToken);
                break;
            case InstallRoute.X64DirectRenoDx:
                await InstallX64Async(game, payload, manifest, cancellationToken);
                break;
        }

        TryDisableNvidiaDlssIndicator();
        WriteLauncherBatch(game, manifest);
        await WriteManifestAsync(game.Root, manifest, cancellationToken);
        _log("Install complete.");
    }

    public async Task RestoreAsync(string gameRoot, CancellationToken cancellationToken = default)
    {
        var manifestPath = ManifestPath(gameRoot);
        if (!File.Exists(manifestPath))
            throw new InvalidOperationException("No restore manifest found.");

        var manifest = JsonSerializer.Deserialize<InstallManifest>(await File.ReadAllTextAsync(manifestPath, cancellationToken))
            ?? throw new InvalidOperationException("Restore manifest is unreadable.");

        foreach (var item in manifest.Replaced)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var backup = Path.Combine(BackupRoot(gameRoot), item.RelativePath);
            var target = Path.Combine(gameRoot, item.RelativePath);
            if (!File.Exists(backup)) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(backup, target, overwrite: true);
            _log("Restored " + item.RelativePath);
        }

        foreach (var relative in manifest.Added.AsEnumerable().Reverse())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var target = Path.Combine(gameRoot, relative);
            if (File.Exists(target))
            {
                File.Delete(target);
                _log("Deleted " + relative);
            }
            else if (Directory.Exists(target))
            {
                Directory.Delete(target, recursive: true);
                _log("Deleted " + relative);
            }
        }

        File.Move(manifestPath, manifestPath + ".done-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"), overwrite: true);
        _log("Restore complete.");
    }

    async Task InstallX86Async(GameCandidate game, PayloadInfo payload, InstallManifest manifest, string gameReShadeDllName, string gameReShadeApi, DgVoodooMode dgVoodooMode, bool installD3D8To9, CancellationToken cancellationToken)
    {
        var gameDir = Path.GetDirectoryName(game.ExePath)!;
        RemoveInactiveX86RouteArtifacts(gameDir, game.Root, manifest, gameReShadeDllName, dgVoodooMode, installD3D8To9);

        if (installD3D8To9)
        {
            if (payload.D3D8To9D3D8 is null)
                throw new InvalidOperationException("DX8 route needs d3d8to9 d3d8.dll in the payload folder.");

            CopyWithBackup(payload.D3D8To9D3D8, Path.Combine(gameDir, "d3d8.dll"), game.Root, manifest);
            _log("Installed d3d8to9 D3D8-to-D3D9 wrapper.");
        }

        if (dgVoodooMode != DgVoodooMode.None)
            InstallDgVoodoo(gameDir, game.Root, payload, manifest, dgVoodooMode);

        var gameReShadeIni = Path.Combine(gameDir, "ReShade.ini");
        var gameReShadeVrIni = Path.Combine(gameDir, "ReShadeVR.ini");
        var gamePreset = Path.Combine(gameDir, "ReShadePreset.ini");
        TrackExternalWrite(gameReShadeIni, game.Root, manifest);
        TrackExternalWrite(gameReShadeVrIni, game.Root, manifest);
        TrackExternalWrite(gamePreset, game.Root, manifest);

        await InstallReShadeAsync(game.ExePath, Path.Combine(gameDir, gameReShadeDllName), payload.ReShade32Dll, payload.ReShadeSetup, gameReShadeApi, game.Root, manifest, cancellationToken);

        CopyWithBackup(AppFile("Runtime", "x86-dx9-dx11", "dlss5-feed.addon32"), Path.Combine(gameDir, "dlss5-feed.addon32"), game.Root, manifest);
        var feedConfig = Path.Combine(gameDir, "dlss5-feed.cfg");
        CopyWithBackup(AppFile("Configs", "dlss5-feed-32.cfg"), feedConfig, game.Root, manifest);
        ConfigureFrameIterations(feedConfig);

        var shaderRoot = Path.Combine(gameDir, "reshade-shaders");
        var shaderDir = Path.Combine(shaderRoot, "Shaders");
        AddDirectoryIfNew(shaderRoot, game.Root, manifest);
        AddDirectoryIfNew(shaderDir, game.Root, manifest);
        CopyWithBackup(AppFile("Runtime", "shaders", "DLSS5_Feed.fx"), Path.Combine(shaderDir, "DLSS5_Feed.fx"), game.Root, manifest);
        CopyWithBackup(AppFile("Runtime", "shaders", "ReShade.fxh"), Path.Combine(shaderDir, "ReShade.fxh"), game.Root, manifest);
        ConfigureGameReShade(gameReShadeIni, enableGeomFit: gameReShadeApi.Equals("d3d10", StringComparison.OrdinalIgnoreCase));
        ConfigureGameReShade(gameReShadeVrIni, enableGeomFit: gameReShadeApi.Equals("d3d10", StringComparison.OrdinalIgnoreCase));
        ConfigureGamePreset(gamePreset);

        var hostDir = Path.Combine(gameDir, "host64");
        Directory.CreateDirectory(hostDir);
        AddDirectoryIfNew(hostDir, game.Root, manifest);
        var hostExe = Path.Combine(hostDir, "dlss5-feed-host64.exe");
        CopyWithBackup(AppFile("Runtime", "host64", "dlss5-feed-host64.exe"), hostExe, game.Root, manifest);

        var hostReShadeIni = Path.Combine(hostDir, "ReShade.ini");
        TrackExternalWrite(hostReShadeIni, game.Root, manifest);

        await InstallReShadeAsync(hostExe, Path.Combine(hostDir, "dxgi.dll"), payload.ReShade64Dll, payload.ReShadeSetup, "d3d10", game.Root, manifest, cancellationToken);
        CopyWithBackup(payload.RenoDxDlss5Addon!, Path.Combine(hostDir, "renodx-dlss5.addon64"), game.Root, manifest);
        foreach (var dll in payload.NvidiaDlls.Concat(payload.StreamlineDlls))
            CopyWithBackup(dll, Path.Combine(hostDir, Path.GetFileName(dll)), game.Root, manifest);

        ConfigureHostReShade(hostReShadeIni);
        _log("Installed x86 feeder and 64-bit DLSS host.");
    }

    async Task InstallX64FeederAsync(GameCandidate game, PayloadInfo payload, InstallManifest manifest, bool forceVrEyeSplit, CancellationToken cancellationToken)
    {
        var gameDir = Path.GetDirectoryName(game.ExePath)!;
        var gameReShadeDll = GameReShadeProxyName(game);
        var gameReShadeApi = game.Api == GraphicsApi.OpenGl ? "opengl" : "d3d10";
        var reShade64Dll = ReShade64DllForInstall(game, payload.ReShade64Dll);
        var gameReShadeIni = Path.Combine(gameDir, "ReShade.ini");
        var gameReShadeVrIni = Path.Combine(gameDir, "ReShadeVR.ini");
        var gamePreset = Path.Combine(gameDir, "ReShadePreset.ini");
        TrackExternalWrite(gameReShadeIni, game.Root, manifest);
        TrackExternalWrite(gameReShadeVrIni, game.Root, manifest);
        TrackExternalWrite(gamePreset, game.Root, manifest);

        if (NeedsOpenVrProxy(game))
            PrepareOpenVrProxyLibrary(gameDir, game.Root, manifest);

        await InstallReShadeAsync(game.ExePath, Path.Combine(gameDir, gameReShadeDll), reShade64Dll, payload.ReShadeSetup, gameReShadeApi, game.Root, manifest, cancellationToken);
        RemoveUnusedReShadeProxy(gameDir, game.Root, manifest, gameReShadeDll);

        RemoveVulkanAppAllowListEntry(game.ExePath);
        RemoveVulkanBridgeArtifacts(gameDir, game.Root, manifest);
        RemoveRootRenoDxAddons(gameDir, game.Root, manifest);
        CopyWithBackup(AppFile("Runtime", "x64-dx9-dx11", "dlss5-feed.addon64"), Path.Combine(gameDir, "dlss5-feed.addon64"), game.Root, manifest);
        var feedConfig = Path.Combine(gameDir, "dlss5-feed.cfg");
        CopyWithBackup(AppFile("Configs", "dlss5-feed-64.cfg"), feedConfig, game.Root, manifest);
        ConfigureFrameIterations(feedConfig);
        ConfigureVrEyeSplit(feedConfig, forceVrEyeSplit);

        var shaderRoot = Path.Combine(gameDir, "reshade-shaders");
        var shaderDir = Path.Combine(shaderRoot, "Shaders");
        AddDirectoryIfNew(shaderRoot, game.Root, manifest);
        AddDirectoryIfNew(shaderDir, game.Root, manifest);
        CopyWithBackup(AppFile("Runtime", "shaders", "DLSS5_Feed.fx"), Path.Combine(shaderDir, "DLSS5_Feed.fx"), game.Root, manifest);
        CopyWithBackup(AppFile("Runtime", "shaders", "ReShade.fxh"), Path.Combine(shaderDir, "ReShade.fxh"), game.Root, manifest);
        ConfigureGameReShade(gameReShadeIni, enableGeomFit: !gameReShadeApi.Equals("opengl", StringComparison.OrdinalIgnoreCase));
        ConfigureGameReShade(gameReShadeVrIni, enableGeomFit: !gameReShadeApi.Equals("opengl", StringComparison.OrdinalIgnoreCase));
        ConfigureOpenVrProxyIfNeeded(game, gameReShadeIni);
        ConfigureOpenVrProxyIfNeeded(game, gameReShadeVrIni);
        ConfigureGamePreset(gamePreset);

        var hostDir = Path.Combine(gameDir, "host64");
        Directory.CreateDirectory(hostDir);
        AddDirectoryIfNew(hostDir, game.Root, manifest);
        var hostExe = Path.Combine(hostDir, "dlss5-feed-host64.exe");
        CopyWithBackup(AppFile("Runtime", "host64", "dlss5-feed-host64.exe"), hostExe, game.Root, manifest);

        var hostReShadeIni = Path.Combine(hostDir, "ReShade.ini");
        TrackExternalWrite(hostReShadeIni, game.Root, manifest);

        await InstallReShadeAsync(hostExe, Path.Combine(hostDir, "dxgi.dll"), payload.ReShade64Dll, payload.ReShadeSetup, "d3d10", game.Root, manifest, cancellationToken);
        CopyWithBackup(payload.RenoDxDlss5Addon!, Path.Combine(hostDir, "renodx-dlss5.addon64"), game.Root, manifest);
        foreach (var dll in payload.NvidiaDlls.Concat(payload.StreamlineDlls))
            CopyWithBackup(dll, Path.Combine(hostDir, Path.GetFileName(dll)), game.Root, manifest);

        ConfigureHostReShade(hostReShadeIni);
        _log("Installed x64 feeder and 64-bit DLSS host.");
    }

    async Task InstallX64VulkanFeederAsync(GameCandidate game, PayloadInfo payload, InstallManifest manifest, bool forceVrEyeSplit, CancellationToken cancellationToken)
    {
        var gameDir = Path.GetDirectoryName(game.ExePath)!;
        var gameReShadeIni = Path.Combine(gameDir, "ReShade.ini");
        var gameReShadeVrIni = Path.Combine(gameDir, "ReShadeVR.ini");
        var gamePreset = Path.Combine(gameDir, "ReShadePreset.ini");
        var bridgeConfig = Path.Combine(gameDir, "dlss5-bridge.cfg");
        TrackExternalWrite(gameReShadeIni, game.Root, manifest);
        TrackExternalWrite(gameReShadeVrIni, game.Root, manifest);
        TrackExternalWrite(gamePreset, game.Root, manifest);
        TrackExternalWrite(bridgeConfig, game.Root, manifest);

        await InstallVulkanReShadeLayerAsync(game.ExePath, payload.ReShade64Dll, payload.ReShadeSetup, cancellationToken);

        RemoveVulkanFeederArtifacts(gameDir, game.Root, manifest);
        RemoveConflictingRenoDxDlss5Addons(gameDir, game.Root, manifest);
        CopyWithBackup(payload.Dlss5BridgeAddon!, Path.Combine(gameDir, "dlss5-bridge.addon64"), game.Root, manifest);
        CopyWithBackup(payload.RenoDxDlss5Addon!, Path.Combine(gameDir, "renodx-dlss5.addon64"), game.Root, manifest);
        foreach (var dll in payload.NvidiaDlls.Concat(payload.StreamlineDlls))
            CopyWithBackup(dll, Path.Combine(gameDir, Path.GetFileName(dll)), game.Root, manifest);

        ConfigureVulkanBridgeReShade(gameReShadeIni);
        ConfigureVulkanBridgeReShade(gameReShadeVrIni);
        ConfigureNoEffectsPreset(gamePreset);
        ConfigureDlss5Bridge(bridgeConfig);
        _log("Installed x64 Vulkan DLSS5 Bridge + RenoDX route.");
        LogDoomEternalVulkanNote(game);
    }

    async Task InstallX64OpenXrLayerAsync(GameCandidate game, PayloadInfo payload, InstallManifest manifest, CancellationToken cancellationToken)
    {
        var gameDir = Path.GetDirectoryName(game.ExePath)!;
        RemoveOpenXrConflictingRootArtifacts(gameDir, game.Root, manifest);

        var stageRoot = Path.Combine(gameDir, "_DLSS5_OpenXR");
        var layerDir = Path.Combine(stageRoot, "openxr");
        var hostDir = Path.Combine(stageRoot, "host64");
        Directory.CreateDirectory(layerDir);
        Directory.CreateDirectory(hostDir);
        AddDirectoryIfNew(stageRoot, game.Root, manifest);
        AddDirectoryIfNew(layerDir, game.Root, manifest);
        AddDirectoryIfNew(hostDir, game.Root, manifest);

        CopyWithBackup(AppFile("Runtime", "openxr", "XR_APILAYER_DLSS5_everything.dll"), Path.Combine(layerDir, "XR_APILAYER_DLSS5_everything.dll"), game.Root, manifest);
        CopyWithBackup(AppFile("Runtime", "openxr", "XR_APILAYER_DLSS5_everything.json"), Path.Combine(layerDir, "XR_APILAYER_DLSS5_everything.json"), game.Root, manifest);

        var hostExe = Path.Combine(hostDir, "dlss5-feed-host64.exe");
        CopyWithBackup(AppFile("Runtime", "host64", "dlss5-feed-host64.exe"), hostExe, game.Root, manifest);

        var hostReShadeIni = Path.Combine(hostDir, "ReShade.ini");
        var hostPreset = Path.Combine(hostDir, "ReShadePreset.ini");
        TrackExternalWrite(hostReShadeIni, game.Root, manifest);
        TrackExternalWrite(hostPreset, game.Root, manifest);

        await InstallReShadeAsync(hostExe, Path.Combine(hostDir, "dxgi.dll"), payload.ReShade64Dll, payload.ReShadeSetup, "d3d10", game.Root, manifest, cancellationToken);
        CopyWithBackup(payload.RenoDxDlss5Addon!, Path.Combine(hostDir, "renodx-dlss5.addon64"), game.Root, manifest);
        foreach (var dll in payload.NvidiaDlls.Concat(payload.StreamlineDlls))
            CopyWithBackup(dll, Path.Combine(hostDir, Path.GetFileName(dll)), game.Root, manifest);

        ConfigureHostReShade(hostReShadeIni);
        ConfigureNoEffectsPreset(hostPreset);
        _log("Installed x64 OpenXR API-layer route.");
    }

    void LogDoomEternalVulkanNote(GameCandidate game)
    {
        var exeName = Path.GetFileNameWithoutExtension(game.ExePath);
        if (!exeName.Contains("DOOMEternal", StringComparison.OrdinalIgnoreCase))
            return;

        _log("DOOM Eternal note: the game can disable ReShade's Vulkan layer with DISABLE_VK_LAYER_reshade_1.");
        _log(@"If ReShade does not appear, edit C:\ProgramData\ReShade\ReShade64.json as administrator and rename DISABLE_VK_LAYER_reshade_1 to DISABLE_VK_LAYER_reshade_2, then launch through Steam.");
    }

    async Task InstallX64Async(GameCandidate game, PayloadInfo payload, InstallManifest manifest, CancellationToken cancellationToken)
    {
        var gameDir = Path.GetDirectoryName(game.ExePath)!;
        var gameReShadeDll = GameReShadeProxyName(game);
        var reShade64Dll = ReShade64DllForInstall(game, payload.ReShade64Dll);
        var gameReShadeIni = Path.Combine(gameDir, "ReShade.ini");
        var gameReShadeVrIni = Path.Combine(gameDir, "ReShadeVR.ini");
        TrackExternalWrite(gameReShadeIni, game.Root, manifest);
        TrackExternalWrite(gameReShadeVrIni, game.Root, manifest);

        if (NeedsOpenVrProxy(game))
            PrepareOpenVrProxyLibrary(gameDir, game.Root, manifest);

        await InstallReShadeAsync(game.ExePath, Path.Combine(gameDir, gameReShadeDll), reShade64Dll, payload.ReShadeSetup, "d3d10", game.Root, manifest, cancellationToken);
        RemoveUnusedReShadeProxy(gameDir, game.Root, manifest, gameReShadeDll);
        RemoveConflictingRenoDxDlss5Addons(gameDir, game.Root, manifest);
        CopyWithBackup(payload.RenoDxDlss5Addon!, Path.Combine(gameDir, "renodx-dlss5.addon64"), game.Root, manifest);

        foreach (var addon in payload.ExtraAddons)
        {
            var name = Path.GetFileName(addon);
            if (name.Equals("renodx-dlss5.addon64", StringComparison.OrdinalIgnoreCase)) continue;
            CopyWithBackup(addon, Path.Combine(gameDir, name), game.Root, manifest);
        }

        foreach (var dll in payload.NvidiaDlls.Concat(payload.StreamlineDlls))
            CopyWithBackup(dll, Path.Combine(gameDir, Path.GetFileName(dll)), game.Root, manifest);

        ConfigureDirectRenoDxReShade(gameReShadeIni);
        ConfigureDirectRenoDxReShade(gameReShadeVrIni);
        ConfigureOpenVrProxyIfNeeded(game, gameReShadeIni);
        ConfigureOpenVrProxyIfNeeded(game, gameReShadeVrIni);
        _log("Installed native x64 RenoDX/DLSS payload.");
    }

    async Task InstallX64NativeThenFeederAsync(GameCandidate game, PayloadInfo payload, InstallManifest manifest, bool forceVrEyeSplit, CancellationToken cancellationToken)
    {
        var gameDir = Path.GetDirectoryName(game.ExePath)!;
        var gameReShadeDll = GameReShadeProxyName(game);
        var reShade64Dll = ReShade64DllForInstall(game, payload.ReShade64Dll);
        var gameReShadeIni = Path.Combine(gameDir, "ReShade.ini");
        var gameReShadeVrIni = Path.Combine(gameDir, "ReShadeVR.ini");
        var gamePreset = Path.Combine(gameDir, "ReShadePreset.ini");
        TrackExternalWrite(gameReShadeIni, game.Root, manifest);
        TrackExternalWrite(gameReShadeVrIni, game.Root, manifest);
        TrackExternalWrite(gamePreset, game.Root, manifest);

        if (NeedsOpenVrProxy(game))
            PrepareOpenVrProxyLibrary(gameDir, game.Root, manifest);

        await InstallReShadeAsync(game.ExePath, Path.Combine(gameDir, gameReShadeDll), reShade64Dll, payload.ReShadeSetup, "d3d10", game.Root, manifest, cancellationToken);
        RemoveUnusedReShadeProxy(gameDir, game.Root, manifest, gameReShadeDll);

        RemoveConflictingRenoDxDlss5Addons(gameDir, game.Root, manifest);
        CopyWithBackup(payload.RenoDxDlss5Addon!, Path.Combine(gameDir, "renodx-dlss5.addon64"), game.Root, manifest);
        foreach (var addon in payload.ExtraAddons)
        {
            var name = Path.GetFileName(addon);
            if (name.Equals("renodx-dlss5.addon64", StringComparison.OrdinalIgnoreCase)) continue;
            CopyWithBackup(addon, Path.Combine(gameDir, name), game.Root, manifest);
        }

        foreach (var dll in payload.NvidiaDlls.Concat(payload.StreamlineDlls))
            CopyWithBackup(dll, Path.Combine(gameDir, Path.GetFileName(dll)), game.Root, manifest);

        CopyWithBackup(AppFile("Runtime", "x64-dx9-dx11", "dlss5-feed.addon64"), Path.Combine(gameDir, "dlss5-feed.addon64"), game.Root, manifest);
        var feedConfig = Path.Combine(gameDir, "dlss5-feed.cfg");
        CopyWithBackup(AppFile("Configs", "dlss5-feed-64.cfg"), feedConfig, game.Root, manifest);
        SetFlatConfigValue(feedConfig, "native_probe_seconds", "12");
        ConfigureFrameIterations(feedConfig);
        ConfigureVrEyeSplit(feedConfig, forceVrEyeSplit);

        var shaderRoot = Path.Combine(gameDir, "reshade-shaders");
        var shaderDir = Path.Combine(shaderRoot, "Shaders");
        AddDirectoryIfNew(shaderRoot, game.Root, manifest);
        AddDirectoryIfNew(shaderDir, game.Root, manifest);
        CopyWithBackup(AppFile("Runtime", "shaders", "DLSS5_Feed.fx"), Path.Combine(shaderDir, "DLSS5_Feed.fx"), game.Root, manifest);
        CopyWithBackup(AppFile("Runtime", "shaders", "ReShade.fxh"), Path.Combine(shaderDir, "ReShade.fxh"), game.Root, manifest);

        ConfigureGameReShade(gameReShadeIni, enableGeomFit: true);
        ConfigureGameReShade(gameReShadeVrIni, enableGeomFit: true);
        ConfigureGamePreset(gamePreset);
        ConfigureDirectRenoDxReShade(gameReShadeIni);
        ConfigureDirectRenoDxReShade(gameReShadeVrIni);
        ConfigureOpenVrProxyIfNeeded(game, gameReShadeIni);
        ConfigureOpenVrProxyIfNeeded(game, gameReShadeVrIni);

        var hostDir = Path.Combine(gameDir, "host64");
        Directory.CreateDirectory(hostDir);
        AddDirectoryIfNew(hostDir, game.Root, manifest);
        var hostExe = Path.Combine(hostDir, "dlss5-feed-host64.exe");
        CopyWithBackup(AppFile("Runtime", "host64", "dlss5-feed-host64.exe"), hostExe, game.Root, manifest);

        var hostReShadeIni = Path.Combine(hostDir, "ReShade.ini");
        TrackExternalWrite(hostReShadeIni, game.Root, manifest);

        await InstallReShadeAsync(hostExe, Path.Combine(hostDir, "dxgi.dll"), payload.ReShade64Dll, payload.ReShadeSetup, "d3d10", game.Root, manifest, cancellationToken);
        CopyWithBackup(payload.RenoDxDlss5Addon!, Path.Combine(hostDir, "renodx-dlss5.addon64"), game.Root, manifest);
        foreach (var dll in payload.NvidiaDlls.Concat(payload.StreamlineDlls))
            CopyWithBackup(dll, Path.Combine(hostDir, Path.GetFileName(dll)), game.Root, manifest);

        ConfigureHostReShade(hostReShadeIni);
        _log("Installed native x64 RenoDX/DLSS with feeder fallback.");
    }

    async Task InstallReShadeAsync(string targetExe, string destinationDll, string? directDll, string? setupExe, string api, string gameRoot, InstallManifest manifest, CancellationToken cancellationToken)
    {
        if (Path.GetFileName(destinationDll).Equals("openvr_api.dll", StringComparison.OrdinalIgnoreCase))
        {
            if (directDll is null)
                throw new InvalidOperationException("OpenVR shim installs need the direct ReShade64.dll payload.");

            var gameDir = Path.GetDirectoryName(destinationDll)!;
            CopyWithBackup(AppFile("Runtime", "openvr", OpenVrShimDllName), destinationDll, gameRoot, manifest);
            CopyWithBackup(directDll, Path.Combine(gameDir, "ReShade64.dll"), gameRoot, manifest);
            _log("Installed OpenVR shim ReShade loader.");
            return;
        }

        if (directDll is not null)
        {
            CopyWithBackup(directDll, destinationDll, gameRoot, manifest);
            return;
        }

        if (setupExe is null)
            throw new InvalidOperationException("Payload is missing ReShade32.dll/ReShade64.dll or ReShade_Setup_*_Addon.exe.");

        TrackExternalWrite(destinationDll, gameRoot, manifest);
        if (File.Exists(destinationDll))
            File.Delete(destinationDll);

        await RunReShadeAsync(setupExe, targetExe, api, cancellationToken);

        if (!File.Exists(destinationDll))
            throw new FileNotFoundException("ReShade setup did not create " + Path.GetFileName(destinationDll), destinationDll);
    }

    void CopyWithBackup(string source, string destination, string gameRoot, InstallManifest manifest)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var relative = Path.GetRelativePath(gameRoot, destination);
        if (File.Exists(destination))
        {
            var backup = Path.Combine(BackupRoot(gameRoot), relative);
            if (!File.Exists(backup))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
                File.Copy(destination, backup);
            }

            if (!manifest.Replaced.Any(x => x.RelativePath.Equals(relative, StringComparison.OrdinalIgnoreCase)))
                manifest.Replaced.Add(new ManifestFile { RelativePath = relative });
        }
        else if (!manifest.Added.Contains(relative, StringComparer.OrdinalIgnoreCase))
        {
            manifest.Added.Add(relative);
        }

        File.Copy(source, destination, overwrite: true);
        _log("Copied " + relative);
    }

    static void SetFlatConfigValue(string path, string key, string value)
    {
        var lines = File.Exists(path) ? File.ReadAllLines(path).ToList() : [];
        var prefix = key + "=";
        for (var i = 0; i < lines.Count; i++)
        {
            if (!lines[i].StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            lines[i] = prefix + value;
            File.WriteAllLines(path, lines);
            return;
        }

        lines.Add(prefix + value);
        File.WriteAllLines(path, lines);
    }

    void ConfigureVrEyeSplit(string feedConfig, bool forceVrEyeSplit)
    {
        SetFlatConfigValue(feedConfig, "vr_eye_split", forceVrEyeSplit ? "1" : "-1");
        _log(forceVrEyeSplit
            ? "Enabled forced VR eye split in dlss5-feed.cfg."
            : "Configured VR eye split auto mode in dlss5-feed.cfg.");
    }

    void ConfigureFrameIterations(string feedConfig)
    {
        SetFlatConfigValue(feedConfig, "iterations", _frameIterations.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (_frameIterations > 1)
            _log("Configured frame iterations=" + _frameIterations + " in dlss5-feed.cfg.");
    }

    static string GameReShadeProxyName(GameCandidate game)
    {
        if (NeedsOpenVrProxy(game))
            return "openvr_api.dll";

        if (game.Api == GraphicsApi.OpenGl)
            return "opengl32.dll";

        return game.Api == GraphicsApi.DirectX11 &&
               game.Detection.Contains("rendersystemdx11.dll", StringComparison.OrdinalIgnoreCase)
            ? "d3d11.dll"
            : "dxgi.dll";
    }

    static bool NeedsOpenVrProxy(GameCandidate game)
    {
        if (game.Api != GraphicsApi.OpenVr &&
            !(game.Api == GraphicsApi.DirectX11 &&
              game.Detection.Contains("rendersystemdx11.dll", StringComparison.OrdinalIgnoreCase)))
            return false;

        var gameDir = Path.GetDirectoryName(game.ExePath);
        return gameDir is not null && File.Exists(Path.Combine(gameDir, "openvr_api.dll"));
    }

    static string? ReShade64DllForInstall(GameCandidate game, string? payloadReShade64Dll)
    {
        if (!NeedsOpenVrProxy(game))
            return payloadReShade64Dll;

        return payloadReShade64Dll ?? AppFile("Runtime", "reshade", "ReShade64.dll");
    }

    void PrepareOpenVrProxyLibrary(string gameDir, string gameRoot, InstallManifest manifest)
    {
        var openVr = Path.Combine(gameDir, "openvr_api.dll");
        var proxyTarget = Path.Combine(gameDir, OpenVrOriginalDllName);

        if (File.Exists(openVr) && !IsReShadeRuntime(openVr))
        {
            CopyWithBackup(openVr, proxyTarget, gameRoot, manifest);
            _log("Prepared OpenVR proxy target: " + Path.GetRelativePath(gameRoot, proxyTarget));
            return;
        }

        if (File.Exists(proxyTarget))
        {
            _log("Using existing OpenVR proxy target: " + Path.GetRelativePath(gameRoot, proxyTarget));
            return;
        }

        var backup = Path.Combine(BackupRoot(gameRoot), Path.GetRelativePath(gameRoot, openVr));
        if (File.Exists(backup))
        {
            CopyWithBackup(backup, proxyTarget, gameRoot, manifest);
            _log("Recovered OpenVR proxy target from backup: " + Path.GetRelativePath(gameRoot, proxyTarget));
            return;
        }

        throw new InvalidOperationException("OpenVR proxy route needs the game's original openvr_api.dll, but only a ReShade proxy was found and no original backup exists.");
    }

    static bool IsReShadeRuntime(string path)
    {
        if (!File.Exists(path))
            return false;

        return PeReader.FindMarkers(path, ["ReShadeVersion"]).Contains("ReShadeVersion");
    }

    void RemoveUnusedReShadeProxy(string gameDir, string gameRoot, InstallManifest manifest, string activeProxy)
    {
        foreach (var candidate in new[] { "dxgi.dll", "d3d11.dll", "opengl32.dll" })
        {
            if (candidate.Equals(activeProxy, StringComparison.OrdinalIgnoreCase))
                continue;

            var path = Path.Combine(gameDir, candidate);
            if (File.Exists(path))
                RemoveRootAddonFile(path, gameRoot, manifest, "Removed unused ReShade proxy: ");
        }
    }

    void InstallDgVoodoo(string gameDir, string gameRoot, PayloadInfo payload, InstallManifest manifest, DgVoodooMode mode)
    {
        switch (mode)
        {
            case DgVoodooMode.DirectXLegacy:
                CopyRequired(payload.DgVoodooDDraw, Path.Combine(gameDir, "DDraw.dll"), "dgVoodoo2 MS/x86/DDraw.dll", gameRoot, manifest);
                CopyRequired(payload.DgVoodooD3DImm, Path.Combine(gameDir, "D3DImm.dll"), "dgVoodoo2 MS/x86/D3DImm.dll", gameRoot, manifest);
                break;
            case DgVoodooMode.DirectX8:
                CopyRequired(payload.DgVoodooD3D8, Path.Combine(gameDir, "D3D8.dll"), "dgVoodoo2 MS/x86/D3D8.dll", gameRoot, manifest);
                break;
            case DgVoodooMode.DirectX9:
                CopyRequired(payload.DgVoodooD3D9, Path.Combine(gameDir, "D3D9.dll"), "dgVoodoo2 MS/x86/D3D9.dll", gameRoot, manifest);
                break;
            case DgVoodooMode.Glide211:
                CopyRequired(payload.DgVoodooGlide, Path.Combine(gameDir, "Glide.dll"), "dgVoodoo2 3Dfx/x86/Glide.dll", gameRoot, manifest);
                break;
            case DgVoodooMode.Glide245:
                CopyRequired(payload.DgVoodooGlide2x, Path.Combine(gameDir, "Glide2x.dll"), "dgVoodoo2 3Dfx/x86/Glide2x.dll", gameRoot, manifest);
                break;
            case DgVoodooMode.Glide31:
                CopyRequired(payload.DgVoodooGlide3x, Path.Combine(gameDir, "Glide3x.dll"), "dgVoodoo2 3Dfx/x86/Glide3x.dll", gameRoot, manifest);
                break;
            case DgVoodooMode.Glide31Napalm:
                CopyRequired(payload.DgVoodooGlide3xNapalm, Path.Combine(gameDir, "Glide3x.dll"), "dgVoodoo2 3Dfx/x86/Napalm/Glide3x.dll", gameRoot, manifest);
                break;
        }

        if (payload.DgVoodooCpl is not null)
            CopyWithBackup(payload.DgVoodooCpl, Path.Combine(gameDir, "dgVoodooCpl.exe"), gameRoot, manifest);

        var dgVoodooConfig = Path.Combine(gameDir, "dgVoodoo.conf");
        CopyWithBackup(AppFile("Configs", "dgVoodoo-dx9.conf"), dgVoodooConfig, gameRoot, manifest);
        ConfigureDgVoodoo(dgVoodooConfig);
        _log("Installed dgVoodoo2 wrapper: " + mode + ".");
    }

    void CopyRequired(string? source, string destination, string label, string gameRoot, InstallManifest manifest)
    {
        if (source is null)
            throw new InvalidOperationException("Payload is missing: " + label);
        CopyWithBackup(source, destination, gameRoot, manifest);
    }

    void RemoveInactiveX86RouteArtifacts(string gameDir, string gameRoot, InstallManifest manifest, string activeProxy, DgVoodooMode dgVoodooMode, bool keepD3D8To9)
    {
        var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { activeProxy };
        if (keepD3D8To9)
            keep.Add("d3d8.dll");
        foreach (var candidate in DgVoodooWrapperNames(dgVoodooMode))
            keep.Add(candidate);

        foreach (var candidate in new[] { "d3d8.dll", "D3D8.dll", "D3D9.dll", "D3DImm.dll", "DDraw.dll", "Glide.dll", "Glide2x.dll", "Glide3x.dll", "dxgi.dll", "opengl32.dll" })
        {
            if (keep.Contains(candidate))
                continue;

            var path = Path.Combine(gameDir, candidate);
            if (File.Exists(path))
                RemoveRootAddonFile(path, gameRoot, manifest, "Removed inactive wrapper: ");
        }

        if (dgVoodooMode != DgVoodooMode.None)
            return;

        foreach (var candidate in new[] { "dgVoodoo.conf", "dgVoodooCpl.exe" })
        {
            var path = Path.Combine(gameDir, candidate);
            if (File.Exists(path))
                RemoveRootAddonFile(path, gameRoot, manifest, "Removed inactive dgVoodoo file: ");
        }
    }

    static IEnumerable<string> DgVoodooWrapperNames(DgVoodooMode mode) => mode switch
    {
        DgVoodooMode.DirectXLegacy => new[] { "DDraw.dll", "D3DImm.dll" },
        DgVoodooMode.DirectX8 => new[] { "D3D8.dll" },
        DgVoodooMode.DirectX9 => new[] { "D3D9.dll" },
        DgVoodooMode.Glide211 => new[] { "Glide.dll" },
        DgVoodooMode.Glide245 => new[] { "Glide2x.dll" },
        DgVoodooMode.Glide31 or DgVoodooMode.Glide31Napalm => new[] { "Glide3x.dll" },
        _ => Array.Empty<string>()
    };

    void RemoveRootRenoDxAddons(string gameDir, string gameRoot, InstallManifest manifest)
    {
        foreach (var file in Directory.EnumerateFiles(gameDir, "renodx-dlss5*.addon64", SearchOption.TopDirectoryOnly))
            RemoveRootAddonFile(file, gameRoot, manifest, "Removed root RenoDX add-on for feeder-host route: ");
    }

    void RemoveConflictingRenoDxDlss5Addons(string gameDir, string gameRoot, InstallManifest manifest)
    {
        foreach (var file in Directory.EnumerateFiles(gameDir, "renodx-dlss5*.addon64", SearchOption.TopDirectoryOnly))
        {
            if (Path.GetFileName(file).Equals("renodx-dlss5.addon64", StringComparison.OrdinalIgnoreCase))
                continue;
            RemoveRootAddonFile(file, gameRoot, manifest, "Removed conflicting RenoDX DLSS5 add-on: ");
        }
    }

    void RemoveVulkanFeederArtifacts(string gameDir, string gameRoot, InstallManifest manifest)
    {
        foreach (var candidate in new[] { "dlss5-feed.addon64", "dlss5-feed.addon32", "dlss5-feed.cfg" })
        {
            var path = Path.Combine(gameDir, candidate);
            if (File.Exists(path))
                RemoveRootAddonFile(path, gameRoot, manifest, "Removed feeder artifact from Vulkan route: ");
        }
    }

    void RemoveVulkanBridgeArtifacts(string gameDir, string gameRoot, InstallManifest manifest)
    {
        foreach (var candidate in new[] { "dlss5-bridge.addon64", "dlss5-bridge.cfg" })
        {
            var path = Path.Combine(gameDir, candidate);
            if (File.Exists(path))
                RemoveRootAddonFile(path, gameRoot, manifest, "Removed Vulkan bridge artifact for feeder route: ");
        }
    }

    void RemoveOpenXrConflictingRootArtifacts(string gameDir, string gameRoot, InstallManifest manifest)
    {
        foreach (var candidate in new[] { "dxgi.dll", "d3d11.dll", "opengl32.dll" })
        {
            var path = Path.Combine(gameDir, candidate);
            if (File.Exists(path) && IsReShadeRuntime(path))
                RemoveRootAddonFile(path, gameRoot, manifest, "Removed root ReShade proxy for OpenXR route: ");
        }

        foreach (var candidate in new[]
        {
            "dlss5-feed.addon64",
            "dlss5-feed.addon32",
            "dlss5-feed.cfg",
            "ReShade.ini",
            "ReShadeVR.ini",
            "ReShadePreset.ini"
        })
        {
            var path = Path.Combine(gameDir, candidate);
            if (File.Exists(path))
                RemoveRootAddonFile(path, gameRoot, manifest, "Removed root ReShade/feeder artifact for OpenXR route: ");
        }
    }

    void RemoveRootAddonFile(string path, string gameRoot, InstallManifest manifest, string message)
    {
        if (!File.Exists(path)) return;

        var relative = Path.GetRelativePath(gameRoot, path);
        var backup = Path.Combine(BackupRoot(gameRoot), relative);
        if (!File.Exists(backup))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
            File.Copy(path, backup);
        }

        if (!manifest.Replaced.Any(x => x.RelativePath.Equals(relative, StringComparison.OrdinalIgnoreCase)))
            manifest.Replaced.Add(new ManifestFile { RelativePath = relative });

        File.Delete(path);
        _log(message + relative);
    }

    void AddDirectoryIfNew(string directory, string gameRoot, InstallManifest manifest)
    {
        if (Directory.Exists(directory) && Directory.EnumerateFileSystemEntries(directory).Any()) return;
        var relative = Path.GetRelativePath(gameRoot, directory);
        if (!manifest.Added.Contains(relative, StringComparer.OrdinalIgnoreCase))
            manifest.Added.Add(relative);
    }

    void TrackExternalWrite(string destination, string gameRoot, InstallManifest manifest)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var relative = Path.GetRelativePath(gameRoot, destination);
        if (File.Exists(destination))
        {
            var backup = Path.Combine(BackupRoot(gameRoot), relative);
            if (!File.Exists(backup))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
                File.Copy(destination, backup);
            }

            if (!manifest.Replaced.Any(x => x.RelativePath.Equals(relative, StringComparison.OrdinalIgnoreCase)))
                manifest.Replaced.Add(new ManifestFile { RelativePath = relative });
        }
        else if (!manifest.Added.Contains(relative, StringComparer.OrdinalIgnoreCase))
        {
            manifest.Added.Add(relative);
        }
    }

    async Task RunReShadeAsync(string setup, string targetExe, string api, CancellationToken cancellationToken)
    {
        _log($"Running ReShade installer for {Path.GetFileName(targetExe)} as {api}...");
        var start = new ProcessStartInfo
        {
            FileName = setup,
            WorkingDirectory = Path.GetDirectoryName(targetExe)!,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        start.ArgumentList.Add(targetExe);
        start.ArgumentList.Add("--api");
        start.ArgumentList.Add(api);
        start.ArgumentList.Add("--headless");

        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start ReShade installer.");
        using var registration = cancellationToken.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch { }
        });

        var finished = await Task.Run(() => process.WaitForExit(120_000), cancellationToken);
        if (!finished)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException("ReShade installer timed out.");
        }

        _log($"ReShade installer exited with code {process.ExitCode}.");
    }

    async Task InstallVulkanReShadeLayerAsync(string targetExe, string? reShade64Dll, string? setupExe, CancellationToken cancellationToken)
    {
        if (setupExe is null)
            throw new InvalidOperationException("Vulkan route needs ReShade_Setup_*_Addon.exe so the Vulkan layer can be registered.");

        await RunReShadeAsync(setupExe, targetExe, "vulkan", cancellationToken);
        EnsureVulkanAppAllowListed(targetExe);
        EnsureUserVulkanLayer(reShade64Dll);
        TryRenameVulkanDisableEnvironment("ReShade64.json");
        _log("Ran ReShade Vulkan-layer setup for " + Path.GetFileName(targetExe) + ".");
    }

    void EnsureVulkanAppAllowListed(string targetExe)
    {
        foreach (var root in new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ReShade"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ReShade")
        })
        {
            try
            {
                Directory.CreateDirectory(root);
                var appsIni = Path.Combine(root, "ReShadeApps.ini");
                var lines = File.Exists(appsIni) ? File.ReadAllLines(appsIni).ToList() : [];
                var appsIndex = lines.FindIndex(x => x.StartsWith("Apps=", StringComparison.OrdinalIgnoreCase));
                var apps = appsIndex >= 0
                    ? lines[appsIndex][5..].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList()
                    : [];

                if (!apps.Contains(targetExe, StringComparer.OrdinalIgnoreCase))
                {
                    apps.Add(targetExe);
                    if (appsIndex >= 0)
                        lines[appsIndex] = "Apps=" + string.Join(",", apps);
                    else
                        lines.Add("Apps=" + string.Join(",", apps));
                    File.WriteAllLines(appsIni, lines);
                    _log("Added Vulkan ReShade allow-list entry: " + appsIni);
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
            {
                _log("Could not update Vulkan ReShade allow-list in " + root + ": " + ex.Message);
            }
        }
    }

    void RemoveVulkanAppAllowListEntry(string targetExe)
    {
        foreach (var root in new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ReShade"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ReShade")
        })
        {
            try
            {
                var appsIni = Path.Combine(root, "ReShadeApps.ini");
                if (!File.Exists(appsIni))
                    continue;

                var lines = File.ReadAllLines(appsIni).ToList();
                var appsIndex = lines.FindIndex(x => x.StartsWith("Apps=", StringComparison.OrdinalIgnoreCase));
                if (appsIndex < 0)
                    continue;

                var apps = lines[appsIndex][5..]
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Where(x => !x.Equals(targetExe, StringComparison.OrdinalIgnoreCase))
                    .ToArray();

                var updated = "Apps=" + string.Join(",", apps);
                if (lines[appsIndex].Equals(updated, StringComparison.Ordinal))
                    continue;

                lines[appsIndex] = updated;
                File.WriteAllLines(appsIni, lines);
                _log("Removed Vulkan ReShade allow-list entry: " + appsIni);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
            {
                _log("Could not update Vulkan ReShade allow-list in " + root + ": " + ex.Message);
            }
        }
    }

    void EnsureUserVulkanLayer(string? reShade64Dll)
    {
        try
        {
            var sourceDll = reShade64Dll ?? AppFile("Runtime", "reshade", "ReShade64.dll");
            var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ReShade");
            Directory.CreateDirectory(root);

            var targetDll = Path.Combine(root, "ReShade64.dll");
            File.Copy(sourceDll, targetDll, overwrite: true);

            var jsonPath = Path.Combine(root, "ReShade64_Dlss5Compat.json");
            var json = """
{
	"file_format_version": "1.0.0",
	"layer": {
		"name": "VK_LAYER_reshade",
		"type": "GLOBAL",
		"library_path": ".\\ReShade64.dll",
		"api_version": "1.3.268",
		"implementation_version": "1",
		"description": "crosire's ReShade post-processing injector for 64-bit",
		"device_extensions": [
			{
				"name": "VK_EXT_tooling_info",
				"spec_version": "1",
				"entrypoints": [ "vkGetPhysicalDeviceToolPropertiesEXT" ]
			}
		],
		"disable_environment": {
			"DLSS5_DISABLE_VK_LAYER_reshade_1": "1"
		}
	}
}
""";
            File.WriteAllText(jsonPath, json);

            using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"SOFTWARE\Khronos\Vulkan\ImplicitLayers", writable: true);
            if (key is null)
                throw new IOException("Could not open HKCU Vulkan ImplicitLayers.");
            key.SetValue(jsonPath, 0, Microsoft.Win32.RegistryValueKind.DWord);
            _log("Registered per-user ReShade Vulkan layer: " + jsonPath);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            _log("Could not register per-user ReShade Vulkan layer: " + ex.Message);
        }
    }

    void TryRenameVulkanDisableEnvironment(string jsonName)
    {
        var json = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ReShade", jsonName);
        try
        {
            if (!File.Exists(json))
                return;

            var text = File.ReadAllText(json);
            const string original = "\"DISABLE_VK_LAYER_reshade_1\"";
            const string replacement = "\"DISABLE_VK_LAYER_reshade_2\"";
            if (!text.Contains(original, StringComparison.Ordinal))
                return;

            var backup = json + ".dlss5compat.bak";
            if (!File.Exists(backup))
                File.Copy(json, backup);
            File.WriteAllText(json, text.Replace(original, replacement, StringComparison.Ordinal));
            _log("Renamed ReShade Vulkan disable environment key in " + jsonName + " so games cannot silently disable the layer.");
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            if (TryRunElevatedVulkanDisablePatch(json, "DISABLE_VK_LAYER_reshade_2"))
            {
                _log("Applied elevated ReShade Vulkan disable-key patch for " + jsonName + ".");
            }
            else
            {
                _log("Could not update " + jsonName + " Vulkan disable key. ReShade may stay blocked in games that disable VK_LAYER_reshade: " + ex.Message);
            }
        }
    }

    bool TryRunElevatedVulkanDisablePatch(string json, string replacementName)
    {
        var processPath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(processPath) ||
            Path.GetFileName(processPath).Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase))
            return false;

        try
        {
            var start = new ProcessStartInfo
            {
                FileName = processPath,
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden
            };
            start.ArgumentList.Add(VulkanDisablePatchCommand);
            start.ArgumentList.Add(json);
            start.ArgumentList.Add(replacementName);

            using var process = Process.Start(start);
            if (process is null)
                return false;

            if (!process.WaitForExit(120_000))
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return false;
            }

            return process.ExitCode == 0 &&
                   !File.ReadAllText(json).Contains("\"DISABLE_VK_LAYER_reshade_1\"", StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException)
        {
            _log("Elevated ReShade Vulkan disable-key patch was not completed: " + ex.Message);
            return false;
        }
    }

    static void ConfigureGameReShade(string ini, bool enableGeomFit)
    {
        IniEditor.SetValue(ini, "GENERAL", "EffectSearchPaths", @".\reshade-shaders\Shaders\**");
        IniEditor.SetValue(ini, "GENERAL", "TextureSearchPaths", @".\reshade-shaders\Textures\**");
        IniEditor.SetValue(ini, "GENERAL", "PresetPath", @".\ReShadePreset.ini");
        ConfigureQuietReShadeUi(ini);
        IniEditor.SetCsvDefinition(ini, "GENERAL", "PreprocessorDefinitions", "RESHADE_DEPTH_INPUT_IS_REVERSED=1");
        IniEditor.SetCsvDefinition(ini, "GENERAL", "PreprocessorDefinitions", "DLSS5_MV_PROVIDER=0");
        IniEditor.SetCsvDefinition(ini, "GENERAL", "PreprocessorDefinitions", "DLSS5_GEOM_FIT=" + (enableGeomFit ? "1" : "0"));
        IniEditor.SetValue(ini, "INPUT", "KeyScreenshot", "0,0,0,0");
    }

    static void ConfigureGamePreset(string preset)
    {
        IniEditor.SetValue(preset, "GENERAL", "Techniques", "DLSS5_Feed@DLSS5_Feed.fx");
        IniEditor.SetValue(preset, "GENERAL", "TechniqueSorting", "DLSS5_Feed@DLSS5_Feed.fx,DLSS5_Feed_Debug@DLSS5_Feed.fx");
    }

    static void ConfigureNoEffectsPreset(string preset)
    {
        IniEditor.SetValue(preset, "GENERAL", "Techniques", "");
        IniEditor.SetValue(preset, "GENERAL", "TechniqueSorting", "");
    }

    static void ConfigureHostReShade(string ini)
    {
        IniEditor.SetValue(ini, "GENERAL", "EffectSearchPaths", @".\reshade-shaders\Shaders\**");
        IniEditor.SetValue(ini, "GENERAL", "TextureSearchPaths", @".\reshade-shaders\Textures\**");
        IniEditor.SetValue(ini, "GENERAL", "PresetPath", @".\ReShadePreset.ini");
        ConfigureQuietReShadeUi(ini);
        IniEditor.SetValue(ini, "INPUT", "KeyOverlay", "36,0,0,0");
        IniEditor.SetValue(ini, "INPUT", "KeyScreenshot", "0,0,0,0");
        ConfigureRenoDxDlss5(ini);
    }

    static void ConfigureDirectRenoDxReShade(string ini)
    {
        ConfigureQuietReShadeUi(ini);
        IniEditor.SetValue(ini, "INPUT", "KeyOverlay", "36,0,0,0");
        IniEditor.SetValue(ini, "INPUT", "KeyScreenshot", "0,0,0,0");
        IniEditor.SetValue(ini, "ADDON", "DisabledAddons", "");
        ConfigureRenoDxDlss5(ini);
    }

    static void ConfigureVulkanBridgeReShade(string ini)
    {
        ConfigureQuietReShadeUi(ini);
        IniEditor.SetValue(ini, "GENERAL", "EffectSearchPaths", "");
        IniEditor.SetValue(ini, "GENERAL", "TextureSearchPaths", "");
        IniEditor.SetValue(ini, "GENERAL", "PresetPath", @".\ReShadePreset.ini");
        IniEditor.SetValue(ini, "INPUT", "KeyOverlay", "36,0,0,0");
        IniEditor.SetValue(ini, "INPUT", "KeyScreenshot", "0,0,0,0");
        IniEditor.SetValue(ini, "ADDON", "DisabledAddons", "");
        ConfigureRenoDxDlss5(ini);
    }

    static void ConfigureDlss5Bridge(string cfg)
    {
        SetFlatConfigValue(cfg, "mode", "2");
        SetFlatConfigValue(cfg, "stage", "3");
        SetFlatConfigValue(cfg, "skip_game", "1");
        SetFlatConfigValue(cfg, "vk_mirror", "1");
        SetFlatConfigValue(cfg, "source", "auto");
        SetFlatConfigValue(cfg, "synth", "1");
        SetFlatConfigValue(cfg, "synth_after", "10");
        SetFlatConfigValue(cfg, "flags", "-1");
        SetFlatConfigValue(cfg, "subrects", "1");
        SetFlatConfigValue(cfg, "reset_every", "0");
        SetFlatConfigValue(cfg, "pixels", "0");
        SetFlatConfigValue(cfg, "dred", "1");
        SetFlatConfigValue(cfg, "skip_exe", "1");
        SetFlatConfigValue(cfg, "unwrap", "1");
        SetFlatConfigValue(cfg, "probe", "0");
        SetFlatConfigValue(cfg, "hash_out", "0");
        SetFlatConfigValue(cfg, "mv_sign_x", "0");
        SetFlatConfigValue(cfg, "mv_sign_y", "0");
        SetFlatConfigValue(cfg, "ofa_grid", "2");
        SetFlatConfigValue(cfg, "ofa_perf", "20");
    }

    static void ConfigureOpenVrProxyIfNeeded(GameCandidate game, string ini)
    {
        if (!NeedsOpenVrProxy(game))
            return;

        IniEditor.SetValue(ini, "PROXY", "EnableProxyLibrary", "1");
        IniEditor.SetValue(ini, "PROXY", "ProxyLibrary", @".\" + OpenVrOriginalDllName);
    }

    static void ConfigureRenoDxDlss5(string ini)
    {
        IniEditor.SetValue(ini, "RenoDX.DLSS5", "NeuralUplift", "1");
        IniEditor.SetValue(ini, "RenoDX.DLSS5", "NREnableUpscaling", "1");
        IniEditor.SetValue(ini, "RenoDX.DLSS5", "NRPreset", "3");
        IniEditor.SetValue(ini, "RenoDX.DLSS5", "NRStyle", "1");
        IniEditor.SetValue(ini, "RenoDX.DLSS5", "NRIntensity", "2.000000");
        IniEditor.SetValue(ini, "RenoDX.DLSS5", "NRLocalStructure", "2.000000");
        IniEditor.SetValue(ini, "RenoDX.DLSS5", "NRLocalTone", "2.000000");
        IniEditor.SetValue(ini, "RenoDX.DLSS5", "NRSkinStructure", "2.000000");
        IniEditor.SetValue(ini, "RenoDX.DLSS5", "NRAutoMask", "1");
        IniEditor.SetValue(ini, "RenoDX.DLSS5", "NRUICorrection", "1");
        IniEditor.SetValue(ini, "RenoDX.DLSS5", "NRDepthMode", "2");
        IniEditor.SetValue(ini, "RenoDX.DLSS5", "NRMVecScaleX", "4");
        IniEditor.SetValue(ini, "RenoDX.DLSS5", "NRMVecScaleY", "4");
        IniEditor.SetValue(ini, "RenoDX.DLSS5", "NRPaperWhiteScale", "16.000000");
        IniEditor.SetValue(ini, "RenoDX.DLSS5", "NRTransferStrength", "2.000000");
        IniEditor.SetValue(ini, "RenoDX.DLSS5", "NRColorStrength", "2.000000");
        IniEditor.SetValue(ini, "RenoDX.DLSS5", "EnableHooks", "2");
    }

    static void EnableAddons(string ini)
    {
        ConfigureQuietReShadeUi(ini);
        IniEditor.SetValue(ini, "ADDON", "DisabledAddons", "");
    }

    static void ConfigureQuietReShadeUi(string ini)
    {
        IniEditor.SetValue(ini, "ADDON", "AddonPath", @".\");
        IniEditor.SetValue(ini, "ADDON", "DisabledAddons", "");
        IniEditor.SetValue(ini, "GENERAL", "TutorialProgress", "4");
        IniEditor.SetValue(ini, "OVERLAY", "TutorialProgress", "4");
        IniEditor.SetValue(ini, "OVERLAY", "ShowOverlay", "0");
        IniEditor.SetValue(ini, "OVERLAY", "ShowClock", "0");
        IniEditor.SetValue(ini, "OVERLAY", "ShowFPS", "0");
        IniEditor.SetValue(ini, "OVERLAY", "ShowFrameTime", "0");
        IniEditor.SetValue(ini, "OVERLAY", "ShowPresetName", "0");
        IniEditor.SetValue(ini, "OVERLAY", "ShowPresetTransitionMessage", "0");
        IniEditor.SetValue(ini, "OVERLAY", "ShowScreenshotMessage", "0");
    }

    void TryDisableNvidiaDlssIndicator()
    {
        const string subKey = @"SOFTWARE\NVIDIA Corporation\Global\NGXCore";
        try
        {
            DisableNvidiaDlssIndicator(Microsoft.Win32.RegistryView.Registry64, subKey);
            DisableNvidiaDlssIndicator(Microsoft.Win32.RegistryView.Registry32, subKey);
            _log("Disabled NVIDIA DLSS on-screen indicator for 64-bit and 32-bit NGX.");
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            _log("Could not disable NVIDIA DLSS on-screen indicator globally. Run the app as administrator or set HKLM\\SOFTWARE\\NVIDIA Corporation\\Global\\NGXCore\\ShowDlssIndicator=0 manually.");
        }
    }

    static void DisableNvidiaDlssIndicator(Microsoft.Win32.RegistryView view, string subKey)
    {
        using var localMachine = Microsoft.Win32.RegistryKey.OpenBaseKey(Microsoft.Win32.RegistryHive.LocalMachine, view);
        using var ngxCore = localMachine.CreateSubKey(subKey, writable: true);
        if (ngxCore is null) throw new IOException("Could not open NVIDIA NGXCore registry key.");
        ngxCore.SetValue("ShowDlssIndicator", 0, Microsoft.Win32.RegistryValueKind.DWord);
    }

    static void ConfigureDgVoodoo(string ini)
    {
        IniEditor.SetValue(ini, "GeneralExt", "DesktopResolution", "false");
        IniEditor.SetValue(ini, "DirectX", "dgVoodooWatermark", "false");
        IniEditor.SetValue(ini, "Glide", "dgVoodooWatermark", "false");
    }

    static void EnsureWritable(string directory)
    {
        var probe = Path.Combine(directory, ".dlss5compat-write-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            File.WriteAllText(probe, "x");
            File.Delete(probe);
        }
        catch (Exception ex)
        {
            throw new UnauthorizedAccessException("Game folder is not writable. Run the app as administrator or move the game out of a protected folder.", ex);
        }
    }

    static async Task WriteManifestAsync(string gameRoot, InstallManifest manifest, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(ManifestPath(gameRoot), json, cancellationToken);
    }

    void WriteLauncherBatch(GameCandidate game, InstallManifest manifest)
    {
        var gameDir = Path.GetDirectoryName(game.ExePath)!;
        var exeName = Path.GetFileName(game.ExePath);
        var batchName = BatchFileName(game);
        var batchPath = Path.Combine(gameDir, batchName);
        TrackExternalWrite(batchPath, game.Root, manifest);

        var relativeBatch = Path.GetRelativePath(game.Root, batchPath);
        var added = manifest.Added
            .Where(x => !x.Equals(relativeBatch, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(x => x.Count(c => c == '\\'))
            .ThenByDescending(x => x.Length)
            .ToArray();
        var replaced = manifest.Replaced
            .Select(x => x.RelativePath)
            .OrderByDescending(x => x.Count(c => c == '\\'))
            .ThenByDescending(x => x.Length)
            .ToArray();

        var text = new StringBuilder();
        text.AppendLine("@echo off");
        text.AppendLine("setlocal");
        text.AppendLine("set \"GAME_DIR=%~dp0\"");
        text.AppendLine("set \"GAME_EXE=" + BatchEscape(exeName) + "\"");
        text.AppendLine("set \"BACKUP_DIR=%GAME_DIR%" + BackupDirectoryName + "\"");
        text.AppendLine();
        text.AppendLine("if /I \"%~1\"==\"--help\" goto help");
        text.AppendLine("if /I \"%~1\"==\"/help\" goto help");
        text.AppendLine("if /I \"%~1\"==\"-h\" goto help");
        text.AppendLine("if /I \"%~1\"==\"--uninstall\" goto uninstall");
        text.AppendLine();
        text.AppendLine("pushd \"%GAME_DIR%\" >nul");
        if (game.Route == InstallRoute.X64OpenXrLayer)
        {
            text.AppendLine("set \"XR_API_LAYER_PATH=%GAME_DIR%_DLSS5_OpenXR\\openxr\"");
            text.AppendLine("set \"XR_ENABLE_API_LAYERS=XR_APILAYER_DLSS5_everything\"");
            text.AppendLine("set \"DLSS5_OPENXR_ENABLED=1\"");
            text.AppendLine("set \"DLSS5_OPENXR_WARMUP_RELEASES=60\"");
            text.AppendLine("set \"DLSS5_OPENXR_PROCESS_EVERY=1\"");
            text.AppendLine("set \"DLSS5_OPENXR_MAX_BLOCK_MS=1500\"");
            text.AppendLine("set \"DLSS5_OPENXR_PROCESS_TIMEOUT_MS=5000\"");
        }
        if (game.Route == InstallRoute.X64OpenVrFeeder)
        {
            if (TryFindSteamAppId(game.Root) is { Length: > 0 } steamAppId)
            {
                text.AppendLine("set \"SteamAppId=" + BatchEscape(steamAppId) + "\"");
                text.AppendLine("set \"SteamGameId=" + BatchEscape(steamAppId) + "\"");
            }
            text.AppendLine("set \"DLSS5_DISABLE_VK_LAYER_reshade_1=1\"");
            text.AppendLine("set \"DISABLE_VK_LAYER_reshade_1=1\"");
            text.AppendLine("set \"DISABLE_VK_LAYER_reshade_2=1\"");
            text.AppendLine("set \"VK_LOADER_LAYERS_DISABLE=VK_LAYER_reshade\"");
        }
        var defaultArgs = string.IsNullOrWhiteSpace(game.SuggestedArguments)
            ? ""
            : " " + BatchEscape(game.SuggestedArguments);
        text.AppendLine("set \"DLSS5_WAIT=\"");
        text.AppendLine("if /I \"%~1\"==\"--wait\" (");
        text.AppendLine("  set \"DLSS5_WAIT=/wait \"");
        text.AppendLine("  shift");
        text.AppendLine(")");
        text.AppendLine("start \"\" %DLSS5_WAIT%\"%GAME_EXE%\"" + defaultArgs + " %*");
        text.AppendLine("popd >nul");
        text.AppendLine("exit /b 0");
        text.AppendLine();
        text.AppendLine(":help");
        text.AppendLine("echo " + BatchEcho("DLSS5 compatibility launcher for " + exeName));
        text.AppendLine("echo " + BatchEcho("Route: " + game.DisplayRoute));
        text.AppendLine("echo.");
        text.AppendLine("echo " + BatchEcho("Run without arguments to start the game."));
        text.AppendLine("echo " + BatchEcho("Run with --wait to keep this launcher open until the game exits."));
        text.AppendLine("echo " + BatchEcho("Run with --uninstall to remove files added by the installer and restore replaced files when backups exist."));
        text.AppendLine("exit /b 0");
        text.AppendLine();
        text.AppendLine(":uninstall");
        text.AppendLine("echo " + BatchEcho("Removing DLSS5 compatibility files for " + exeName + "..."));
        text.AppendLine();
        foreach (var item in added)
            text.AppendLine("rem DLSS5_ADDED: " + item);
        foreach (var item in replaced)
            text.AppendLine("rem DLSS5_REPLACED: " + item);
        text.AppendLine();
        foreach (var item in replaced)
        {
            var path = BatchEscape(item);
            text.AppendLine("if exist \"%BACKUP_DIR%\\" + path + "\" (");
            text.AppendLine("  if not exist \"%GAME_DIR%" + BatchEscape(Path.GetDirectoryName(item) ?? "") + "\" mkdir \"%GAME_DIR%" + BatchEscape(Path.GetDirectoryName(item) ?? "") + "\" >nul 2>nul");
            text.AppendLine("  copy /Y \"%BACKUP_DIR%\\" + path + "\" \"%GAME_DIR%" + path + "\" >nul");
            text.AppendLine(")");
        }
        foreach (var item in added)
        {
            var path = BatchEscape(item);
            text.AppendLine("if exist \"%GAME_DIR%" + path + "\" del /F /Q \"%GAME_DIR%" + path + "\" >nul 2>nul");
            text.AppendLine("if exist \"%GAME_DIR%" + path + "\\\" rd /S /Q \"%GAME_DIR%" + path + "\" >nul 2>nul");
        }
        text.AppendLine("if exist \"%BACKUP_DIR%\" rd /S /Q \"%BACKUP_DIR%\" >nul 2>nul");
        text.AppendLine("echo " + BatchEcho("Done."));
        text.AppendLine("del /F /Q \"%~f0\" >nul 2>nul");
        text.AppendLine("exit /b 0");

        File.WriteAllText(batchPath, text.ToString(), Encoding.ASCII);
        _log("Wrote launcher " + relativeBatch);
    }

    public static string LauncherPathFor(GameCandidate game) =>
        Path.Combine(Path.GetDirectoryName(game.ExePath)!, BatchFileName(game));

    public static string LaunchTargetFor(GameCandidate game)
    {
        var exact = LauncherPathFor(game);
        if (File.Exists(exact))
            return exact;

        var gameDir = Path.GetDirectoryName(game.ExePath);
        if (string.IsNullOrWhiteSpace(gameDir))
            return game.ExePath;

        var exe = SanitizeBatchName(Path.GetFileNameWithoutExtension(game.ExePath));
        var manifest = TryReadManifest(game.Root);
        if (manifest is not null)
        {
            foreach (var relative in manifest.Added.Where(x => x.EndsWith(".bat", StringComparison.OrdinalIgnoreCase)))
            {
                var candidate = Path.Combine(game.Root, relative);
                if (File.Exists(candidate) &&
                    Path.GetFileName(candidate).StartsWith(exe + "-dlss5-", StringComparison.OrdinalIgnoreCase))
                    return candidate;
            }
        }

        return Directory.EnumerateFiles(gameDir, exe + "-dlss5-*.bat", SearchOption.TopDirectoryOnly)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault() ?? game.ExePath;
    }

    static string BatchFileName(GameCandidate game)
    {
        var exe = SanitizeBatchName(Path.GetFileNameWithoutExtension(game.ExePath));
        return exe + "-dlss5-" + RouteSlug(game) + ".bat";
    }

    static string RouteSlug(GameCandidate game) => game.Api switch
    {
        GraphicsApi.DirectX7OrOlder => "directx1-7",
        GraphicsApi.DirectX8 => "dx8",
        GraphicsApi.DirectX8DgVoodoo => "dx8-dgvoodoo",
        GraphicsApi.DirectX9 => game.Route == InstallRoute.X86Dx9ViaDgVoodoo ? "dx9-dgvoodoo" : "dx9",
        GraphicsApi.DirectX11 => "dx11",
        GraphicsApi.DirectX12 => "dx12",
        GraphicsApi.Dxgi => "dxgi",
        GraphicsApi.OpenXr => "openxr",
        GraphicsApi.OpenVr => "openvr",
        GraphicsApi.Vulkan => "vulkan",
        GraphicsApi.OpenGl => "opengl",
        GraphicsApi.Glide211 => "glide211",
        GraphicsApi.Glide245 => "glide245",
        GraphicsApi.Glide31 => "glide31",
        GraphicsApi.Glide31Napalm => "glide31-napalm",
        _ => SanitizeBatchName(game.DisplayApi).ToLowerInvariant()
    };

    static string SanitizeBatchName(string value)
    {
        var chars = value.Select(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' ? ch : '-').ToArray();
        var cleaned = new string(chars).Trim('-');
        while (cleaned.Contains("--", StringComparison.Ordinal))
            cleaned = cleaned.Replace("--", "-", StringComparison.Ordinal);
        return string.IsNullOrWhiteSpace(cleaned) ? "game" : cleaned;
    }

    static string BatchEscape(string value) =>
        value.Replace("%", "%%", StringComparison.Ordinal);

    static string BatchEcho(string value) =>
        BatchEscape(value).Replace("^", "^^", StringComparison.Ordinal).Replace("&", "^&", StringComparison.Ordinal).Replace("|", "^|", StringComparison.Ordinal).Replace("<", "^<", StringComparison.Ordinal).Replace(">", "^>", StringComparison.Ordinal);

    static InstallManifest? TryReadManifest(string gameRoot)
    {
        try
        {
            var manifestPath = ManifestPath(gameRoot);
            return File.Exists(manifestPath)
                ? JsonSerializer.Deserialize<InstallManifest>(File.ReadAllText(manifestPath))
                : null;
        }
        catch
        {
            return null;
        }
    }

    static string? TryFindSteamAppId(string gameRoot)
    {
        try
        {
            var dir = new DirectoryInfo(gameRoot);
            while (dir?.Parent is not null)
            {
                if (dir.Parent.Name.Equals("common", StringComparison.OrdinalIgnoreCase) &&
                    dir.Parent.Parent?.Name.Equals("steamapps", StringComparison.OrdinalIgnoreCase) == true)
                {
                    var installDir = dir.Name;
                    foreach (var manifest in Directory.EnumerateFiles(dir.Parent.Parent.FullName, "appmanifest_*.acf", SearchOption.TopDirectoryOnly))
                    {
                        var text = File.ReadAllText(manifest);
                        var appId = Regex.Match(text, "\"appid\"\\s+\"(?<value>\\d+)\"", RegexOptions.IgnoreCase).Groups["value"].Value;
                        var manifestInstallDir = Regex.Match(text, "\"installdir\"\\s+\"(?<value>[^\"]+)\"", RegexOptions.IgnoreCase).Groups["value"].Value;
                        if (appId.Length > 0 && manifestInstallDir.Equals(installDir, StringComparison.OrdinalIgnoreCase))
                            return appId;
                    }
                }
                dir = dir.Parent;
            }
        }
        catch
        {
        }

        return null;
    }

    static string AppFile(params string[] parts)
    {
        var path = Path.Combine(new[] { AppContext.BaseDirectory }.Concat(parts).ToArray());
        if (File.Exists(path)) return path;

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray());
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }

        throw new FileNotFoundException("Bundled app file is missing: " + Path.Combine(parts));
    }

    static string BackupRoot(string gameRoot) => Path.Combine(gameRoot, BackupDirectoryName);
    static string ManifestPath(string gameRoot) => Path.Combine(BackupRoot(gameRoot), "manifest.json");
}
