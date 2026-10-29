using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text;
using System.Text.RegularExpressions;

namespace Dlss5CompatApp;

sealed partial class InstallerEngine
{
    const string BackupDirectoryName = "_DLSS5_Compat_Backup";
    const string OpenVrOriginalDllName = "openvr_api.dlss5-original.dll";
    const string OpenVrShimDllName = "dlss5-openvr-shim64.dll";
    const string VulkanDisablePatchCommand = "--patch-reshade-vulkan-disable";

    readonly Action<string> _log;
    readonly Func<bool, VulkanLayerInstaller> _vulkanLayer;
    int _frameIterations = 1;
    PhoenixSettings _phoenixSettings = new();
    string _phoenixInputProvider = PhoenixConfiguration.GameInputProvider;
    string _phoenixModelPath = "";
    readonly HashSet<string> _previouslyOwned = new(StringComparer.OrdinalIgnoreCase);

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

    public InstallerEngine(Action<string> log, Func<bool, VulkanLayerInstaller>? vulkanLayer = null)
    {
        _log = log;
        _vulkanLayer = vulkanLayer ?? (x86 => new VulkanLayerInstaller(x86));
    }

    public static bool IsVulkanDisablePatchCommand(string[] args) =>
        args.Length >= 3 && args[0].Equals(VulkanDisablePatchCommand, StringComparison.OrdinalIgnoreCase);

    // Kept for old launchers; global files must never be patched outside a game transaction.
    public static int RunVulkanDisablePatchCommand(string[] args) => 2;

    internal static GameCandidate InstalledGame(GameCandidate detected)
    {
        if (HasPending(detected.Root)) throw new InvalidOperationException("Recover the interrupted installation before verification.");
        if (File.Exists(ManifestPath(detected.Root)))
            new InstallResult(ReadManifest(detected.Root, ManifestPath(detected.Root)).FileFailures).RequireComplete();
        if (!RuntimeDiagnostics.HasInstallManifest(detected))
            throw new InvalidOperationException("No complete Phoenix installation was found for this executable. Install it first.");
        var manifest = ReadManifest(detected.Root, ManifestPath(detected.Root));
        if (!Enum.TryParse<InstallRoute>(manifest.Route, out var route) || !Enum.IsDefined(route) || route == InstallRoute.Unsupported)
            throw new InvalidOperationException("The recorded installation route is invalid.");
        // Older journals only recorded the route. Derive concrete APIs where unambiguous;
        // do not let a new UI renderer selection silently replace the installed route.
        var api = manifest.Api ?? route switch
        {
            InstallRoute.X86DirectXLegacyViaDgVoodoo or InstallRoute.X64DirectXLegacyViaDgVoodoo => GraphicsApi.DirectX7OrOlder,
            InstallRoute.X86Dx8ViaD3D8To9 => GraphicsApi.DirectX8,
            InstallRoute.X86Dx8ViaDgVoodoo => GraphicsApi.DirectX8DgVoodoo,
            InstallRoute.X86Dx9NativeFeeder or InstallRoute.X86Dx9ViaDgVoodoo or InstallRoute.X64Dx9NativeFeeder => GraphicsApi.DirectX9,
            InstallRoute.X86OpenGlFeeder or InstallRoute.X64OpenGlFeeder => GraphicsApi.OpenGl,
            InstallRoute.X64OpenXrLayer => GraphicsApi.OpenXr,
            InstallRoute.X64OpenVrFeeder => GraphicsApi.OpenVr,
            InstallRoute.X64VulkanFeeder => GraphicsApi.Vulkan,
            InstallRoute.X64DirectPhoenix or InstallRoute.X64NativeThenFeeder => GraphicsApi.DirectX12,
            InstallRoute.X86Glide211ViaDgVoodoo => GraphicsApi.Glide211,
            InstallRoute.X86Glide245ViaDgVoodoo => GraphicsApi.Glide245,
            InstallRoute.X86Glide31ViaDgVoodoo => GraphicsApi.Glide31,
            InstallRoute.X86Glide31NapalmViaDgVoodoo => GraphicsApi.Glide31Napalm,
            _ => detected.Api
        };
        var automaticBackbuffer = route == InstallRoute.X64DirectPhoenix && manifest.PreferredRoute is null &&
            manifest.InputProvider == PhoenixConfiguration.FeederInputProvider;
        var installed = (detected with { PreferredRoute = null, ProxyName = null }).WithApi(api) with
        {
            PreferredRoute = automaticBackbuffer ? null : manifest.PreferredRoute ?? (route == InstallRoute.X64DirectPhoenix
                ? GameRoutePreference.Native : GameRoutePreference.Feeder),
            // Preserve the recorded direct-addon route without turning an
            // automatic backbuffer install into forced engine-input mode.
            HasNativeDlssSignal = detected.HasNativeDlssSignal || automaticBackbuffer,
            ProxyName = manifest.ProxyName ?? (manifest.Added.Concat(manifest.Replaced.Select(file => file.RelativePath))
                .Any(path => LocalPaths.Same(Path.Combine(detected.Root, path), Path.Combine(Path.GetDirectoryName(detected.ExePath)!, "d3d11.dll"))) ? "d3d11.dll" : null),
            SuggestedArguments = manifest.LaunchArguments ?? detected.WithApi(api).SuggestedArguments
        };
        installed.ValidateOverrides();
        // X64NativeThenFeeder is a legacy name for the current DXGI feeder.
        if (installed.Route != route && !(route == InstallRoute.X64NativeThenFeeder && installed.Route == InstallRoute.X64DxgiFeeder))
            throw new InvalidOperationException("The old installation route cannot be identified reliably. Select a renderer and install again before verification.");
        return installed;
    }

