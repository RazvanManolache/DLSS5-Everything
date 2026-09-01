using System.Diagnostics;
using System.Text.Json;

namespace Dlss5CompatApp;

sealed class InstallerEngine
{
    const string BackupDirectoryName = "_DLSS5_Compat_Backup";

    readonly Action<string> _log;

    public InstallerEngine(Action<string> log)
    {
        _log = log;
    }

    public async Task InstallAsync(GameCandidate game, PayloadInfo payload, CancellationToken cancellationToken = default)
    {
        if (game.Route == InstallRoute.Unsupported)
            throw new InvalidOperationException("This executable/API combination is not supported.");

        var missing = payload.MissingFor(game.Route);
        if (!string.IsNullOrWhiteSpace(missing))
            throw new InvalidOperationException("Payload is missing: " + missing);

        var gameDir = Path.GetDirectoryName(game.ExePath)!;
        EnsureWritable(gameDir);

        var manifest = new InstallManifest
        {
            GameRoot = game.Root,
            ExeRelativePath = Path.GetRelativePath(game.Root, game.ExePath),
            Route = game.Route.ToString()
        };

        Directory.CreateDirectory(BackupRoot(game.Root));

        switch (game.Route)
        {
            case InstallRoute.X86Dx9NativeFeeder:
                await InstallX86Async(game, payload, manifest, gameReShadeDllName: "D3D9.dll", gameReShadeApi: "d3d9", installDgVoodoo: false, cancellationToken);
                break;
            case InstallRoute.X86Dx9ViaDgVoodoo:
                await InstallX86Async(game, payload, manifest, gameReShadeDllName: "dxgi.dll", gameReShadeApi: "d3d10", installDgVoodoo: true, cancellationToken);
                break;
            case InstallRoute.X86DxgiFeeder:
                await InstallX86Async(game, payload, manifest, gameReShadeDllName: "dxgi.dll", gameReShadeApi: "d3d10", installDgVoodoo: false, cancellationToken);
                break;
            case InstallRoute.X64DxgiFeeder:
                await InstallX64FeederAsync(game, payload, manifest, cancellationToken);
                break;
            case InstallRoute.X64NativeThenFeeder:
                await InstallX64NativeThenFeederAsync(game, payload, manifest, cancellationToken);
                break;
            case InstallRoute.X64DirectRenoDx:
                await InstallX64Async(game, payload, manifest, cancellationToken);
                break;
        }

        TryDisableNvidiaDlssIndicator();
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

    async Task InstallX86Async(GameCandidate game, PayloadInfo payload, InstallManifest manifest, string gameReShadeDllName, string gameReShadeApi, bool installDgVoodoo, CancellationToken cancellationToken)
    {
        var gameDir = Path.GetDirectoryName(game.ExePath)!;

        if (installDgVoodoo)
        {
            if (payload.DgVoodooD3D9 is null)
                throw new InvalidOperationException("DX9 route needs dgVoodoo2 MS/x86/D3D9.dll in the payload folder.");

            CopyWithBackup(payload.DgVoodooD3D9, Path.Combine(gameDir, "D3D9.dll"), game.Root, manifest);
            if (payload.DgVoodooCpl is not null)
                CopyWithBackup(payload.DgVoodooCpl, Path.Combine(gameDir, "dgVoodooCpl.exe"), game.Root, manifest);
            var dgVoodooConfig = Path.Combine(gameDir, "dgVoodoo.conf");
            CopyWithBackup(AppFile("Configs", "dgVoodoo-dx9.conf"), dgVoodooConfig, game.Root, manifest);
            ConfigureDgVoodoo(dgVoodooConfig);
            _log("Installed dgVoodoo2 D3D9 wrapper.");
        }

        var gameReShadeIni = Path.Combine(gameDir, "ReShade.ini");
        var gamePreset = Path.Combine(gameDir, "ReShadePreset.ini");
        TrackExternalWrite(gameReShadeIni, game.Root, manifest);
        TrackExternalWrite(gamePreset, game.Root, manifest);

        await InstallReShadeAsync(game.ExePath, Path.Combine(gameDir, gameReShadeDllName), payload.ReShade32Dll, payload.ReShadeSetup, gameReShadeApi, game.Root, manifest, cancellationToken);

        CopyWithBackup(AppFile("Runtime", "x86-dx9-dx11", "dlss5-feed.addon32"), Path.Combine(gameDir, "dlss5-feed.addon32"), game.Root, manifest);
        CopyWithBackup(AppFile("Configs", "dlss5-feed-32.cfg"), Path.Combine(gameDir, "dlss5-feed.cfg"), game.Root, manifest);

        var shaderRoot = Path.Combine(gameDir, "reshade-shaders");
        var shaderDir = Path.Combine(shaderRoot, "Shaders");
        AddDirectoryIfNew(shaderRoot, game.Root, manifest);
        AddDirectoryIfNew(shaderDir, game.Root, manifest);
        CopyWithBackup(AppFile("Runtime", "shaders", "DLSS5_Feed.fx"), Path.Combine(shaderDir, "DLSS5_Feed.fx"), game.Root, manifest);
        CopyWithBackup(AppFile("Runtime", "shaders", "ReShade.fxh"), Path.Combine(shaderDir, "ReShade.fxh"), game.Root, manifest);
        ConfigureGameReShade(gameReShadeIni, enableGeomFit: !gameReShadeApi.Equals("d3d9", StringComparison.OrdinalIgnoreCase));
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

    async Task InstallX64FeederAsync(GameCandidate game, PayloadInfo payload, InstallManifest manifest, CancellationToken cancellationToken)
    {
        var gameDir = Path.GetDirectoryName(game.ExePath)!;
        var gameReShadeIni = Path.Combine(gameDir, "ReShade.ini");
        var gamePreset = Path.Combine(gameDir, "ReShadePreset.ini");
        TrackExternalWrite(gameReShadeIni, game.Root, manifest);
        TrackExternalWrite(gamePreset, game.Root, manifest);

        await InstallReShadeAsync(game.ExePath, Path.Combine(gameDir, "dxgi.dll"), payload.ReShade64Dll, payload.ReShadeSetup, "d3d10", game.Root, manifest, cancellationToken);

        RemoveRootRenoDxAddons(gameDir, game.Root, manifest);
        CopyWithBackup(AppFile("Runtime", "x64-dx9-dx11", "dlss5-feed.addon64"), Path.Combine(gameDir, "dlss5-feed.addon64"), game.Root, manifest);
        CopyWithBackup(AppFile("Configs", "dlss5-feed-64.cfg"), Path.Combine(gameDir, "dlss5-feed.cfg"), game.Root, manifest);

        var shaderRoot = Path.Combine(gameDir, "reshade-shaders");
        var shaderDir = Path.Combine(shaderRoot, "Shaders");
        AddDirectoryIfNew(shaderRoot, game.Root, manifest);
        AddDirectoryIfNew(shaderDir, game.Root, manifest);
        CopyWithBackup(AppFile("Runtime", "shaders", "DLSS5_Feed.fx"), Path.Combine(shaderDir, "DLSS5_Feed.fx"), game.Root, manifest);
        CopyWithBackup(AppFile("Runtime", "shaders", "ReShade.fxh"), Path.Combine(shaderDir, "ReShade.fxh"), game.Root, manifest);
        ConfigureGameReShade(gameReShadeIni, enableGeomFit: true);
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

    async Task InstallX64Async(GameCandidate game, PayloadInfo payload, InstallManifest manifest, CancellationToken cancellationToken)
    {
        var gameDir = Path.GetDirectoryName(game.ExePath)!;
        var gameReShadeIni = Path.Combine(gameDir, "ReShade.ini");
        TrackExternalWrite(gameReShadeIni, game.Root, manifest);

        await InstallReShadeAsync(game.ExePath, Path.Combine(gameDir, "dxgi.dll"), payload.ReShade64Dll, payload.ReShadeSetup, "d3d10", game.Root, manifest, cancellationToken);
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
        _log("Installed native x64 RenoDX/DLSS payload.");
    }

    async Task InstallX64NativeThenFeederAsync(GameCandidate game, PayloadInfo payload, InstallManifest manifest, CancellationToken cancellationToken)
    {
        var gameDir = Path.GetDirectoryName(game.ExePath)!;
        var gameReShadeIni = Path.Combine(gameDir, "ReShade.ini");
        var gamePreset = Path.Combine(gameDir, "ReShadePreset.ini");
        TrackExternalWrite(gameReShadeIni, game.Root, manifest);
        TrackExternalWrite(gamePreset, game.Root, manifest);

        await InstallReShadeAsync(game.ExePath, Path.Combine(gameDir, "dxgi.dll"), payload.ReShade64Dll, payload.ReShadeSetup, "d3d10", game.Root, manifest, cancellationToken);

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

        var shaderRoot = Path.Combine(gameDir, "reshade-shaders");
        var shaderDir = Path.Combine(shaderRoot, "Shaders");
        AddDirectoryIfNew(shaderRoot, game.Root, manifest);
        AddDirectoryIfNew(shaderDir, game.Root, manifest);
        CopyWithBackup(AppFile("Runtime", "shaders", "DLSS5_Feed.fx"), Path.Combine(shaderDir, "DLSS5_Feed.fx"), game.Root, manifest);
        CopyWithBackup(AppFile("Runtime", "shaders", "ReShade.fxh"), Path.Combine(shaderDir, "ReShade.fxh"), game.Root, manifest);

        ConfigureGameReShade(gameReShadeIni, enableGeomFit: true);
        ConfigureGamePreset(gamePreset);
        ConfigureDirectRenoDxReShade(gameReShadeIni);

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
