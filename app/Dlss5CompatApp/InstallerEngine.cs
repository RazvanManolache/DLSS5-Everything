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

        if (payload.RenoDxDlss5Addon is null)
            throw new InvalidOperationException("renodx-dlss5.addon64 is missing from the payload folder.");

        if (!payload.HasCoreDlss)
            throw new InvalidOperationException("nvngx_dlss.dll and nvngx_dlssnr.dll are required in the payload folder.");

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
            case InstallRoute.X86Dx9ViaDgVoodoo:
                await InstallX86Async(game, payload, manifest, installDgVoodoo: true, cancellationToken);
                break;
            case InstallRoute.X86DxgiFeeder:
                await InstallX86Async(game, payload, manifest, installDgVoodoo: false, cancellationToken);
                break;
            case InstallRoute.X64DirectRenoDx:
                await InstallX64Async(game, payload, manifest, cancellationToken);
                break;
        }

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

    Task InstallX86Async(GameCandidate game, PayloadInfo payload, InstallManifest manifest, bool installDgVoodoo, CancellationToken cancellationToken)
    {
        var gameDir = Path.GetDirectoryName(game.ExePath)!;

        if (installDgVoodoo)
        {
            if (payload.DgVoodooD3D9 is null)
                throw new InvalidOperationException("DX9 route needs dgVoodoo2 MS/x86/D3D9.dll in the payload folder.");

            CopyWithBackup(payload.DgVoodooD3D9, Path.Combine(gameDir, "D3D9.dll"), game.Root, manifest);
            if (payload.DgVoodooCpl is not null)
                CopyWithBackup(payload.DgVoodooCpl, Path.Combine(gameDir, "dgVoodooCpl.exe"), game.Root, manifest);
            CopyWithBackup(AppFile("Configs", "dgVoodoo-dx9.conf"), Path.Combine(gameDir, "dgVoodoo.conf"), game.Root, manifest);
            _log("Installed dgVoodoo2 D3D9 wrapper.");
        }

        CopyWithBackup(AppFile("Runtime", "reshade", "ReShade32.dll"), Path.Combine(gameDir, "dxgi.dll"), game.Root, manifest);

        CopyWithBackup(AppFile("Runtime", "x86-dx9-dx11", "dlss5-feed.addon32"), Path.Combine(gameDir, "dlss5-feed.addon32"), game.Root, manifest);
        CopyWithBackup(AppFile("Configs", "dlss5-feed-32.cfg"), Path.Combine(gameDir, "dlss5-feed.cfg"), game.Root, manifest);

        var shaderRoot = Path.Combine(gameDir, "reshade-shaders");
        var shaderDir = Path.Combine(shaderRoot, "Shaders");
        AddDirectoryIfNew(shaderRoot, game.Root, manifest);
        AddDirectoryIfNew(shaderDir, game.Root, manifest);
        CopyWithBackup(AppFile("Runtime", "shaders", "DLSS5_Feed.fx"), Path.Combine(shaderDir, "DLSS5_Feed.fx"), game.Root, manifest);
        CopyWithBackup(AppFile("Runtime", "shaders", "ReShade.fxh"), Path.Combine(shaderDir, "ReShade.fxh"), game.Root, manifest);
        ConfigureGameReShade(Path.Combine(gameDir, "ReShade.ini"));
        ConfigureGamePreset(Path.Combine(gameDir, "ReShadePreset.ini"));

        var hostDir = Path.Combine(gameDir, "host64");
        Directory.CreateDirectory(hostDir);
        AddDirectoryIfNew(hostDir, game.Root, manifest);
        var hostExe = Path.Combine(hostDir, "dlss5-feed-host64.exe");
        CopyWithBackup(AppFile("Runtime", "host64", "dlss5-feed-host64.exe"), hostExe, game.Root, manifest);

        CopyWithBackup(AppFile("Runtime", "reshade", "ReShade64.dll"), Path.Combine(hostDir, "dxgi.dll"), game.Root, manifest);
        CopyWithBackup(payload.RenoDxDlss5Addon!, Path.Combine(hostDir, "renodx-dlss5.addon64"), game.Root, manifest);
        foreach (var dll in payload.NvidiaDlls.Concat(payload.StreamlineDlls))
            CopyWithBackup(dll, Path.Combine(hostDir, Path.GetFileName(dll)), game.Root, manifest);

        ConfigureHostReShade(Path.Combine(hostDir, "ReShade.ini"));
        _log("Installed x86 feeder and 64-bit DLSS host.");
        return Task.CompletedTask;
    }

    Task InstallX64Async(GameCandidate game, PayloadInfo payload, InstallManifest manifest, CancellationToken cancellationToken)
    {
        var gameDir = Path.GetDirectoryName(game.ExePath)!;
        CopyWithBackup(AppFile("Runtime", "reshade", "ReShade64.dll"), Path.Combine(gameDir, "dxgi.dll"), game.Root, manifest);
        CopyWithBackup(payload.RenoDxDlss5Addon!, Path.Combine(gameDir, "renodx-dlss5.addon64"), game.Root, manifest);

        foreach (var addon in payload.ExtraAddons)
        {
            var name = Path.GetFileName(addon);
            if (name.Equals("renodx-dlss5.addon64", StringComparison.OrdinalIgnoreCase)) continue;
            CopyWithBackup(addon, Path.Combine(gameDir, name), game.Root, manifest);
        }

        foreach (var dll in payload.NvidiaDlls.Concat(payload.StreamlineDlls))
            CopyWithBackup(dll, Path.Combine(gameDir, Path.GetFileName(dll)), game.Root, manifest);

        EnableAddons(Path.Combine(gameDir, "ReShade.ini"));
        _log("Installed native x64 RenoDX/DLSS payload.");
        return Task.CompletedTask;
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

    void AddDirectoryIfNew(string directory, string gameRoot, InstallManifest manifest)
    {
        if (Directory.Exists(directory) && Directory.EnumerateFileSystemEntries(directory).Any()) return;
        var relative = Path.GetRelativePath(gameRoot, directory);
        if (!manifest.Added.Contains(relative, StringComparer.OrdinalIgnoreCase))
            manifest.Added.Add(relative);
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

    static void ConfigureGameReShade(string ini)
    {
        IniEditor.SetValue(ini, "GENERAL", "EffectSearchPaths", @".\reshade-shaders\Shaders\**");
        IniEditor.SetValue(ini, "GENERAL", "TextureSearchPaths", @".\reshade-shaders\Textures\**");
        IniEditor.SetValue(ini, "GENERAL", "PresetPath", @".\ReShadePreset.ini");
        IniEditor.SetCsvDefinition(ini, "GENERAL", "PreprocessorDefinitions", "RESHADE_DEPTH_INPUT_IS_REVERSED=1");
        IniEditor.SetCsvDefinition(ini, "GENERAL", "PreprocessorDefinitions", "DLSS5_MV_PROVIDER=0");
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
        IniEditor.SetValue(ini, "INPUT", "KeyOverlay", "36,0,0,0");
        IniEditor.SetValue(ini, "INPUT", "KeyScreenshot", "0,0,0,0");
        IniEditor.SetValue(ini, "RenoDX.DLSS5", "NeuralUplift", "1");
        IniEditor.SetValue(ini, "RenoDX.DLSS5", "NREnableUpscaling", "1");
        IniEditor.SetValue(ini, "RenoDX.DLSS5", "NRPreset", "2");
        IniEditor.SetValue(ini, "RenoDX.DLSS5", "NRStyle", "1");
        IniEditor.SetValue(ini, "RenoDX.DLSS5", "NRIntensity", "2.000000");
        IniEditor.SetValue(ini, "RenoDX.DLSS5", "NRLocalStructure", "2.000000");
        IniEditor.SetValue(ini, "RenoDX.DLSS5", "NRLocalTone", "2.000000");
        IniEditor.SetValue(ini, "RenoDX.DLSS5", "NRSkinStructure", "2.000000");
        IniEditor.SetValue(ini, "RenoDX.DLSS5", "NRAutoMask", "1");
        IniEditor.SetValue(ini, "RenoDX.DLSS5", "NRUICorrection", "1");
        IniEditor.SetValue(ini, "RenoDX.DLSS5", "NRDepthMode", "1");
        IniEditor.SetValue(ini, "RenoDX.DLSS5", "NRMVecScaleX", "2");
        IniEditor.SetValue(ini, "RenoDX.DLSS5", "NRMVecScaleY", "2");
    }

    static void EnableAddons(string ini)
    {
        if (!File.Exists(ini)) return;
        IniEditor.SetValue(ini, "ADDON", "DisabledAddons", "");
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