    public async Task<InstallResult> InstallAsync(GameCandidate game, PayloadInfo payload, bool forceVrEyeSplit = false, int frameIterations = 1, CancellationToken cancellationToken = default, PhoenixSettings? phoenixSettings = null, string? phoenixModelPath = null)
    {
        ValidateRoot(game.Root);
        ValidateTarget(game.Root, Path.GetRelativePath(game.Root, game.ExePath));
        EnsureGameStopped(game.ExePath);
        var packaged = PackagedPhoenixPlan.For(game, payload);
        CheckPreflight(game, payload, cancellationToken, phoenixSettings).ThrowIfBlocked();
        if (packaged is null && game.Route != InstallRoute.X64DirectPhoenix && !payload.HasPhoenixFeederApi)
            throw new InvalidOperationException(PayloadScanner.FeederApiRemediation);
        using var transactionLease = OpenTransaction(game.Root);
        if (HasPending(game.Root)) await RecoverCoreAsync(game.Root, cancellationToken);
        AdoptDriveRelocatedLegacyManifest(game);
        // Capture configured runtime locations before restoring the outgoing install.
        // Preflight and the newly written INIs must select the same files.
        _phoenixSettings = phoenixSettings ?? new PhoenixSettings { Iterations = Math.Clamp(frameIterations, 1, 10) };
        if (packaged is not null) _phoenixSettings = PhoenixRuntimePackage.ConfiguredSettings(game, _phoenixSettings);
        _phoenixSettings = _phoenixSettings with { ModelLibraryDirectory = _phoenixSettings.ModelLibraryDirectory ?? OptionalModelLibrary.Root(payload.Root) };
        _phoenixInputProvider = packaged?.InputProvider ?? (game.Route == InstallRoute.X64DirectPhoenix
            ? PhoenixConfiguration.GameInputProvider : PhoenixConfiguration.FeederInputProvider);
        _frameIterations = _phoenixSettings.Iterations;
        _phoenixModelPath = phoenixModelPath ?? payload.NvidiaDlls.FirstOrDefault(p =>
            Path.GetFileName(p).Equals("nvngx_dlssnr.dll", StringComparison.OrdinalIgnoreCase)) ?? "";
        if (string.IsNullOrEmpty(phoenixModelPath) && !PhoenixConfiguration.RequiresNativeNr(_phoenixSettings))
        {
            var package = OptionalModelLibrary.Installed(_phoenixSettings.ModelLibraryDirectory)
                .FirstOrDefault(p => p.model == _phoenixSettings.ModelId);
            if (package is not null) _phoenixModelPath = Path.GetDirectoryName(OptionalModelLibrary.Contained(_phoenixSettings.ModelLibraryDirectory, package.manifest))!;
        }
        if (!_phoenixSettings.CopyNvidiaDllToGame && File.Exists(_phoenixModelPath))
            _phoenixSettings = _phoenixSettings with { NvidiaDllFolder = Path.GetDirectoryName(Path.GetFullPath(_phoenixModelPath)) };
        if (_phoenixInputProvider == PhoenixConfiguration.FeederInputProvider &&
            PhoenixConfiguration.RequiresNativeNr(_phoenixSettings) && !PayloadScanner.IsX64Dll(_phoenixModelPath))
            throw new InvalidOperationException("NVIDIA DLSS5 comparison requires a selected x64 NR model DLL. Select a DLL or use Phoenix DLSS5 enhancement without NVIDIA DLSS5 comparison.");
        _ = PhoenixConfiguration.Create(Path.GetDirectoryName(game.ExePath)!, _phoenixModelPath, _phoenixSettings, _phoenixInputProvider);
        if (!payload.HasEmbeddedPhoenixRuntime &&
            (string.IsNullOrWhiteSpace(_phoenixSettings.BundlePath) || !Directory.Exists(_phoenixSettings.BundlePath) ||
             string.IsNullOrWhiteSpace(_phoenixSettings.RuntimeRoot) ||
             !File.Exists(Path.Combine(_phoenixSettings.RuntimeRoot, "phoenix_runtime_cpp.dll")) ||
             !File.Exists(Path.Combine(_phoenixSettings.RuntimeRoot, "phoenix_native_cpp.json"))))
            throw new InvalidOperationException("Use a Phoenix DLSS5 addon with an embedded C++ runtime, or supply a matching compiled BundlePath and RuntimeRoot. Python workers are not supported.");
        if (packaged is null && game.Route != InstallRoute.X64DirectPhoenix)
        {
            var host = AppFile("Runtime", "host64", "dlss5-feed-host64.exe");
            if (!PayloadScanner.IsDirectPhoenixHost(host))
                throw new InvalidOperationException("Rebuild the direct Phoenix DLSS5 host (phoenix-host/2) before installing; the bundled host still uses the legacy SR/DLAA route.");
        }
        if (packaged is null && game.Route == InstallRoute.X64VulkanFeeder)
            _ = AppFile("Runtime", "host64", "phoenix-vulkan-feed.addon64");

        if (game.Route == InstallRoute.Unsupported)
            throw new InvalidOperationException("This executable/API combination is not supported.");

        var missing = payload.MissingFor(game);
        if (!string.IsNullOrWhiteSpace(missing))
            throw new InvalidOperationException("Payload is missing: " + missing);
        ValidateCustomAddons(payload.ExtraAddons);

        var gameDir = Path.GetDirectoryName(game.ExePath)!;
        CheckPreflight(game, payload, cancellationToken, _phoenixSettings).ThrowIfBlocked();

        _previouslyOwned.Clear();
        _previouslyRetiredOverlays.Clear();
        var profiles = new BackendProfileStore(game.Root, game.ExePath);
        // Validate the selected profile before restoring or replacing the outgoing installation.
        var selectedProfile = profiles.Load(game.Route.ToString());

        InstallManifest? retained = null;
        if (File.Exists(ManifestPath(game.Root)))
        {
            var outgoing = ReadManifest(game.Root, ManifestPath(game.Root));
            ValidateRestorePlan(game.Root, outgoing, checkLocks: false);
            RememberRetiredOverlays(outgoing);
            _previouslyOwned.UnionWith(ManagedFiles(outgoing).Select(path => path.Replace('/', Path.DirectorySeparatorChar)));
            try { profiles.Snapshot(outgoing.Route); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            { _log("Could not save outgoing settings: " + error.Message); }
            if (outgoing.Route.Equals(game.Route.ToString(), StringComparison.OrdinalIgnoreCase))
                selectedProfile = profiles.Load(outgoing.Route);
            // When a file is blocked, retain the original backup ownership and
            // update in place. Never replace an original backup with mod bytes.
            var availability = InstallPreflight.Check(game.Root, game.ExePath, ManagedFiles(outgoing), ManagedFiles(outgoing),
                cancellationToken: cancellationToken);
            if (outgoing.FileFailures.Count > 0 || availability.Findings.Any(f => f.Code == "file-unavailable"))
                retained = outgoing;
            else
            {
                _log("Restoring previous DLSS5 compatibility install before applying the selected route.");
                await RestoreCoreAsync(game.Root, cancellationToken);
            }
        }

        var manifest = retained ?? new InstallManifest
        {
            GameRoot = game.Root,
            ExeRelativePath = Path.GetRelativePath(game.Root, game.ExePath),
            Route = game.Route.ToString(),
            Api = game.Api,
            PreferredRoute = game.PreferredRoute,
            ProxyName = game.ProxyName,
            LaunchArguments = game.SuggestedArguments
        };
        manifest.Operation = "install";
        manifest.ExeRelativePath = Path.GetRelativePath(game.Root, game.ExePath);
        manifest.Route = game.Route.ToString();
        manifest.Api = game.Api;
        manifest.PreferredRoute = game.PreferredRoute;
        manifest.InputProvider = _phoenixInputProvider;
        manifest.ProxyName = game.ProxyName;
        manifest.LaunchArguments = game.SuggestedArguments;
        manifest.FileFailures.Clear();

        ValidateNoReparse(BackupRoot(game.Root));
        Directory.CreateDirectory(BackupRoot(game.Root));
        PersistPending(game.Root, manifest);

        try
        {
        if (retained is not null)
        {
            var planned = PlannedFiles(game, payload, PhoenixConfiguration.RequiresNativeNr(_phoenixSettings),
                _phoenixSettings.CopyNvidiaDllToGame).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var relative in ManagedFiles(retained).Where(path => !planned.Contains(path)).ToArray())
            {
                var target = ValidateTarget(game.Root, relative);
                var replaced = retained.Replaced.FirstOrDefault(item => item.RelativePath.Equals(relative, StringComparison.OrdinalIgnoreCase));
                if (replaced is null) TryInstallFile(target, game.Root, manifest, () => File.Delete(target));
                else CopyWithBackup(ValidateBackup(game.Root, replaced, retained), target, game.Root, manifest);
            }
        }
        RetireLegacyComponents(game, manifest, cancellationToken);
        RemoveLegacyManagedAddons(gameDir, game.Root, manifest);
        foreach (var directory in new[] { Path.Combine(gameDir, "host64"), Path.Combine(gameDir, "_DLSS5_OpenXR", "host64") })
            if (Directory.Exists(directory)) RemoveLegacyManagedAddons(directory, game.Root, manifest);
        if (packaged is not null)
            await InstallPackagedAsync(game, payload, packaged, manifest, cancellationToken);
        else switch (game.Route)
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
                await InstallX86Async(game, payload, manifest, gameReShadeDllName: game.ProxyName?.ToLowerInvariant() ?? "dxgi.dll", gameReShadeApi: "d3d10", dgVoodooMode: DgVoodooMode.None, installD3D8To9: false, cancellationToken);
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
            case InstallRoute.X64Dx9NativeFeeder:
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
            case InstallRoute.X64DirectPhoenix:
                await InstallX64Async(game, payload, manifest, cancellationToken);
                break;
        }

        InstallCustomAddons(game, payload, manifest);
        ConfigureFile(Path.Combine(gameDir, BatchFileName(game)), game.Root, manifest, () => WriteLauncherBatch(game, manifest));
        var owned = ManagedFiles(manifest).Select(path => path.Replace('\\', '/')).ToHashSet(StringComparer.OrdinalIgnoreCase);
        // Deployment/model paths and transport configuration must come from this install, not a snapshot.
        var settingsFiles = selectedProfile.Keys.Where(relative => owned.Contains(relative) &&
            new[] { "ReShade.ini", "ReShadeVR.ini", "ReShadePreset.ini" }
                .Contains(Path.GetFileName(relative), StringComparer.OrdinalIgnoreCase)).ToArray();
        foreach (var relative in settingsFiles)
        {
            var destination = ValidateTarget(game.Root, relative);
            ConfigureFile(destination, game.Root, manifest, () => File.WriteAllBytes(destination, selectedProfile[relative]));
        }
        // Profile snapshots can predate the Phoenix migration.
        foreach (var directory in settingsFiles.Select(relative => Path.GetDirectoryName(ValidateTarget(game.Root, relative))!)
            .Distinct(StringComparer.OrdinalIgnoreCase))
            RemoveLegacyManagedAddons(directory, game.Root, manifest);
        if (selectedProfile.Keys.Except(settingsFiles, StringComparer.OrdinalIgnoreCase).Any())
            _log("Skipped saved deployment/transport files or settings not owned by the selected route.");
        // Saved controls must not reinstate the outgoing route's OpenVR proxy chain.
        if (game.ProxyName is not null || NeedsOpenVrProxy(game))
            foreach (var name in new[] { "ReShade.ini", "ReShadeVR.ini" })
            {
                var path = Path.Combine(gameDir, name);
                if (!owned.Contains(Path.GetRelativePath(game.Root, path).Replace('\\', '/'))) continue;
                ConfigureFile(path, game.Root, manifest, () => ConfigureOpenVrProxyIfNeeded(game, path));
            }
        foreach (var relative in owned.Where(path => Path.GetFileName(path).Equals(PhoenixConfiguration.FileName, StringComparison.OrdinalIgnoreCase)))
        {
            var path = ValidateTarget(game.Root, relative);
            var directory = Path.GetDirectoryName(path)!;
            var model = Directory.Exists(_phoenixModelPath) || !_phoenixSettings.CopyNvidiaDllToGame
                ? _phoenixModelPath : Path.Combine(directory, "PhoenixModel.dll");
            ConfigureFile(path, game.Root, manifest, () => PhoenixConfiguration.WriteSidecar(directory, model, _phoenixSettings, _phoenixInputProvider));
            if (game.Route == InstallRoute.X64OpenXrLayer)
            {
                var hostIni = Path.Combine(directory, "ReShade.ini");
                ConfigureFile(hostIni, game.Root, manifest, () => IniEditor.SetValue(hostIni, "DLSS5_HOST", "FrameIterations", _frameIterations.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            }
            // A snapshot's explicit model override must not supersede the newly selected model.
            foreach (var iniName in new[] { "ReShade.ini", "ReShadeVR.ini" })
            {
                var ini = Path.Combine(directory, iniName);
                ConfigureFile(ini, game.Root, manifest, () =>
                {
                    IniEditor.SetValue(ini, "Phoenix", "ModelSource", "configured");
                    // The runner belongs to this request, not an old snapshot.
                    var controls = PhoenixConfiguration.Controls(_phoenixSettings);
                    foreach (var key in new[] { "Backend", "ExecutionDevice", "ModelId", "NvidiaModel", "NvidiaDllFolder", "ModelLibraryDirectory" })
                        IniEditor.SetValue(ini, "Phoenix", key, controls[key]);
                    if (_phoenixInputProvider == PhoenixConfiguration.FeederInputProvider)
                    {
                        if (phoenixSettings is not null)
                        {
                            // Explicit comparison must agree with this install's NR deployment.
                            foreach (var key in new[] { "Comparison", "SourceA", "SourceB" })
                                IniEditor.SetValue(ini, "Phoenix", key, controls[key]);
                        }
                        else if (!PhoenixConfiguration.RequiresNativeNr(_phoenixSettings))
                        {
                            // Do not activate an undeployed NR runtime from saved controls.
                            foreach (var key in new[] { "SourceA", "SourceB" })
                                if (IniEditor.GetValue(ini, "Phoenix", key) == "1")
                                    IniEditor.SetValue(ini, "Phoenix", key, controls[key]);
                        }
                    }
                });
            }
        }
        if (packaged is not null)
        {
            foreach (var name in new[] { "ReShade.ini", "ReShadeVR.ini" })
            {
                var ini = Path.Combine(gameDir, name);
                ConfigureFile(ini, game.Root, manifest, () =>
                {
                    ConfigurePackagedPhoenixReShade(ini);
                    IniEditor.SetValue(ini, "Phoenix.Compatibility", "Enabled", packaged.NativeGameInput ? "0" : "1");
                    IniEditor.SetValue(ini, "Phoenix.Compatibility", "StandaloneInput", packaged.NativeGameInput ? "0" : "1");
                    ConfigurePhoenixRuntimePaths(ini, game.Arch, _phoenixSettings);
                    if (packaged.RequiresHelperDefault(payload, game, _phoenixSettings))
                        IniEditor.SetValue(ini, "Phoenix.Compatibility", "ExecutionMode", "helper");
                    if (packaged.SoftwareDeviceSupport) IniEditor.SetValue(ini, "APP", "IncludeSoftwareDevices", "1");
                    IniEditor.SetValue(ini, "Phoenix.Compatibility", "CoherentFrames", "1");
                });
            }
            foreach (var change in LegacyEffectMigration.Find(game))
            {
                if (!LocalPaths.Same(change.Source, change.Destination))
                    CopyWithBackup(change.Source, change.Destination, game.Root, manifest);
                ConfigureFile(change.Destination, game.Root, manifest, () => LegacyEffectMigration.Filter(change.Destination));
                ConfigureFile(change.Ini, game.Root, manifest, () => IniEditor.SetValue(change.Ini, "GENERAL", "PresetPath", Path.GetRelativePath(gameDir, change.Destination)));
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        File.Move(PendingPath(game.Root), ManifestPath(game.Root), overwrite: true);
        var result = new InstallResult(manifest.FileFailures.ToArray()) { RetiredComponents = manifest.Retired.ToArray() };
        if (result.RetiredComponents.Count > 0) _log(result.CleanupSummary);
        _log(result.Complete
            ? "Install complete. Phoenix DLSS5 inference and image quality have not been verified by this installer."
            : result.Warning);
        return result;
        }
        catch
        {
            // Keep recovery available for partial installs, including cancellation.
            _log("Installation failed; the durable pending journal retains all recorded changes. Recover before reinstalling.");
            throw;
        }
        finally { _previouslyOwned.Clear(); _previouslyRetiredOverlays.Clear(); }
    }

    public static bool HasPending(string gameRoot)
    {
        ValidateRoot(gameRoot);
        ValidateNoReparse(BackupRoot(gameRoot));
        ValidateNoReparse(PendingPath(gameRoot));
        return File.Exists(PendingPath(gameRoot));
    }

    public async Task RecoverAsync(string gameRoot, CancellationToken cancellationToken = default)
    {
        using var lease = OpenTransaction(gameRoot);
        await RecoverCoreAsync(gameRoot, cancellationToken);
    }

    async Task RecoverCoreAsync(string gameRoot, CancellationToken cancellationToken)
    {
        if (!HasPending(gameRoot)) return;
        await RestoreJournalAsync(gameRoot, PendingPath(gameRoot), cancellationToken);
        _log("Recovered interrupted Phoenix DLSS5 transaction.");
    }

    public async Task RestoreAsync(string gameRoot, CancellationToken cancellationToken = default)
    {
        using var lease = OpenTransaction(gameRoot);
        await RestoreCoreAsync(gameRoot, cancellationToken, snapshotProfile: true);
    }

    async Task RestoreCoreAsync(string gameRoot, CancellationToken cancellationToken, bool snapshotProfile = false)
    {
        ValidateRoot(gameRoot);
        if (HasPending(gameRoot))
        {
            await RecoverCoreAsync(gameRoot, cancellationToken);
            if (!File.Exists(ManifestPath(gameRoot))) return;
        }
        if (!File.Exists(ManifestPath(gameRoot))) throw new InvalidOperationException("No restore manifest found.");
        var manifest = ReadManifest(gameRoot, ManifestPath(gameRoot));
        ValidateRestorePlan(gameRoot, manifest);
        PreflightRestore(gameRoot, manifest, cancellationToken);
        // Explicit removal must retain current tuning, but recovery must never snapshot a partial restore.
        if (snapshotProfile)
        {
            new BackendProfileStore(gameRoot, ValidateTarget(gameRoot, manifest.ExeRelativePath)).Snapshot(manifest.Route);
            cancellationToken.ThrowIfCancellationRequested();
        }
        if (manifest.BackupHashEvidence == "adopted-existing-v1-backups")
            _log("Audited legacy backups adopted with current hashes; historical original-content integrity cannot be established.");
        manifest.Operation = "restore";
        PersistPending(gameRoot, manifest);
        await RestoreJournalAsync(gameRoot, PendingPath(gameRoot), cancellationToken);
    }

    async Task RestoreJournalAsync(string gameRoot, string journal, CancellationToken cancellationToken)
    {
        var manifest = ReadManifest(gameRoot, journal);
        // Check every target, every backup and every lock before changing any file.
        ValidateRestorePlan(gameRoot, manifest);
        PreflightRestore(gameRoot, manifest, cancellationToken);
        foreach (var item in manifest.Replaced)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var target = ValidateTarget(gameRoot, item.RelativePath);
            var backup = ValidateBackup(gameRoot, item, manifest);
            if (!Hash(backup).Equals(item.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new IOException("Original backup changed after restore validation: " + item.RelativePath);
            await CopyFileWithRetryAsync(backup, target, gameRoot, manifest, cancellationToken);
            _log("Restored " + item.RelativePath);
        }
        foreach (var relative in manifest.Added.AsEnumerable().Reverse())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var target = ValidateTarget(gameRoot, relative);
            if (File.Exists(target)) await DeleteFileWithRetryAsync(target, cancellationToken);
        }
        foreach (var relative in manifest.AddedDirectories.OrderByDescending(path => path.Length))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = ValidateTarget(gameRoot, relative);
            if (!Directory.Exists(directory)) continue;
            if (Directory.EnumerateFileSystemEntries(directory).Any())
                _log("Preserved nonempty directory: " + relative);
            else Directory.Delete(directory, recursive: false);
        }
        if (manifest.VulkanLayerX86 is { } x86)
        {
            await _vulkanLayer(x86).RestoreAsync(ValidateTarget(gameRoot, manifest.ExeRelativePath), cancellationToken);
            manifest.VulkanLayerX86 = null;
            PersistPending(gameRoot, manifest);
        }
        var suffix = ".done-" + Guid.NewGuid().ToString("N");
        // The journal remains recoverable until all restoration writes have completed.
        if (File.Exists(ManifestPath(gameRoot))) File.Move(ManifestPath(gameRoot), ManifestPath(gameRoot) + suffix);
        File.Move(journal, journal + suffix);
        _log("Restore complete.");
    }

    static InstallManifest ReadManifest(string gameRoot, string path, bool allowDriveRelocation = false)
    {
        ValidateNoReparse(path);
        if (new FileInfo(path).Length > 4 * 1024 * 1024) throw new IOException("Restore journal exceeds its size limit.");
        var bytes = File.ReadAllBytes(path);
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 32 });
        RejectDuplicateProperties(document.RootElement);
        var manifest = JsonSerializer.Deserialize<InstallManifest>(bytes)
            ?? throw new InvalidOperationException("Restore journal is unreadable.");
        foreach (var required in new[] { "GameRoot", "ExeRelativePath", "Route", "Replaced", "Added" })
            if (!document.RootElement.TryGetProperty(required, out _)) throw new IOException("Journal is missing " + required + ".");
        var sameRoot = Path.IsPathFullyQualified(manifest.GameRoot) && LocalPaths.Same(manifest.GameRoot, gameRoot);
        var relocated = allowDriveRelocation && manifest.Version == 1 && IsDriveRelocation(manifest.GameRoot, gameRoot);
        if (!document.RootElement.TryGetProperty("Version", out _) || manifest.Version is not (1 or 2) ||
            !sameRoot && !relocated)
            throw new InvalidOperationException("Restore journal has an unsupported version or mismatched root. No game files were changed.");
        if (relocated) manifest.GameRoot = gameRoot;
        if (manifest.InputProvider is not null && manifest.InputProvider is not (PhoenixConfiguration.GameInputProvider or PhoenixConfiguration.FeederInputProvider))
            throw new IOException("Restore journal has an invalid input provider.");
        if (manifest.Replaced is null || manifest.Added is null || manifest.AddedDirectories is null || manifest.FileFailures is null ||
            manifest.FileFailures.Count > 4096 || manifest.FileFailures.Any(file => file is null || string.IsNullOrWhiteSpace(file.RelativePath) || file.Reason is null || file.Reason.Length > 4096) ||
            manifest.Replaced.Any(item => item is null || string.IsNullOrWhiteSpace(item.RelativePath)) ||
            manifest.Replaced.Count + manifest.Added.Count + manifest.AddedDirectories.Count > 4096)
            throw new IOException("Restore journal has invalid or excessive entries.");
        if (manifest.Retired is null || manifest.Retired.Count > 4096 || manifest.Retired.Any(relative =>
            string.IsNullOrWhiteSpace(relative) || !manifest.Replaced.Any(file => file.RelativePath.Equals(relative, StringComparison.OrdinalIgnoreCase))))
            throw new IOException("Restore journal has invalid retirement entries.");
        foreach (var retired in manifest.Retired) ValidateTarget(gameRoot, retired);
        foreach (var failure in manifest.FileFailures) ValidateTarget(gameRoot, failure.RelativePath);
        if (manifest.Version == 1)
        {
            // This establishes present-day backup integrity only, not a historical original hash.
            ValidateTarget(gameRoot, manifest.ExeRelativePath);
            foreach (var item in manifest.Replaced)
            {
                ValidateTarget(gameRoot, item.RelativePath);
                item.BackupRelativePath = item.RelativePath;
                var backup = ContainedPath(BackupRoot(gameRoot), item.RelativePath);
                if (!File.Exists(backup)) throw new IOException("Legacy original backup is missing: " + item.RelativePath +
                    ". Recover it from your own backup into the matching recovery-folder path before retrying. No game files were changed.");
                item.Sha256 = Hash(backup);
            }
            // V1 mixed directories into Added. Never delete their contents, even during adoption.
            foreach (var relative in manifest.Added.ToArray())
                if (Directory.Exists(ValidateTarget(gameRoot, relative)))
                { manifest.Added.Remove(relative); manifest.AddedDirectories.Add(relative); }
            manifest.Version = 2;
            manifest.TransactionId = Guid.NewGuid().ToString("N");
            manifest.BackupHashEvidence = "adopted-existing-v1-backups";
        }
        else if (!document.RootElement.TryGetProperty("TransactionId", out _) || !Guid.TryParseExact(manifest.TransactionId, "N", out _))
            throw new IOException("Invalid transaction identity.");
        if (manifest.BackupHashEvidence is not ("captured-before-mutation" or "adopted-existing-v1-backups"))
            throw new IOException("Unknown backup integrity evidence.");
        return manifest;
    }

