using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Dlss5CompatApp.Tests;

public sealed class PhoenixInstallerTests
{
    [Fact]
    public async Task LockedConfigurationDoesNotStopCopiesAndIsPreservedForRestore()
    {
        using var fixture = new Fixture();
        var ini = Path.Combine(fixture.Game.Root, "ReShade.ini");
        const string original = "[USER]\nKeep=1\n";
        File.WriteAllText(ini, original);
        InstallResult result;
        using (var locked = new FileStream(ini, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            result = await fixture.Engine.InstallAsync(fixture.Game, fixture.Payload, phoenixSettings: fixture.Settings);
        Assert.False(result.Complete);
        Assert.Single(result.Failures, file => file.RelativePath == "ReShade.ini");
        Assert.Contains("antivirus", result.Warning);
        Assert.Contains("ReShade.ini", result.Warning);
        Assert.True(File.Exists(Path.Combine(fixture.Game.Root, "Phoenix.addon64")));
        Assert.True(File.Exists(Path.Combine(fixture.Game.Root, "PhoenixModel.dll")));
        Assert.True(File.Exists(Path.Combine(fixture.Game.Root, "ReShadeVR.ini")));
        Assert.True(File.Exists(InstallerEngine.LauncherPathFor(fixture.Game)));
        Assert.False(InstallerEngine.HasPending(fixture.Game.Root));
        Assert.NotEmpty(fixture.ReadManifest().FileFailures);
        Assert.Throws<InvalidOperationException>(() => InstallerEngine.InstalledGame(fixture.Game));
        await fixture.Engine.RestoreAsync(fixture.Game.Root);
        Assert.Equal(original, File.ReadAllText(ini));
    }

    [Theory]
    [InlineData("dxgi.dll")]
    [InlineData("Phoenix.addon64")]
    [InlineData("PhoenixModel.dll")]
    [InlineData("ReShade.ini")]
    [InlineData("ReShadeVR.ini")]
    public async Task ReinstallSkipsAnyLockedFileCopiesTheRestAndCanBeRetried(string relative)
    {
        using var fixture = new Fixture();
        var ini = Path.Combine(fixture.Game.Root, "ReShade.ini");
        const string original = "[USER]\nOriginal=1\n";
        File.WriteAllText(ini, original);
        var addon = Dll(fixture.Workspace, "payload/CustomAddons/custom.addon64", CpuArch.X64);
        fixture.Payload = fixture.Payload with { ExtraAddons = [addon] };
        await fixture.Install();
        var expected = File.ReadAllBytes(addon).Concat(new byte[] { 42 }).ToArray();
        File.WriteAllBytes(addon, expected);
        var target = Path.Combine(fixture.Game.Root, relative);
        InstallResult result;
        using (var locked = new FileStream(target, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.True(InstallerEngine.CheckPreflight(fixture.Game, fixture.Payload, phoenixSettings: fixture.Settings).CanInstall);
            result = await fixture.Engine.InstallAsync(fixture.Game, fixture.Payload, phoenixSettings: fixture.Settings);
        }
        Assert.False(result.Complete);
        Assert.Contains(result.Failures, file => file.RelativePath == relative);
        Assert.Equal(expected, File.ReadAllBytes(Path.Combine(fixture.Game.Root, "custom.addon64")));
        Assert.NotEmpty(fixture.ReadManifest().FileFailures);
        var repaired = await fixture.Engine.InstallAsync(fixture.Game, fixture.Payload, phoenixSettings: fixture.Settings);
        Assert.True(repaired.Complete, repaired.Warning);
        Assert.Empty(fixture.ReadManifest().FileFailures);
        await fixture.Engine.RestoreAsync(fixture.Game.Root);
        Assert.Equal(original, File.ReadAllText(ini));
        Assert.False(File.Exists(target == ini ? Path.Combine(fixture.Game.Root, "custom.addon64") : target));
    }

    [Fact]
    public async Task JournalFailureIsFatalAndDoesNotTurnIntoAFileWarning()
    {
        using var fixture = new Fixture();
        FileStream? lockedJournal = null;
        var engine = new InstallerEngine(message =>
        {
            if (lockedJournal is null && message.StartsWith("Copied "))
                lockedJournal = new FileStream(Path.Combine(fixture.Game.Root, "_DLSS5_Compat_Backup", "pending.json"),
                    FileMode.Open, FileAccess.Read, FileShare.None);
        });
        try
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Install(engine));
            Assert.Contains("recovery journal", error.Message);
        }
        finally { lockedJournal?.Dispose(); }
        Assert.True(InstallerEngine.HasPending(fixture.Game.Root));
        Assert.False(File.Exists(fixture.ManifestPath));
        await fixture.Engine.RecoverAsync(fixture.Game.Root);
    }

    [Fact]
    public async Task NativeExportOnlyInstallUsesCentralWeightsAndUninstallKeepsTheLibrary()
    {
        using var fixture = new Fixture();
        var library = OptionalModelLibraryTests.PublishFixture(fixture.Payload.Root);
        var payload = fixture.Payload with { NvidiaDlls = Array.Empty<string>() };
        Assert.False(payload.HasCoreDlss); Assert.True(payload.HasPhoenixModel);
        await fixture.Engine.InstallAsync(fixture.Game, payload, phoenixSettings: fixture.Settings);
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(fixture.Game.Root, "Phoenix.json")));
        Assert.Equal(library, json.RootElement.GetProperty("model_library_directory").GetString());
        Assert.Equal("model_folder", json.RootElement.GetProperty("weight_source").GetProperty("kind").GetString());
        Assert.Equal(Path.Combine(library, "models", "fixture"), json.RootElement.GetProperty("weight_source").GetProperty("path").GetString());
        Assert.False(File.Exists(Path.Combine(fixture.Game.Root, "PhoenixModel.dll")));
        var inspection = await ComponentInspectionService.InspectAsync(fixture.Game, payload);
        var shared = Assert.Single(inspection.Rows, row => row.Name == "Phoenix native model (shared library)");
        Assert.Equal(ComponentInspectionState.Current, shared.State);
        File.WriteAllBytes(shared.InstalledPath, [4, 3, 2, 1]);
        inspection = await ComponentInspectionService.InspectAsync(fixture.Game, payload);
        Assert.Equal(ComponentInspectionState.Different, inspection.Rows.Single(row => row.Name == shared.Name).State);
        File.WriteAllBytes(shared.InstalledPath, [1, 2, 3, 4]);
        await fixture.Engine.RestoreAsync(fixture.Game.Root);
        Assert.Single(OptionalModelLibrary.Installed(library, verifyWeights: true));
    }

    [Theory]
    [InlineData((int)GraphicsApi.DirectX11, "host64")]
    [InlineData((int)GraphicsApi.DirectX9, "host64")]
    [InlineData((int)GraphicsApi.OpenGl, "host64")]
    [InlineData((int)GraphicsApi.OpenXr, "_DLSS5_OpenXR/host64")]
    public async Task SharedLibraryForLegacyNvidiaHelperDoesNotTouchAnUnownedLocalDll(int api, string host)
    {
        using var fixture = new Fixture();
        var game = fixture.Game with { Api = (GraphicsApi)api };
        var directory = Path.Combine(game.Root, host);
        Directory.CreateDirectory(directory);
        var localNr = Path.Combine(directory, "nvngx_dlssnr.dll");
        File.WriteAllText(localNr, "Unowned local runtime");
        var settings = fixture.Settings with { Runner = "nvidia", CopyNvidiaDllToGame = false };
        using (var locked = new FileStream(localNr, FileMode.Open, FileAccess.Read, FileShare.None)) {
            var preflight = InstallerEngine.CheckPreflight(game, fixture.Payload, phoenixSettings: settings);
            Assert.True(preflight.CanInstall, preflight.DisplayText);
            await fixture.Engine.InstallAsync(game, fixture.Payload, phoenixSettings: settings);
        }
        Assert.False(File.Exists(Path.Combine(directory, "PhoenixModel.dll")));
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "Phoenix.json")));
        var shared = fixture.Payload.NvidiaDlls.Single(path => Path.GetFileName(path) == "nvngx_dlssnr.dll");
        Assert.Equal(shared, json.RootElement.GetProperty("weight_source").GetProperty("path").GetString());
        Assert.Equal(Path.GetDirectoryName(shared), IniEditor.GetValue(Path.Combine(directory, "ReShade.ini"), "Phoenix", "NvidiaDllFolder"));
        await fixture.Engine.RestoreAsync(game.Root);
        Assert.Equal("Unowned local runtime", File.ReadAllText(localNr));
        Assert.True(File.Exists(shared));
    }

    [Theory]
    [InlineData(false, "native", "original")]
    [InlineData(false, "nvidia", "small-fp8")]
    [InlineData(true, "native", "small-fp8")]
    [InlineData(true, "nvidia", "original")]
    public async Task ModelStoragePolicyAndStartupDefaultsSurviveReinstallAndRestore(bool copy, string renderer, string modelId)
    {
        using var fixture = new Fixture();
        var shared = fixture.Payload.NvidiaDlls.Single(path => Path.GetFileName(path) == "nvngx_dlssnr.dll");
        var original = File.ReadAllBytes(shared);
        // Start with an existing installation and edited model selection so the
        // saved backend profile cannot silently supersede the new defaults.
        await fixture.Install();
        IniEditor.SetValue(Path.Combine(fixture.Game.Root, "ReShade.ini"), "Phoenix", "ModelId", "stale-model");
        var settings = fixture.Settings with { CopyNvidiaDllToGame = copy, Runner = renderer, ModelId = modelId };
        await fixture.Engine.InstallAsync(fixture.Game, fixture.Payload, phoenixSettings: settings);
        var local = Path.Combine(fixture.Game.Root, "PhoenixModel.dll");
        Assert.Equal(copy, File.Exists(local));
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(fixture.Game.Root, "Phoenix.json")));
        var source = json.RootElement.GetProperty("weight_source");
        Assert.Equal(copy ? local : shared, source.GetProperty("path").GetString());
        Assert.Equal(modelId, source.GetProperty("model").GetString());
        var ini = Path.Combine(fixture.Game.Root, "ReShade.ini");
        Assert.Equal(modelId, IniEditor.GetValue(ini, "Phoenix", "ModelId"));
        Assert.Equal(renderer == "nvidia" ? "1" : "3", IniEditor.GetValue(ini, "Phoenix", "Backend"));
        Assert.Equal(copy ? "" : Path.GetDirectoryName(shared), IniEditor.GetValue(ini, "Phoenix", "NvidiaDllFolder"));
        Assert.DoesNotContain(fixture.ReadManifest().Added, path => Path.IsPathFullyQualified(path));
        await fixture.Engine.RestoreAsync(fixture.Game.Root);
        Assert.Equal(original, File.ReadAllBytes(shared));
        Assert.False(File.Exists(local));
    }

    [Fact]
    public async Task VerifyOnlyKeepsActualInstalledFilesSettingsAndLogsEvenWhenPayloadIsMissing()
    {
        using var fixture = new Fixture();
        var game = fixture.Game with { SuggestedArguments = "-windowed" };
        await fixture.Engine.InstallAsync(game, fixture.Payload, phoenixSettings: fixture.Settings);
        var recorded = InstallerEngine.InstalledGame(game.WithApi(GraphicsApi.Vulkan));
        Assert.Equal(GraphicsApi.DirectX12, recorded.Api);
        Assert.Equal("-windowed", recorded.SuggestedArguments);
        File.WriteAllText(Path.Combine(game.Root, "ReShade.log"), "Keep previous diagnostics.");
        Dictionary<string, string> Snapshot() => Directory.GetFiles(game.Root, "*", SearchOption.AllDirectories)
            .ToDictionary(path => path, path => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));
        var before = Snapshot();
        // Reject arguments at the launch boundary; this reaches the complete verification
        // preparation path without starting an executable or requiring a GPU.
        var result = await SmokeTestRunner.RunAttemptAsync(new SmokeTestTarget { Exe = game.ExePath, Arguments = "\n" },
            new SmokeTestConfig
            {
                VerifyExistingInstall = true, UpdatePayload = false, RestoreBeforeInstall = false,
                RestoreAfterRun = false, AutoResolve = false
            }, PayloadScanner.Scan(fixture.Workspace.FilePath("missing-payload")), (_, _, _) => Task.CompletedTask);
        Assert.False(result.Passed);
        Assert.Null(result.ProcessId);
        Assert.Null(result.InstallCompletedAt);
        Assert.Contains("Launch arguments contain unsupported", result.Error);
        Assert.Contains(result.Log, line => line.Contains("Verifying existing installation"));
        Assert.Equal(before.OrderBy(pair => pair.Key), Snapshot().OrderBy(pair => pair.Key));
    }

    [Fact]
    public async Task RestoreReplacesHardLinkWithoutChangingSharedPayload()
    {
        using var fixture = new Fixture();
        var ini = Path.Combine(fixture.Game.Root, "ReShade.ini");
        File.WriteAllText(ini, "[Original]\nKeep=before installation\n");
        await fixture.Install();
        var shared = fixture.Workspace.FilePath("shared-payload.ini");
        var installed = File.ReadAllText(ini);
        File.WriteAllText(shared, installed);
        File.Delete(ini);
        Assert.True(CreateHardLink(ini, shared, 0), "Creating the NTFS test link failed.");
        await fixture.Engine.RestoreAsync(fixture.Game.Root);
        Assert.Equal(installed, File.ReadAllText(shared));
        Assert.Equal("[Original]\nKeep=before installation\n", File.ReadAllText(ini));
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    static extern bool CreateHardLink(string link, string existing, nint security);

    [Theory]
    [InlineData("E:\\Games\\Library", "D:\\Games\\Library", true)]
    [InlineData("E:\\Games\\Library", "D:\\Other\\Library", false)]
    [InlineData("E:\\Games\\Library", "E:\\Games\\Library", false)]
    [InlineData("Games\\Library", "D:\\Games\\Library", false)]
    [InlineData("\\\\server\\share\\Library", "D:\\Games\\Library", false)]
    public void OnlyExactDriveRelocationsCanAdoptHashlessLegacyJournals(string before, string after, bool accepted) =>
        Assert.Equal(accepted, InstallerEngine.IsDriveRelocation(before, after));

    [Fact]
    public async Task DriveRelocatedLegacyInstallKeepsOldJournalAndUsesOnlyAuditedLocalBackups()
    {
        using var fixture = new Fixture();
        var ini = Path.Combine(fixture.Game.Root, "ReShade.ini");
        File.WriteAllText(ini, "[Original]\nKeep=local backup\n");
        fixture.LegacyJournal("ReShade.ini");
        var manifest = fixture.ReadManifest();
        manifest.GameRoot = (fixture.Game.Root[0] == 'Z' ? "Y" : "Z") + fixture.Game.Root[1..];
        fixture.WriteManifest(manifest);
        var originalJournal = File.ReadAllText(fixture.ManifestPath);
        File.WriteAllText(ini, "[Outgoing]\nKeep=old installed version\n");
        Assert.True(InstallerEngine.CheckPreflight(fixture.Game, fixture.Payload).CanInstall);
        await fixture.Install();
        Assert.Equal(originalJournal, File.ReadAllText(Assert.Single(Directory.GetFiles(Path.GetDirectoryName(fixture.ManifestPath)!, "manifest.json.relocated-*"))));
        await fixture.Engine.RestoreAsync(fixture.Game.Root);
        Assert.Equal("[Original]\nKeep=local backup\n", File.ReadAllText(ini));
    }

    [Fact]
    public async Task RelocatedJournalWithMissingOriginalStillCannotInstallOrArchive()
    {
        using var fixture = new Fixture();
        fixture.LegacyJournal();
        var manifest = fixture.ReadManifest();
        manifest.GameRoot = "Z" + fixture.Game.Root[1..];
        manifest.Replaced.Add(new() { RelativePath = "missing.dll" });
        fixture.WriteManifest(manifest);
        Assert.False(InstallerEngine.CheckPreflight(fixture.Game, fixture.Payload).CanInstall);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Install());
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(fixture.ManifestPath)!, "manifest.json.relocated-*"));
    }

    [Fact]
    public void ConfigurationUsesActualSidecarAndVisitorContracts()
    {
        using var workspace = new TestWorkspace();
        var model = Dll(workspace, "model.dll", CpuArch.X64);
        var settings = new PhoenixSettings { Runner = "cpu", Iterations = 10, Comparison = 1,
            OverrideStrengths = true, LocalTone = 0.7f, LocalStructure = 1.2f, Advanced = true, SkinStructure = 0.4f, CpuThreads = 8 };
        File.WriteAllText(workspace.FilePath("ReShade.ini"), "[OTHER]\nKeep=1\n");
        File.WriteAllText(workspace.FilePath("dlss5-feed.cfg"), "iterations=1\nmode=2\n");
        var path = PhoenixConfiguration.Write(workspace.Root, model, settings);
        using var json = JsonDocument.Parse(File.ReadAllText(path));
        var config = json.RootElement;
        Assert.Equal("phoenix-live/1", config.GetProperty("schema").GetString());
        Assert.Equal("game_dlss", config.GetProperty("input_provider").GetString());
        Assert.True(config.GetProperty("armed").GetBoolean());
        Assert.False(config.TryGetProperty("portable_worker", out _));
        Assert.Equal(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(model))).ToLowerInvariant(),
            config.GetProperty("weight_source").GetProperty("sha256").GetString());
        var ini = File.ReadAllText(workspace.FilePath("ReShade.ini"));
        Assert.Contains("Keep=1", ini); Assert.Contains("ExecutionDevice=cpu", ini);
        Assert.Contains("Tone=0.7", ini); Assert.Contains("Structure=1.2", ini); Assert.Contains("Skin=0.4", ini);
        Assert.Contains("CpuThreads=8", ini); Assert.DoesNotContain("RenoDX", ini);
        Assert.Equal("iterations=1\nmode=2\n", File.ReadAllText(workspace.FilePath("dlss5-feed.cfg")));
    }

    [Theory]
    [InlineData("native", null, "native", "3")]
    [InlineData("nvidia", null, "native", "1")]
    [InlineData("cpu", null, "cpu", "3")]
    [InlineData("cuda", "cuda:GPU-12345678-1234-1234-1234-123456789abc", "cuda:GPU-12345678-1234-1234-1234-123456789abc", "3")]
    [InlineData("vulkan", "vulkan:0123456789abcdef0123456789abcdef", "vulkan:0123456789abcdef0123456789abcdef", "3")]
    [InlineData("hip", "hip:0123456789abcdef0123456789abcdef", "hip:0123456789abcdef0123456789abcdef", "3")]
    [InlineData("sycl", "sycl:cpu", "sycl:cpu", "3")]
    public void RunnerRequiresActualDeviceContract(string runner, string? id, string device, string backend)
    {
        var controls = PhoenixConfiguration.Controls(new PhoenixSettings { Runner = runner, ExecutionDeviceId = id });
        Assert.Equal(device, controls["ExecutionDevice"]); Assert.Equal(backend, controls["Backend"]);
    }

    [Theory]
    [InlineData("cuda")] [InlineData("vulkan")] [InlineData("hip")] [InlineData("sycl")]
    public void FamilyNameIsNotAConcreteDevice(string runner) =>
        Assert.Throws<ArgumentException>(() => PhoenixConfiguration.Controls(new PhoenixSettings { Runner = runner }));

    [Fact]
    public void InvalidControlsAreRejectedBeforeAnyWrite()
    {
        using var workspace = new TestWorkspace();
        var model = Dll(workspace, "model.dll", CpuArch.X64);
        Assert.Throws<ArgumentOutOfRangeException>(() => PhoenixConfiguration.Write(workspace.Root, model, new() { Iterations = 11 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => PhoenixConfiguration.Write(workspace.Root, model, new() { RefinementPasses = 5 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => PhoenixConfiguration.Write(workspace.Root, model, new() { LocalTone = float.NaN }));
        Assert.False(File.Exists(workspace.FilePath("Phoenix.json")));
    }

    [Fact]
    public void ScannerChoosesNewestValidPhoenixAndOnlyActiveCustomAddons()
    {
        using var workspace = new TestWorkspace();
        var first = Phoenix(workspace, "old/Phoenix.addon64");
        var latest = Phoenix(workspace, "new/Phoenix.addon64");
        File.SetLastWriteTimeUtc(first, DateTime.UtcNow.AddDays(-2));
        File.SetLastWriteTimeUtc(latest, DateTime.UtcNow.AddDays(-1));
        var wrong = Dll(workspace, "wrong/Phoenix.addon64", CpuArch.X86); File.SetLastWriteTimeUtc(wrong, DateTime.UtcNow);
        Dll(workspace, "CustomAddons/a.addon32", CpuArch.X86);
        Dll(workspace, "CustomAddons/b.addon64", CpuArch.X64);
        Dll(workspace, "CustomAddons/b.addon64.disabled", CpuArch.X64);
        Dll(workspace, "CustomAddons/renodx-dlss5-old.addon64", CpuArch.X64);
        Dll(workspace, "CustomAddons/dlss5-feed.addon32", CpuArch.X86);
        Dll(workspace, "backups/stray.addon64", CpuArch.X64);
        var payload = PayloadScanner.Scan(workspace.Root);
        Assert.Equal(Path.GetFullPath(latest), payload.PhoenixAddon);
        Assert.Equal(new[] { "a.addon32", "b.addon64" }, payload.ExtraAddons.Select(Path.GetFileName).OrderBy(x => x));
        Assert.False(PayloadScanner.IsPhoenixAddon(Dll(workspace, "renamed/Phoenix.addon64", CpuArch.X64)));
    }

    [Fact]
    public void Dx11AlwaysFeedsAndDx12RequiresNativeSignal()
    {
        using var fixture = new Fixture();
        Assert.Equal(InstallRoute.X64DxgiFeeder, (fixture.Game with { Api = GraphicsApi.DirectX11 }).Route);
        Assert.Equal(InstallRoute.X64DxgiFeeder, (fixture.Game with { HasNativeDlssSignal = false }).Route);
        Assert.Equal(InstallRoute.X64DirectPhoenix, fixture.Game.Route);
        var modelOnly = fixture.Payload with { NvidiaDlls = fixture.Payload.NvidiaDlls.Where(p => Path.GetFileName(p) == "nvngx_dlssnr.dll").ToArray() };
        Assert.True(modelOnly.IsReadyFor(InstallRoute.X64DirectPhoenix));
        Assert.True(modelOnly.IsReadyFor(InstallRoute.X64DxgiFeeder));
        Assert.Equal("", modelOnly.MissingFor(InstallRoute.X64DxgiFeeder));
    }

    [Theory]
    [InlineData((int)CpuArch.X64, (int)GraphicsApi.DirectX11, "host64")]
    [InlineData((int)CpuArch.X86, (int)GraphicsApi.DirectX11, "host64")]
    [InlineData((int)CpuArch.X64, (int)GraphicsApi.OpenXr, "_DLSS5_OpenXR/host64")]
    public async Task FeederInstallsSelectedModelWithoutSrOrNativeNr(int architecture, int api, string hostRelative)
    {
        using var fixture = new Fixture();
        var game = fixture.Game with { Arch = (CpuArch)architecture, Api = (GraphicsApi)api };
        Dll(fixture.Workspace, "payload/ReShade32.dll", CpuArch.X86);
        fixture.Payload = PayloadScanner.Scan(fixture.Payload.Root);
        var selected = Dll(fixture.Workspace, "selected-model.dll", CpuArch.X64);
        var bytes = File.ReadAllBytes(selected); bytes[^1] = 42; File.WriteAllBytes(selected, bytes);
        // Even with optional runtimes in Payload, own enhancement only deploys the selected weights.
        await fixture.Engine.InstallAsync(game, fixture.Payload, phoenixSettings: fixture.Settings, phoenixModelPath: selected);
        var host = Path.GetFullPath(Path.Combine(game.Root, hostRelative));
        Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(host, "PhoenixModel.dll")));
        Assert.False(File.Exists(Path.Combine(host, "nvngx_dlss.dll")));
        Assert.False(File.Exists(Path.Combine(host, "nvngx_dlssnr.dll")));
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(host, "Phoenix.json")));
        Assert.Equal("feeder_d3d12", json.RootElement.GetProperty("input_provider").GetString());
        Assert.Equal(Path.Combine(host, "PhoenixModel.dll"), json.RootElement.GetProperty("weight_source").GetProperty("path").GetString());
        await fixture.Engine.RestoreAsync(game.Root);
        Assert.False(File.Exists(Path.Combine(host, "PhoenixModel.dll")));
    }

    [Fact]
    public async Task FeederInstallsWithModelOnlyAndNativeNrRequiresExplicitSelection()
    {
        using var fixture = new Fixture();
        var game = fixture.Game with { Api = GraphicsApi.DirectX11 };
        File.Delete(Path.Combine(fixture.Payload.Root, "nvngx_dlss.dll"));
        fixture.Payload = PayloadScanner.Scan(fixture.Payload.Root);
        Assert.Equal("nvngx_dlssnr.dll", Path.GetFileName(Assert.Single(fixture.Payload.NvidiaDlls)));
        Assert.True(fixture.Payload.IsReadyFor(game.Route));
        await fixture.Engine.InstallAsync(game, fixture.Payload, phoenixSettings: fixture.Settings);
        var host = Path.Combine(game.Root, "host64");
        Assert.False(File.Exists(Path.Combine(host, "nvngx_dlssnr.dll")));
        await fixture.Engine.InstallAsync(game, fixture.Payload, phoenixSettings: fixture.Settings with { Runner = "nvidia" });
        Assert.Equal(File.ReadAllBytes(Path.Combine(host, "PhoenixModel.dll")), File.ReadAllBytes(Path.Combine(host, "nvngx_dlssnr.dll")));
        Assert.False(File.Exists(Path.Combine(host, "nvngx_dlss.dll")));
        Assert.Equal("1", IniEditor.GetValue(Path.Combine(host, "ReShade.ini"), "Phoenix", "Backend"));
        IniEditor.SetValue(Path.Combine(host, "ReShade.ini"), "Phoenix", "SourceB", "1");
        await fixture.Engine.InstallAsync(game, fixture.Payload, phoenixSettings: fixture.Settings);
        Assert.False(File.Exists(Path.Combine(host, "nvngx_dlssnr.dll")));
        Assert.Equal("3", IniEditor.GetValue(Path.Combine(host, "ReShade.ini"), "Phoenix", "Backend"));
        Assert.Equal("2", IniEditor.GetValue(Path.Combine(host, "ReShade.ini"), "Phoenix", "SourceB"));
    }

    [Theory]
    [InlineData((int)CpuArch.X86, (int)GraphicsApi.DirectX9)]
    [InlineData((int)CpuArch.X86, (int)GraphicsApi.DirectX11)]
    [InlineData((int)CpuArch.X64, (int)GraphicsApi.DirectX9)]
    [InlineData((int)CpuArch.X64, (int)GraphicsApi.DirectX11)]
    [InlineData((int)CpuArch.X64, (int)GraphicsApi.DirectX12)]
    [InlineData((int)CpuArch.X64, (int)GraphicsApi.OpenGl)]
    public async Task FeederPresentsPhoenixResultAfterFreshInstallAndProfileRestore(int architecture, int api)
    {
        using var fixture = new Fixture();
        Dll(fixture.Workspace, "payload/ReShade32.dll", CpuArch.X86);
        fixture.Payload = PayloadScanner.Scan(fixture.Payload.Root);
        var game = fixture.Game with { Arch = (CpuArch)architecture, Api = (GraphicsApi)api, HasNativeDlssSignal = false };
        var config = Path.Combine(game.Root, "dlss5-feed.cfg");
        var hostIni = Path.Combine(game.Root, "host64", "ReShade.ini");
        await fixture.Engine.InstallAsync(game, fixture.Payload, phoenixSettings: fixture.Settings);
        Assert.Contains("compare_mode=1", File.ReadAllLines(config));
        Assert.True(File.Exists(Path.Combine(game.Root, architecture == (int)CpuArch.X86 ? "dlss5-feed.addon32" : "dlss5-feed.addon64")));
        File.WriteAllText(config, File.ReadAllText(config).Replace("compare_mode=1", "compare_mode=0"));
        IniEditor.SetValue(hostIni, "RenoDX.DLSS5", "EnableHooks", "2");
        IniEditor.SetValue(hostIni, "Phoenix", "Tone", "0.37");
        await fixture.Engine.InstallAsync(game, fixture.Payload, phoenixSettings: fixture.Settings);
        Assert.Contains("compare_mode=1", File.ReadAllLines(config));
        Assert.DoesNotContain("[RenoDX.DLSS5]", File.ReadAllText(hostIni));
        Assert.Equal("0.37", IniEditor.GetValue(hostIni, "Phoenix", "Tone"));
    }

    [Fact]
    public async Task ExplicitNvidiaComparisonDeploysOnlyNrAlongsideSelectedWeights()
    {
        using var fixture = new Fixture();
        await fixture.Engine.InstallAsync(fixture.Game with { Api = GraphicsApi.DirectX11 }, fixture.Payload,
            phoenixSettings: fixture.Settings with { Comparison = 1, SourceA = 1 });
        var host = Path.Combine(fixture.Game.Root, "host64");
        Assert.True(File.Exists(Path.Combine(host, "nvngx_dlssnr.dll")));
        Assert.False(File.Exists(Path.Combine(host, "nvngx_dlss.dll")));
    }

    [Theory]
    [InlineData((int)GraphicsApi.DirectX11, "host64", false)]
    [InlineData((int)GraphicsApi.DirectX11, "host64", true)]
    [InlineData((int)GraphicsApi.OpenXr, "_DLSS5_OpenXR/host64", false)]
    [InlineData((int)GraphicsApi.OpenXr, "_DLSS5_OpenXR/host64", true)]
    public async Task OwnModelInstallIgnoresUnavailableUnownedNrButExplicitComparisonChecksIt(int api, string hostRelative, bool readOnly)
    {
        using var fixture = new Fixture();
        var game = fixture.Game with { Api = (GraphicsApi)api };
        var nr = Path.GetFullPath(Path.Combine(game.Root, hostRelative, "nvngx_dlssnr.dll"));
        Directory.CreateDirectory(Path.GetDirectoryName(nr)!);
        var original = Encoding.UTF8.GetBytes("unowned optional NR runtime");
        File.WriteAllBytes(nr, original);
        var attributes = File.GetAttributes(nr);
        if (readOnly) File.SetAttributes(nr, attributes | FileAttributes.ReadOnly);
        try
        {
            using (var locked = readOnly ? null : new FileStream(nr, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                var own = InstallerEngine.CheckPreflight(game, fixture.Payload);
                Assert.True(own.CanInstall, own.DisplayText);
                foreach (var selected in new[] { fixture.Settings with { Runner = "nvidia" },
                    fixture.Settings with { Comparison = 1, SourceA = 1 } })
                {
                    var native = InstallerEngine.CheckPreflight(game, fixture.Payload, phoenixSettings: selected);
                    Assert.True(native.CanInstall, native.DisplayText);
                    Assert.Contains(native.Findings, finding => finding.Code == "file-unavailable" && finding.Path == nr &&
                        finding.Severity == PreflightSeverity.Warning);
                }
                await fixture.Engine.InstallAsync(game, fixture.Payload, phoenixSettings: fixture.Settings);
            }
            Assert.Equal(original, File.ReadAllBytes(nr));
            Assert.DoesNotContain(fixture.ReadManifest().Added, path => Path.GetFileName(path) == "nvngx_dlssnr.dll");
            Assert.DoesNotContain(fixture.ReadManifest().Replaced, file => Path.GetFileName(file.RelativePath) == "nvngx_dlssnr.dll");
            await fixture.Engine.RestoreAsync(game.Root);
            Assert.Equal(original, File.ReadAllBytes(nr));
        }
        finally { File.SetAttributes(nr, attributes); }
    }

    [Theory]
    [InlineData("native", 1, 1, 2, true)]
    [InlineData("native", 2, 2, 1, true)]
    [InlineData("native", 3, 0, 2, false)]
    [InlineData("nvidia", 0, 0, 2, true)]
    public async Task ExplicitComparisonOnReinstallMatchesDeploymentAndPreservesOtherProfileTuning(
        string runner, int comparison, int sourceA, int sourceB, bool nativeNr)
    {
        using var fixture = new Fixture();
        var game = fixture.Game with { Api = GraphicsApi.DirectX11 };
        await fixture.Engine.InstallAsync(game, fixture.Payload, phoenixSettings: fixture.Settings);
        var host = Path.Combine(game.Root, "host64");
        var tuning = new Dictionary<string, string> { ["Tone"] = "0.37", ["Structure"] = "1.23",
            ["Intensity"] = "0.8", ["Divider"] = "0.63", ["DifferenceGain"] = "7",
            ["ComparisonLabels"] = "0", ["ComparisonLines"] = "0" };
        foreach (var name in new[] { "ReShade.ini", "ReShadeVR.ini" })
        {
            var ini = Path.Combine(host, name);
            foreach (var pair in tuning) IniEditor.SetValue(ini, "Phoenix", pair.Key, pair.Value);
            IniEditor.SetValue(ini, "Phoenix", "Comparison", "5");
            IniEditor.SetValue(ini, "Phoenix", "SourceA", "2");
            IniEditor.SetValue(ini, "Phoenix", "SourceB", "0");
        }
        var selected = fixture.Settings with { Runner = runner, Comparison = comparison, SourceA = sourceA, SourceB = sourceB };
        await fixture.Engine.InstallAsync(game, fixture.Payload, phoenixSettings: selected);
        Assert.Equal(nativeNr, File.Exists(Path.Combine(host, "nvngx_dlssnr.dll")));
        Assert.False(File.Exists(Path.Combine(host, "nvngx_dlss.dll")));
        foreach (var name in new[] { "ReShade.ini", "ReShadeVR.ini" })
        {
            var ini = Path.Combine(host, name);
            Assert.Equal(comparison.ToString(), IniEditor.GetValue(ini, "Phoenix", "Comparison"));
            Assert.Equal(sourceA.ToString(), IniEditor.GetValue(ini, "Phoenix", "SourceA"));
            Assert.Equal(sourceB.ToString(), IniEditor.GetValue(ini, "Phoenix", "SourceB"));
            Assert.Equal(runner == "nvidia" ? "1" : "3", IniEditor.GetValue(ini, "Phoenix", "Backend"));
            foreach (var pair in tuning) Assert.Equal(pair.Value, IniEditor.GetValue(ini, "Phoenix", pair.Key));
        }
    }

    [Fact]
    public void DosLauncherRejectsTheLegacyStagingPackage()
    {
        using var fixture = new Fixture();
        var dosbox = fixture.Workspace.WritePe("dosbox-staging/dosbox.exe", CpuArch.X64);
        var error = Assert.Throws<InvalidOperationException>(() =>
            DosGameLauncher.PrepareRuntime(fixture.Payload with { DosBoxEnhancedExe = dosbox }));
        Assert.Contains("DOSBox Enhanced", error.Message);
    }

    [Fact]
    public void EnhancedDosPreparationUsesNativeLibraryWithoutReShadeOrAnAddon()
    {
        using var fixture = new Fixture();
        var executable = fixture.Workspace.WritePe("dosbox-enhanced/app/DosboxEnhanced.exe", CpuArch.X64);
        foreach (var file in new[] { "dosbox_pure_libretro.dll", "dosbox_nvngx.dll", "phoenix_native.dll" })
            fixture.Workspace.WritePe("dosbox-enhanced/runtime/" + file, CpuArch.X64);
        Directory.CreateDirectory(fixture.Workspace.FilePath("dosbox-enhanced/runtime/native"));
        File.WriteAllText(fixture.Workspace.FilePath("dosbox-enhanced/runtime/native/native_manifest.json"), "{}");
        fixture.Workspace.WritePe("dosbox-enhanced/runtime/nvidia/sl.interposer.dll", CpuArch.X64);
        fixture.Workspace.WritePe("dosbox-enhanced/runtime/nvidia/nvngx_dlss.dll", CpuArch.X64);
        var game = fixture.Workspace.FilePath("DOS game/PLAY.COM");
        Directory.CreateDirectory(Path.GetDirectoryName(game)!); File.WriteAllText(game, "test program");
        var payload = fixture.Payload with { DosBoxEnhancedExe = executable, PhoenixAddon = null,
            ReShade32Dll = null, ReShade64Dll = null };
        var runtime = DosGameLauncher.PrepareRuntime(payload, fixture.Settings with { Runner = "cuda_rtx" });
        try
        {
            var profile = DosGameLauncher.WriteGameConfig(runtime, game);
            using var json = JsonDocument.Parse(File.ReadAllText(profile));
            Assert.Equal("phoenix", json.RootElement.GetProperty("Effects").GetProperty("Renderer").GetString());
            Assert.False(json.RootElement.GetProperty("Effects").GetProperty("NvidiaUpscaling").GetBoolean());
            Assert.False(json.RootElement.GetProperty("Effects").GetProperty("NvidiaFrameGeneration").GetBoolean());
            Assert.Equal("C:\\PLAY.COM", json.RootElement.GetProperty("Startup").GetString());
            Assert.True(File.Exists(Path.Combine(runtime, "app", "DosboxEnhanced.exe")));
            Assert.False(Directory.Exists(Path.Combine(runtime, "host64")));
            Assert.Empty(Directory.GetFiles(runtime, "*.addon*", SearchOption.AllDirectories));
            Assert.False(File.Exists(Path.Combine(runtime, "opengl32.dll")));
            Assert.False(File.Exists(Path.Combine(runtime, "ReShade.ini")));
            Assert.False(File.Exists(Path.Combine(runtime, "nvngx_dlssnr.dll")));
            using var graphics = JsonDocument.Parse(File.ReadAllText(Path.Combine(runtime, "graphics.json")));
            Assert.Equal("nvidia_dll", graphics.RootElement.GetProperty("weight_source").GetProperty("kind").GetString());
            var start = DosGameLauncher.CreateStartInfo(runtime, profile);
            Assert.False(start.UseShellExecute);
            Assert.Equal(new[] { "--profile", profile }, start.ArgumentList.ToArray());
        }
        finally { Directory.Delete(runtime, true); }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("CnrPHOENIXFeederVersion")]
    [InlineData("CnrPHOENIXFeederEvaluate")]
    public async Task OlderOrPartialFeederApiBlocksOnlyFeederRoutesBeforeInstallWrites(string? singleExport)
    {
        using var fixture = new Fixture();
        var exports = new[] { "CnrPHOENIXSummary", "CnrPHOENIXRegister" }.Concat(singleExport is null ? [] : new[] { singleExport }).ToArray();
        TestWorkspace.SetPhoenixExports(fixture.Payload.PhoenixAddon!, exports);
        Assert.True(fixture.Payload.HasPhoenixAddon);
        Assert.False(fixture.Payload.HasPhoenixFeederApi);
        Assert.True(fixture.Payload.IsReadyFor(InstallRoute.X64DirectPhoenix));
        Assert.False(fixture.Payload.IsReadyFor(InstallRoute.X64DxgiFeeder));
        Assert.Contains("Import a newly built", fixture.Payload.MissingFor(InstallRoute.X64DxgiFeeder));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Engine.InstallAsync(
            fixture.Game with { Api = GraphicsApi.DirectX11 }, fixture.Payload, phoenixSettings: fixture.Settings));
        Assert.Contains("CnrPHOENIXFeederVersion", error.Message);
        Assert.Contains("CnrPHOENIXFeederEvaluate", error.Message);
        Assert.False(Directory.Exists(Path.Combine(fixture.Game.Root, "host64")));
        Assert.False(File.Exists(fixture.ManifestPath));
        await fixture.Install();
        using var config = JsonDocument.Parse(File.ReadAllText(Path.Combine(fixture.Game.Root, "Phoenix.json")));
        Assert.Equal("game_dlss", config.RootElement.GetProperty("input_provider").GetString());
    }

    [Fact]
    public void FeederApiNamesInUnexportedDataDoNotSatisfyCompatibility()
    {
        using var workspace = new TestWorkspace();
        var addon = Phoenix(workspace, "Phoenix.addon64");
        TestWorkspace.SetPhoenixExports(addon, "CnrPHOENIXSummary", "CnrPHOENIXRegister");
        var bytes = File.ReadAllBytes(addon);
        Encoding.ASCII.GetBytes("CnrPHOENIXFeederVersion\0CnrPHOENIXFeederEvaluate\0").CopyTo(bytes, 0x10000);
        File.WriteAllBytes(addon, bytes);
        Assert.False(PayloadScanner.HasPhoenixFeederApi(addon));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0xc00 + 24), uint.MaxValue);
        File.WriteAllBytes(addon, bytes);
        Assert.False(PayloadScanner.HasPhoenixFeederApi(addon));
    }

    [Theory]
    [InlineData((int)CpuArch.X64, "phoenix-host/2", true)]
    [InlineData((int)CpuArch.X64, "phoenix-host/1", false)]
    [InlineData((int)CpuArch.X86, "phoenix-host/2", false)]
    public void HostPackageMustIdentifyTheDirectFeederRoute(int architecture, string marker, bool ready)
    {
        using var workspace = new TestWorkspace();
        var path = workspace.FilePath("host.exe");
        File.WriteAllBytes(path, TestWorkspace.PeBytes((CpuArch)architecture, [], marker));
        Assert.Equal(ready, PayloadScanner.IsDirectPhoenixHost(path));
    }

    [Fact]
    public async Task DirectInstallPreservesGameAbiAndRestoresLegacyAddonExactly()
    {
        using var fixture = new Fixture();
        var oldAddon = Path.Combine(fixture.Game.Root, "renodx-dlss5.addon64");
        File.WriteAllText(oldAddon, "old addon");
        var streamline = Path.Combine(fixture.Game.Root, "sl.interposer.dll"); File.WriteAllText(streamline, "game ABI");
        var ngx = Path.Combine(fixture.Game.Root, "nvngx_dlss.dll"); File.WriteAllText(ngx, "game SR");
        var ini = Path.Combine(fixture.Game.Root, "ReShade.ini"); File.WriteAllText(ini, "[GAME]\nKeep=1\n[RenoDX.DLSS5]\nEnableHooks=2\n");
        var originalIni = File.ReadAllText(ini);
        fixture.LegacyJournal("renodx-dlss5.addon64", "ReShade.ini");
        var addon64 = Dll(fixture.Workspace, "payload/CustomAddons/custom.addon64", CpuArch.X64);
        Dll(fixture.Workspace, "payload/CustomAddons/custom.addon32", CpuArch.X86);
        fixture.Payload = PayloadScanner.Scan(fixture.Payload.Root);
        await fixture.Install();
        Assert.False(File.Exists(oldAddon)); Assert.True(File.Exists(Path.Combine(fixture.Game.Root, "Phoenix.json")));
        Assert.Equal("game ABI", File.ReadAllText(streamline)); Assert.Equal("game SR", File.ReadAllText(ngx));
        Assert.Equal(File.ReadAllBytes(addon64), File.ReadAllBytes(Path.Combine(fixture.Game.Root, "custom.addon64")));
        Assert.False(File.Exists(Path.Combine(fixture.Game.Root, "custom.addon32")));
        using (var config = JsonDocument.Parse(File.ReadAllText(Path.Combine(fixture.Game.Root, "Phoenix.json"))))
            Assert.Equal("game_dlss", config.RootElement.GetProperty("input_provider").GetString());
        Assert.DoesNotContain("RenoDX", File.ReadAllText(ini));
        Assert.False(InstallerEngine.HasPending(fixture.Game.Root));
        var launcher = File.ReadAllText(InstallerEngine.LauncherPathFor(fixture.Game));
        Assert.DoesNotContain("rd /S", launcher); Assert.DoesNotContain("copy /Y", launcher);
        await fixture.Engine.RestoreAsync(fixture.Game.Root);
        Assert.Equal("old addon", File.ReadAllText(oldAddon)); Assert.Equal(originalIni, File.ReadAllText(ini));
        Assert.False(File.Exists(Path.Combine(fixture.Game.Root, "Phoenix.addon64")));
    }

    [Fact]
    public async Task InterruptedInstallHasDurableJournalAndRecoversOriginals()
    {
        using var fixture = new Fixture();
        var old = Path.Combine(fixture.Game.Root, "renodx-dlss5.addon64"); File.WriteAllText(old, "original");
        fixture.LegacyJournal("renodx-dlss5.addon64");
        var stopped = new InstallerEngine(message => { if (message.StartsWith("Backed up and removed")) throw new IOException("simulated crash"); });
        await Assert.ThrowsAsync<IOException>(() => fixture.Install(stopped));
        Assert.True(InstallerEngine.HasPending(fixture.Game.Root)); Assert.False(File.Exists(fixture.ManifestPath));
        Assert.False(File.Exists(old));
        await fixture.Engine.RecoverAsync(fixture.Game.Root);
        Assert.Equal("original", File.ReadAllText(old)); Assert.False(InstallerEngine.HasPending(fixture.Game.Root));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task MissingOrCorruptBackupStopsWholeRestoreBeforeWriting(bool corrupt)
    {
        using var fixture = new Fixture();
        File.WriteAllText(Path.Combine(fixture.Game.Root, "renodx-dlss5.addon64"), "old");
        var ini = Path.Combine(fixture.Game.Root, "ReShade.ini"); File.WriteAllText(ini, "[GAME]\nOriginal=1\n");
        fixture.LegacyJournal("renodx-dlss5.addon64", "ReShade.ini");
        await fixture.Install();
        var before = File.ReadAllText(ini);
        var manifest = fixture.ReadManifest();
        var backup = Path.Combine(fixture.Game.Root, "_DLSS5_Compat_Backup", manifest.Replaced.Last().BackupRelativePath);
        if (corrupt) File.WriteAllText(backup, "corrupt"); else File.Delete(backup);
        await Assert.ThrowsAsync<IOException>(() => fixture.Engine.RestoreAsync(fixture.Game.Root));
        Assert.Equal(before, File.ReadAllText(ini)); Assert.True(File.Exists(fixture.ManifestPath));
        var store = new BackendProfileStore(fixture.Game.Root, fixture.Game.ExePath);
        Assert.False(File.Exists(store.ProfilePath(fixture.Game.Route.ToString())));
    }

    [Theory]
    [InlineData("../outside.txt")] [InlineData("C:/outside.txt")] [InlineData("_DLSS5_Compat_Backup/manifest.json")]
    public async Task UnsafeRestorePathIsRejectedBeforeAnyWrite(string unsafePath)
    {
        using var fixture = new Fixture(); await fixture.Install();
        var manifest = fixture.ReadManifest(); manifest.Added.Add(unsafePath); fixture.WriteManifest(manifest);
        var phoenix = Path.Combine(fixture.Game.Root, "Phoenix.addon64"); var before = File.ReadAllBytes(phoenix);
        await Assert.ThrowsAsync<IOException>(() => fixture.Engine.RestoreAsync(fixture.Game.Root));
        Assert.Equal(before, File.ReadAllBytes(phoenix)); Assert.True(File.Exists(fixture.ManifestPath));
    }

    [Fact]
    public async Task RestorePreservesNonemptyAddedDirectory()
    {
        using var fixture = new Fixture(); await fixture.Install();
        var manifest = fixture.ReadManifest(); manifest.AddedDirectories.Add("new-directory"); fixture.WriteManifest(manifest);
        var directory = Path.Combine(fixture.Game.Root, "new-directory"); Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "user-save.txt"), "keep");
        await fixture.Engine.RestoreAsync(fixture.Game.Root);
        Assert.Equal("keep", File.ReadAllText(Path.Combine(directory, "user-save.txt")));
    }

    [Fact]
    public async Task LockedTargetPreventsInstallBeforeLegacyRemoval()
    {
        using var fixture = new Fixture();
        var legacy = Path.Combine(fixture.Game.Root, "renodx-dlss5.addon64"); File.WriteAllText(legacy, "original");
        var target = Path.Combine(fixture.Game.Root, "ReShade.ini"); File.WriteAllText(target, "locked");
        using var locked = new FileStream(target, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Install());
        Assert.Equal("original", File.ReadAllText(legacy)); Assert.False(InstallerEngine.HasPending(fixture.Game.Root));
    }

    [Fact]
    public async Task InvalidCustomAddonCannotStartTransaction()
    {
        using var fixture = new Fixture();
        var wrong = Dll(fixture.Workspace, "payload/CustomAddons/wrong.addon64", CpuArch.X86);
        fixture.Payload = fixture.Payload with { ExtraAddons = new[] { wrong } };
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Install());
        Assert.False(InstallerEngine.HasPending(fixture.Game.Root)); Assert.False(File.Exists(fixture.ManifestPath));
    }

    [Fact]
    public async Task UnmanagedRenoDxIsBlockedWithoutAnyRemoval()
    {
        using var fixture = new Fixture();
        var path = Path.Combine(fixture.Game.Root, "renodx-dlss5.addon64"); File.WriteAllText(path, "external mod");
        Assert.False(InstallerEngine.CheckPreflight(fixture.Game, CancellationToken.None).CanInstall);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Install());
        Assert.Equal("external mod", File.ReadAllText(path)); Assert.False(InstallerEngine.HasPending(fixture.Game.Root));
    }

    [Fact]
    public async Task LegacyAuditRecordsPresentDayEvidenceAndPreservesUserDirectory()
    {
        using var fixture = new Fixture();
        var ini = Path.Combine(fixture.Game.Root, "ReShade.ini"); File.WriteAllText(ini, "original");
        fixture.LegacyJournal("ReShade.ini"); File.WriteAllText(ini, "installed");
        var manifest = fixture.ReadManifest(); manifest.Added.Add("user-directory"); fixture.WriteManifest(manifest);
        Directory.CreateDirectory(Path.Combine(fixture.Game.Root, "user-directory"));
        File.WriteAllText(Path.Combine(fixture.Game.Root, "user-directory/user.txt"), "keep");
        Assert.Contains(InstallerEngine.CheckPreflight(fixture.Game).Findings, f => f.Code == "legacy-backup-adoption");
        await fixture.Engine.RestoreAsync(fixture.Game.Root);
        Assert.Equal("original", File.ReadAllText(ini));
        Assert.Equal("keep", File.ReadAllText(Path.Combine(fixture.Game.Root, "user-directory/user.txt")));
        var journal = Directory.GetFiles(Path.GetDirectoryName(fixture.ManifestPath)!, "pending.json.done-*").Single();
        Assert.Contains("adopted-existing-v1-backups", File.ReadAllText(journal));
    }

    [Fact]
    public async Task MissingLegacyBackupBlocksUiAndRestoreBeforeWriting()
    {
        using var fixture = new Fixture();
        var ini = Path.Combine(fixture.Game.Root, "ReShade.ini"); File.WriteAllText(ini, "original");
        fixture.LegacyJournal("ReShade.ini"); File.Delete(Path.Combine(Path.GetDirectoryName(fixture.ManifestPath)!, "ReShade.ini"));
        Assert.False(InstallerEngine.CheckPreflight(fixture.Game).CanInstall);
        await Assert.ThrowsAsync<IOException>(() => fixture.Engine.RestoreAsync(fixture.Game.Root));
        Assert.Equal("original", File.ReadAllText(ini));
    }

    [Fact]
    public void UnrelatedStockDllIsNotAnOverwriteConflict()
    {
        using var fixture = new Fixture();
        File.WriteAllText(Path.Combine(fixture.Game.Root, "d3d11.dll"), "stock");
        var result = InstallerEngine.CheckPreflight(fixture.Game);
        Assert.True(result.CanInstall, result.DisplayText);
    }

    [Fact]
    public async Task RouteProfilesRestoreOwnedSettingsAndSurviveOriginalRestore()
    {
        using var fixture = new Fixture();
        await fixture.Install();
        var ini = Path.Combine(fixture.Game.Root, "ReShade.ini");
        IniEditor.SetValue(ini, "Phoenix", "Tone", "0.37");
        var feeder = fixture.Game with { Api = GraphicsApi.DirectX11 };
        await fixture.Engine.InstallAsync(feeder, fixture.Payload, phoenixSettings: fixture.Settings);
        var store = new BackendProfileStore(fixture.Game.Root, fixture.Game.ExePath);
        Assert.True(File.Exists(store.ProfilePath(InstallRoute.X64DirectPhoenix.ToString())));
        await fixture.Install();
        Assert.Contains("Tone=0.37", File.ReadAllText(ini));
        await fixture.Engine.RestoreAsync(fixture.Game.Root);
        Assert.True(File.Exists(store.ProfilePath(InstallRoute.X64DirectPhoenix.ToString())));
        Assert.False(File.Exists(ini));
    }

    [Theory]
    [InlineData(false, false)] [InlineData(false, true)]
    [InlineData(true, false)] [InlineData(true, true)]
    public async Task RemoveThenReinstallPreservesLatestControlsWithoutRouteSwitch(bool feeder, bool staleProfile)
    {
        using var fixture = new Fixture();
        var game = feeder ? fixture.Game with { Api = GraphicsApi.DirectX11 } : fixture.Game;
        var directory = feeder ? Path.Combine(game.Root, "host64") : game.Root;
        Directory.CreateDirectory(directory);
        var ini = Path.Combine(directory, "ReShade.ini");
        var original = Encoding.UTF8.GetBytes("; user original\r\n[GAME]\r\nKeep=1\r\n");
        File.WriteAllBytes(ini, original);
        await fixture.Engine.InstallAsync(game, fixture.Payload, phoenixSettings: fixture.Settings);
        var store = new BackendProfileStore(game.Root, game.ExePath);
        if (staleProfile) store.Snapshot(game.Route.ToString());
        var controls = new PhoenixOfflineControls(directory);
        controls.Read();
        controls.Apply(new PhoenixControlSettings().Set(PhoenixSetting.Tone, 0.37f)
            .Set(PhoenixSetting.Structure, 1.23f).Set(PhoenixSetting.ExecutionDevice, "cpu"), () => true);
        var tunedIni = File.ReadAllBytes(ini);
        var vrIni = Path.Combine(directory, "ReShadeVR.ini");
        var tunedVrIni = File.ReadAllBytes(vrIni);

        await fixture.Engine.RestoreAsync(game.Root);

        Assert.Equal(original, File.ReadAllBytes(ini));
        Assert.False(File.Exists(vrIni));
        Assert.False(File.Exists(Path.Combine(directory, "Phoenix.json")));
        Assert.False(File.Exists(fixture.ManifestPath));
        Assert.False(InstallerEngine.HasPending(game.Root));
        var profile = store.Load(game.Route.ToString());
        Assert.Equal(tunedIni, profile[Path.GetRelativePath(game.Root, ini).Replace('\\', '/')]);
        Assert.Equal(tunedVrIni, profile[Path.GetRelativePath(game.Root, vrIni).Replace('\\', '/')]);

        await fixture.Engine.InstallAsync(game, fixture.Payload, phoenixSettings: fixture.Settings);

        foreach (var path in new[] { ini, vrIni })
        {
            Assert.Contains("Tone=0.37", File.ReadAllText(path));
            Assert.Contains("Structure=1.23", File.ReadAllText(path));
            Assert.Contains("ExecutionDevice=native", File.ReadAllText(path));
        }
        await fixture.Engine.RestoreAsync(game.Root);
        Assert.Equal(original, File.ReadAllBytes(ini));
    }

    [Fact]
    public async Task SnapshotFailureLeavesInstallationAndPreviousProfileIntactBeforeRestoreJournal()
    {
        using var fixture = new Fixture();
        await fixture.Install();
        var store = new BackendProfileStore(fixture.Game.Root, fixture.Game.ExePath);
        var profilePath = store.Snapshot(fixture.Game.Route.ToString()).Path;
        var previousProfile = File.ReadAllBytes(profilePath);
        var ini = Path.Combine(fixture.Game.Root, "ReShade.ini");
        File.AppendAllText(ini, new string('x', BackendProfileStore.MaxFileBytes));
        var beforeIni = File.ReadAllBytes(ini);
        var beforeManifest = File.ReadAllBytes(fixture.ManifestPath);
        var addon = Path.Combine(fixture.Game.Root, "Phoenix.addon64");
        var beforeAddon = File.ReadAllBytes(addon);

        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Engine.RestoreAsync(fixture.Game.Root));

        Assert.Equal(beforeIni, File.ReadAllBytes(ini));
        Assert.Equal(beforeAddon, File.ReadAllBytes(addon));
        Assert.Equal(beforeManifest, File.ReadAllBytes(fixture.ManifestPath));
        Assert.Equal(previousProfile, File.ReadAllBytes(profilePath));
        Assert.False(InstallerEngine.HasPending(fixture.Game.Root));
    }

    [Fact]
    public async Task RemovalRejectsReparseProfileStoreBeforeChangingInstallation()
    {
        using var fixture = new Fixture();
        await fixture.Install();
        var store = new BackendProfileStore(fixture.Game.Root, fixture.Game.ExePath);
        var outside = fixture.Workspace.FilePath("external-profiles");
        Directory.CreateDirectory(outside);
        var sentinel = Path.Combine(outside, "user.txt");
        File.WriteAllText(sentinel, "unchanged");
        if (Directory.Exists(store.DirectoryPath)) Directory.Delete(store.DirectoryPath);
        SwapperTestLinks.CreateJunction(store.DirectoryPath, outside);
        var ini = Path.Combine(fixture.Game.Root, "ReShade.ini");
        var beforeIni = File.ReadAllBytes(ini);
        var beforeManifest = File.ReadAllBytes(fixture.ManifestPath);
        var addon = Path.Combine(fixture.Game.Root, "Phoenix.addon64");
        var beforeAddon = File.ReadAllBytes(addon);
        try
        {
            await Assert.ThrowsAsync<IOException>(() => fixture.Engine.RestoreAsync(fixture.Game.Root));
            Assert.Equal(beforeIni, File.ReadAllBytes(ini));
            Assert.Equal(beforeAddon, File.ReadAllBytes(addon));
            Assert.Equal(beforeManifest, File.ReadAllBytes(fixture.ManifestPath));
            Assert.False(InstallerEngine.HasPending(fixture.Game.Root));
            Assert.Equal("unchanged", File.ReadAllText(sentinel));
            Assert.Equal(new[] { sentinel }, Directory.GetFileSystemEntries(outside));
        }
        finally { Directory.Delete(store.DirectoryPath); }
    }

    [Fact]
    public async Task InterruptedRemovalRecoveryDoesNotReplaceSavedControlsWithRestoredOriginals()
    {
        using var fixture = new Fixture();
        var ini = Path.Combine(fixture.Game.Root, "ReShade.ini");
        var original = "[GAME]\nOriginal=1\n";
        File.WriteAllText(ini, original);
        await fixture.Install();
        IniEditor.SetValue(ini, "Phoenix", "Tone", "0.37");
        var tunedIni = File.ReadAllBytes(ini);
        var interrupted = new InstallerEngine(message =>
        {
            if (message == "Restored ReShade.ini") throw new IOException("simulated removal interruption");
        });

        await Assert.ThrowsAsync<IOException>(() => interrupted.RestoreAsync(fixture.Game.Root));

        Assert.True(InstallerEngine.HasPending(fixture.Game.Root));
        Assert.Equal(original, File.ReadAllText(ini));
        var store = new BackendProfileStore(fixture.Game.Root, fixture.Game.ExePath);
        var profilePath = store.ProfilePath(fixture.Game.Route.ToString());
        var savedProfile = File.ReadAllBytes(profilePath);
        Assert.Equal(tunedIni, store.Load(fixture.Game.Route.ToString())["ReShade.ini"]);
        await fixture.Engine.RestoreAsync(fixture.Game.Root);
        Assert.False(InstallerEngine.HasPending(fixture.Game.Root));
        Assert.False(File.Exists(fixture.ManifestPath));
        Assert.Equal(savedProfile, File.ReadAllBytes(profilePath));
        await fixture.Install();
        Assert.Contains("Tone=0.37", File.ReadAllText(ini));
    }

    [Fact]
    public async Task ReinstallRegeneratesDeploymentForModelBWhileKeepingSavedControls()
    {
        using var fixture = new Fixture();
        await fixture.Install();
        var sidecar = Path.Combine(fixture.Game.Root, "Phoenix.json");
        using var oldConfig = JsonDocument.Parse(File.ReadAllText(sidecar));
        var oldHash = oldConfig.RootElement.GetProperty("weight_source").GetProperty("sha256").GetString();
        var ini = Path.Combine(fixture.Game.Root, "ReShade.ini");
        IniEditor.SetValue(ini, "Phoenix", "Tone", "0.37");
        IniEditor.SetValue(ini, "Phoenix", "ModelSource", "nvidia_dll");
        var modelB = Dll(fixture.Workspace, "model-B.dll", CpuArch.X64);
        var bytes = File.ReadAllBytes(modelB); bytes[^1] = 42; File.WriteAllBytes(modelB, bytes);
        await fixture.Engine.InstallAsync(fixture.Game, fixture.Payload, phoenixSettings: fixture.Settings, phoenixModelPath: modelB);
        using var newConfig = JsonDocument.Parse(File.ReadAllText(sidecar));
        var source = newConfig.RootElement.GetProperty("weight_source");
        var expectedHash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        Assert.NotEqual(oldHash, expectedHash);
        Assert.Equal(expectedHash, source.GetProperty("sha256").GetString());
        Assert.Equal(Path.Combine(fixture.Game.Root, "PhoenixModel.dll"), source.GetProperty("path").GetString());
        Assert.Equal(bytes, File.ReadAllBytes(source.GetProperty("path").GetString()!));
        Assert.Equal(fixture.Game.Root, newConfig.RootElement.GetProperty("sandbox").GetString());
        Assert.Contains("Tone=0.37", File.ReadAllText(ini));
        Assert.Contains("ModelSource=configured", File.ReadAllText(ini));
    }

    [Fact]
    public async Task DirectInstallAndRestoreLeavePreexistingFeedConfigExactlyUntouched()
    {
        using var fixture = new Fixture();
        var feed = Path.Combine(fixture.Game.Root, "dlss5-feed.cfg");
        var original = Encoding.UTF8.GetBytes("iterations=7\r\nuser_setting=preserve\n");
        File.WriteAllBytes(feed, original);
        await fixture.Install();
        Assert.Equal(original, File.ReadAllBytes(feed));
        Assert.DoesNotContain("dlss5-feed.cfg", fixture.ReadManifest().Added);
        Assert.DoesNotContain(fixture.ReadManifest().Replaced, file => file.RelativePath == "dlss5-feed.cfg");
        await fixture.Engine.RestoreAsync(fixture.Game.Root);
        Assert.Equal(original, File.ReadAllBytes(feed));
    }

    [Theory]
    [InlineData("Phoenix.json")] [InlineData("ReShade.ini")] [InlineData("ReShadeVR.ini")] [InlineData("dlss5-feed.cfg")]
    public void ConfigurationReparsePathsAreRejectedBeforeAnyConfigWrite(string name)
    {
        using var workspace = new TestWorkspace();
        var model = Dll(workspace, "model.dll", CpuArch.X64);
        var directory = workspace.FilePath("config"); Directory.CreateDirectory(directory);
        var outside = workspace.FilePath("outside"); Directory.CreateDirectory(outside);
        var sentinel = Path.Combine(outside, "user.txt"); File.WriteAllText(sentinel, "unchanged");
        var link = Path.Combine(directory, name);
        SwapperTestLinks.CreateJunction(link, outside);
        try
        {
            Assert.Throws<IOException>(() => PhoenixConfiguration.Write(directory, model));
            Assert.Equal(new[] { link }, Directory.GetFileSystemEntries(directory));
            Assert.Equal("unchanged", File.ReadAllText(sentinel));
            Assert.Equal(new[] { sentinel }, Directory.GetFileSystemEntries(outside));
        }
        finally { Directory.Delete(link); }
    }

    [Fact]
    public async Task DirectFeedConfigReparseIsBlockedBeforeStartingTransaction()
    {
        using var fixture = new Fixture();
        var outside = fixture.Workspace.FilePath("external"); Directory.CreateDirectory(outside);
        var sentinel = Path.Combine(outside, "user.txt"); File.WriteAllText(sentinel, "unchanged");
        var link = Path.Combine(fixture.Game.Root, "dlss5-feed.cfg");
        SwapperTestLinks.CreateJunction(link, outside);
        try
        {
            Assert.False(InstallerEngine.CheckPreflight(fixture.Game).CanInstall);
            await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Install());
            Assert.False(InstallerEngine.HasPending(fixture.Game.Root));
            Assert.False(File.Exists(Path.Combine(fixture.Game.Root, "Phoenix.json")));
            Assert.False(File.Exists(Path.Combine(fixture.Game.Root, "ReShade.ini")));
            Assert.Equal("unchanged", File.ReadAllText(sentinel));
        }
        finally { Directory.Delete(link); }
    }

    [Fact]
    public async Task OpenVrAbiLibraryIsChainedAndRestoredExactly()
    {
        using var fixture = new Fixture();
        var game = fixture.Game with { Api = GraphicsApi.OpenVr };
        var library = ExportDll(fixture.Workspace, "game/openvr_api.dll", "VR_GetGenericInterface", "VR_InitInternal", "VR_ShutdownInternal", "VR_IsHmdPresent");
        var original = File.ReadAllBytes(library);
        var preflight = InstallerEngine.CheckPreflight(game, fixture.Payload);
        Assert.True(preflight.CanInstall, preflight.DisplayText);
        Assert.Contains(preflight.Findings, finding => finding.Code == "openvr-original" && finding.Severity == PreflightSeverity.Warning);
        await fixture.Engine.InstallAsync(game, fixture.Payload, phoenixSettings: fixture.Settings);
        Assert.Equal(original, File.ReadAllBytes(Path.Combine(game.Root, "openvr_api.dlss5-original.dll")));
        Assert.Contains(fixture.ReadManifest().Replaced, file => file.RelativePath == "openvr_api.dll");
        await fixture.Engine.RestoreAsync(game.Root);
        Assert.Equal(original, File.ReadAllBytes(library));
        Assert.False(File.Exists(Path.Combine(game.Root, "openvr_api.dlss5-original.dll")));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void UnrecognizedOrInjectedOpenVrProxyRemainsBlocked(bool exports)
    {
        using var fixture = new Fixture();
        var library = exports
            ? ExportDll(fixture.Workspace, "game/openvr_api.dll", "VR_GetGenericInterface", "VR_InitInternal", "VR_ShutdownInternal", "VR_IsHmdPresent")
            : Dll(fixture.Workspace, "game/openvr_api.dll", CpuArch.X64);
        if (exports)
        {
            var bytes = File.ReadAllBytes(library);
            Encoding.ASCII.GetBytes("ReShadeVersion\0").CopyTo(bytes, 0xe00);
            File.WriteAllBytes(library, bytes);
        }
        var result = InstallerEngine.CheckPreflight(fixture.Game with { Api = GraphicsApi.OpenVr }, fixture.Payload);
        Assert.False(result.CanInstall);
        Assert.Contains(result.Findings, finding => finding.Code == "mod-conflict" && finding.Severity == PreflightSeverity.Error);
    }

    [Fact]
    public async Task OpenXrHostConsumesSelectedIterationsAfterProfileRestore()
    {
        using var fixture = new Fixture();
        var game = fixture.Game with { Api = GraphicsApi.OpenXr };
        await fixture.Engine.InstallAsync(game, fixture.Payload, frameIterations: 3, phoenixSettings: fixture.Settings with { Iterations = 3 });
        var ini = Path.Combine(game.Root, "_DLSS5_OpenXR", "host64", "ReShade.ini");
        Assert.Contains("FrameIterations=3", File.ReadAllText(ini));
        await fixture.Engine.InstallAsync(game, fixture.Payload, phoenixSettings: fixture.Settings with { Iterations = 7 });
        Assert.Contains("FrameIterations=7", File.ReadAllText(ini));
        Assert.DoesNotContain("FrameIterations=3", File.ReadAllText(ini));
        Assert.False(File.Exists(Path.Combine(game.Root, "dlss5-feed.cfg")));
        await fixture.Engine.RestoreAsync(game.Root);
        Assert.False(File.Exists(ini));
    }

    [Fact]
    public async Task X64Dx9InstallsNativeFeederAndOnlyPlannedProxy()
    {
        using var fixture = new Fixture();
        var game = fixture.Game with { Api = GraphicsApi.DirectX9 };
        Assert.Equal(InstallRoute.X64Dx9NativeFeeder, game.Route);
        Assert.True(fixture.Payload.IsReadyFor(game.Route));
        Assert.Contains("ReShade64.dll", (fixture.Payload with { ReShade64Dll = null }).MissingFor(game.Route));
        await fixture.Engine.InstallAsync(game, fixture.Payload, phoenixSettings: fixture.Settings);
        Assert.True(File.Exists(Path.Combine(game.Root, "D3D9.dll")));
        Assert.False(File.Exists(Path.Combine(game.Root, "dxgi.dll")));
        Assert.True(File.Exists(Path.Combine(game.Root, "dlss5-feed.addon64")));
        Assert.False(File.Exists(Path.Combine(game.Root, "dlss5-feed.addon32")));
        Assert.True(File.Exists(Path.Combine(game.Root, "host64", "Phoenix.json")));
        Assert.Contains("render_scale=1.000", File.ReadAllText(Path.Combine(game.Root, "dlss5-feed.cfg")));
        await fixture.Engine.RestoreAsync(game.Root);
        Assert.False(File.Exists(Path.Combine(game.Root, "D3D9.dll")));
    }

    static string ExportDll(TestWorkspace workspace, string name, params string[] names)
    {
        var file = Dll(workspace, name, CpuArch.X64); var bytes = File.ReadAllBytes(file);
        U32(0x108, 0x1800); U32(0x10c, 40);
        U32(0xc00 + 24, (uint)names.Length); U32(0xc00 + 32, 0x1840);
        var offset = 0xc80;
        for (var i = 0; i < names.Length; i++)
        {
            U32(0xc40 + i * 4, (uint)(0x1800 + offset - 0xc00));
            var encoded = Encoding.ASCII.GetBytes(names[i] + "\0"); encoded.CopyTo(bytes, offset); offset += encoded.Length;
        }
        File.WriteAllBytes(file, bytes); return file;
        void U32(int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset), value);
    }

    static string Dll(TestWorkspace workspace, string name, CpuArch architecture)
    {
        var bytes = TestWorkspace.PeBytes(architecture, []);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(0x96), 0x2022);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0x98 + (architecture == CpuArch.X64 ? 108 : 92)), 16);
        var file = workspace.FilePath(name); Directory.CreateDirectory(Path.GetDirectoryName(file)!); File.WriteAllBytes(file, bytes); return file;
    }

    static string Phoenix(TestWorkspace workspace, string name)
    {
        var file = Dll(workspace, name, CpuArch.X64);
        TestWorkspace.SetPhoenixFeederExports(file);
        return file;
    }

    sealed class Fixture : IDisposable
    {
        public TestWorkspace Workspace { get; } = new();
        public GameCandidate Game { get; }
        public PayloadInfo Payload { get; set; }
        public PhoenixSettings Settings { get; }
        public InstallerEngine Engine { get; } = new(_ => { });
        public string ManifestPath => Path.Combine(Game.Root, "_DLSS5_Compat_Backup", "manifest.json");
        public Fixture()
        {
            var exe = Workspace.WritePe("game/game.exe", CpuArch.X64, "d3d12.dll");
            Game = new(Path.GetDirectoryName(exe)!, exe, "Fixture", "Fixture", CpuArch.X64, GraphicsApi.DirectX12, "fixture", 1, HasNativeDlssSignal: true);
            Phoenix(Workspace, "payload/Phoenix.addon64"); Dll(Workspace, "payload/ReShade64.dll", CpuArch.X64);
            Dll(Workspace, "payload/nvngx_dlss.dll", CpuArch.X64); Dll(Workspace, "payload/nvngx_dlssnr.dll", CpuArch.X64);
            var bundle = Workspace.FilePath("compiled-bundle"); Directory.CreateDirectory(bundle);
            var runtime = Workspace.FilePath("compiled-runtime"); Directory.CreateDirectory(runtime);
            File.WriteAllText(Path.Combine(runtime, "phoenix_runtime_cpp.dll"), "CPU fixture; not executable");
            File.WriteAllText(Path.Combine(runtime, "phoenix_native_cpp.json"), "{}");
            Settings = new() { BundlePath = bundle, RuntimeRoot = runtime };
            Payload = PayloadScanner.Scan(Workspace.FilePath("payload"));
        }
        public Task Install(InstallerEngine? engine = null) => (engine ?? Engine).InstallAsync(Game, Payload, phoenixSettings: Settings);
        public InstallManifest ReadManifest() => JsonSerializer.Deserialize<InstallManifest>(File.ReadAllText(ManifestPath))!;
        public void WriteManifest(InstallManifest manifest) => File.WriteAllText(ManifestPath, JsonSerializer.Serialize(manifest));
        public void LegacyJournal(params string[] relativePaths)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ManifestPath)!);
            foreach (var relative in relativePaths)
            {
                var backup = Path.Combine(Path.GetDirectoryName(ManifestPath)!, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
                File.Copy(Path.Combine(Game.Root, relative), backup);
            }
            WriteManifest(new() { Version = 1, GameRoot = Game.Root, ExeRelativePath = "game.exe", Route = "X64DirectRenoDx",
                Replaced = relativePaths.Select(path => new ManifestFile { RelativePath = path }).ToList() });
        }
        public void Dispose() => Workspace.Dispose();
    }
}
