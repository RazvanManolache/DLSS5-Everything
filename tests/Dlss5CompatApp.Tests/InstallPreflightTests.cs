namespace Dlss5CompatApp.Tests;

public sealed class InstallPreflightTests
{
    [Fact]
    public void ProcessImageQueryReadsCurrentImageAndHandlesAnExitedProcess()
    {
        Assert.Equal(Path.GetFullPath(Environment.ProcessPath!),
            InstallPreflight.ReadProcessPath(Environment.ProcessId), ignoreCase: true);
        Assert.Null(InstallPreflight.ReadProcessPath(int.MaxValue));
        Assert.InRange(InstallPreflight.ReadProcesses(1).Count, 1, 2);
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => InstallPreflight.ReadProcesses(1, cancel.Token));
    }

    [Fact]
    public void AnUnusedOldHostCannotBlockARootOnlyPackagedInstallation()
    {
        using var workspace = new TestWorkspace();
        var executable = workspace.WritePe("game.exe", CpuArch.X64);
        Directory.CreateDirectory(workspace.FilePath("host64"));
        var other = workspace.FilePath("host64/renodx-unrelated.addon64");
        File.WriteAllText(other, "Keep this unrelated addon.");
        var plan = new[] { "ReShade.ini", "host64/retired-owned-file.addon64" };
        var active = InstallPreflight.Check(workspace.Root, executable, plan, processProvider: () => [],
            activeInjectionDirectories: [workspace.Root]);
        Assert.True(active.CanInstall, active.DisplayText);
        Assert.Equal("Keep this unrelated addon.", File.ReadAllText(other));
        var legacy = InstallPreflight.Check(workspace.Root, executable, plan, processProvider: () => []);
        Assert.False(legacy.CanInstall);
    }

    static InstallPreflightResult Check(TestWorkspace w, IEnumerable<string>? writes = null, IEnumerable<string>? owned = null,
        Func<IReadOnlyList<PreflightProcess>>? processes = null, PreflightLimits? limits = null) =>
        InstallPreflight.Check(w.Root, w.FilePath("game.exe"), writes ?? [], owned, processes ?? (() => []), limits);

    [Fact]
    public void CleanCheckIsVisibleAndReadOnly()
    {
        using var w = new TestWorkspace(); w.WritePe("game.exe", CpuArch.X64);
        var before = File.ReadAllBytes(w.FilePath("game.exe"));
        var result = Check(w);
        Assert.True(result.CanInstall); Assert.False(result.HasWarnings);
        Assert.False(result.RequiresAntiCheatAcknowledgement);
        Assert.Contains("checks-passed", result.Findings.Select(f => f.Code));
        Assert.Contains("Information", result.DisplayText);
        result.ThrowIfBlocked();
        Assert.Equal(before, File.ReadAllBytes(w.FilePath("game.exe")));
        Assert.Single(Directory.GetFileSystemEntries(w.Root));
    }

    [Fact]
    public void RunningProcessesUseContainedPathOrProtectedNameAndSkipCurrentProcess()
    {
        using var w = new TestWorkspace(); w.WritePe("game.exe", CpuArch.X64);
        var result = Check(w, processes: () => [
            new(Environment.ProcessId, "game", w.FilePath("game.exe")),
            new(10, "helper", w.FilePath("host64/helper.exe")),
            new(11, "game.exe", null), new(12, "dlss5-feed-host64", null),
            new(13, "game", w.Root + "-other/game.exe"), new(14, "unrelated", null)]);
        Assert.False(result.CanInstall);
        Assert.Equal(3, result.Findings.Count(f => f.Code == "game-running"));
        Assert.Throws<InvalidOperationException>(result.ThrowIfBlocked);
        Assert.DoesNotContain("PID 13", result.DisplayText);
    }

    [Fact]
    public void ProcessFailureAndBudgetRemainVisible()
    {
        using var w = new TestWorkspace(); w.WritePe("game.exe", CpuArch.X64);
        var unavailable = Check(w, processes: () => throw new IOException("fixture unavailable"));
        Assert.True(unavailable.CanInstall); Assert.Contains(unavailable.Findings, f => f.Code == "process-check-unavailable");
        var limited = Check(w, processes: () => [new(10, "unrelated", null), new(11, "game", null)], limits: new(MaxProcesses: 1));
        Assert.Contains(limited.Findings, f => f.Code == "process-limit");
    }

    [Fact]
    public void LockedExecutableAndPlannedDestinationBlockWithoutChangingBytes()
    {
        using var w = new TestWorkspace(); w.WritePe("game.exe", CpuArch.X64);
        File.WriteAllText(w.FilePath("ReShade.ini"), "keep");
        using (var locked = new FileStream(w.FilePath("ReShade.ini"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Assert.Contains(Check(w, ["ReShade.ini"]).Findings, f => f.Code == "file-unavailable");
        using (var locked = new FileStream(w.FilePath("game.exe"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Assert.False(Check(w).CanInstall);
        Assert.Equal("keep", File.ReadAllText(w.FilePath("ReShade.ini")));
    }

    [Fact]
    public void ReadOnlyDestinationBlocksAndIsPreserved()
    {
        using var w = new TestWorkspace(); w.WritePe("game.exe", CpuArch.X64);
        var path = w.FilePath("ReShade.ini"); File.WriteAllText(path, "keep");
        File.SetAttributes(path, FileAttributes.ReadOnly);
        try { Assert.False(Check(w, ["ReShade.ini"]).CanInstall); Assert.Equal("keep", File.ReadAllText(path)); }
        finally { File.SetAttributes(path, FileAttributes.Normal); }
    }

    [Theory]
    [InlineData("EasyAntiCheat/EasyAntiCheat_EOS_Setup.exe")]
    [InlineData("BattlEye/BEService.exe")]
    [InlineData("EAAntiCheat.GameServiceLauncher.exe")]
    [InlineData("bin/eac_launcher.exe")]
    public void AntiCheatWarningRequiresAcknowledgementAndDoesNotDeleteAnything(string marker)
    {
        using var w = new TestWorkspace(); w.WritePe("game.exe", CpuArch.X64);
        Directory.CreateDirectory(Path.GetDirectoryName(w.FilePath(marker))!);
        File.WriteAllText(w.FilePath(marker), "fixture");
        var result = Check(w);
        Assert.True(result.CanInstall); Assert.True(result.HasWarnings); Assert.True(result.RequiresAntiCheatAcknowledgement);
        Assert.Contains(result.Findings, f => f.Code == "anti-cheat" && f.Severity == PreflightSeverity.Warning);
        Assert.Equal("fixture", File.ReadAllText(w.FilePath(marker)));
    }

    [Fact]
    public void TitleAloneIsNotAntiCheatEvidenceAndBackupMarkersAreExcluded()
    {
        using var w = new TestWorkspace();
        var game = w.WritePe("Some Multiplayer Title/game.exe", CpuArch.X64);
        var backup = Path.Combine(Path.GetDirectoryName(game)!, "_DLSS5_Compat_Backup/EasyAntiCheat");
        Directory.CreateDirectory(backup);
        var result = InstallPreflight.Check(Path.GetDirectoryName(game)!, game, [], processProvider: () => []);
        Assert.False(result.RequiresAntiCheatAcknowledgement);
    }

    [Theory]
    [InlineData("dxgi.dll", true)] [InlineData("dxgi.dll", false)]
    [InlineData("ReShade.asi", false)] [InlineData("renodx.addon64", false)]
    [InlineData("OptiScaler.ini", false)]
    public void UnmanagedModsAreReportedAndNeverRemoved(string filename, bool planned)
    {
        using var w = new TestWorkspace(); w.WritePe("game.exe", CpuArch.X64);
        File.WriteAllText(w.FilePath(filename), "external mod");
        var result = Check(w, planned ? [filename] : []);
        var finding = Assert.Single(result.Findings, f => f.Code == "mod-conflict");
        Assert.Equal(planned || filename.Contains("renodx") || filename.Contains("OptiScaler") ? PreflightSeverity.Error : PreflightSeverity.Warning, finding.Severity);
        Assert.Equal("external mod", File.ReadAllText(w.FilePath(filename)));
    }

    [Fact]
    public void ValidatedOwnershipAvoidsFalsePositiveButDoesNotBypassFileLocks()
    {
        using var w = new TestWorkspace(); w.WritePe("game.exe", CpuArch.X64);
        File.WriteAllText(w.FilePath("dxgi.dll"), "managed proxy");
        Assert.True(Check(w, ["dxgi.dll"], ["DXGI.dll"]).CanInstall);
        using var locked = new FileStream(w.FilePath("dxgi.dll"), FileMode.Open, FileAccess.Read, FileShare.None);
        Assert.False(Check(w, ["dxgi.dll"], ["dxgi.dll"]).CanInstall);
    }

    [Fact]
    public void HostModConflictsAndInvalidDestinationParentsAreCheckedBeforeMutation()
    {
        using var w = new TestWorkspace(); w.WritePe("game.exe", CpuArch.X64);
        Directory.CreateDirectory(w.FilePath("_DLSS5_OpenXR/host64"));
        File.WriteAllText(w.FilePath("_DLSS5_OpenXR/host64/dxgi.dll"), "external host proxy");
        var result = Check(w, ["_DLSS5_OpenXR/host64/dxgi.dll"]);
        Assert.False(result.CanInstall);
        Assert.Contains(result.Findings, f => f.Code == "mod-conflict");
        File.WriteAllText(w.FilePath("host64"), "not a directory");
        Assert.Contains(Check(w, ["host64/Phoenix.json"]).Findings, f => f.Code == "file-unavailable");
    }

    [Theory]
    [InlineData("../outside.dll")] [InlineData("C:\\outside.dll")] [InlineData("ReShade.ini:stream")]
    [InlineData("\\\\server\\share\\file")] [InlineData("CON.ini")] [InlineData("folder/../ReShade.ini")]
    [InlineData("folder./ReShade.ini")] [InlineData("folder//ReShade.ini")]
    public void UnsafePlannedPathsBlockBeforeProcessInspection(string path)
    {
        using var w = new TestWorkspace(); w.WritePe("game.exe", CpuArch.X64);
        var called = false;
        var result = Check(w, [path], processes: () => { called = true; return []; });
        Assert.False(called); Assert.False(result.CanInstall);
        Assert.Contains(result.Findings, f => f.Code == "unsafe-target");
    }

    [Fact]
    public void RootContainmentDepthEntryBudgetAndCancellationAreEnforced()
    {
        using var w = new TestWorkspace(); w.WritePe("game.exe", CpuArch.X64);
        var outside = InstallPreflight.Check(w.FilePath("subfolder"), w.FilePath("game.exe"), [], processProvider: () => []);
        Assert.False(outside.CanInstall);
        Assert.Contains(Check(w, limits: new(MaxEntries: 1)).Findings, f => f.Code == "scan-limit");
        Directory.CreateDirectory(w.FilePath("a/b/c/EasyAntiCheat"));
        Assert.False(Check(w, limits: new(MaxDepth: 0)).RequiresAntiCheatAcknowledgement);
        Assert.True(Check(w, limits: new(MaxDepth: 3)).RequiresAntiCheatAcknowledgement);
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => InstallPreflight.Check(w.Root, w.FilePath("game.exe"), cancellationToken: cancel.Token));
        Assert.Throws<ArgumentOutOfRangeException>(() => Check(w, limits: new(MaxEntries: 0)));
    }

    [Fact]
    public void DeepExecutableFolderIsInspectedAndModOrganizerBlocks()
    {
        using var w = new TestWorkspace();
        var exe = w.WritePe("Stock Game/a/b/c/game.exe", CpuArch.X64);
        File.WriteAllText(w.FilePath("ModOrganizer.exe"), "manager");
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(exe)!, "EasyAntiCheat.exe"), "marker");
        var result = InstallPreflight.Check(w.Root, exe, [], processProvider: () => []);
        Assert.True(result.RequiresAntiCheatAcknowledgement);
        Assert.Contains(result.Findings, f => f.Code == "managed-modpack");
        Assert.False(result.CanInstall);
    }

    [Fact]
    public void LinkedDestinationIsRejectedAndLinkedScanDirectoryIsNotFollowed()
    {
        using var w = new TestWorkspace(); w.WritePe("game.exe", CpuArch.X64);
        using var outside = new TestWorkspace(); Directory.CreateDirectory(outside.FilePath("EasyAntiCheat"));
        var link = w.FilePath("linked");
        SwapperTestLinks.CreateJunction(link, outside.Root);
        try
        {
            var scan = Check(w);
            Assert.False(scan.RequiresAntiCheatAcknowledgement);
            Assert.Contains(scan.Findings, f => f.Code == "link-skipped");
            Assert.False(Check(w, ["linked/ReShade.ini"]).CanInstall);
        }
        finally { Directory.Delete(link); }
    }
}