    internal static bool IsDriveRelocation(string recordedRoot, string currentRoot)
    {
        if (!Path.IsPathFullyQualified(recordedRoot) || !Path.IsPathFullyQualified(currentRoot)) return false;
        var oldRoot = Path.GetFullPath(recordedRoot).TrimEnd(Path.DirectorySeparatorChar);
        var newRoot = Path.GetFullPath(currentRoot).TrimEnd(Path.DirectorySeparatorChar);
        // Legacy copied libraries lack executable hashes. Accept only a drive
        // change with the complete remaining directory path still identical.
        return oldRoot.Length > 3 && newRoot.Length > 3 && oldRoot[1] == ':' && newRoot[1] == ':' &&
            char.ToUpperInvariant(oldRoot[0]) != char.ToUpperInvariant(newRoot[0]) && oldRoot[2..].Equals(newRoot[2..], StringComparison.OrdinalIgnoreCase);
    }

    void AdoptDriveRelocatedLegacyManifest(GameCandidate game)
    {
        var path = ManifestPath(game.Root);
        if (!File.Exists(path)) return;
        ValidateNoReparse(path);
        if (new FileInfo(path).Length > 4 * 1024 * 1024) throw new IOException("Restore journal exceeds its size limit.");
        using var document = JsonDocument.Parse(File.ReadAllBytes(path));
        if (document.RootElement.GetProperty("Version").GetInt32() != 1 ||
            !IsDriveRelocation(document.RootElement.GetProperty("GameRoot").GetString() ?? "", game.Root)) return;
        var manifest = ReadManifest(game.Root, path, allowDriveRelocation: true);
        var previous = ValidateTarget(game.Root, manifest.ExeRelativePath);
        if (!LocalPaths.Same(previous, game.ExePath) && !CanMigrateSharedDirectory(previous, game.ExePath))
            throw new IOException("Relocated installation belongs to another executable directory.");
        // Validate every backup, relative destination and lock before adopting.
        // No path in the recorded old drive is read or modified.
        ValidateRestorePlan(game.Root, manifest);
        var retained = path + ".relocated-" + Guid.NewGuid().ToString("N");
        File.Copy(path, retained, overwrite: false);
        var temporary = path + ".adopting-" + Guid.NewGuid().ToString("N");
        File.WriteAllText(temporary, JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, path, overwrite: true);
        _log("Validated local legacy backups after a drive relocation; previous journal retained: " + retained);
    }

    static void RejectDuplicateProperties(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in value.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new IOException("Duplicate journal property.");
                RejectDuplicateProperties(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) RejectDuplicateProperties(item);
    }

