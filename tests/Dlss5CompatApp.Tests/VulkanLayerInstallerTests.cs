using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Dlss5CompatApp.Tests;

public sealed class VulkanLayerInstallerTests
{
    const string Local = @"C:\Users\Test\AppData\Local\ReShade";
    const string Common = @"C:\ProgramData\ReShade";
    const string Game = @"C:\Games\First\game.exe";
    const string Source = @"C:\Payload\ReShade64.dll";
    static string Manifest => Path.Combine(Local, VulkanLayerInstaller.ManifestName);
    static string Library => Path.Combine(Local, VulkanLayerInstaller.LibraryName);
    static string State => Path.Combine(Local, VulkanLayerInstaller.StateName);
    static string Apps => Path.Combine(Local, "ReShadeApps.ini");
    readonly FakeFiles files = new();
    readonly FakeRegistry registry = new();
    VulkanLayerInstaller Service => new(files, registry, Local, Common);

    public VulkanLayerInstallerTests()
    {
        files.Put(Game, Pe(dll: false));
        files.Put(Source, Pe(dll: true));
    }

    [Fact]
    public async Task X86UsesMatchingLayerAndIndependentRestoreState()
    {
        byte[] X86(bool dll)
        {
            var bytes = Pe(dll);
            var header = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(0x3c));
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(header + 4), 0x14c);
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(header + 24), 0x10b);
            return bytes;
        }
        files.Put(Game, X86(false)); files.Put(Source, X86(true));
        var service = new VulkanLayerInstaller(files, registry, Local, Common, x86: true);
        var result = await service.InstallAsync(Game, Source);
        Assert.EndsWith("ReShade32_Dlss5Compat.json", result.ManifestPath);
        Assert.EndsWith("ReShade32_Dlss5Compat.dll", result.LibraryPath);
        Assert.EndsWith("Dlss5CompatVulkan32-state.json", result.RestoreStatePath);
        Assert.Contains("ReShade32_Dlss5Compat.dll", files.Text(result.ManifestPath));
        Assert.Null(files.Attributes(State));
        Assert.True(await service.RestoreAsync(Game));
        Assert.Null(files.Attributes(result.LibraryPath));
    }

    [Fact]
    public async Task MissingOwnedRegistrationDoesNotHideAnAvailableOfficialLayer()
    {
        registry.User64[Manifest] = 0; // Stale entry from an earlier removed private installation.
        var official = Path.Combine(Common, "ReShade64.json");
        files.PutText(official, "{\"file_format_version\":\"1.0.0\",\"layer\":{\"name\":\"VK_LAYER_reshade\",\"type\":\"GLOBAL\",\"library_path\":\"ReShade64.dll\"}}");
        files.Put(Path.Combine(Common, "ReShade64.dll"), Pe(true));
        files.PutText(Path.Combine(Common, "ReShadeApps.ini"), "Apps=" + Game + "\r\n");
        registry.Machine64[official] = 0;
        var result = await Service.InstallAsync(Game, Source);
        Assert.False(result.OwnsLayer);
        Assert.Equal(official, result.ManifestPath);
        Assert.Empty(registry.Writes);
    }

    [Fact]
    public async Task JournalCallbackRunsBeforeAnySharedLayerWrite()
    {
        var before = files.Snapshot();
        await Assert.ThrowsAsync<IOException>(() => Service.InstallAsync(Game, Source, beforeCommit: _ =>
            throw new IOException("Could not persist the owning game journal")));
        AssertSnapshot(before);
        Assert.Empty(registry.Writes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GameInstallerOwnsOnlyTheVulkanReferenceItAdds(bool x86)
    {
        using var workspace = new TestWorkspace();
        var arch = x86 ? CpuArch.X86 : CpuArch.X64;
        string Dll(string name, CpuArch machine, params string[] exports)
        {
            var path = workspace.WritePe(name, machine);
            var data = File.ReadAllBytes(path);
            BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(0x96), 0x2022);
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0x98 + (machine == CpuArch.X64 ? 108 : 92)), 16);
            File.WriteAllBytes(path, data); TestWorkspace.SetPhoenixExports(path, exports); return Path.GetFullPath(path);
        }
        var exe = Path.GetFullPath(workspace.WritePe("game/game.exe", arch, "vulkan-1.dll"));
        var executableBytes = File.ReadAllBytes(exe);
        BinaryPrimitives.WriteUInt16LittleEndian(executableBytes.AsSpan(0x96), 0x22);
        File.WriteAllBytes(exe, executableBytes);
        var addon = Dll("payload/Phoenix.addon64", CpuArch.X64, "CnrPHOENIXSummary", "CnrPHOENIXRegister",
            "CnrPHOENIXFeederVersion", "CnrPHOENIXFeederEvaluate", "CnrPHOENIXCompatibilityStatus", "AddonInit", "AddonUninit");
        TestWorkspace.SetEmbeddedPhoenixRuntime(addon);
        Dll("payload/Phoenix.addon32", CpuArch.X86, "CnrPHOENIXCompatibilityStatus", "AddonInit", "AddonUninit");
        var reshade = Dll("payload/ReShade" + (x86 ? "32" : "64") + ".dll", arch);
        Dll("payload/nvngx_dlssnr.dll", CpuArch.X64);
        files.Put(exe, File.ReadAllBytes(exe)); files.Put(reshade, File.ReadAllBytes(reshade));
        var layer = new VulkanLayerInstaller(files, registry, Local, Common, x86);
        var installer = new InstallerEngine(_ => { }, selectedX86 => { Assert.Equal(x86, selectedX86); return layer; });
        var game = GameScanner.ScanSingleExe(exe)!;
        var payload = PayloadScanner.Scan(workspace.FilePath("payload"));
        Assert.NotNull(PackagedPhoenixPlan.For(game, payload));
        await installer.InstallAsync(game, payload);
        var journal = Path.Combine(game.Root, "_DLSS5_Compat_Backup", "manifest.json");
        var installed = JsonSerializer.Deserialize<InstallManifest>(File.ReadAllText(journal))!;
        Assert.Equal(x86, installed.VulkanLayerX86);
        Assert.Contains(exe, files.Text(Apps));
        Assert.False(File.Exists(Path.Combine(game.Root, "dxgi.dll")));
        Assert.False(Directory.Exists(Path.Combine(game.Root, "host64")));
        var privateManifest = Path.Combine(game.Root, "_DLSS5_Vulkan", "ReShade.json");
        using (var document = JsonDocument.Parse(File.ReadAllText(privateManifest)))
            Assert.Equal(x86 ? Path.Combine(Local, "ReShade32_Dlss5Compat.dll") : Library,
                document.RootElement.GetProperty("layer").GetProperty("library_path").GetString());
        Assert.Contains("VK_ADD_IMPLICIT_LAYER_PATH=", File.ReadAllText(InstallerEngine.LauncherPathFor(game)));
        Assert.Equal(Path.GetDirectoryName(privateManifest), GameLauncher.Create(game, "").Environment["VK_ADD_IMPLICIT_LAYER_PATH"]);
        await installer.RestoreAsync(game.Root);
        Assert.False(File.Exists(privateManifest));
        Assert.Null(files.Attributes(Apps));
        Assert.False(File.Exists(Path.Combine(game.Root, x86 ? "Phoenix.addon32" : "Phoenix.addon64")));
    }

    [Fact]
    public async Task InstallsBoundX64OfficialManifestAndPerUserRegistration()
    {
        var original = files.Snapshot();
        var result = await Service.InstallAsync(Game, Source);
        Assert.True(result.OwnsLayer);
        Assert.True(result.ReferenceAdded);
        Assert.Equal(Game, result.GamePath);
        Assert.Equal(Manifest, result.ManifestPath);
        Assert.Equal(Library, result.LibraryPath);
        Assert.Equal(State, result.RestoreStatePath);
        Assert.Equal(0, registry.User64[Manifest]);
        Assert.Single(registry.Writes);
        Assert.Equal("HKCU64 " + Manifest + "=0", registry.Writes[0]);
        Assert.Empty(registry.User32);
        Assert.Empty(registry.Machine64);
        Assert.Equal(original[Source], files.Data[Library]);
        Assert.Equal(original[Source], files.Data[Source]);
        Assert.Equal(original[Game], files.Data[Game]);
        using var json = JsonDocument.Parse(files.Data[Manifest]);
        var layer = json.RootElement.GetProperty("layer");
        Assert.Equal("1.0.0", json.RootElement.GetProperty("file_format_version").GetString());
        Assert.Equal("VK_LAYER_reshade", layer.GetProperty("name").GetString());
        Assert.Equal("GLOBAL", layer.GetProperty("type").GetString());
        Assert.Equal(".\\" + VulkanLayerInstaller.LibraryName, layer.GetProperty("library_path").GetString());
        Assert.Equal("1", layer.GetProperty("disable_environment").GetProperty("DISABLE_VK_LAYER_reshade_1").GetString());
        Assert.Equal("VK_EXT_tooling_info", layer.GetProperty("device_extensions")[0].GetProperty("name").GetString());
        Assert.Equal("Apps=" + Game + "\r\n", files.Text(Apps));
    }

    [Fact]
    public async Task SharedReferencesKeepLayerUntilLastRestoreAndPreserveOtherValues()
    {
        var second = @"C:\Games\Second\game.exe";
        files.Put(second, Pe(dll: false));
        registry.User64[@"C:\Other\overlay.json"] = 0;
        registry.User32[@"C:\Other\ReShade32.json"] = 0;
        registry.Machine64[@"C:\Other\ReShade64.json"] = 1;
        registry.Machine32[@"C:\Other\ReShade32.json"] = 0;
        var before = registry.Snapshot();
        var unrelated = Path.Combine(Local, "user.txt");
        files.PutText(unrelated, "keep");
        await Service.InstallAsync(Game, Source);
        await Service.InstallAsync(second, Source);
        Assert.True(await Service.RestoreAsync(Game));
        Assert.True(files.Data.ContainsKey(Library));
        Assert.Equal(0, registry.User64[Manifest]);
        Assert.DoesNotContain(Game, files.Text(Apps));
        Assert.Contains(second, files.Text(Apps));
        Assert.True(await Service.RestoreAsync(second));
        Assert.False(files.Data.ContainsKey(Library));
        Assert.False(files.Data.ContainsKey(Manifest));
        Assert.False(files.Data.ContainsKey(State));
        Assert.False(files.Data.ContainsKey(Apps));
        Assert.Equal("keep", files.Text(unrelated));
        Assert.Equal(before, registry.Snapshot());
    }

    [Fact]
    public async Task RepeatedInstallDoesNotDuplicateReferences()
    {
        await Service.InstallAsync(Game, Source);
        var repeated = await Service.InstallAsync(Game.ToUpperInvariant(), Source);
        Assert.False(repeated.ReferenceAdded);
        Assert.Single(JsonNode.Parse(files.Text(State))!["Games"]!.AsArray());
        Assert.Equal("Apps=" + Game + "\r\n", files.Text(Apps));
        Assert.True(await Service.RestoreAsync(Game));
        Assert.False(files.Data.ContainsKey(State));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExistingAppsAndOtherIniSectionsSurviveRestore(bool gameAlreadyListed)
    {
        var text = "; user comment\r\nApps=C:\\Games\\User\\existing.exe" + (gameAlreadyListed ? "," + Game : "") +
            "\r\nOther=keep\r\n[Custom]\r\nApps=unrelated\r\n";
        files.PutText(Apps, text);
        await Service.InstallAsync(Game, Source);
        Assert.Contains("Other=keep\r\n[Custom]\r\nApps=unrelated", files.Text(Apps));
        await Service.RestoreAsync(Game);
        Assert.Equal(text, files.Text(Apps));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReusesOfficialRegisteredLayerWithoutCopyOrRegistryChanges(bool common)
    {
        var root = common ? Common : Local;
        var officialManifest = Path.Combine(root, "ReShade64.json");
        var officialDll = Path.Combine(root, "ReShade64.dll");
        files.Put(officialDll, Pe(dll: true));
        files.PutText(officialManifest, OfficialManifest(".\\ReShade64.dll"));
        if (common) files.PutText(Path.Combine(Common, "ReShadeApps.ini"), "Apps=" + Game + "\r\n");
        (common ? registry.Machine64 : registry.User64)[officialManifest] = 0;
        var before = files.Snapshot();
        var beforeRegistry = registry.Snapshot();
        var result = await Service.InstallAsync(Game, Source);
        Assert.False(result.OwnsLayer);
        Assert.Equal(officialManifest, result.ManifestPath);
        Assert.Equal(officialDll, result.LibraryPath);
        Assert.Empty(registry.Writes);
        Assert.False(files.Data.ContainsKey(Library));
        await Service.RestoreAsync(Game);
        AssertSnapshot(before);
        Assert.Equal(beforeRegistry, registry.Snapshot());
        Assert.Empty(registry.Writes);
    }

    [Fact]
    public async Task NewGameUsesPerUserLayerWithoutWritingTheSystemAllowList()
    {
        var official = Path.Combine(Common, "ReShade64.json");
        var commonApps = Path.Combine(Common, "ReShadeApps.ini");
        var officialJson = JsonNode.Parse(OfficialManifest(".\\ReShade64.dll"))!;
        officialJson["layer"]!["disable_environment"] = new JsonObject { ["DISABLE_VK_LAYER_reshade_1"] = "1" };
        files.PutText(official, officialJson.ToJsonString());
        files.Put(Path.Combine(Common, "ReShade64.dll"), Pe(true));
        files.PutText(commonApps, "Apps=C:\\Games\\Existing\\game.exe\r\n");
        files.Flags[commonApps] = FileAttributes.ReadOnly;
        registry.Machine64[official] = 0;
        var previous = files.ReadBounded(commonApps, 10000);
        var layer = await Service.InstallAsync(Game, Source);
        Assert.True(layer.OwnsLayer);
        Assert.Equal(Manifest, layer.ManifestPath);
        Assert.Equal(previous, files.ReadBounded(commonApps, 10000));
        Assert.DoesNotContain(files.Writes, path => path.StartsWith(Common, StringComparison.OrdinalIgnoreCase));
        // Both official and private manifests use the same disable control.
        // Reinstallation must keep it unset; the game-local manifest wins by priority.
        var again = await Service.InstallAsync(Game, Source);
        Assert.True(again.OwnsLayer);
        Assert.Empty(again.LaunchEnvironment);
        Assert.True(await Service.RestoreAsync(Game));
        Assert.Equal(previous, files.ReadBounded(commonApps, 10000));
        Assert.Equal(0, registry.Machine64[official]);
    }

    [Fact]
    public async Task DisabledPreviousOwnedRegistryValueIsRestoredExactly()
    {
        registry.User64[Manifest] = 1;
        await Service.InstallAsync(Game, Source);
        Assert.Equal(0, registry.User64[Manifest]);
        await Service.RestoreAsync(Game);
        Assert.Equal(1, registry.User64[Manifest]);
    }

    [Theory]
    [InlineData("x86-game")]
    [InlineData("x86-source")]
    [InlineData("dll-game")]
    [InlineData("exe-source")]
    [InlineData("text-source")]
    [InlineData("truncated-source")]
    [InlineData("oversized-source")]
    [InlineData("32-bit-os")]
    public async Task InvalidArchitectureOrFilesFailBeforeAnyMutation(string kind)
    {
        if (kind == "x86-game") files.Put(Game, Pe(dll: false, x86: true));
        if (kind == "x86-source") files.Put(Source, Pe(dll: true, x86: true));
        if (kind == "dll-game") files.Put(Game, Pe(dll: true));
        if (kind == "exe-source") files.Put(Source, Pe(dll: false));
        if (kind == "text-source") files.PutText(Source, "not ReShade");
        if (kind == "truncated-source") files.Put(Source, Pe(dll: true)[..100]);
        if (kind == "oversized-source") files.Oversized.Add(Source);
        if (kind == "32-bit-os") files.Is64BitOperatingSystem = false;
        var before = files.Snapshot();
        await Assert.ThrowsAnyAsync<Exception>(() => Service.InstallAsync(Game, Source));
        AssertSnapshot(before);
        Assert.Empty(files.Writes);
        Assert.Empty(registry.Writes);
    }

    [Theory]
    [InlineData("relative.exe")]
    [InlineData(@"\\server\share\game.exe")]
    [InlineData(@"C:\Games\..\outside.exe")]
    [InlineData(@"C:\Games\game.exe:stream")]
    [InlineData(@"C:\Games\with,comma\game.exe")]
    [InlineData(@"C:\Games\trailing.\game.exe")]
    [InlineData("C:\\Games\\bad\n\\game.exe")]
    public async Task UnsafeGamePathFailsWithoutChanges(string path)
    {
        var before = files.Snapshot();
        await Assert.ThrowsAnyAsync<ArgumentException>(() => Service.InstallAsync(path, Source));
        AssertSnapshot(before);
        Assert.Empty(files.Writes);
        Assert.Empty(registry.Writes);
    }

    [Theory]
    [InlineData("game-parent")]
    [InlineData("source")]
    [InlineData("layer-root")]
    [InlineData("manifest")]
    [InlineData("apps")]
    [InlineData("state")]
    public async Task ReparsePathsRejectedBeforeMutation(string kind)
    {
        var path = kind switch
        {
            "game-parent" => Path.GetDirectoryName(Game)!, "source" => Source, "layer-root" => Local,
            "manifest" => Manifest, "apps" => Apps, _ => State
        };
        files.Flags[path] = FileAttributes.ReparsePoint | (kind is "game-parent" or "layer-root" ? FileAttributes.Directory : 0);
        var before = files.Snapshot();
        await Assert.ThrowsAsync<IOException>(() => Service.InstallAsync(Game, Source));
        AssertSnapshot(before);
        Assert.Empty(files.Writes);
        Assert.Empty(registry.Writes);
    }

    [Fact]
    public async Task KnownDuplicateLayersAreIsolatedOnlyInTheGameLauncherEnvironment()
    {
        var primary = Path.Combine(Common, "ReShade64.json");
        void Layer(string path, string control)
        {
            files.PutText(path, JsonSerializer.Serialize(new { file_format_version = "1.0.0", layer = new {
                name = "VK_LAYER_reshade", type = "GLOBAL", library_path = "ReShade64.dll",
                disable_environment = new Dictionary<string, string> { [control] = "1" } } }));
            files.Put(Path.Combine(Path.GetDirectoryName(path)!, "ReShade64.dll"), Pe(true));
        }
        Layer(primary, "DISABLE_OFFICIAL"); Layer(Manifest, "DISABLE_OLD_PRIVATE");
        registry.Machine64[primary] = 0; registry.User64[Manifest] = 0;
        var previous = files.ReadBounded(Manifest, 10000);
        var result = await Service.InstallAsync(Game, Source);
        Assert.False(result.OwnsLayer);
        Assert.Equal(Manifest, result.ManifestPath);
        Assert.Equal("1", Assert.Single(result.LaunchEnvironment).Value);
        Assert.Equal("DISABLE_OFFICIAL", Assert.Single(result.LaunchEnvironment).Key);
        Assert.Equal(previous, files.ReadBounded(Manifest, 10000));
        Assert.Empty(registry.Writes);
        var again = await Service.InstallAsync(Game, Source);
        Assert.Equal(result.LaunchEnvironment, again.LaunchEnvironment);
        Assert.True(await Service.RestoreAsync(Game));
        Assert.Equal(previous, files.ReadBounded(Manifest, 10000));
        Assert.Empty(registry.Writes);
    }

    [Fact]
    public async Task ForeignOrMultipleEnabledLayersAreNotDisabledOrReplaced()
    {
        var before = files.Snapshot();
        registry.User64[@"C:\Foreign\ReShade64.json"] = 0;
        await Assert.ThrowsAsync<InvalidOperationException>(() => Service.InstallAsync(Game, Source));
        registry.Machine64[Path.Combine(Common, "ReShade64.json")] = 0;
        await Assert.ThrowsAsync<InvalidOperationException>(() => Service.InstallAsync(Game, Source));
        AssertSnapshot(before);
        Assert.Empty(registry.Writes);
    }

    [Theory]
    [InlineData("dll")]
    [InlineData("manifest")]
    [InlineData("registry-kind")]
    public async Task UntrackedOwnedPathsAndWrongRegistryKindsAreNeverOverwritten(string kind)
    {
        if (kind == "dll") files.PutText(Library, "user DLL");
        if (kind == "manifest") files.PutText(Manifest, "user manifest");
        if (kind == "registry-kind") registry.User64[Manifest] = "0";
        var before = files.Snapshot();
        var beforeRegistry = registry.Snapshot();
        if (kind == "registry-kind") await Assert.ThrowsAsync<InvalidDataException>(() => Service.InstallAsync(Game, Source));
        else await Assert.ThrowsAsync<IOException>(() => Service.InstallAsync(Game, Source));
        AssertSnapshot(before);
        Assert.Equal(beforeRegistry, registry.Snapshot());
        Assert.Empty(files.Writes);
        Assert.Empty(registry.Writes);
    }

    [Fact]
    public async Task RegistryFailureRollsBackEveryFileAndPreviousValue()
    {
        files.PutText(Apps, "; keep\r\nApps=C:\\User\\game.exe\r\n");
        registry.User64[Manifest] = 1;
        var before = files.Snapshot();
        var beforeRegistry = registry.Snapshot();
        registry.FailNextWrite = true;
        await Assert.ThrowsAsync<IOException>(() => Service.InstallAsync(Game, Source));
        AssertSnapshot(before);
        Assert.Equal(beforeRegistry, registry.Snapshot());
    }

    [Theory]
    [InlineData("after-read")]
    [InlineData("after-pending")]
    [InlineData("before-publish")]
    public async Task ConcurrentOfficialInstallerAdditionBeforeWriteIsPreserved(string timing)
    {
        var original = "Apps=C:\\Games\\Existing\\game.exe\r\n";
        var external = "Apps=C:\\Games\\Existing\\game.exe,C:\\Games\\Official\\game.exe\r\n";
        files.PutText(Apps, original);
        var edited = false;
        void EditOnce(string path)
        {
            if (edited) return;
            if ((timing == "after-pending" && path == State) || (timing != "after-pending" && path == Apps))
            {
                edited = true;
                files.PutText(Apps, external);
            }
        }
        if (timing == "after-read") files.AfterRead = EditOnce;
        else if (timing == "after-pending") files.AfterWrite = EditOnce;
        else files.BeforeReplace = EditOnce;
        await Assert.ThrowsAnyAsync<Exception>(() => Service.InstallAsync(Game, Source));
        Assert.True(edited);
        Assert.Equal(external, files.Text(Apps));
        Assert.Empty(registry.User64);
        Assert.False(files.Data.ContainsKey(Library));
        Assert.False(files.Data.ContainsKey(Manifest));
        if (files.Data.ContainsKey(State))
        {
            Assert.True(await Service.RestoreAsync(Game));
            Assert.Equal(external, files.Text(Apps));
        }
    }

    [Fact]
    public async Task ConcurrentCreationOfAppsFileIsNotOverwrittenOrDeletedByRollback()
    {
        var external = "Apps=C:\\Games\\Official\\game.exe\r\n";
        var edited = false;
        files.BeforeReplace = path =>
        {
            if (path == Apps && !edited) { edited = true; files.PutText(Apps, external); }
        };
        await Assert.ThrowsAnyAsync<Exception>(() => Service.InstallAsync(Game, Source));
        Assert.Equal(external, files.Text(Apps));
        Assert.Empty(registry.User64);
        Assert.True(await Service.RestoreAsync(Game));
        Assert.Equal(external, files.Text(Apps));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OutsideEditsAfterOurWriteSurviveConditionalRollbackAndRecovery(bool duringRollback)
    {
        files.PutText(Apps, "Apps=C:\\Games\\Existing\\game.exe\r\n");
        string? external = null;
        var edits = 0;
        if (duringRollback)
            files.BeforeReplace = path =>
            {
                if (path == Apps && ++edits == 2)
                {
                    external = files.Text(Apps).Replace("\r\n", ",C:\\Games\\Official\\game.exe\r\n");
                    files.PutText(Apps, external);
                }
            };
        else
            files.AfterWrite = path =>
            {
                if (path == Apps && edits++ == 0)
                {
                    external = files.Text(Apps).Replace("\r\n", ",C:\\Games\\Official\\game.exe\r\n");
                    files.PutText(Apps, external);
                }
            };
        registry.FailNextWrite = true;
        await Assert.ThrowsAsync<AggregateException>(() => Service.InstallAsync(Game, Source));
        Assert.NotNull(external);
        Assert.Equal(external, files.Text(Apps));
        Assert.True(JsonNode.Parse(files.Text(State))!["Pending"]!.GetValue<bool>());
        files.AfterWrite = null;
        files.BeforeReplace = null;
        Assert.True(await Service.RestoreAsync(Game));
        Assert.Contains(@"C:\Games\Official\game.exe", files.Text(Apps));
        Assert.Contains(@"C:\Games\Existing\game.exe", files.Text(Apps));
        Assert.DoesNotContain(Game, files.Text(Apps));
        Assert.False(files.Data.ContainsKey(State));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConcurrentAllowListAdditionDuringRestoreIsNotLost(bool deleteApps)
    {
        if (!deleteApps) files.PutText(Apps, "Apps=C:\\Games\\Existing\\game.exe\r\n");
        await Service.InstallAsync(Game, Source);
        string? external = null;
        files.BeforeReplace = path =>
        {
            if (path == Apps && external is null)
            {
                external = files.Text(Apps).Replace("\r\n", ",C:\\Games\\Official\\game.exe\r\n");
                files.PutText(Apps, external);
            }
        };
        await Assert.ThrowsAnyAsync<Exception>(() => Service.RestoreAsync(Game));
        Assert.Equal(external, files.Text(Apps));
        Assert.Equal(0, registry.User64[Manifest]);
        Assert.True(files.Data.ContainsKey(Library));
        files.BeforeReplace = null;
        Assert.True(await Service.RestoreAsync(Game));
        Assert.Contains(@"C:\Games\Official\game.exe", files.Text(Apps));
        Assert.DoesNotContain(Game, files.Text(Apps));
    }

    [Fact]
    public async Task RollbackNeverOverwritesExternallyEditedRestoreState()
    {
        using var token = new CancellationTokenSource();
        var external = "external restore state";
        files.AfterWrite = path =>
        {
            if (path == Apps) { files.PutText(State, external); token.Cancel(); }
        };
        await Assert.ThrowsAsync<AggregateException>(() => Service.InstallAsync(Game, Source, token.Token));
        Assert.Equal(external, files.Text(State));
        Assert.Empty(registry.User64);
    }

    [Theory]
    [InlineData("library")]
    [InlineData("manifest")]
    [InlineData("apps")]
    [InlineData("state")]
    public async Task FileWriteFailureRollsBackInstallation(string kind)
    {
        var before = files.Snapshot();
        files.FailNextWriteTo = kind switch { "library" => Library, "manifest" => Manifest, "apps" => Apps, _ => State };
        await Assert.ThrowsAsync<IOException>(() => Service.InstallAsync(Game, Source));
        AssertSnapshot(before);
        Assert.Empty(registry.User64);
    }

    [Fact]
    public async Task CancellationDuringInstallRollsBackWithoutRegistration()
    {
        using var token = new CancellationTokenSource();
        var before = files.Snapshot();
        files.AfterWrite = path => { if (path == Apps) token.Cancel(); };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Service.InstallAsync(Game, Source, token.Token));
        AssertSnapshot(before);
        Assert.DoesNotContain(registry.Writes, write => write.EndsWith("=0"));
    }

    [Fact]
    public async Task PreCancelledInstallOrRestoreHasNoSideEffects()
    {
        var before = files.Snapshot();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Service.InstallAsync(Game, Source, new CancellationToken(true)));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Service.RestoreAsync(Game, new CancellationToken(true)));
        AssertSnapshot(before);
        Assert.Empty(files.Writes);
        Assert.Empty(registry.Writes);
    }

    [Fact]
    public async Task PreexistingEmptyAppsFileIsRestoredRatherThanDeleted()
    {
        files.PutText(Apps, "Apps=\r\n");
        await Service.InstallAsync(Game, Source);
        await Service.RestoreAsync(Game);
        Assert.Equal("Apps=\r\n", files.Text(Apps));
    }

    [Theory]
    [InlineData(100L * 1024 * 1024)]
    [InlineData(3L * 1024 * 1024 * 1024)]
    public async Task LargeGameUsesOnlyBoundedPeHeaders(long executableLength)
    {
        files.VirtualLengths[Game] = executableLength;
        files.Oversized.Add(Game); // Reading the whole executable would fail this fixture.
        await Service.InstallAsync(Game, Source);
        Assert.Equal(new[] { Game + ":65536" }, files.HeaderReads);
        Assert.DoesNotContain(Game, files.FullReads);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RestoreFailureRollsBackFilesAndEnabledRegistration(bool registryFailure)
    {
        await Service.InstallAsync(Game, Source);
        var before = files.Snapshot();
        var beforeRegistry = registry.Snapshot();
        if (registryFailure) registry.FailNextWrite = true;
        else files.FailNextDeleteTo = Library;
        await Assert.ThrowsAsync<IOException>(() => Service.RestoreAsync(Game));
        AssertSnapshot(before);
        Assert.Equal(beforeRegistry, registry.Snapshot());
    }

    [Fact]
    public async Task IncompleteRollbackRetainsPendingRecoveryState()
    {
        files.FailNextWriteTo = Manifest;
        files.FailNextDeleteTo = Library;
        await Assert.ThrowsAsync<AggregateException>(() => Service.InstallAsync(Game, Source));
        Assert.True(JsonNode.Parse(files.Text(State))!["Pending"]!.GetValue<bool>());
        Assert.True(files.Data.ContainsKey(Library));
        Assert.True(await Service.RestoreAsync(Game));
        Assert.False(files.Data.ContainsKey(Library));
        Assert.False(files.Data.ContainsKey(State));
        Assert.Empty(registry.User64);
    }

    [Fact]
    public async Task CommitFailureDisablesNewLayerBeforeRollbackDeletesDll()
    {
        var before = files.Snapshot();
        var stateWrites = 0;
        files.AfterWrite = path =>
        {
            if (path == State && ++stateWrites == 2) throw new IOException("Simulated commit failure.");
        };
        await Assert.ThrowsAsync<IOException>(() => Service.InstallAsync(Game, Source));
        AssertSnapshot(before);
        Assert.Equal(new[] { "HKCU64 " + Manifest + "=0", "HKCU64 " + Manifest + "=1", "HKCU64 " + Manifest + "=deleted" }, registry.Writes);
    }

    [Fact]
    public async Task PendingInstallNeedsRestoreAndCanRecoverMissingOwnedFiles()
    {
        await Service.InstallAsync(Game, Source);
        var state = JsonNode.Parse(files.Text(State))!;
        state["Pending"] = true;
        files.PutText(State, state.ToJsonString());
        files.Data.Remove(Library);
        registry.User64.Remove(Manifest);
        var before = files.Snapshot();
        await Assert.ThrowsAsync<InvalidOperationException>(() => Service.InstallAsync(Game, Source));
        AssertSnapshot(before);
        Assert.True(await Service.RestoreAsync(Game));
        Assert.False(files.Data.ContainsKey(Manifest));
        Assert.False(files.Data.ContainsKey(State));
    }

    [Theory]
    [InlineData("library")]
    [InlineData("manifest")]
    [InlineData("registry")]
    [InlineData("apps-duplicate")]
    public async Task RestoreRejectsExternallyChangedOwnershipBeforeMutation(string kind)
    {
        await Service.InstallAsync(Game, Source);
        if (kind == "library") files.Data[Library][^1] ^= 0xff;
        if (kind == "manifest") files.PutText(Manifest, "changed");
        if (kind == "registry") registry.User64[Manifest] = 1;
        if (kind == "apps-duplicate") files.PutText(Apps, "Apps=" + Game + "\r\nApps=C:\\Other\\game.exe\r\n");
        var before = files.Snapshot();
        var beforeRegistry = registry.Snapshot();
        if (kind == "apps-duplicate") await Assert.ThrowsAsync<InvalidDataException>(() => Service.RestoreAsync(Game));
        else await Assert.ThrowsAsync<IOException>(() => Service.RestoreAsync(Game));
        AssertSnapshot(before);
        Assert.Equal(beforeRegistry, registry.Snapshot());
    }

    [Fact]
    public async Task DifferentSourceCannotOverwriteSharedDll()
    {
        await Service.InstallAsync(Game, Source);
        files.Data[Source][^1] ^= 0xff;
        var before = files.Snapshot();
        await Assert.ThrowsAsync<IOException>(() => Service.InstallAsync(Game, Source));
        AssertSnapshot(before);
    }

    [Fact]
    public async Task RestoreWorksAfterGameAndSourceAreRemoved()
    {
        await Service.InstallAsync(Game, Source);
        files.Data.Remove(Game);
        files.Data.Remove(Source);
        Assert.True(await Service.RestoreAsync(Game));
        Assert.Empty(files.Data);
    }

    [Fact]
    public async Task UnknownGameRestoreDoesNothing()
    {
        var before = files.Snapshot();
        Assert.False(await Service.RestoreAsync(Game));
        AssertSnapshot(before);
        Assert.Empty(files.Writes);
        Assert.Empty(registry.Writes);
    }

    [Theory]
    [InlineData("invalid-json")]
    [InlineData("unknown-field")]
    [InlineData("wrong-version")]
    [InlineData("duplicate-property")]
    [InlineData("unsafe-game")]
    public async Task CorruptStateIsNotTreatedAsNoOwnership(string kind)
    {
        await Service.InstallAsync(Game, Source);
        var state = JsonNode.Parse(files.Text(State))!;
        if (kind == "unknown-field") state["ArbitraryPath"] = "C:\\outside";
        if (kind == "wrong-version") state["Version"] = 99;
        if (kind == "unsafe-game") state["Games"]![0]!["Path"] = "..\\outside.exe";
        files.PutText(State, kind == "invalid-json" ? "invalid" : kind == "duplicate-property" ? "{\"Version\":1,\"Version\":1}" : state.ToJsonString());
        var before = files.Snapshot();
        var beforeRegistry = registry.Snapshot();
        await Assert.ThrowsAnyAsync<Exception>(() => Service.RestoreAsync(Game));
        AssertSnapshot(before);
        Assert.Equal(beforeRegistry, registry.Snapshot());
    }

    [Fact]
    public async Task ExistingManifestCannotPointToDifferentArchitectureDirectory()
    {
        var manifest = Path.Combine(Common, "ReShade64.json");
        files.PutText(manifest, OfficialManifest(@"C:\Other\ReShade32.dll"));
        files.Put(Path.Combine(Common, "ReShade64.dll"), Pe(dll: true));
        registry.Machine64[manifest] = 0;
        var before = files.Snapshot();
        await Assert.ThrowsAsync<InvalidDataException>(() => Service.InstallAsync(Game, Source));
        AssertSnapshot(before);
        Assert.Empty(files.Writes);
        Assert.Empty(registry.Writes);
    }

    [Theory]
    [InlineData("create")]
    [InlineData("update")]
    [InlineData("delete")]
    public async Task RealAdapterBlocksOutsideWriteRenameDeleteAndParentRenameAfterVerification(string operation)
    {
        using var workspace = new TestWorkspace();
        var parent = workspace.FilePath("layer");
        Directory.CreateDirectory(parent);
        var path = Path.Combine(parent, "ReShadeApps.ini");
        byte[]? before = operation == "create" ? null : Encoding.UTF8.GetBytes("Apps=original-game.exe\r\n");
        byte[]? after = operation == "delete" ? null : Encoding.UTF8.GetBytes("Apps=new.exe\r\n");
        if (before is not null) File.WriteAllBytes(path, before);
        var verified = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var adapter = new VulkanLayerFiles(verifiedPath =>
        {
            Assert.Equal(path, verifiedPath);
            verified.SetResult();
            if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Real adapter contention test did not release its handle.");
        });
        var update = Task.Run(() => adapter.ReplaceIfUnchanged(path, before, after));
        try
        {
            await verified.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Throws<IOException>(() => File.WriteAllText(path, "outside edit"));
            Assert.Throws<IOException>(() => File.Move(path, path + ".moved"));
            Assert.Throws<IOException>(() => File.Delete(path));
            Assert.Throws<IOException>(() => Directory.Move(parent, parent + ".moved"));
        }
        finally { release.Set(); }
        await update.WaitAsync(TimeSpan.FromSeconds(10));
        if (after is null) Assert.False(File.Exists(path));
        else Assert.Equal(after, File.ReadAllBytes(path));
        Assert.Equal(after is null ? Array.Empty<string>() : new[] { path }, Directory.GetFiles(parent));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RealAdapterRejectsOutsideEditBeforeExclusiveOpenWithoutMutation(bool delete)
    {
        using var workspace = new TestWorkspace();
        var path = workspace.FilePath("ReShadeApps.ini");
        var baseline = Encoding.UTF8.GetBytes("Apps=old.exe\r\n");
        var outside = Encoding.UTF8.GetBytes("Apps=new.exe\r\n");
        File.WriteAllBytes(path, outside);
        Assert.Throws<IOException>(() => new VulkanLayerFiles().ReplaceIfUnchanged(path, baseline, delete ? null : baseline));
        Assert.Equal(outside, File.ReadAllBytes(path));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RealAdapterRefusesExistingWriterAndPreservesItsLaterEdit(bool delete)
    {
        using var workspace = new TestWorkspace();
        var path = workspace.FilePath("ReShadeApps.ini");
        var baseline = Encoding.UTF8.GetBytes("Apps=old.exe\r\n");
        var outside = Encoding.UTF8.GetBytes("Apps=external-game.exe\r\n");
        File.WriteAllBytes(path, baseline);
        using (var writer = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete))
        {
            Assert.Throws<IOException>(() => new VulkanLayerFiles().ReplaceIfUnchanged(path, baseline, delete ? null : baseline));
            writer.Position = 0;
            writer.Write(outside);
            writer.SetLength(outside.Length);
            writer.Flush(true);
        }
        Assert.Equal(outside, File.ReadAllBytes(path));
    }

    [Fact]
    public void RealAdapterCreateNewNeverOverwritesAnInterveningCreation()
    {
        using var workspace = new TestWorkspace();
        var path = workspace.FilePath("ReShadeApps.ini");
        File.WriteAllText(path, "outside creation");
        Assert.Throws<IOException>(() => new VulkanLayerFiles().ReplaceIfUnchanged(path, null, Encoding.UTF8.GetBytes("ours")));
        Assert.Equal("outside creation", File.ReadAllText(path));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RealAdapterVerificationHookFailurePreservesOriginalOrDeletesOnlyItsNewFile(bool create)
    {
        using var workspace = new TestWorkspace();
        var path = workspace.FilePath("ReShadeApps.ini");
        byte[]? before = create ? null : Encoding.UTF8.GetBytes("Apps=original.exe\r\n");
        if (before is not null) File.WriteAllBytes(path, before);
        var adapter = new VulkanLayerFiles(_ => throw new IOException("Injected failure while exclusively held."));
        Assert.Throws<IOException>(() => adapter.ReplaceIfUnchanged(path, before, Encoding.UTF8.GetBytes("ours")));
        if (create) Assert.False(File.Exists(path));
        else Assert.Equal(before, File.ReadAllBytes(path));
    }

    void AssertSnapshot(Dictionary<string, byte[]> expected)
    {
        Assert.Equal(expected.Keys.Order(StringComparer.OrdinalIgnoreCase), files.Data.Keys.Order(StringComparer.OrdinalIgnoreCase));
        foreach (var (path, data) in expected) Assert.Equal(data, files.Data[path]);
    }

    static string OfficialManifest(string library) => JsonSerializer.Serialize(new
    {
        file_format_version = "1.0.0", layer = new { name = "VK_LAYER_reshade", type = "GLOBAL", library_path = library }
    });

    static byte[] Pe(bool dll, bool x86 = false)
    {
        var bytes = TestWorkspace.PeBytes(x86 ? CpuArch.X86 : CpuArch.X64, []);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(0x96), dll ? (ushort)0x2002 : (ushort)2);
        return bytes;
    }

    sealed class FakeFiles : IVulkanLayerFiles
    {
        public Dictionary<string, byte[]> Data { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, FileAttributes> Flags { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> Oversized { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, long> VirtualLengths { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<string> HeaderReads { get; } = [];
        public List<string> FullReads { get; } = [];
        public List<string> Writes { get; } = [];
        public bool Is64BitOperatingSystem { get; set; } = true;
        public string? FailNextWriteTo { get; set; }
        public string? FailNextDeleteTo { get; set; }
        public Action<string>? AfterWrite { get; set; }
        public Action<string>? BeforeReplace { get; set; }
        public Action<string>? AfterRead { get; set; }
        public void Put(string path, byte[] data) => Data[path] = (byte[])data.Clone();
        public void PutText(string path, string text) => Put(path, Encoding.UTF8.GetBytes(text));
        public string Text(string path) => Encoding.UTF8.GetString(Data[path]);
        public Dictionary<string, byte[]> Snapshot() => Data.ToDictionary(pair => pair.Key, pair => (byte[])pair.Value.Clone(), StringComparer.OrdinalIgnoreCase);
        public void RequireLocalDrive(string path) { }
        public FileAttributes? Attributes(string path)
        {
            if (Flags.TryGetValue(path, out var flags)) return flags;
            if (Data.ContainsKey(path)) return FileAttributes.Normal;
            if (Data.Keys.Any(p => p.StartsWith(path.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))) return FileAttributes.Directory;
            return null;
        }
        public byte[] ReadBounded(string path, int limit)
        {
            FullReads.Add(path);
            if (!Data.TryGetValue(path, out var bytes)) throw new FileNotFoundException(path);
            if (bytes.Length > limit || Oversized.Contains(path)) throw new InvalidDataException("Oversized test file.");
            var result = (byte[])bytes.Clone();
            AfterRead?.Invoke(path);
            return result;
        }
        public VulkanPeHeaders ReadPeHeaders(string path, int limit)
        {
            HeaderReads.Add(path + ":" + limit);
            if (!Data.TryGetValue(path, out var bytes)) throw new FileNotFoundException(path);
            return new(bytes[..Math.Min(bytes.Length, limit)], VirtualLengths.GetValueOrDefault(path, bytes.Length));
        }
        public void ReplaceIfUnchanged(string path, byte[]? expected, byte[]? data)
        {
            BeforeReplace?.Invoke(path);
            var current = Data.GetValueOrDefault(path);
            if (expected is null ? current is not null : current is null || !current.AsSpan().SequenceEqual(expected))
                throw new IOException("Simulated external edit before publication.");
            if (data is null) { DeleteFile(path); return; }
            if (string.Equals(FailNextWriteTo, path, StringComparison.OrdinalIgnoreCase)) { FailNextWriteTo = null; throw new IOException("Simulated file failure."); }
            Writes.Add("write " + path);
            Put(path, data);
            AfterWrite?.Invoke(path);
        }
        public void DeleteFile(string path)
        {
            if (string.Equals(FailNextDeleteTo, path, StringComparison.OrdinalIgnoreCase)) { FailNextDeleteTo = null; throw new IOException("Simulated deletion failure."); }
            Writes.Add("delete " + path);
            Data.Remove(path);
        }
        public IDisposable Lock(string identity) => new Lease();
        sealed class Lease : IDisposable { public void Dispose() { } }
    }

    sealed class FakeRegistry : IVulkanLayerRegistry
    {
        public Dictionary<string, object> User64 { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, object> User32 { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, object> Machine64 { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, object> Machine32 { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<string> Writes { get; } = [];
        public bool FailNextWrite { get; set; }
        public string[] Snapshot() => new[] { User64, User32, Machine64, Machine32 }.SelectMany((table, i) =>
            table.Select(pair => i + ":" + pair.Key + "=" + pair.Value + ":" + pair.Value.GetType().Name)).Order().ToArray();
        public IReadOnlyList<string> EnabledReShade64Manifests() => User64.Concat(Machine64)
            .Where(pair => Path.GetFileName(pair.Key).Contains("reshade", StringComparison.OrdinalIgnoreCase) && pair.Value is int number && number == 0)
            .Select(pair => pair.Key).ToArray();
        public int? ReadCurrentUser64(string manifestPath)
        {
            if (!User64.TryGetValue(manifestPath, out var value)) return null;
            if (value is not int number) throw new InvalidDataException("Not REG_DWORD.");
            return number;
        }
        public void WriteCurrentUser64(string manifestPath, int? value)
        {
            if (FailNextWrite) { FailNextWrite = false; throw new IOException("Simulated registry failure."); }
            Writes.Add("HKCU64 " + manifestPath + "=" + (value?.ToString() ?? "deleted"));
            if (value is { } number) User64[manifestPath] = number;
            else User64.Remove(manifestPath);
        }
    }
}
