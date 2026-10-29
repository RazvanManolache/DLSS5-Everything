using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Dlss5CompatApp.Tests;

public sealed class LegacyInstallCleanupTests
{
    [Fact]
    public void FindsVerifiedOverlayInParentJournalWithoutOwningArbitraryAddedFiles()
    {
        using var fixture = new Fixture();
        var before = File.ReadAllBytes(fixture.Manifest);
        var plan = LegacyInstallCleanup.Inspect(fixture.Game);
        Assert.Empty(plan.Findings);
        Assert.True(LocalPaths.Same(fixture.Overlay, Assert.Single(plan.Retire).Path));
        Assert.True(File.Exists(fixture.Overlay));
        Assert.Equal(before, File.ReadAllBytes(fixture.Manifest));
        Assert.True(File.Exists(fixture.Unrelated));
        var preflight = InstallerEngine.CheckPreflight(fixture.Game, fixture.Payload);
        Assert.True(preflight.CanInstall, preflight.DisplayText);
        Assert.Contains(preflight.Findings, item => item.Code == "verified-legacy-cleanup");
        Assert.DoesNotContain(preflight.Findings, item => item.Code == "mod-conflict" && LocalPaths.Same(item.Path!, fixture.Overlay));
    }

    [Theory]
    [InlineData("changed")]
    [InlineData("missing-hash")]
    [InlineData("not-added")]
    [InlineData("wrong-identity")]
    [InlineData("outside-game")]
    [InlineData("corrupt-json")]
    [InlineData("duplicate-fields")]
    public void AmbiguousOrModifiedComponentsAreKeptWithVisiblePreflightWarning(string scenario)
    {
        using var fixture = new Fixture();
        switch (scenario)
        {
            case "changed": File.AppendAllText(fixture.Overlay, "user modification"); break;
            case "missing-hash": fixture.WriteManifest(hash: null); break;
            case "not-added": fixture.WriteManifest(added: []); break;
            case "wrong-identity":
                File.WriteAllBytes(fixture.Overlay, TestWorkspace.PeBytes(CpuArch.X64, []));
                TestWorkspace.SetPhoenixExports(fixture.Overlay, "AddonInit", "AddonUninit");
                fixture.WriteManifest(); break;
            case "outside-game": fixture.WriteManifest(overlayRelative: "../outside.addon64"); break;
            case "corrupt-json": File.WriteAllText(fixture.Manifest, "{not a journal"); break;
            case "duplicate-fields": File.WriteAllText(fixture.Manifest, File.ReadAllText(fixture.Manifest).Replace("\"version\":1", "\"version\":1,\"version\":1")); break;
        }
        var bytes = File.ReadAllBytes(fixture.Overlay);
        var plan = LegacyInstallCleanup.Inspect(fixture.Game);
        Assert.Empty(plan.Retire);
        Assert.Contains(plan.Findings, item => item.Code == "legacy-cleanup-review" && item.Severity == PreflightSeverity.Warning);
        var checks = InstallerEngine.CheckPreflight(fixture.Game, fixture.Payload);
        Assert.True(checks.HasWarnings);
        Assert.Contains("kept", checks.DisplayText);
        Assert.Equal(bytes, File.ReadAllBytes(fixture.Overlay));
    }

    [Fact]
    public void JournalForAnotherExecutableIsNotUsedAsOwnershipEvidence()
    {
        using var fixture = new Fixture();
        fixture.WriteManifest(executableRelative: "Engine/Binaries/Win64/another.exe");
        Assert.Empty(LegacyInstallCleanup.Inspect(fixture.Game).Retire);
    }

    [Fact]
    public void AMatchingFilenameWithoutAnInstallRecordRemainsAnUnmanagedWarning()
    {
        using var fixture = new Fixture();
        File.Delete(fixture.Manifest);
        Assert.Empty(LegacyInstallCleanup.Inspect(fixture.Game).Retire);
        var checks = InstallerEngine.CheckPreflight(fixture.Game, fixture.Payload);
        Assert.Contains(checks.Findings, finding => finding.Code == "mod-conflict" && finding.Severity == PreflightSeverity.Warning);
        Assert.True(File.Exists(fixture.Overlay));
    }