    static void ValidateRestorePlan(string gameRoot, InstallManifest manifest, bool checkLocks = true)
    {
        ValidateRoot(gameRoot);
        ValidateNoReparse(BackupRoot(gameRoot));
        EnsureGameStopped(ValidateTarget(gameRoot, manifest.ExeRelativePath));
        var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in manifest.Replaced)
        {
            var target = ValidateTarget(gameRoot, item.RelativePath);
            if (!targets.Add(target)) throw new InvalidOperationException("Duplicate restore target.");
            if (Directory.Exists(target)) throw new IOException("Restore target became a directory: " + target);
            var backup = ValidateBackup(gameRoot, item, manifest);
            if (!File.Exists(backup) || string.IsNullOrWhiteSpace(item.Sha256) ||
                !Hash(backup).Equals(item.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new IOException("Original backup missing or checksum mismatch: " + item.RelativePath);
            if (checkLocks) EnsureUnlocked(target);
        }
        foreach (var relative in manifest.Added)
        {
            var target = ValidateTarget(gameRoot, relative);
            if (!targets.Add(target) || Directory.Exists(target)) throw new IOException("Invalid added-file restore target: " + relative);
            if (checkLocks) EnsureUnlocked(target);
        }
        foreach (var relative in manifest.AddedDirectories)
        {
            var target = ValidateTarget(gameRoot, relative);
            if (!targets.Add(target) || File.Exists(target)) throw new IOException("Invalid added-directory restore target: " + relative);
        }
    }

    static string ValidateBackup(string root, ManifestFile item, InstallManifest manifest)
    {
        if (string.IsNullOrWhiteSpace(item.BackupRelativePath)) throw new IOException("Journal backup path is missing.");
        var expected = manifest.BackupHashEvidence == "adopted-existing-v1-backups"
            ? item.RelativePath : Path.Combine("snapshots", manifest.TransactionId, item.RelativePath);
        if (!item.BackupRelativePath.Replace('\\', '/').Equals(expected.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase))
            throw new IOException("Restore backup is outside transaction snapshots.");
        return ContainedPath(BackupRoot(root), item.BackupRelativePath);
    }

    static void ValidateRoot(string root)
    {
        if (!Path.IsPathFullyQualified(root) || !Directory.Exists(root))
            throw new IOException("Game root must be an existing absolute directory.");
        ValidateNoReparse(root);
    }

    static string ValidateTarget(string root, string relative)
    {
        var target = ContainedPath(root, relative);
        var backup = Path.GetFullPath(BackupRoot(root)).TrimEnd(Path.DirectorySeparatorChar);
        if (target.Equals(backup, StringComparison.OrdinalIgnoreCase) ||
            target.StartsWith(backup + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new IOException("A game target cannot be the recovery directory.");
        return target;
    }

    static string ContainedPath(string root, string relative)
    {
        try
        {
            var path = BoundedInstallPaths.Resolve(root, relative);
            ValidateNoReparse(path);
            return path;
        }
        catch (ArgumentException error) { throw new IOException("Unsafe transaction path: " + relative, error); }
    }

    static void ValidateNoReparse(string path)
    {
        var current = Path.GetFullPath(path);
        while (!string.IsNullOrEmpty(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Transaction paths cannot traverse reparse points: " + current);
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
            current = Path.GetDirectoryName(current);
        }
    }

    static void EnsureGameStopped(string executable)
    {
        foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(executable)))
        {
            using (process)
            {
                string? running;
                try
                {
                    if (process.HasExited) continue;
                    running = process.MainModule?.FileName;
                    if (process.HasExited) continue;
                }
                catch { throw new IOException("Cannot verify whether the game is running. Close matching processes before changing files."); }
                if (running is not null && Path.GetFullPath(running).Equals(Path.GetFullPath(executable), StringComparison.OrdinalIgnoreCase))
                    throw new IOException("Close the game before installing, restoring or recovering.");
            }
        }
    }

    static void EnsureUnlocked(string path)
    {
        if (!File.Exists(path)) return;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    static FileStream OpenTransaction(string root)
    {
        ValidateRoot(root);
        ValidateNoReparse(BackupRoot(root));
        Directory.CreateDirectory(BackupRoot(root));
        var path = Path.Combine(BackupRoot(root), "transaction.lock");
        ValidateNoReparse(path);
        return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }

    static void PersistPending(string root, InstallManifest manifest)
    {
        try { WritePending(root, manifest); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Losing the journal is not an ordinary file failure: stop all writes.
            throw new InvalidOperationException("Cannot save the recovery journal. Installation stopped to protect original files.", error);
        }
    }

    static void WritePending(string root, InstallManifest manifest)
    {
        ValidateNoReparse(BackupRoot(root));
        ValidateNoReparse(PendingPath(root));
        var bytes = JsonSerializer.SerializeToUtf8Bytes(manifest, new JsonSerializerOptions { WriteIndented = true });
        if (bytes.Length > 4 * 1024 * 1024)
            throw new InvalidOperationException("Cannot save the recovery journal: its size limit would be exceeded.");
        Directory.CreateDirectory(BackupRoot(root));
        var temporary = PendingPath(root) + ".tmp-" + Guid.NewGuid().ToString("N");
        using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        {
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
        }
        File.Move(temporary, PendingPath(root), overwrite: true);
    }

    async Task CopyFileWithRetryAsync(string source, string target, string gameRoot,
        InstallManifest manifest, CancellationToken cancellationToken)
    {
        await RetryFileOperationAsync(() =>
        {
            var temporary = target + ".restoring-" + Guid.NewGuid().ToString("N") + ".tmp";
            // Recovery must own the partial copy even if this process is killed
            // before the atomic replacement or its finally block can run.
            TrackExternalWrite(temporary, gameRoot, manifest);
            try
            {
                File.Copy(source, temporary, overwrite: false);
                if (File.Exists(target)) File.SetAttributes(target, FileAttributes.Normal);
                // Restoring must also replace a hard link, rather than modify
                // another installation or payload sharing the same file bytes.
                File.Move(temporary, target, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
                manifest.Added.Remove(Path.GetRelativePath(gameRoot, temporary));
                PersistPending(gameRoot, manifest);
            }
        }, target, cancellationToken);
    }

    static async Task DeleteFileWithRetryAsync(string target, CancellationToken cancellationToken)
    {
        await RetryFileOperationAsync(() =>
        {
            if (!File.Exists(target)) return;
            File.SetAttributes(target, FileAttributes.Normal);
            File.Delete(target);
        }, target, cancellationToken);
    }

    static async Task DeleteDirectoryWithRetryAsync(string target, CancellationToken cancellationToken)
    {
        await RetryFileOperationAsync(() =>
        {
            if (Directory.Exists(target))
                Directory.Delete(target, recursive: false);
        }, target, cancellationToken);
    }

    static async Task MoveFileWithRetryAsync(string source, string target, CancellationToken cancellationToken)
    {
        await RetryFileOperationAsync(() => File.Move(source, target, overwrite: true), target, cancellationToken);
    }

    static async Task RetryFileOperationAsync(Action operation, string target, CancellationToken cancellationToken)
    {
        Exception? lastError = null;
        for (var attempt = 0; attempt < 20; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                operation();
                return;
            }
            catch (UnauthorizedAccessException ex)
            {
                lastError = ex;
            }
            catch (IOException ex)
            {
                lastError = ex;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(150 + attempt * 150), cancellationToken);
        }

        throw new IOException("File operation did not complete for: " + target, lastError);
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
        InstallLegacyCom(game, payload, manifest, dgVoodooMode == DgVoodooMode.DirectXLegacy ? InstallRoute.X86DirectXLegacyViaDgVoodoo : InstallRoute.Unsupported);

        var gameReShadeIni = Path.Combine(gameDir, "ReShade.ini");
        var gameReShadeVrIni = Path.Combine(gameDir, "ReShadeVR.ini");
        var gamePreset = Path.Combine(gameDir, "ReShadePreset.ini");
        await InstallReShadeAsync(game.ExePath, Path.Combine(gameDir, gameReShadeDllName), payload.ReShade32Dll, payload.ReShadeSetup, gameReShadeApi, game.Root, manifest, cancellationToken);

        CopyWithBackup(AppFile("Runtime", "x86-dx9-dx11", "dlss5-feed.addon32"), Path.Combine(gameDir, "dlss5-feed.addon32"), game.Root, manifest);
        var feedConfig = Path.Combine(gameDir, "dlss5-feed.cfg");
        CopyWithBackup(AppFile("Configs", "dlss5-feed-32.cfg"), feedConfig, game.Root, manifest);
        ConfigureFile(feedConfig, game.Root, manifest, () => ConfigureFeeder(feedConfig));

        var shaderRoot = Path.Combine(gameDir, "reshade-shaders");
        var shaderDir = Path.Combine(shaderRoot, "Shaders");
        CopyWithBackup(AppFile("Runtime", "shaders", "DLSS5_Feed.fx"), Path.Combine(shaderDir, "DLSS5_Feed.fx"), game.Root, manifest);
        CopyWithBackup(AppFile("Runtime", "shaders", "ReShade.fxh"), Path.Combine(shaderDir, "ReShade.fxh"), game.Root, manifest);
        ConfigureFile(gameReShadeIni, game.Root, manifest, () => ConfigureGameReShade(gameReShadeIni, enableGeomFit: gameReShadeApi.Equals("d3d10", StringComparison.OrdinalIgnoreCase)));
        ConfigureFile(gameReShadeVrIni, game.Root, manifest, () => ConfigureGameReShade(gameReShadeVrIni, enableGeomFit: gameReShadeApi.Equals("d3d10", StringComparison.OrdinalIgnoreCase)));
        ConfigureFile(gamePreset, game.Root, manifest, () => ConfigureGamePreset(gamePreset));

        var hostDir = Path.Combine(gameDir, "host64");
        var hostExe = Path.Combine(hostDir, "dlss5-feed-host64.exe");
        CopyWithBackup(AppFile("Runtime", "host64", "dlss5-feed-host64.exe"), hostExe, game.Root, manifest);

        var hostReShadeIni = Path.Combine(hostDir, "ReShade.ini");
        await InstallReShadeAsync(hostExe, Path.Combine(hostDir, "dxgi.dll"), payload.ReShade64Dll, payload.ReShadeSetup, "d3d10", game.Root, manifest, cancellationToken);
        InstallPhoenix(hostDir, game.Root, payload, manifest);

        ConfigureFile(hostReShadeIni, game.Root, manifest, () => ConfigureHostReShade(hostReShadeIni));
        _log("Installed x86 feeder and 64-bit direct Phoenix DLSS5 host.");
    }

    async Task InstallX64FeederAsync(GameCandidate game, PayloadInfo payload, InstallManifest manifest, bool forceVrEyeSplit, CancellationToken cancellationToken)
    {
        var gameDir = Path.GetDirectoryName(game.ExePath)!;
        var gameReShadeDll = game.Route == InstallRoute.X64Dx9NativeFeeder ? "D3D9.dll" : GameReShadeProxyName(game);
        var gameReShadeApi = game.Api == GraphicsApi.OpenGl ? "opengl" : game.Route == InstallRoute.X64Dx9NativeFeeder ? "d3d9" : "d3d10";
        var reShade64Dll = ReShade64DllForInstall(game, payload.ReShade64Dll);
        var gameReShadeIni = Path.Combine(gameDir, "ReShade.ini");
        var gameReShadeVrIni = Path.Combine(gameDir, "ReShadeVR.ini");
        var gamePreset = Path.Combine(gameDir, "ReShadePreset.ini");
        if (NeedsOpenVrProxy(game))
            PrepareOpenVrProxyLibrary(gameDir, game.Root, manifest);

        await InstallReShadeAsync(game.ExePath, Path.Combine(gameDir, gameReShadeDll), reShade64Dll, payload.ReShadeSetup, gameReShadeApi, game.Root, manifest, cancellationToken);
        RemoveUnusedReShadeProxy(gameDir, game.Root, manifest, gameReShadeDll);

        RemoveVulkanBridgeArtifacts(gameDir, game.Root, manifest);
        RemoveLegacyManagedAddons(gameDir, game.Root, manifest);
        CopyWithBackup(AppFile("Runtime", "x64-dx9-dx11", "dlss5-feed.addon64"), Path.Combine(gameDir, "dlss5-feed.addon64"), game.Root, manifest);
        var feedConfig = Path.Combine(gameDir, "dlss5-feed.cfg");
        CopyWithBackup(AppFile("Configs", "dlss5-feed-64.cfg"), feedConfig, game.Root, manifest);
        ConfigureFile(feedConfig, game.Root, manifest, () =>
        {
            ConfigureFeeder(feedConfig);
            ConfigureVrEyeSplit(feedConfig, forceVrEyeSplit);
        });

        var shaderRoot = Path.Combine(gameDir, "reshade-shaders");
        var shaderDir = Path.Combine(shaderRoot, "Shaders");
        CopyWithBackup(AppFile("Runtime", "shaders", "DLSS5_Feed.fx"), Path.Combine(shaderDir, "DLSS5_Feed.fx"), game.Root, manifest);
        CopyWithBackup(AppFile("Runtime", "shaders", "ReShade.fxh"), Path.Combine(shaderDir, "ReShade.fxh"), game.Root, manifest);
        foreach (var ini in new[] { gameReShadeIni, gameReShadeVrIni })
            ConfigureFile(ini, game.Root, manifest, () =>
            {
                ConfigureGameReShade(ini, enableGeomFit: !gameReShadeApi.Equals("opengl", StringComparison.OrdinalIgnoreCase));
                ConfigureOpenVrProxyIfNeeded(game, ini);
            });
        ConfigureFile(gamePreset, game.Root, manifest, () => ConfigureGamePreset(gamePreset));

        var hostDir = Path.Combine(gameDir, "host64");
        var hostExe = Path.Combine(hostDir, "dlss5-feed-host64.exe");
        CopyWithBackup(AppFile("Runtime", "host64", "dlss5-feed-host64.exe"), hostExe, game.Root, manifest);

        var hostReShadeIni = Path.Combine(hostDir, "ReShade.ini");
        await InstallReShadeAsync(hostExe, Path.Combine(hostDir, "dxgi.dll"), payload.ReShade64Dll, payload.ReShadeSetup, "d3d10", game.Root, manifest, cancellationToken);
        InstallPhoenix(hostDir, game.Root, payload, manifest);

        ConfigureFile(hostReShadeIni, game.Root, manifest, () => ConfigureHostReShade(hostReShadeIni));
        _log("Installed x64 feeder and 64-bit direct Phoenix DLSS5 host.");
    }

    async Task InstallX64VulkanFeederAsync(GameCandidate game, PayloadInfo payload, InstallManifest manifest, bool forceVrEyeSplit, CancellationToken cancellationToken)
    {
        var gameDir = Path.GetDirectoryName(game.ExePath)!;
        await InstallVulkanReShadeLayerAsync(game.ExePath, payload.ReShade64Dll, payload.ReShadeSetup, cancellationToken);
        RemoveVulkanBridgeArtifacts(gameDir, game.Root, manifest);
        RemoveVulkanFeederArtifacts(gameDir, game.Root, manifest);
        CopyWithBackup(AppFile("Runtime", "host64", "phoenix-vulkan-feed.addon64"),
            Path.Combine(gameDir, "phoenix-vulkan-feed.addon64"), game.Root, manifest);
        var feedConfig = Path.Combine(gameDir, "dlss5-feed.cfg");
        ConfigureFile(feedConfig, game.Root, manifest, () => SetFlatConfigValue(feedConfig, "iterations", _frameIterations.ToString()));
        foreach (var name in new[] { "ReShade.ini", "ReShadeVR.ini" })
        {
            var ini = Path.Combine(gameDir, name);
            ConfigureFile(ini, game.Root, manifest, () => ConfigureVulkanBridgeReShade(ini));
        }
        var preset = Path.Combine(gameDir, "ReShadePreset.ini");
        ConfigureFile(preset, game.Root, manifest, () => ConfigureNoEffectsPreset(preset));
        var hostDir = Path.Combine(gameDir, "host64");
        var hostExe = Path.Combine(hostDir, "dlss5-feed-host64.exe");
        CopyWithBackup(AppFile("Runtime", "host64", "dlss5-feed-host64.exe"), hostExe, game.Root, manifest);
        await InstallReShadeAsync(hostExe, Path.Combine(hostDir, "dxgi.dll"), payload.ReShade64Dll, payload.ReShadeSetup,
            "d3d10", game.Root, manifest, cancellationToken);
        var hostIni = Path.Combine(hostDir, "ReShade.ini");
        ConfigureFile(hostIni, game.Root, manifest, () => ConfigureHostReShade(hostIni));
        InstallPhoenix(hostDir, game.Root, payload, manifest);
        _log("Installed Vulkan SDR CPU bridge -> direct D3D12 Phoenix DLSS5 host. GPU execution remains unverified.");
        LogVulkanLayerDisableNote(game);
    }

    async Task InstallX64OpenXrLayerAsync(GameCandidate game, PayloadInfo payload, InstallManifest manifest, CancellationToken cancellationToken)
    {
        var gameDir = Path.GetDirectoryName(game.ExePath)!;
        RemoveOpenXrConflictingRootArtifacts(gameDir, game.Root, manifest);

        var stageRoot = Path.Combine(gameDir, "_DLSS5_OpenXR");
        var layerDir = Path.Combine(stageRoot, "openxr");
        var hostDir = Path.Combine(stageRoot, "host64");
        CopyWithBackup(AppFile("Runtime", "openxr", "XR_APILAYER_DLSS5_everything.dll"), Path.Combine(layerDir, "XR_APILAYER_DLSS5_everything.dll"), game.Root, manifest);
        CopyWithBackup(AppFile("Runtime", "openxr", "XR_APILAYER_DLSS5_everything.json"), Path.Combine(layerDir, "XR_APILAYER_DLSS5_everything.json"), game.Root, manifest);

        var hostExe = Path.Combine(hostDir, "dlss5-feed-host64.exe");
        CopyWithBackup(AppFile("Runtime", "host64", "dlss5-feed-host64.exe"), hostExe, game.Root, manifest);

        var hostReShadeIni = Path.Combine(hostDir, "ReShade.ini");
        var hostPreset = Path.Combine(hostDir, "ReShadePreset.ini");
        await InstallReShadeAsync(hostExe, Path.Combine(hostDir, "dxgi.dll"), payload.ReShade64Dll, payload.ReShadeSetup, "d3d10", game.Root, manifest, cancellationToken);
        InstallPhoenix(hostDir, game.Root, payload, manifest);

        ConfigureFile(hostReShadeIni, game.Root, manifest, () =>
        {
            ConfigureHostReShade(hostReShadeIni);
            IniEditor.SetValue(hostReShadeIni, "DLSS5_HOST", "FrameIterations", _frameIterations.ToString(System.Globalization.CultureInfo.InvariantCulture));
        });
        ConfigureFile(hostPreset, game.Root, manifest, () => ConfigureNoEffectsPreset(hostPreset));
        _log("Installed x64 OpenXR API-layer route.");
    }

    void LogVulkanLayerDisableNote(GameCandidate game)
    {
        const string marker = "DISABLE_VK_LAYER_reshade_1";
        if (PeReader.FindMarkers(game.ExePath, [marker]).Contains(marker))
            _log("The executable references a ReShade Vulkan disable flag. Verify layer activation at runtime; global layer files are not modified.");
    }

    async Task InstallX64Async(GameCandidate game, PayloadInfo payload, InstallManifest manifest, CancellationToken cancellationToken)
    {
        var gameDir = Path.GetDirectoryName(game.ExePath)!;
        var gameReShadeDll = GameReShadeProxyName(game);
        var reShade64Dll = ReShade64DllForInstall(game, payload.ReShade64Dll);
        var gameReShadeIni = Path.Combine(gameDir, "ReShade.ini");
        if (NeedsOpenVrProxy(game))
            PrepareOpenVrProxyLibrary(gameDir, game.Root, manifest);

        await InstallReShadeAsync(game.ExePath, Path.Combine(gameDir, gameReShadeDll), reShade64Dll, payload.ReShadeSetup, "d3d10", game.Root, manifest, cancellationToken);
        RemoveUnusedReShadeProxy(gameDir, game.Root, manifest, gameReShadeDll);
        RemoveLegacyManagedAddons(gameDir, game.Root, manifest);
        InstallPhoenix(gameDir, game.Root, payload, manifest);


        ConfigureFile(gameReShadeIni, game.Root, manifest, () =>
        {
            ConfigureDirectPhoenixReShade(gameReShadeIni);
            ConfigureOpenVrProxyIfNeeded(game, gameReShadeIni);
        });
        _log("Installed Phoenix DLSS5 for native D3D12 NGX SR/RR. Game Streamline and NGX libraries were preserved.");
    }

    Task InstallX64NativeThenFeederAsync(GameCandidate game, PayloadInfo payload, InstallManifest manifest, bool forceVrEyeSplit, CancellationToken cancellationToken) =>
        InstallX64FeederAsync(game, payload, manifest, forceVrEyeSplit, cancellationToken);

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

        await Task.CompletedTask;
        throw new InvalidOperationException("Extract the matching ReShade32.dll/ReShade64.dll into Payload first. An external setup executable cannot provide bounded game-folder rollback.");
    }

    bool CopyWithBackup(string source, string destination, string gameRoot, InstallManifest manifest, string? expectedSha256 = null)
    {
        var copied = TryInstallFile(destination, gameRoot, manifest, () =>
        {
            TrackExternalWrite(destination, gameRoot, manifest);
            // Replace the directory entry, not a possibly hard-linked payload's bytes.
            var temporary = destination + ".phoenix-" + Guid.NewGuid().ToString("N") + ".tmp";
            TrackExternalWrite(temporary, gameRoot, manifest);
            try
            {
                File.Copy(source, temporary);
                if (expectedSha256 is not null && !Hash(temporary).Equals(expectedSha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Phoenix runtime changed while installing: " + Path.GetRelativePath(gameRoot, destination));
                File.Move(temporary, destination, overwrite: true);
                if (expectedSha256 is not null && !Hash(destination).Equals(expectedSha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Installed Phoenix runtime checksum mismatch: " + Path.GetRelativePath(gameRoot, destination));
            }
            finally
            {
                // A blocked partial copy must remain owned for later removal.
                if (TryInstallFile(temporary, gameRoot, manifest, () => File.Delete(temporary)))
                    manifest.Added.Remove(Path.GetRelativePath(gameRoot, temporary));
                PersistPending(gameRoot, manifest);
            }
        });
        if (copied) _log("Copied " + Path.GetRelativePath(gameRoot, destination));
        return copied;
    }

    bool TryInstallFile(string destination, string root, InstallManifest manifest, Action operation)
    {
        // Unsafe paths and links are fatal, not recoverable copy errors.
        ValidateTarget(root, Path.GetRelativePath(root, destination));
        try { operation(); return true; }
        catch (Exception error) when (error is UnauthorizedAccessException or IOException)
        {
            var relative = Path.GetRelativePath(root, destination);
            manifest.FileFailures.RemoveAll(file => file.RelativePath.Equals(relative, StringComparison.OrdinalIgnoreCase));
            manifest.FileFailures.Add(new(relative, error.Message.Length > 4096 ? error.Message[..4096] : error.Message));
            PersistPending(root, manifest);
            _log("WARNING: Could not install " + relative + ". Continuing with other files. " + error.Message);
            return false;
        }
    }

    void ConfigureFile(string path, string root, InstallManifest manifest, Action configure)
    {
        if (manifest.FileFailures.Any(file => file.RelativePath.Equals(Path.GetRelativePath(root, path), StringComparison.OrdinalIgnoreCase))) return;
        TryInstallFile(path, root, manifest, () => { TrackExternalWrite(path, root, manifest); configure(); });
    }

    void WritePhoenixConfiguration(string directory, string model, string root, InstallManifest manifest)
    {
        ConfigureFile(Path.Combine(directory, PhoenixConfiguration.FileName), root, manifest,
            () => PhoenixConfiguration.WriteSidecar(directory, model, _phoenixSettings, _phoenixInputProvider));
        foreach (var name in new[] { "ReShade.ini", "ReShadeVR.ini" })
        {
            var ini = Path.Combine(directory, name);
            ConfigureFile(ini, root, manifest, () =>
            {
                foreach (var pair in PhoenixConfiguration.Controls(_phoenixSettings))
                    IniEditor.SetValue(ini, "Phoenix", pair.Key, pair.Value);
            });
        }
    }

    async Task InstallPackagedAsync(GameCandidate game, PayloadInfo payload, PackagedPhoenixPlan plan,
        InstallManifest manifest, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(game.ExePath)!;
        if (plan.Translation != InstallRoute.Unsupported)
        {
            var mode = plan.Translation switch
            {
                InstallRoute.X86DirectXLegacyViaDgVoodoo or InstallRoute.X64DirectXLegacyViaDgVoodoo => DgVoodooMode.DirectXLegacy,
                InstallRoute.X86Dx8ViaDgVoodoo => DgVoodooMode.DirectX8,
                InstallRoute.X86Dx9ViaDgVoodoo => DgVoodooMode.DirectX9,
                InstallRoute.X86Glide211ViaDgVoodoo => DgVoodooMode.Glide211,
                InstallRoute.X86Glide245ViaDgVoodoo => DgVoodooMode.Glide245,
                InstallRoute.X86Glide31ViaDgVoodoo => DgVoodooMode.Glide31,
                InstallRoute.X86Glide31NapalmViaDgVoodoo => DgVoodooMode.Glide31Napalm,
                _ => DgVoodooMode.None
            };
            RemoveInactiveX86RouteArtifacts(directory, game.Root, manifest, plan.Proxy, mode,
                plan.Translation == InstallRoute.X86Dx8ViaD3D8To9);
            if (mode != DgVoodooMode.None) InstallDgVoodoo(directory, game.Root, payload, manifest, mode, game.Arch);
            else foreach (var file in plan.TranslationFiles(payload))
                CopyWithBackup(file.Source!, Path.Combine(directory, file.Target), game.Root, manifest);
        }
        foreach (var path in PackagedPhoenixPlan.RetiredComponents(game, plan.Proxy))
        {
            ConfigureFile(path, game.Root, manifest, () =>
            {
                File.Delete(path);
                manifest.Retired.Add(Path.GetRelativePath(game.Root, path));
                _log("Backed up and retired obsolete graphics component: " + Path.GetRelativePath(game.Root, path));
            });
        }
        var angle = GameScanner.IsAngleLayout(game.ExePath);
        if (game.Api == GraphicsApi.Vulkan || angle)
        {
            var x86 = game.Arch == CpuArch.X86;
            var layer = await _vulkanLayer(x86).InstallAsync(game.ExePath, (x86 ? payload.ReShade32Dll : payload.ReShade64Dll)!,
                cancellationToken, beforeCommit: result =>
                {
                    manifest.VulkanLaunchEnvironment = new(result.LaunchEnvironment);
                    if (!result.ReferenceAdded) { PersistPending(game.Root, manifest); return; }
                    // Persist ownership before the separate layer transaction writes
                    // shared state. Recovery can safely detach a missing reference.
                    manifest.VulkanLayerX86 = x86;
                    PersistPending(game.Root, manifest);
                });
            // The Vulkan loader deduplicates layer names before evaluating their
            // disable controls. Search this architecture's manifest first so the
            // selected local layer wins over a protected system installation.
            var layerDirectory = Path.Combine(directory, "_DLSS5_Vulkan");
            var layerManifest = Path.Combine(layerDirectory, "ReShade.json");
            ConfigureFile(layerManifest, game.Root, manifest, () => File.WriteAllText(layerManifest, layer.LaunchManifestJson));
            manifest.VulkanLaunchEnvironment["VK_ADD_IMPLICIT_LAYER_PATH"] = layerDirectory;
        }
        if (game.Api != GraphicsApi.Vulkan)
            await InstallReShadeAsync(game.ExePath, Path.Combine(directory, plan.Proxy),
                plan.ReShadeOverride ?? (game.Arch == CpuArch.X86 ? payload.ReShade32Dll : payload.ReShade64Dll),
                payload.ReShadeSetup, game.Api.ToString(), game.Root, manifest, cancellationToken);
        var addonName = game.Arch == CpuArch.X86 ? "Phoenix.addon32" : "Phoenix.addon64";
        CopyWithBackup(plan.Addon, Path.Combine(directory, addonName), game.Root, manifest);
        var runtimeFiles = PhoenixRuntimePackage.For(payload, game.Arch);
        foreach (var runtimeFile in runtimeFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var destination = Path.Combine(directory, "PhoenixRuntime", runtimeFile.RelativePath);
            CopyWithBackup(runtimeFile.SourcePath, destination, game.Root, manifest, runtimeFile.Sha256);
        }
        if (runtimeFiles.Count > 0) _log("Processed architecture-matched Phoenix runtime companions; blocked files are listed in the installation result.");
        var model = _phoenixModelPath;
        if (_phoenixSettings.CopyNvidiaDllToGame && File.Exists(model))
        {
            var destination = Path.Combine(directory, "PhoenixModel.dll");
            if (!LocalPaths.Same(model, destination)) CopyWithBackup(model, destination, game.Root, manifest);
            model = destination;
        }
        WritePhoenixConfiguration(directory, model, game.Root, manifest);
        foreach (var name in new[] { "ReShade.ini", "ReShadeVR.ini" })
        {
            var ini = Path.Combine(directory, name);
            ConfigureFile(ini, game.Root, manifest, () =>
            {
                ConfigurePackagedPhoenixReShade(ini);
                IniEditor.SetValue(ini, "Phoenix.Compatibility", "Enabled", plan.NativeGameInput ? "0" : "1");
                IniEditor.SetValue(ini, "Phoenix.Compatibility", "StandaloneInput", plan.NativeGameInput ? "0" : "1");
                ConfigurePhoenixRuntimePaths(ini, game.Arch, _phoenixSettings);
                // A helper-only x64 frontend cannot use the same-device API.
                // x86 optional runtimes use their own compatibility contract;
                // preserve those choices and validated explicit mechanisms.
                if (plan.RequiresHelperDefault(payload, game, _phoenixSettings))
                    IniEditor.SetValue(ini, "Phoenix.Compatibility", "ExecutionMode", "helper");
                if (plan.SoftwareDeviceSupport) IniEditor.SetValue(ini, "APP", "IncludeSoftwareDevices", "1");
                IniEditor.SetValue(ini, "Phoenix.Compatibility", "CoherentFrames", "1");
            });
        }
        InstallLegacyCom(game, payload, manifest, plan.Translation);
        if (plan.Translation is InstallRoute.X86DirectXLegacyViaDgVoodoo or InstallRoute.X64DirectXLegacyViaDgVoodoo)
        {
            foreach (var alias in ImportedDirectDrawPlan.Find(game.ExePath, game.Arch,
                         path => _previouslyOwned.Contains(Path.GetRelativePath(game.Root, path))))
            {
                CopyWithBackup(Path.Combine(directory, "DDraw.dll"), alias, game.Root, manifest);
                _log("Installed DirectDraw translator at imported alias: " + Path.GetFileName(alias));
            }
        }
        _log("Installed " + plan.Description + ".");
    }

    void InstallLegacyCom(GameCandidate game, PayloadInfo payload, InstallManifest manifest, InstallRoute translation)
    {
        var directory = Path.GetDirectoryName(game.ExePath)!;
        var legacy = LegacyComPlan.For(game.ExePath, translation);
        if (legacy is null)
        {
            foreach (var name in new[] { "PhoenixLegacy.addon32", "PhoenixLegacy.addon64", "PhoenixLegacy.ini", "PhoenixDgVoodooDDraw.dll" })
            {
                var path = Path.Combine(directory, name);
                if (File.Exists(path) && _previouslyOwned.Contains(Path.GetRelativePath(game.Root, path)))
                    RemoveRootAddonFile(path, game.Root, manifest, "Removed inactive legacy COM compatibility: ");
            }
            return;
        }
        var proxy = game.Arch == CpuArch.X86 ? "PhoenixLegacyDDraw32.dll" : "PhoenixLegacyDDraw64.dll";
        var source = Path.Combine(payload.Root, "legacy-com", proxy);
        if (!PayloadScanner.IsPeDll(source, game.Arch) || !PayloadScanner.HasExports(source, "DirectDrawCreate", "DllGetClassObject"))
            throw new InvalidOperationException("Payload is missing architecture-matched legacy COM compatibility: " + proxy);
        CopyRequired(game.Arch == CpuArch.X86 ? payload.DgVoodooDDraw : payload.DgVoodooDDraw64,
            Path.Combine(directory, "PhoenixDgVoodooDDraw.dll"), "dgVoodoo DirectDraw forwarding target", game.Root, manifest);
        CopyWithBackup(source, Path.Combine(directory, "DDraw.dll"), game.Root, manifest);
        foreach (var name in new[] { "PhoenixLegacy.addon32", "PhoenixLegacy.addon64" })
        {
            var previous = Path.Combine(directory, name);
            if (File.Exists(previous) && _previouslyOwned.Contains(Path.GetRelativePath(game.Root, previous)))
                RemoveRootAddonFile(previous, game.Root, manifest, "Removed superseded legacy COM addon: ");
        }
        var ini = Path.Combine(directory, "PhoenixLegacy.ini");
        ConfigureFile(ini, game.Root, manifest, () =>
        {
            IniEditor.SetValue(ini, "Compatibility", "DirectDrawCOM", legacy.DirectDraw ? "1" : "0");
            IniEditor.SetValue(ini, "Compatibility", "DirectPlayUserRegistration", legacy.DirectPlayUserRegistration ? "1" : "0");
        });
        _log("Installed process-local legacy COM activation and per-user registration compatibility.");
    }

    static void ConfigurePhoenixRuntimePaths(string ini, CpuArch arch, PhoenixSettings settings)
    {
        // Reapply the locations checked before mutation, including external folders.
        // Runtime files outside the managed deployment remain user-owned.
        IniEditor.SetValue(ini, "Phoenix.Compatibility", "ExecutionMode", settings.CompatibilityExecutionMode);
        IniEditor.SetValue(ini, "Phoenix.Compatibility", "NativeRuntime", settings.NativeRuntimePath ?? "PhoenixRuntime/wow64");
        IniEditor.SetValue(ini, "Phoenix.Compatibility", "PortableRuntime", settings.PortableRuntimePath ?? "PhoenixRuntime/" + (arch == CpuArch.X86 ? "win-x86" : "win-x64"));
        if (settings.PortableDevice is not null)
            IniEditor.SetValue(ini, "Phoenix.Compatibility", "PortableDevice", settings.PortableDevice);
        if (settings.PortableAllowUnvalidated is not null)
            IniEditor.SetValue(ini, "Phoenix.Compatibility", "PortableAllowUnvalidated", settings.PortableAllowUnvalidated.Value ? "1" : "0");
    }

    void InstallPhoenix(string directory, string gameRoot, PayloadInfo payload, InstallManifest manifest)
    {
        RemoveLegacyManagedAddons(directory, gameRoot, manifest);
        CopyWithBackup(payload.PhoenixAddon!, Path.Combine(directory, "Phoenix.addon64"), gameRoot, manifest);
        var model = _phoenixModelPath;
        if (_phoenixSettings.CopyNvidiaDllToGame && File.Exists(model))
        {
            if (_phoenixInputProvider == PhoenixConfiguration.FeederInputProvider && PhoenixConfiguration.RequiresNativeNr(_phoenixSettings))
                CopyWithBackup(model, Path.Combine(directory, "nvngx_dlssnr.dll"), gameRoot, manifest);
            var target = Path.Combine(directory, "PhoenixModel.dll");
            if (!Path.GetFullPath(model).Equals(Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase))
                CopyWithBackup(model, target, gameRoot, manifest);
            model = target;
        }
        WritePhoenixConfiguration(directory, model, gameRoot, manifest);
    }

    static bool IsBundledManagedAddon(string name) =>
        name.StartsWith("renodx", StringComparison.OrdinalIgnoreCase) ||
        new[] { "Phoenix", "phoenix-vulkan-feed", "dlss5-feed", "dlss5-bridge" }
            .Contains(Path.GetFileNameWithoutExtension(name), StringComparer.OrdinalIgnoreCase);

    internal static void ValidateCustomAddons(IEnumerable<string> addons)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var addon in addons)
        {
            var extension = Path.GetExtension(addon);
            if (extension is not (".addon32" or ".addon64") &&
                !extension.Equals(".addon32", StringComparison.OrdinalIgnoreCase) && !extension.Equals(".addon64", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Inactive or invalid custom add-on: " + addon);
            if (IsBundledManagedAddon(Path.GetFileName(addon)))
                throw new InvalidOperationException("Managed add-on cannot be installed as a custom add-on: " + addon);
            var architecture = extension.Equals(".addon32", StringComparison.OrdinalIgnoreCase) ? CpuArch.X86 : CpuArch.X64;
            if (!PayloadScanner.IsPeDll(addon, architecture))
                throw new InvalidOperationException("Custom add-on is not a valid PE DLL matching its filename: " + addon);
            if (!names.Add(Path.GetFileName(addon))) throw new InvalidOperationException("Duplicate custom add-on filename: " + addon);
        }
    }

    void InstallCustomAddons(GameCandidate game, PayloadInfo payload, InstallManifest manifest)
    {
        var extension = game.Arch == CpuArch.X86 ? ".addon32" : ".addon64";
        var directory = Path.GetDirectoryName(game.ExePath)!;
        foreach (var addon in payload.ExtraAddons.Where(path => Path.GetExtension(path).Equals(extension, StringComparison.OrdinalIgnoreCase)))
            CopyWithBackup(addon, Path.Combine(directory, Path.GetFileName(addon)), game.Root, manifest);
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

    void ConfigureFeeder(string path)
    {
        ConfigureFrameIterations(path);
        SetFlatConfigValue(path, "native_probe_seconds", "0");
        SetFlatConfigValue(path, "render_scale", "1.000");
        SetFlatConfigValue(path, "output_scale", "1.000");
        // Phoenix owns its original/split/difference views.
        SetFlatConfigValue(path, "compare_mode", "1");
    }

    static string GameReShadeProxyName(GameCandidate game)
    {
        game.ValidateOverrides();
        if (game.ProxyName is { } proxy) return proxy.ToLowerInvariant();
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
        // An explicit DXGI proxy replaces the DX11/OpenVR detection heuristic, not an OpenVR route.
        if (game.ProxyName is not null) return false;
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

        if (File.Exists(openVr) && IsOpenVrRuntimeLibrary(openVr))
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

    static bool IsOpenVrRuntimeLibrary(string path)
    {
        LocalPaths.RequireNoLinks(path);
        return PayloadScanner.IsX64Dll(path) &&
            PayloadScanner.HasExports(path, "VR_GetGenericInterface", "VR_InitInternal", "VR_ShutdownInternal", "VR_IsHmdPresent") &&
            PeReader.FindMarkers(path, new[] { "ReShadeVersion", OpenVrOriginalDllName, "SpecialK", "OptiScaler", "RenoDX" }).Count == 0;
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

    void InstallDgVoodoo(string gameDir, string gameRoot, PayloadInfo payload, InstallManifest manifest, DgVoodooMode mode, CpuArch architecture = CpuArch.X86)
    {
        switch (mode)
        {
            case DgVoodooMode.DirectXLegacy:
                var x64 = architecture == CpuArch.X64;
                CopyRequired(x64 ? payload.DgVoodooDDraw64 : payload.DgVoodooDDraw, Path.Combine(gameDir, "DDraw.dll"), "dgVoodoo2 architecture-matched DDraw.dll", gameRoot, manifest);
                CopyRequired(x64 ? payload.DgVoodooD3DImm64 : payload.DgVoodooD3DImm, Path.Combine(gameDir, "D3DImm.dll"), "dgVoodoo2 architecture-matched D3DImm.dll", gameRoot, manifest);
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
        ConfigureFile(dgVoodooConfig, gameRoot, manifest, () => ConfigureDgVoodoo(dgVoodooConfig));
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

        foreach (var candidate in new[] { "d3d8.dll", "D3D8.dll", "D3D9.dll", "D3DImm.dll", "DDraw.dll", "Glide.dll", "Glide2x.dll", "Glide3x.dll", "dxgi.dll", "d3d11.dll", "opengl32.dll" })
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

    void RemoveLegacyManagedAddons(string gameDir, string gameRoot, InstallManifest manifest)
    {
        if (!Directory.Exists(gameDir)) return;
        foreach (var file in Directory.EnumerateFiles(gameDir, "renodx-dlss5*.addon*", SearchOption.TopDirectoryOnly))
            if (_previouslyOwned.Contains(Path.GetRelativePath(gameRoot, file)) || PackagedPhoenixPlan.IsPreviousNeuralAddon(file))
                RemoveRootAddonFile(file, gameRoot, manifest, "Backed up and removed legacy managed add-on: ");
        foreach (var name in new[] { "ReShade.ini", "ReShadeVR.ini" })
        {
            var path = Path.Combine(gameDir, name);
            if (!File.Exists(path)) continue;
            if (!_previouslyOwned.Contains(Path.GetRelativePath(gameRoot, path)) &&
                !ManagedFiles(manifest).Contains(Path.GetRelativePath(gameRoot, path), StringComparer.OrdinalIgnoreCase)) continue;
            ConfigureFile(path, gameRoot, manifest, () =>
            {
                var lines = File.ReadAllLines(path);
                var result = new List<string>();
                var skip = false;
                foreach (var line in lines)
                {
                    var trimmed = line.Trim();
                    if (trimmed.StartsWith('[') && trimmed.EndsWith(']'))
                        skip = trimmed.Equals("[RenoDX.DLSS5]", StringComparison.OrdinalIgnoreCase);
                    if (!skip) result.Add(line);
                }
                if (result.Count != lines.Length) File.WriteAllLines(path, result);
            });
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
        if (!_previouslyOwned.Contains(relative) && !ManagedFiles(manifest).Contains(relative, StringComparer.OrdinalIgnoreCase))
            return;
        if (TryInstallFile(path, gameRoot, manifest, () =>
        {
            TrackExternalWrite(path, gameRoot, manifest);
            File.Delete(path);
            manifest.Retired.Add(relative);
        })) _log(message + Path.GetRelativePath(gameRoot, path));
    }

    void AddDirectoryIfNew(string directory, string gameRoot, InstallManifest manifest)
    {
        if (Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar).Equals(
                Path.GetFullPath(gameRoot).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)) return;
        var relative = Path.GetRelativePath(gameRoot, directory);
        directory = ValidateTarget(gameRoot, relative);
        if (Directory.Exists(directory)) return;
        if (File.Exists(directory)) throw new IOException("Directory target is a file: " + directory);
        AddDirectoryIfNew(Path.GetDirectoryName(directory)!, gameRoot, manifest);
        if (!manifest.AddedDirectories.Contains(relative, StringComparer.OrdinalIgnoreCase))
            manifest.AddedDirectories.Add(relative);
        PersistPending(gameRoot, manifest);
        Directory.CreateDirectory(directory);
    }

    void TrackExternalWrite(string destination, string gameRoot, InstallManifest manifest)
    {
        var relative = Path.GetRelativePath(gameRoot, destination);
        destination = ValidateTarget(gameRoot, relative);
        EnsureUnlocked(destination);
        AddDirectoryIfNew(Path.GetDirectoryName(destination)!, gameRoot, manifest);
        if (!manifest.Added.Contains(relative, StringComparer.OrdinalIgnoreCase) &&
            !manifest.Replaced.Any(item => item.RelativePath.Equals(relative, StringComparison.OrdinalIgnoreCase)))
        {
            if (File.Exists(destination))
            {
                var backupRelative = manifest.BackupHashEvidence == "adopted-existing-v1-backups"
                    ? relative : Path.Combine("snapshots", manifest.TransactionId, relative);
                var backup = ContainedPath(BackupRoot(gameRoot), backupRelative);
                Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
                if (File.Exists(backup)) throw new IOException("Transaction backup already exists: " + backup);
                var staging = backup + ".backing-up-" + Guid.NewGuid().ToString("N");
                string digest;
                try
                {
                    using (var original = File.OpenRead(destination))
                    using (var copy = new FileStream(staging, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
                    {
                        original.CopyTo(copy); copy.Flush(flushToDisk: true);
                    }
                    digest = Hash(staging);
                    if (digest != Hash(destination)) throw new IOException("Original changed while backing up: " + destination);
                    File.Move(staging, backup);
                }
                finally
                {
                    try { File.Delete(staging); }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                    { _log("Could not remove an unused staged backup: " + error.Message); }
                }
                manifest.Replaced.Add(new ManifestFile { RelativePath = relative, BackupRelativePath = backupRelative, Sha256 = digest });
            }
            else manifest.Added.Add(relative);
        }
        PersistPending(gameRoot, manifest);
    }

    static Task InstallVulkanReShadeLayerAsync(string targetExe, string? reShade64Dll, string? setupExe, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RequireExistingVulkanLayer(targetExe);
        return Task.CompletedTask;
    }

    static void RequireExistingVulkanLayer(string targetExe)
    {
        foreach (var root in new[] {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ReShade"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ReShade") })
        {
            var apps = Path.Combine(root, "ReShadeApps.ini");
            ValidateNoReparse(apps);
            if (!File.Exists(apps) || new FileInfo(apps).Length > 256 * 1024) continue;
            var allowed = File.ReadLines(apps).Where(line => line.StartsWith("Apps=", StringComparison.OrdinalIgnoreCase))
                .SelectMany(line => line[5..].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            if (!allowed.Contains(targetExe, StringComparer.OrdinalIgnoreCase)) continue;
            foreach (var name in new[] { "ReShade64.json", "ReShade64_Dlss5Compat.json" })
            {
                var layer = Path.Combine(root, name);
                ValidateNoReparse(layer);
                if (!File.Exists(layer)) continue;
                foreach (var hive in new[] { Microsoft.Win32.Registry.CurrentUser, Microsoft.Win32.Registry.LocalMachine })
                {
                    using var key = hive.OpenSubKey(@"SOFTWARE\Khronos\Vulkan\ImplicitLayers");
                    if (key?.GetValue(layer) is int enabled && enabled == 0) return;
                }
            }
        }
        throw new InvalidOperationException("Vulkan requires an already registered x64 ReShade layer with this executable in ReShadeApps.ini. Configure it with the official ReShade installer first. Global layer registration is outside the game-folder recovery journal.");
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
    }

    static void ConfigureDirectPhoenixReShade(string ini)
    {
        ConfigureQuietReShadeUi(ini);
        IniEditor.SetValue(ini, "INPUT", "KeyOverlay", "36,0,0,0");
        IniEditor.SetValue(ini, "INPUT", "KeyScreenshot", "0,0,0,0");
        IniEditor.SetValue(ini, "ADDON", "DisabledAddons", "");
    }

    static void ConfigurePackagedPhoenixReShade(string ini)
    {
        var disabled = IniEditor.GetValue(ini, "ADDON", "DisabledAddons") ?? "";
        ConfigureQuietReShadeUi(ini);
        IniEditor.SetValue(ini, "INPUT", "KeyOverlay", "36,0,0,0");
        var keep = disabled.Split(',').Where(value =>
            !new[] { "Phoenix.addon32", "Phoenix.addon64" }.Contains(value.Split('@').Last().Trim(), StringComparer.OrdinalIgnoreCase));
        IniEditor.SetValue(ini, "ADDON", "DisabledAddons", string.Join(',', keep));
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
    }

    static void ConfigureOpenVrProxyIfNeeded(GameCandidate game, string ini)
    {
        if (game.ProxyName is not null)
        {
            IniEditor.SetValue(ini, "PROXY", "EnableProxyLibrary", "0");
            IniEditor.SetValue(ini, "PROXY", "ProxyLibrary", "");
            return;
        }
        if (!NeedsOpenVrProxy(game))
            return;

        IniEditor.SetValue(ini, "PROXY", "EnableProxyLibrary", "1");
        IniEditor.SetValue(ini, "PROXY", "ProxyLibrary", @".\" + OpenVrOriginalDllName);
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
        if (ngxCore is null) throw new IOException("Could not open NVIDIA DLSS5 NGXCore registry key.");
        ngxCore.SetValue("ShowDlssIndicator", 0, Microsoft.Win32.RegistryValueKind.DWord);
    }

    static void ConfigureDgVoodoo(string ini)
    {
        IniEditor.SetValue(ini, "GeneralExt", "DesktopResolution", "false");
        IniEditor.SetValue(ini, "DirectX", "dgVoodooWatermark", "false");
        IniEditor.SetValue(ini, "Glide", "dgVoodooWatermark", "false");
    }

    static IEnumerable<string> ManagedFiles(InstallManifest manifest) =>
        manifest.Replaced.Select(item => item.RelativePath).Concat(manifest.Added);

    public static InstallPreflightResult CheckPreflight(GameCandidate game, PayloadInfo? payload = null,
        CancellationToken cancellationToken = default, PhoenixSettings? phoenixSettings = null)
    {
        try
        {
            game.ValidateOverrides();
            ValidateRoot(game.Root);
            ValidateTarget(game.Root, Path.GetRelativePath(game.Root, game.ExePath));
            var owned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var planned = PlannedFiles(game, payload, phoenixSettings is not null &&
                PhoenixConfiguration.RequiresNativeNr(phoenixSettings), phoenixSettings?.CopyNvidiaDllToGame ?? true).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var relative in planned.Where(path => Path.GetFileName(path).Equals(PhoenixConfiguration.FileName, StringComparison.OrdinalIgnoreCase)))
                PhoenixConfiguration.ValidateWritePaths(Path.GetDirectoryName(ValidateTarget(game.Root, relative))!);
            var notes = new List<PreflightFinding>();
            var legacyCleanup = LegacyInstallCleanup.Inspect(game, cancellationToken);
            notes.AddRange(legacyCleanup.Findings);
            var legacyRetire = legacyCleanup.Retire.Select(file => file.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (payload is not null)
            {
                notes.AddRange(PhoenixRuntimePackage.CheckRequested(game, payload, phoenixSettings ?? new()));
                var inputPlan = PackagedPhoenixPlan.For(game, payload);
                if (inputPlan?.NativeGameInput == true && PeReader.FindMarkers(inputPlan.Addon,
                        ["Direct mode currently requires DLAA/native-size motion guides"]).Count > 0)
                    notes.Add(new("native-input-requirements", PreflightSeverity.Warning,
                        "This addon requires DLAA/native-resolution engine guides for native game-DLSS input. " +
                        "Scaled DLSS input can leave enhancement inactive; use Automatic with a standalone-capable addon.", inputPlan.Addon));
                var translation = PackagedPhoenixPlan.For(game, payload)?.Translation ?? game.Route;
                if (LegacyComPlan.For(game.ExePath, translation) is not null)
                {
                    var source = Path.Combine(payload.Root, "legacy-com", game.Arch == CpuArch.X86 ? "PhoenixLegacyDDraw32.dll" : "PhoenixLegacyDDraw64.dll");
                    if (!PayloadScanner.IsPeDll(source, game.Arch) || !PayloadScanner.HasExports(source, "DirectDrawCreate", "DllGetClassObject"))
                        throw new InvalidOperationException("Payload is missing architecture-matched legacy COM compatibility: " + source);
                }
            }
            foreach (var path in new[] { ManifestPath(game.Root), PendingPath(game.Root) })
            {
                ValidateNoReparse(path);
                if (!File.Exists(path)) continue;
                var manifest = ReadManifest(game.Root, path, allowDriveRelocation: path == ManifestPath(game.Root));
                var previousExecutable = ValidateTarget(game.Root, manifest.ExeRelativePath);
                if (!LocalPaths.Same(previousExecutable, game.ExePath))
                {
                    // Several engines ship a launcher and renderer side by side.
                    // They share injection files, so migrate the complete journal
                    // before applying the newly selected executable's architecture.
                    if (!CanMigrateSharedDirectory(previousExecutable, game.ExePath))
                        throw new IOException("Existing installation belongs to a different executable. Restore it before selecting another.");
                    notes.Add(new("shared-renderer-directory", PreflightSeverity.Warning,
                        "The existing installation in this executable directory will be restored before applying the selected renderer.", previousExecutable));
                }
                ValidateRestorePlan(game.Root, manifest, checkLocks: false);
                owned.UnionWith(ManagedFiles(manifest));
                planned.UnionWith(ManagedFiles(manifest));
                if (manifest.BackupHashEvidence == "adopted-existing-v1-backups")
                    notes.Add(new("legacy-backup-adoption", PreflightSeverity.Warning,
                        "Legacy backup paths and present-day hashes passed audit. Historical original-content integrity cannot be proven.", path));
                if (path == PendingPath(game.Root))
                    notes.Add(new("pending-recovery", PreflightSeverity.Warning, "Interrupted installation will be recovered before new installation.", path));
            }
            var packaged = PackagedPhoenixPlan.For(game, payload);
            var result = InstallPreflight.Check(game.Root, game.ExePath, planned, owned, cancellationToken: cancellationToken,
                activeInjectionDirectories: packaged is null ? null : [Path.GetDirectoryName(game.ExePath)!]);
            result = new(result.Findings.Select(finding => finding.Code == "file-unavailable" && finding.Path is { } path &&
                !LocalPaths.Same(path, game.ExePath) && !legacyRetire.Contains(path)
                ? finding with { Severity = PreflightSeverity.Warning,
                    Message = "This file may be skipped if it remains inaccessible; installation will continue with other files: " + path }
                : finding).ToArray());
            result = new(result.Findings.Select(finding => finding.Code == "mod-conflict" &&
                finding.Path is { } legacyPath && legacyRetire.Contains(legacyPath)
                ? finding with { Code = "verified-legacy-cleanup", Severity = PreflightSeverity.Information,
                    Message = "Previous installer-owned overlay will be backed up and retired before installation: " + legacyPath }
                : finding).ToArray());
            if (packaged is not null)
            {
                var retire = PackagedPhoenixPlan.RetiredComponents(game, packaged.Proxy).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var translators = packaged.TranslationFiles(payload!).Select(file =>
                    Path.Combine(Path.GetDirectoryName(game.ExePath)!, file.Target)).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var active = Path.Combine(Path.GetDirectoryName(game.ExePath)!, packaged.Proxy);
                result = new(result.Findings.Select(finding => finding.Code == "mod-conflict" && finding.Path is { } path &&
                    (retire.Contains(path) || translators.Contains(path) && PackagedPhoenixPlan.IsExistingDgVoodoo(path, game.Arch) ||
                     LocalPaths.Same(path, active) && PackagedPhoenixPlan.IsReShade(path) ||
                     Path.GetFileName(path).Equals(Path.GetFileName(packaged.Addon), StringComparison.OrdinalIgnoreCase) &&
                     PackagedPhoenixPlan.IsPreviousNeuralAddon(path))
                    ? finding with { Code = "verified-graphics-backup", Severity = PreflightSeverity.Information,
                        Message = "Recognized graphics component will be backed up before replacement or retirement: " + path }
                    : finding).ToArray());
            }
            if (game.Route == InstallRoute.X64VulkanFeeder && packaged is null) RequireExistingVulkanLayer(game.ExePath);
            var openVr = Path.Combine(Path.GetDirectoryName(game.ExePath)!, "openvr_api.dll");
            // This route intentionally chains the game's ABI library through the shim.
            // Do not label it journal-owned or exempt any other unmanaged destination.
            var chainOriginal = NeedsOpenVrProxy(game) && IsOpenVrRuntimeLibrary(openVr);
            return new(result.Findings.Select(finding => chainOriginal && finding.Code == "mod-conflict" &&
                    string.Equals(finding.Path, openVr, StringComparison.OrdinalIgnoreCase)
                ? finding with { Code = "openvr-original", Severity = PreflightSeverity.Warning,
                    Message = "OpenVR ABI library will be preserved in a verified backup and chained through the shim; review any existing OpenVR modifications." }
                : finding).Concat(notes).ToArray());
        }
        catch (IOException error) when ((error.HResult & 0xffff) is 32 or 33)
        {
            return new(new[] { new PreflightFinding("file-unavailable", PreflightSeverity.Error, error.Message) });
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or JsonException)
        {
            return new(new[] { new PreflightFinding("unsafe-installation", PreflightSeverity.Error, error.Message) });
        }
    }

    public static InstallPreflightResult CheckPreflight(GameCandidate game, CancellationToken cancellationToken) =>
        CheckPreflight(game, payload: null, cancellationToken: cancellationToken);

    static IReadOnlyList<string> PlannedFiles(GameCandidate game, PayloadInfo? payload, bool requiresNativeNr, bool copyModel)
    {
        var directory = Path.GetDirectoryName(game.ExePath)!;
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string folder, params string[] names)
        {
            foreach (var name in names) paths.Add(Path.GetRelativePath(game.Root, Path.Combine(folder, name)));
        }
        void Phoenix(string folder) {
            Add(folder, "Phoenix.addon64", "Phoenix.json", "ReShade.ini", "ReShadeVR.ini");
            if (copyModel) Add(folder, "PhoenixModel.dll");
        }
        Add(directory, BatchFileName(game));
        foreach (var file in LegacyInstallCleanup.Inspect(game).Retire)
            paths.Add(Path.GetRelativePath(game.Root, file.Path));
        if (PackagedPhoenixPlan.For(game, payload) is { } packaged)
        {
            if (game.Api == GraphicsApi.Vulkan) Add(Path.Combine(directory, "_DLSS5_Vulkan"), "ReShade.json");
            foreach (var file in packaged.TranslationFiles(payload!)) Add(directory, file.Target);
            if (LegacyComPlan.For(game.ExePath, packaged.Translation) is not null)
                Add(directory, "DDraw.dll", "PhoenixDgVoodooDDraw.dll", "PhoenixLegacy.ini");
            if (packaged.Translation != InstallRoute.Unsupported && packaged.Translation != InstallRoute.X86Dx8ViaD3D8To9)
                Add(directory, "dgVoodoo.conf", "dgVoodooCpl.exe");
            if (packaged.Proxy.Length > 0) Add(directory, packaged.Proxy);
            Add(directory, game.Arch == CpuArch.X86 ? "Phoenix.addon32" : "Phoenix.addon64",
                "Phoenix.json", "ReShade.ini", "ReShadeVR.ini");
            if (copyModel) Add(directory, "PhoenixModel.dll");
            foreach (var file in PhoenixRuntimePackage.For(payload!, game.Arch))
                Add(directory, Path.Combine("PhoenixRuntime", file.RelativePath));
            foreach (var retired in PackagedPhoenixPlan.RetiredComponents(game, packaged.Proxy))
                paths.Add(Path.GetRelativePath(game.Root, retired));
            foreach (var preset in LegacyEffectMigration.Find(game))
                paths.Add(Path.GetRelativePath(game.Root, preset.Destination));
            foreach (var addon in payload!.ExtraAddons.Where(path => Path.GetExtension(path).Equals(
                game.Arch == CpuArch.X86 ? ".addon32" : ".addon64", StringComparison.OrdinalIgnoreCase)))
                Add(directory, Path.GetFileName(addon));
            return paths.ToArray();
        }
        var direct = game.Route == InstallRoute.X64DirectPhoenix;
        var xr = game.Route == InstallRoute.X64OpenXrLayer;
        var vulkan = game.Route == InstallRoute.X64VulkanFeeder;
        if (direct)
        {
            Add(directory, GameReShadeProxyName(game));
            Phoenix(directory);
        }
        else
        {
            var host = Path.Combine(directory, xr ? "_DLSS5_OpenXR/host64" : "host64");
            Add(host, "dlss5-feed-host64.exe", "dxgi.dll", "ReShade.ini", "ReShadeVR.ini", "ReShadePreset.ini");
            Phoenix(host);
            // Only an explicitly selected NVIDIA comparison backend deploys the NR runtime.
            if (requiresNativeNr && copyModel) Add(host, "nvngx_dlssnr.dll");
            if (xr)
                Add(Path.Combine(directory, "_DLSS5_OpenXR/openxr"), "XR_APILAYER_DLSS5_everything.dll", "XR_APILAYER_DLSS5_everything.json");
            else
            {
                Add(directory, "ReShade.ini", "ReShadeVR.ini", "ReShadePreset.ini", "dlss5-feed.cfg");
                if (vulkan) Add(directory, "phoenix-vulkan-feed.addon64");
                else
                {
                    var proxy = game.Route == InstallRoute.X64Dx9NativeFeeder ? "D3D9.dll" : game.Arch == CpuArch.X64 ? GameReShadeProxyName(game) : game.ProxyName?.ToLowerInvariant() ?? (
                        game.Route is InstallRoute.X86Dx9NativeFeeder or InstallRoute.X86Dx8ViaD3D8To9 ? "D3D9.dll" :
                        game.Route == InstallRoute.X86OpenGlFeeder ? "opengl32.dll" : "dxgi.dll");
                    Add(directory, proxy, game.Arch == CpuArch.X64 ? "dlss5-feed.addon64" : "dlss5-feed.addon32");
                    Add(Path.Combine(directory, "reshade-shaders/Shaders"), "DLSS5_Feed.fx", "ReShade.fxh");
                }
            }
        }
        switch (game.Route)
        {
            case InstallRoute.X86DirectXLegacyViaDgVoodoo:
            case InstallRoute.X64DirectXLegacyViaDgVoodoo: Add(directory, "DDraw.dll", "D3DImm.dll"); break;
            case InstallRoute.X86Dx8ViaD3D8To9:
            case InstallRoute.X86Dx8ViaDgVoodoo: Add(directory, "D3D8.dll"); break;
            case InstallRoute.X86Dx9ViaDgVoodoo: Add(directory, "D3D9.dll"); break;
            case InstallRoute.X86Glide211ViaDgVoodoo: Add(directory, "Glide.dll"); break;
            case InstallRoute.X86Glide245ViaDgVoodoo: Add(directory, "Glide2x.dll"); break;
            case InstallRoute.X86Glide31ViaDgVoodoo:
            case InstallRoute.X86Glide31NapalmViaDgVoodoo: Add(directory, "Glide3x.dll"); break;
        }
        if (game.Route.ToString().Contains("DgVoodoo", StringComparison.Ordinal))
            Add(directory, "dgVoodoo.conf", "dgVoodooCpl.exe");
        if (NeedsOpenVrProxy(game)) Add(directory, OpenVrOriginalDllName, "ReShade64.dll");
        foreach (var addon in payload?.ExtraAddons ?? Array.Empty<string>())
            if (Path.GetExtension(addon).Equals(game.Arch == CpuArch.X86 ? ".addon32" : ".addon64", StringComparison.OrdinalIgnoreCase))
                Add(directory, Path.GetFileName(addon));
        return paths.ToArray();
    }

    static void PreflightRestore(string root, InstallManifest manifest, CancellationToken cancellationToken)
    {
        var files = ManagedFiles(manifest).ToArray();
        var result = InstallPreflight.Check(root, ValidateTarget(root, manifest.ExeRelativePath), files, files,
            cancellationToken: cancellationToken);
        // A recognized project addon added after this journal is not a restore
        // destination. Preserve it here; a subsequent install can retire it in
        // its own transaction. Unknown conflicting mods still block the plan.
        result = new(result.Findings.Select(finding => finding.Code == "mod-conflict" && finding.Path is { } path &&
            PackagedPhoenixPlan.IsPreviousNeuralAddon(path)
            ? finding with { Severity = PreflightSeverity.Warning,
                Message = "Recognized project addon is outside this restore journal and will be preserved: " + path }
            : finding).ToArray());
        result.ThrowIfBlocked();
    }

    internal static bool CanMigrateSharedDirectory(string previous, string selected) =>
        LocalPaths.Same(Path.GetDirectoryName(previous)!, Path.GetDirectoryName(selected)!) &&
        File.Exists(previous) && File.Exists(selected) &&
        PeReader.GetArchitecture(previous) != CpuArch.Unknown && PeReader.GetArchitecture(selected) != CpuArch.Unknown;

    void WriteLauncherBatch(GameCandidate game, InstallManifest manifest)
    {
        var gameDir = Path.GetDirectoryName(game.ExePath)!;
        var exeName = Path.GetFileName(game.ExePath);
        var batchName = BatchFileName(game);
        var batchPath = Path.Combine(gameDir, batchName);
        TrackExternalWrite(batchPath, game.Root, manifest);

        var relativeBatch = Path.GetRelativePath(game.Root, batchPath);
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
        var bootstrap = EngineBootstrap.Find(game.ExePath);
        if (bootstrap is not null)
            text.AppendLine("popd >nul\r\npushd \"" + BatchEscape(bootstrap.WorkingDirectory) + "\" >nul");
        foreach (var pair in manifest.VulkanLaunchEnvironment)
        {
            if (pair.Key == "VK_ADD_IMPLICIT_LAYER_PATH")
            {
                if (!LocalPaths.Same(pair.Value, Path.Combine(gameDir, "_DLSS5_Vulkan")))
                    throw new InvalidDataException("Unexpected Vulkan manifest search folder.");
                text.AppendLine("set \"VK_ADD_IMPLICIT_LAYER_PATH=" + BatchEscape(pair.Value) + "\"");
                continue;
            }
            if (!Regex.IsMatch(pair.Key, @"\A[A-Za-z_][A-Za-z0-9_]{0,127}\z") ||
                !Regex.IsMatch(pair.Value, @"\A[A-Za-z0-9_]{1,128}\z"))
                throw new InvalidDataException("Unsafe Vulkan launcher environment control.");
            text.AppendLine("set \"" + pair.Key + "=" + pair.Value + "\"");
        }
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
        if (TryFindSteamAppId(game.Root) is { Length: > 0 } steamAppId)
        {
            text.AppendLine("set \"SteamAppId=" + BatchEscape(steamAppId) + "\"");
            text.AppendLine("set \"SteamGameId=" + BatchEscape(steamAppId) + "\"");
        }
        if (game.Route == InstallRoute.X64OpenVrFeeder)
        {
            text.AppendLine("set \"DLSS5_DISABLE_VK_LAYER_reshade_1=1\"");
            text.AppendLine("set \"DISABLE_VK_LAYER_reshade_1=1\"");
            text.AppendLine("set \"DISABLE_VK_LAYER_reshade_2=1\"");
            text.AppendLine("set \"VK_LOADER_LAYERS_DISABLE=VK_LAYER_reshade\"");
        }
        var engineArguments = bootstrap is null ? game.SuggestedArguments :
            bootstrap.Arguments + (game.SuggestedArguments.Length > 0 ? " " + game.SuggestedArguments : "");
        var defaultArgs = string.IsNullOrWhiteSpace(engineArguments)
            ? ""
            : " " + BatchEscape(engineArguments);
        text.AppendLine("set \"DLSS5_WAIT=\"");
        text.AppendLine("if /I \"%~1\"==\"--wait\" (");
        text.AppendLine("  set \"DLSS5_WAIT=/wait \"");
        text.AppendLine("  shift");
        text.AppendLine(")");
        text.AppendLine("start \"\" %DLSS5_WAIT%\"%GAME_DIR%%GAME_EXE%\"" + defaultArgs + " %*");
        text.AppendLine("popd >nul");
        text.AppendLine("exit /b 0");
        text.AppendLine();
        text.AppendLine(":help");
        text.AppendLine("echo " + BatchEcho("DLSS5 compatibility launcher for " + exeName));
        text.AppendLine("echo " + BatchEcho("Route: " + game.DisplayRoute));
        text.AppendLine("echo.");
        text.AppendLine("echo " + BatchEcho("Run without arguments to start the game."));
        text.AppendLine("echo " + BatchEcho("Run with --wait to keep this launcher open until the game exits."));
        text.AppendLine("echo " + BatchEcho("Use Restore in the installer to validate backups and remove this installation."));
        text.AppendLine("exit /b 0");
        text.AppendLine();
        text.AppendLine(":uninstall");
        text.AppendLine("echo " + BatchEcho("Open DLSS5 Compatibility Installer and use Restore. Backups must be validated before any file changes."));
        text.AppendLine("exit /b 2");

        File.WriteAllText(batchPath, text.ToString(), Encoding.ASCII);
        _log("Wrote launcher " + relativeBatch);
    }

    public static string LauncherPathFor(GameCandidate game) =>
        Path.Combine(Path.GetDirectoryName(game.ExePath)!, BatchFileName(game));

    internal static IReadOnlyDictionary<string, string> VulkanEnvironmentFor(GameCandidate game)
    {
        var manifest = TryReadManifest(game.Root);
        if (manifest is null || !LocalPaths.Same(ValidateTarget(game.Root, manifest.ExeRelativePath), game.ExePath))
            return new Dictionary<string, string>();
        var result = new Dictionary<string, string>();
        foreach (var pair in manifest.VulkanLaunchEnvironment)
        {
            if (pair.Key == "VK_ADD_IMPLICIT_LAYER_PATH")
            {
                if (!LocalPaths.Same(pair.Value, Path.Combine(Path.GetDirectoryName(game.ExePath)!, "_DLSS5_Vulkan")))
                    throw new InvalidDataException("Unexpected Vulkan manifest search folder.");
            }
            else if (!Regex.IsMatch(pair.Key, @"\A[A-Za-z_][A-Za-z0-9_]{0,127}\z") ||
                !Regex.IsMatch(pair.Value, @"\A[A-Za-z0-9_]{1,128}\z"))
                throw new InvalidDataException("Unsafe Vulkan launcher environment control.");
            result.Add(pair.Key, pair.Value);
        }
        return result;
    }

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
                var candidate = ValidateTarget(game.Root, relative);
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
        GraphicsApi.DirectX10 => "dx10",
        GraphicsApi.DirectX10_1 => "dx10_1",
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
                ? ReadManifest(gameRoot, manifestPath)
                : null;
        }
        catch
        {
            return null;
        }
    }

    public static string? FindSteamAppId(string gameRoot) => TryFindSteamAppId(gameRoot);

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
    static string PendingPath(string gameRoot) => Path.Combine(BackupRoot(gameRoot), "pending.json");
    static string ManifestPath(string gameRoot) => Path.Combine(BackupRoot(gameRoot), "manifest.json");
}
