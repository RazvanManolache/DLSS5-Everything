namespace Dlss5CompatApp;

sealed partial class InstallerEngine
{
    readonly Dictionary<string, string> _previouslyRetiredOverlays = new(StringComparer.OrdinalIgnoreCase);

    void RememberRetiredOverlays(InstallManifest manifest)
    {
        foreach (var file in manifest.Replaced.Where(file => manifest.Retired.Contains(file.RelativePath, StringComparer.OrdinalIgnoreCase)))
            if (Path.GetExtension(file.RelativePath).Equals(".addon32", StringComparison.OrdinalIgnoreCase) ||
                Path.GetExtension(file.RelativePath).Equals(".addon64", StringComparison.OrdinalIgnoreCase))
                _previouslyRetiredOverlays[file.RelativePath] = file.Sha256;
    }

    void RetireLegacyComponents(GameCandidate game, InstallManifest manifest, CancellationToken token)
    {
        var plan = LegacyInstallCleanup.Inspect(game, token);
        foreach (var finding in plan.Findings) _log("WARNING: " + finding.Message);
        var candidates = plan.Retire.ToDictionary(file => file.Path, StringComparer.OrdinalIgnoreCase);
        // A route reinstall restores the outgoing journal first. Keep ownership
        // from that verified journal even if its older Swapper journal was removed.
        foreach (var previous in _previouslyRetiredOverlays)
        {
            var path = ValidateTarget(game.Root, previous.Key);
            if (File.Exists(path) && new FileInfo(path).Length <= 32L * 1024 * 1024 &&
                PeReader.FindMarkers(path, ["DLSS 5 Swapper Overlay"]).Count > 0)
                candidates.TryAdd(path, new(path, previous.Value, ManifestPath(game.Root)));
        }
        foreach (var file in candidates.Values)
        {
            token.ThrowIfCancellationRequested();
            LegacyInstallCleanup.Verify(file);
            TrackExternalWrite(file.Path, game.Root, manifest);
            // Recheck after the verified backup, immediately before retirement.
            // Failure leaves a recoverable pending journal and stops installation.
            LegacyInstallCleanup.Verify(file);
            File.Delete(file.Path);
            var relative = Path.GetRelativePath(game.Root, file.Path);
            if (!manifest.Retired.Contains(relative, StringComparer.OrdinalIgnoreCase)) manifest.Retired.Add(relative);
            PersistPending(game.Root, manifest);
            _log("Backed up and retired previous installer-owned overlay: " + relative);
        }
    }

    // Repair an existing install without resetting the user's Phoenix controls
    // or reinstalling graphics/runtime files. Uses the same durable journal.
    internal async Task<InstallResult> CleanupPreviousInstallAsync(GameCandidate game, CancellationToken token = default)
    {
        ValidateRoot(game.Root);
        ValidateTarget(game.Root, Path.GetRelativePath(game.Root, game.ExePath));
        EnsureGameStopped(game.ExePath);
        using var lease = OpenTransaction(game.Root);
        if (HasPending(game.Root)) await RecoverCoreAsync(game.Root, token);
        var manifest = ReadManifest(game.Root, ManifestPath(game.Root));
        if (!LocalPaths.Same(ValidateTarget(game.Root, manifest.ExeRelativePath), game.ExePath))
            throw new InvalidOperationException("The current install belongs to another executable.");
        ValidateRestorePlan(game.Root, manifest, checkLocks: true);
        token.ThrowIfCancellationRequested();
        var plan = LegacyInstallCleanup.Inspect(game, token);
        if (plan.Findings.Count != 0) throw new InvalidOperationException(string.Join(Environment.NewLine, plan.Findings.Select(f => f.Message)));
        if (plan.Retire.Count == 0) return new(manifest.FileFailures.ToArray());
        PersistPending(game.Root, manifest);
        RetireLegacyComponents(game, manifest, token);
        token.ThrowIfCancellationRequested();
        File.Move(PendingPath(game.Root), ManifestPath(game.Root), overwrite: true);
        return new(manifest.FileFailures.ToArray()) { RetiredComponents = plan.Retire.Select(file => Path.GetRelativePath(game.Root, file.Path)).ToArray() };
    }
}
