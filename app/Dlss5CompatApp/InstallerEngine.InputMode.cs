using System.Text.Json.Nodes;

namespace Dlss5CompatApp;

sealed partial class InstallerEngine
{
    // Repair the input contract without replacing the model, addon or the
    // user's strength/backend settings. Installation uses the same policy.
    internal async Task RepairPackagedInputAsync(GameCandidate game, PayloadInfo payload, CancellationToken token = default)
    {
        ValidateRoot(game.Root);
        ValidateTarget(game.Root, Path.GetRelativePath(game.Root, game.ExePath));
        EnsureGameStopped(game.ExePath);
        using var lease = OpenTransaction(game.Root);
        if (HasPending(game.Root)) await RecoverCoreAsync(game.Root, token);
        var plan = PackagedPhoenixPlan.For(game, payload) ?? throw new InvalidOperationException("A compatible packaged Phoenix addon is required.");
        if (game.Arch != CpuArch.X64 || game.Api != GraphicsApi.DirectX12 || plan.NativeGameInput ||
            !plan.SupportsInProcessBackbuffer || !PayloadScanner.HasStandaloneD3D12Input(plan.Addon))
            throw new InvalidOperationException("Select Automatic with a standalone-capable x64 DX12 addon before repairing its backbuffer input.");
        var folder = Path.GetDirectoryName(game.ExePath)!;
        var installedAddon = ValidateTarget(game.Root, Path.GetRelativePath(game.Root, Path.Combine(folder, "Phoenix.addon64")));
        if (Hash(installedAddon) != Hash(plan.Addon)) throw new InvalidOperationException("Installed addon differs from the selected component; reinstall it before repairing input mode.");
        var manifest = ReadManifest(game.Root, ManifestPath(game.Root));
        if (!LocalPaths.Same(ValidateTarget(game.Root, manifest.ExeRelativePath), game.ExePath))
            throw new InvalidOperationException("Installation belongs to another executable.");
        ValidateRestorePlan(game.Root, manifest, checkLocks: true);
        var configPath = Path.Combine(folder, PhoenixConfiguration.FileName);
        var config = JsonNode.Parse(LocalData.ReadText(configPath, 1024 * 1024))?.AsObject()
            ?? throw new InvalidDataException("Invalid Phoenix configuration.");
        if (config["schema"]?.GetValue<string>() != "phoenix-live/1") throw new InvalidDataException("Unsupported Phoenix configuration.");
        config["input_provider"] = plan.InputProvider;
        manifest.InputProvider = plan.InputProvider;
        manifest.PreferredRoute = game.PreferredRoute;
        token.ThrowIfCancellationRequested();
        PersistPending(game.Root, manifest);
        ConfigureFile(configPath, game.Root, manifest, () => LocalData.WriteAtomic(configPath, config.ToJsonString(LocalData.Json)));
        foreach (var name in new[] { "ReShade.ini", "ReShadeVR.ini" })
        {
            token.ThrowIfCancellationRequested();
            var ini = Path.Combine(folder, name);
            ConfigureFile(ini, game.Root, manifest, () =>
            {
                IniEditor.SetValue(ini, "Phoenix.Compatibility", "Enabled", "1");
                IniEditor.SetValue(ini, "Phoenix.Compatibility", "StandaloneInput", "1");
            });
        }
        token.ThrowIfCancellationRequested();
        File.Move(PendingPath(game.Root), ManifestPath(game.Root), overwrite: true);
        new InstallResult(manifest.FileFailures.ToArray()).RequireComplete();
        _log("Repaired Phoenix input: automatic backbuffer processing; model, renderer and live controls preserved.");
    }
}