    [Fact]
    public void LockedVerifiedOverlayBlocksInstallationBeforeAnyWrites()
    {
        using var fixture = new Fixture();
        using var locked = new FileStream(fixture.Overlay, FileMode.Open, FileAccess.Read, FileShare.Read);
        var result = InstallerEngine.CheckPreflight(fixture.Game, fixture.Payload);
        Assert.False(result.CanInstall);
        Assert.Contains(result.Findings, item => item.Code == "file-unavailable" && item.Severity == PreflightSeverity.Error);
        Assert.False(InstallerEngine.HasPending(fixture.Game.Root));
    }

    [Fact]
    public async Task InstallationRetiresBeforeNewAddonAndReinstallRestorePreserveOriginalBytes()
    {
        using var fixture = new Fixture();
        var original = File.ReadAllBytes(fixture.Overlay);
        var oldManifest = File.ReadAllBytes(fixture.Manifest);
        var log = new List<string>();
        var engine = new InstallerEngine(message =>
        {
            log.Add(message);
            if (message.StartsWith("Backed up and retired previous"))
                Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(fixture.Game.ExePath)!, "Phoenix.addon64")));
        });
        var result = await engine.InstallAsync(fixture.Game, fixture.Payload);
        Assert.True(result.Complete, result.Warning);
        Assert.Contains(result.RetiredComponents, path => path.EndsWith(Path.GetFileName(fixture.Overlay)));
        Assert.False(File.Exists(fixture.Overlay));
        Assert.True(File.Exists(fixture.Unrelated));
        Assert.Equal(oldManifest, File.ReadAllBytes(fixture.Manifest));
        Assert.Contains(log, item => item.Contains("Backed up and retired previous"));
        var status = InstallerEngine.InstalledGame(fixture.Game);
        Assert.NotNull(status);
        await engine.InstallAsync(fixture.Game, fixture.Payload);
        Assert.False(File.Exists(fixture.Overlay));
        await engine.RestoreAsync(fixture.Game.Root);
        Assert.Equal(original, File.ReadAllBytes(fixture.Overlay));
        Assert.Equal(oldManifest, File.ReadAllBytes(fixture.Manifest));
        Assert.True(File.Exists(fixture.Unrelated));
    }

    [Fact]
    public async Task RepairExistingInstallOnlyRetiresOverlayAndPreservesCurrentSettings()
    {
        using var fixture = new Fixture();
        var engine = new InstallerEngine(_ => { });
        // Simulate an installation made before the migration logic existed.
        var legacy = File.ReadAllBytes(fixture.Manifest);
        File.Delete(fixture.Manifest);
        await engine.InstallAsync(fixture.Game, fixture.Payload);
        File.WriteAllBytes(fixture.Manifest, legacy);
        var folder = Path.GetDirectoryName(fixture.Game.ExePath)!;
        var ini = Path.Combine(folder, "ReShade.ini");
        File.AppendAllText(ini, "\n[User]\nKeepMyTuning=42\n");
        var settings = File.ReadAllBytes(ini);
        var addon = File.ReadAllBytes(Path.Combine(folder, "Phoenix.addon64"));
        var result = await engine.CleanupPreviousInstallAsync(fixture.Game);
        Assert.Single(result.RetiredComponents);
        Assert.False(File.Exists(fixture.Overlay));
        Assert.Equal(settings, File.ReadAllBytes(ini));
        Assert.Equal(addon, File.ReadAllBytes(Path.Combine(folder, "Phoenix.addon64")));
        Assert.Equal(legacy, File.ReadAllBytes(fixture.Manifest));
        await engine.RestoreAsync(fixture.Game.Root);
        Assert.True(File.Exists(fixture.Overlay));
    }

    [Fact]
    public async Task ReinstallUsesCurrentJournalOwnershipEvenIfOldInstallerRecordWasRemoved()
    {
        using var fixture = new Fixture();
        var original = File.ReadAllBytes(fixture.Overlay);
        var engine = new InstallerEngine(_ => { });
        await engine.InstallAsync(fixture.Game, fixture.Payload);
        File.Delete(fixture.Manifest);
        await engine.InstallAsync(fixture.Game, fixture.Payload);
        Assert.False(File.Exists(fixture.Overlay));
        await engine.RestoreAsync(fixture.Game.Root);
        Assert.Equal(original, File.ReadAllBytes(fixture.Overlay));
        Assert.False(File.Exists(fixture.Manifest));
    }

    [Fact]
    public async Task InterruptedInstallKeepsOverlayBackupRecoverable()
    {
        using var fixture = new Fixture();
        var original = File.ReadAllBytes(fixture.Overlay);
        using var cancellation = new CancellationTokenSource();
        var engine = new InstallerEngine(message =>
        {
            if (message.StartsWith("Backed up and retired previous")) cancellation.Cancel();
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => engine.InstallAsync(fixture.Game, fixture.Payload, cancellationToken: cancellation.Token));
        Assert.True(InstallerEngine.HasPending(fixture.Game.Root));
        await engine.RecoverAsync(fixture.Game.Root);
        Assert.Equal(original, File.ReadAllBytes(fixture.Overlay));
        Assert.False(InstallerEngine.HasPending(fixture.Game.Root));
    }

    sealed class Fixture : IDisposable
    {
        readonly TestWorkspace workspace = new();
        public string OldRoot { get; }
        public string Overlay { get; }
        public string Unrelated { get; }
        public string Manifest { get; }
        public GameCandidate Game { get; }
        public PayloadInfo Payload { get; }
        public Fixture()
        {
            OldRoot = workspace.FilePath("game");
            var exe = workspace.WritePe("game/Engine/Binaries/Win64/renderer.exe", CpuArch.X64);
            Game = new(workspace.FilePath("game/Engine"), exe, "Unrelated fixture", "renderer.exe", CpuArch.X64, GraphicsApi.DirectX12, "fixture", 160 * 1024);
            Overlay = Dll("game/Engine/Binaries/Win64/dlss5-lab-overlay-fixture.addon64", "AddonInit", "AddonUninit");
            using (var file = File.Open(Overlay, FileMode.Append)) file.Write(Encoding.ASCII.GetBytes("DLSS 5 Swapper Overlay"));
            Unrelated = workspace.FilePath("game/Engine/Binaries/Win64/user-notes.txt");
            File.WriteAllText(Unrelated, "User-owned data remains untouched.");
            Manifest = workspace.FilePath("game/_DLSS5_Backup/manifest.json");
            Directory.CreateDirectory(Path.GetDirectoryName(Manifest)!);
            WriteManifest();
            var addon = Dll("payload/Phoenix.addon64", "CnrPHOENIXSummary", "CnrPHOENIXRegister", "CnrPHOENIXFeederVersion", "CnrPHOENIXFeederEvaluate", "CnrPHOENIXCompatibilityStatus", "CnrPHOENIXInProcessBackbufferVersion", "AddonInit", "AddonUninit");
            TestWorkspace.SetEmbeddedPhoenixRuntime(addon);
            using (var marker = new FileStream(addon, FileMode.Open, FileAccess.Write))
            { marker.Position = 0x11000; marker.Write(Encoding.ASCII.GetBytes("StandaloneInput")); }
            Dll("payload/ReShade64.dll");
            Dll("payload/nvngx_dlssnr.dll");
            Payload = PayloadScanner.Scan(workspace.FilePath("payload"));
        }
        public void WriteManifest(string? hash = "current", string[]? added = null, string? overlayRelative = null, string? executableRelative = null)
        {
            var relative = Path.GetRelativePath(OldRoot, Overlay);
            File.WriteAllText(Manifest, JsonSerializer.Serialize(new
            {
                version = 1, game = new { dir = OldRoot, exe = executableRelative ?? Path.GetRelativePath(OldRoot, Game.ExePath) },
                added = added ?? new[] { relative, Path.GetRelativePath(OldRoot, Unrelated) },
                labOverlay = new { rel = overlayRelative ?? relative, sha256 = hash == "current" ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Overlay))) : hash }
            }));
        }
        string Dll(string relative, params string[] exports)
        {
            var path = workspace.WritePe(relative, CpuArch.X64);
            var bytes = File.ReadAllBytes(path);
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(0x96), 0x2022);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0x98 + 108), 16);
            File.WriteAllBytes(path, bytes);
            TestWorkspace.SetPhoenixExports(path, exports);
            return path;
        }
        public void Dispose() => workspace.Dispose();
    }
}
