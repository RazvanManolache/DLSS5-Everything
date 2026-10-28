using System.Buffers.Binary;

namespace Dlss5CompatApp.Tests;

public sealed class PackagedPhoenixTests
{
    [Fact]
    public void Direct3DImmediateCompanionUsesItsActualInterfaceNotComServerExports()
    {
        using var workspace=new TestWorkspace();
        var valid=Dll(workspace,"valid/D3DImm.dll",CpuArch.X86,"GetD3DInterface","GetD3DVersion");
        var unrelated=Dll(workspace,"other/D3DImm.dll",CpuArch.X86,"DllGetClassObject","DllCanUnloadNow");
        Assert.True(PackagedPhoenixPlan.HasDgVoodooInterface(valid));
        Assert.False(PackagedPhoenixPlan.HasDgVoodooInterface(unrelated));
        Assert.False(PackagedPhoenixPlan.IsExistingDgVoodoo(valid,CpuArch.X86)); // Vendor metadata is still required.
    }
    [Theory]
    [InlineData((int)GraphicsApi.DirectX7OrOlder,(int)InstallRoute.X86DirectXLegacyViaDgVoodoo)]
    [InlineData((int)GraphicsApi.DirectX8,(int)InstallRoute.X86Dx8ViaDgVoodoo)]
    [InlineData((int)GraphicsApi.DirectX9,(int)InstallRoute.X86Dx9ViaDgVoodoo)]
    [InlineData((int)GraphicsApi.Glide211,(int)InstallRoute.X86Glide211ViaDgVoodoo)]
    [InlineData((int)GraphicsApi.Glide245,(int)InstallRoute.X86Glide245ViaDgVoodoo)]
    [InlineData((int)GraphicsApi.Glide31,(int)InstallRoute.X86Glide31ViaDgVoodoo)]
    public void WrapperPresentationRetainsItsOriginalTranslationRequirement(int input,int route)
    {
        using var workspace=new TestWorkspace();
        var exe=workspace.WritePe("game.exe",CpuArch.X86);
        Dll(workspace,"payload/Phoenix.addon32",CpuArch.X86,"CnrPHOENIXCompatibilityStatus","AddonInit","AddonUninit");
        var payload=PayloadScanner.Scan(workspace.FilePath("payload"));
        var wrapped=new GameCandidate(workspace.Root,exe,"Game","game.exe",CpuArch.X86,GraphicsApi.Dxgi,"wrapper",160*1024)
        {DetectedInputApis=[(GraphicsApi)input],TranslatorKinds=["dgVoodoo"]};
        Assert.Equal((InstallRoute)route,PackagedPhoenixPlan.For(wrapped,payload)!.Translation);
        Assert.Equal(InstallRoute.Unsupported,PackagedPhoenixPlan.For(wrapped.WithApi(GraphicsApi.OpenGl),payload)!.Translation);
    }
    [Theory]
    [InlineData((int)CpuArch.X86)]
    [InlineData((int)CpuArch.X64)]
    public void FullRuntimeCapabilityDependsOnAbiNotProcessBitness(int machine)
    {
        using var workspace = new TestWorkspace();
        var arch = (CpuArch)machine;
        var full = Dll(workspace, "full.addon", arch, "CnrPHOENIXInProcessBackbufferVersion");
        var thin = Dll(workspace, "thin.addon", arch, "CnrPHOENIXCompatibilityStatus");
        Assert.True(new PackagedPhoenixPlan(full, "dxgi.dll", false).SupportsInProcessBackbuffer);
        Assert.False(new PackagedPhoenixPlan(thin, "dxgi.dll", false).SupportsInProcessBackbuffer);
    }
    [Fact]
    public void RendererMigrationRequiresTwoRealExecutablesInTheSameInjectionDirectory()
    {
        using var workspace = new TestWorkspace();
        var launcher = workspace.WritePe("game/launcher.exe", CpuArch.X64);
        var renderer = workspace.WritePe("game/renderer.exe", CpuArch.X86);
        var elsewhere = workspace.WritePe("other/renderer.exe", CpuArch.X86);
        Assert.True(InstallerEngine.CanMigrateSharedDirectory(launcher, renderer));
        Assert.False(InstallerEngine.CanMigrateSharedDirectory(launcher, elsewhere));
        Assert.False(InstallerEngine.CanMigrateSharedDirectory(launcher, workspace.FilePath("game/missing.exe")));
    }

    [Fact]
    public void HistoricalProjectAbiIsRecognizedWithoutTrustingItsFilename()
    {
        using var workspace = new TestWorkspace();
        var old = Dll(workspace, "unrelated.addon64", CpuArch.X64,
            "CnrY41Summary", "CnrY41Register", "CnrY41Unregister", "AddonInit", "AddonUninit");
        var other = Dll(workspace, "phoenix_named_but_unknown.addon64", CpuArch.X64, "AddonInit", "AddonUninit");
        Assert.True(PackagedPhoenixPlan.IsPreviousNeuralAddon(old));
        Assert.False(PackagedPhoenixPlan.IsPreviousNeuralAddon(other));
    }

    [Theory]
    [InlineData((int)CpuArch.X86, (int)GraphicsApi.DirectX9, "d3d9.dll")]
    [InlineData((int)CpuArch.X86, (int)GraphicsApi.OpenGl, "opengl32.dll")]
    [InlineData((int)CpuArch.X86, (int)GraphicsApi.DirectX11, "dxgi.dll")]
    [InlineData((int)CpuArch.X86, (int)GraphicsApi.DirectX10, "dxgi.dll")]
    [InlineData((int)CpuArch.X86, (int)GraphicsApi.DirectX10_1, "dxgi.dll")]
    [InlineData((int)CpuArch.X86, (int)GraphicsApi.DirectX7OrOlder, "dxgi.dll")]
    [InlineData((int)CpuArch.X86, (int)GraphicsApi.DirectX8, "d3d9.dll")]
    [InlineData((int)CpuArch.X86, (int)GraphicsApi.DirectX8DgVoodoo, "dxgi.dll")]
    [InlineData((int)CpuArch.X86, (int)GraphicsApi.Glide211, "dxgi.dll")]
    [InlineData((int)CpuArch.X86, (int)GraphicsApi.Glide245, "dxgi.dll")]
    [InlineData((int)CpuArch.X86, (int)GraphicsApi.Glide31, "dxgi.dll")]
    [InlineData((int)CpuArch.X86, (int)GraphicsApi.Glide31Napalm, "dxgi.dll")]
    [InlineData((int)CpuArch.X64, (int)GraphicsApi.DirectX9, "d3d9.dll")]
    [InlineData((int)CpuArch.X64, (int)GraphicsApi.DirectX7OrOlder, "dxgi.dll")]
    [InlineData((int)CpuArch.X64, (int)GraphicsApi.DirectX11, "dxgi.dll")]
    [InlineData((int)CpuArch.X64, (int)GraphicsApi.DirectX10, "dxgi.dll")]
    [InlineData((int)CpuArch.X64, (int)GraphicsApi.DirectX10_1, "dxgi.dll")]
    [InlineData((int)CpuArch.X64, (int)GraphicsApi.DirectX12, "dxgi.dll")]
    [InlineData((int)CpuArch.X64, (int)GraphicsApi.OpenGl, "opengl32.dll")]
    public async Task InstallsMatchingBridgeWithoutLooseFeederAndRestoresOriginalBytes(int machine, int apiValue, string proxy)
    {
        using var workspace = new TestWorkspace();
        var arch = (CpuArch)machine;
        var exe = workspace.WritePe("game/unrelated-name.exe", arch);
        if ((GraphicsApi)apiValue == GraphicsApi.DirectX7OrOlder)
        {
            using var executable = new FileStream(exe, FileMode.Append, FileAccess.Write);
            executable.Write(System.Text.Encoding.ASCII.GetBytes("CoCreateInstance\0"));
            executable.Write(new Guid("d7b70ee0-4340-11cf-b063-0020afc2cd35").ToByteArray());
            executable.Write(new Guid("2fe8f810-b2a5-11d0-a787-0000f803abfc").ToByteArray());
        }
        var game = new GameCandidate(Path.GetDirectoryName(exe)!, exe, "Unrelated", "unrelated-name.exe", arch,
            (GraphicsApi)apiValue, "synthetic PE", 160 * 1024);
        var addon = Dll(workspace, "payload/Phoenix.addon64", CpuArch.X64,
            "CnrPHOENIXSummary", "CnrPHOENIXRegister", "CnrPHOENIXFeederVersion", "CnrPHOENIXFeederEvaluate",
            "CnrPHOENIXCompatibilityStatus", "AddonInit", "AddonUninit");
        TestWorkspace.SetEmbeddedPhoenixRuntime(addon);
        using (var marker = new FileStream(addon, FileMode.Open, FileAccess.Write))
        { marker.Position = 0x11000; marker.Write(System.Text.Encoding.ASCII.GetBytes("StandaloneInput")); }
        var addon32 = Dll(workspace, "payload/Phoenix.addon32", CpuArch.X86,
            "CnrPHOENIXCompatibilityStatus", "AddonInit", "AddonUninit");
        Dll(workspace, "payload/ReShade64.dll", CpuArch.X64);
        Dll(workspace, "payload/ReShade32.dll", CpuArch.X86);
        Dll(workspace, "payload/nvngx_dlssnr.dll", CpuArch.X64);
        foreach (var name in new[] { "DDraw.dll", "D3DImm.dll", "D3D8.dll", "D3D9.dll" })
            Dll(workspace, "payload/dgvoodoo2/MS/x86/" + name, CpuArch.X86);
        foreach (var name in new[] { "DDraw.dll", "D3DImm.dll" })
            Dll(workspace, "payload/dgvoodoo2/MS/x64/" + name, CpuArch.X64);
        foreach (var name in new[] { "Glide.dll", "Glide2x.dll", "Glide3x.dll", "Napalm/Glide3x.dll" })
            Dll(workspace, "payload/dgvoodoo2/3Dfx/x86/" + name, CpuArch.X86);
        Dll(workspace, "payload/d3d8to9/d3d8.dll", CpuArch.X86);
        var legacyProxy = Dll(workspace, "payload/legacy-com/PhoenixLegacyDDraw" + (arch == CpuArch.X86 ? "32" : "64") + ".dll", arch,
            "DirectDrawCreate", "DllGetClassObject");
        var oldHook = Dll(workspace, "game/" + proxy, arch, "ReShadeRegisterAddon", "ReShadeUnregisterAddon");
        var oldHookBytes = File.ReadAllBytes(oldHook);
        var oldAddon = Dll(workspace, "game/previous-name.addon" + (arch == CpuArch.X86 ? "32" : "64"), arch,
            "CnrPHOENIXCompatibilityStatus");
        var oldAddonBytes = File.ReadAllBytes(oldAddon);
        var oldFeeder = Dll(workspace, "game/dlss5-feed.addon" + (arch == CpuArch.X86 ? "32" : "64"), arch,
            "CnrPHOENIXCompatibilityStatus");
        var oldFeederBytes = File.ReadAllBytes(oldFeeder);
        var oldFeederSettings = workspace.FilePath("game/dlss5-feed.cfg");
        File.WriteAllText(oldFeederSettings, "[LegacyFeeder]\nUserSetting=preserve-on-restore\n");
        var oldFeederSettingsBytes = File.ReadAllBytes(oldFeederSettings);
        var ini = Path.Combine(game.Root, "ReShade.ini");
        File.WriteAllText(ini, "[CUSTOM]\nKeep=1\n[Phoenix.Compatibility]\nEnabled=0\n" +
            "[ADDON]\nDisabledAddons=User choice@other.addon64,Phoenix DLSS5@Phoenix.addon64\n");
        var preset = Path.Combine(game.Root, "ReShadePreset.ini");
        File.WriteAllText(preset, "[GENERAL]\nTechniques=UserFX@User.fx,DLSS5_Feed@DLSS5_Feed.fx\n" +
            "TechniqueSorting=DLSS5_Feed_Debug@DLSS5_Feed.fx,UserFX@User.fx\n[User.fx]\nStrength=0.75\n");
        var oldPreset = File.ReadAllBytes(preset);
        var oldIni = File.ReadAllBytes(ini);
        var payload = PayloadScanner.Scan(workspace.FilePath("payload"));
        Assert.True(PayloadScanner.IsPeDll(addon32, CpuArch.X86), "x86 PE headers");
        using (var stream = File.OpenRead(addon32))
        using (var pe = new System.Reflection.PortableExecutable.PEReader(stream))
        {
            Assert.Equal(0x1800, pe.PEHeaders.PEHeader!.ExportTableDirectory.RelativeVirtualAddress);
            Assert.Equal(0x1000, pe.PEHeaders.SectionHeaders[0].VirtualAddress);
        }
        Assert.True(PayloadScanner.HasExports(addon32, "CnrPHOENIXCompatibilityStatus", "AddonInit", "AddonUninit"), "x86 exports");
        Assert.Equal(Path.GetFullPath(addon32), payload.PhoenixAddon32);
        Assert.NotNull(PackagedPhoenixPlan.For(game, payload));
        Assert.True(payload.IsReadyFor(game));
        if (game.Api == GraphicsApi.DirectX7OrOlder)
        {
            File.Move(legacyProxy, legacyProxy + ".missing");
            Assert.False(InstallerEngine.CheckPreflight(game, payload).CanInstall);
            Assert.Equal(oldIni, File.ReadAllBytes(ini));
            Assert.False(File.Exists(Path.Combine(game.Root,"PhoenixDgVoodooDDraw.dll")));
            File.Move(legacyProxy + ".missing", legacyProxy);
        }
        Assert.True(InstallerEngine.CheckPreflight(game, payload).CanInstall);
        var engine = new InstallerEngine(_ => { });
        await engine.InstallAsync(game, payload);
        if (game.Api == GraphicsApi.DirectX7OrOlder)
        {
            var wrapped = game.WithApi(GraphicsApi.Dxgi) with
            {
                DetectedInputApis = [GraphicsApi.DirectX7OrOlder], TranslatorKinds = ["dgVoodoo"]
            };
            Assert.Equal(arch == CpuArch.X86 ? InstallRoute.X86DirectXLegacyViaDgVoodoo : InstallRoute.X64DirectXLegacyViaDgVoodoo, PackagedPhoenixPlan.For(wrapped,payload)!.Translation);
            await engine.InstallAsync(wrapped,payload);
            Assert.True(File.Exists(Path.Combine(game.Root,"DDraw.dll")));
            Assert.True(File.Exists(Path.Combine(game.Root,"D3DImm.dll")));
            Assert.Equal(File.ReadAllBytes(legacyProxy), File.ReadAllBytes(Path.Combine(game.Root,"DDraw.dll")));
            Assert.Equal(File.ReadAllBytes(arch == CpuArch.X86 ? payload.DgVoodooDDraw! : payload.DgVoodooDDraw64!),
                File.ReadAllBytes(Path.Combine(game.Root,"PhoenixDgVoodooDDraw.dll")));
            Assert.Equal("1", IniEditor.GetValue(Path.Combine(game.Root,"PhoenixLegacy.ini"),"Compatibility","DirectDrawCOM"));
            Assert.Equal("1", IniEditor.GetValue(Path.Combine(game.Root,"PhoenixLegacy.ini"),"Compatibility","DirectPlayUserRegistration"));
        }
        Assert.False(Directory.Exists(Path.Combine(game.Root, "host64")));
        Assert.False(File.Exists(oldAddon));
        Assert.False(File.Exists(Path.Combine(game.Root, "dlss5-feed.cfg")));
        Assert.Contains("Enabled=1", File.ReadAllText(ini));
        Assert.Contains("StandaloneInput=1", File.ReadAllText(ini));
        Assert.Contains("ExecutionMode=helper", File.ReadAllText(ini));
        Assert.Contains("Keep=1", File.ReadAllText(ini));
        Assert.Contains("User choice@other.addon64", File.ReadAllText(ini));
        Assert.DoesNotContain("Phoenix DLSS5@Phoenix.addon64", File.ReadAllText(ini));
        Assert.Contains("UserFX@User.fx", File.ReadAllText(preset));
        Assert.Contains("Strength=0.75", File.ReadAllText(preset));
        Assert.DoesNotContain("DLSS5_Feed", File.ReadAllText(preset));
        Assert.Equal(File.ReadAllBytes(arch == CpuArch.X86 ? addon32 : addon),
            File.ReadAllBytes(Path.Combine(game.Root, "Phoenix.addon" + (arch == CpuArch.X86 ? "32" : "64"))));
        await engine.InstallAsync(game, payload, phoenixSettings: new() { CopyNvidiaDllToGame = false,
            Runner = "nvidia", ModelId = "small-fp8" });
        Assert.False(File.Exists(Path.Combine(game.Root, "PhoenixModel.dll")));
        Assert.Equal("small-fp8", IniEditor.GetValue(ini, "Phoenix", "ModelId"));
        Assert.Equal("1", IniEditor.GetValue(ini, "Phoenix", "Backend"));
        using (var sharedConfig = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(game.Root, "Phoenix.json"))))
            Assert.Equal(Path.Combine(payload.Root, "nvngx_dlssnr.dll"),
                sharedConfig.RootElement.GetProperty("weight_source").GetProperty("path").GetString());
        var laterAddon = Dll(workspace, "game/renodx-dlss-later.addon" + (arch == CpuArch.X86 ? "32" : "64"), arch,
            "CnrY41Summary", "CnrY41Register", "CnrY41Unregister", "AddonInit", "AddonUninit");
        var laterAddonBytes = File.ReadAllBytes(laterAddon);
        await engine.RestoreAsync(game.Root);
        Assert.False(File.Exists(Path.Combine(game.Root,"PhoenixDgVoodooDDraw.dll")));
        Assert.False(File.Exists(Path.Combine(game.Root,"PhoenixLegacy.ini")));
        Assert.Equal(laterAddonBytes, File.ReadAllBytes(laterAddon));
        Assert.Equal(oldHookBytes, File.ReadAllBytes(oldHook));
        Assert.Equal(oldAddonBytes, File.ReadAllBytes(oldAddon));
        Assert.Equal(oldFeederBytes, File.ReadAllBytes(oldFeeder));
        Assert.Equal(oldFeederSettingsBytes, File.ReadAllBytes(oldFeederSettings));
        Assert.Equal(oldIni, File.ReadAllBytes(ini));
        Assert.Equal(oldPreset, File.ReadAllBytes(preset));
        Assert.False(File.Exists(Path.Combine(game.Root, "PhoenixModel.dll")));
    }

    [Fact]
    public void UnpairedOrUnverifiedFeederSettingsAreNotRetired()
    {
        using var workspace = new TestWorkspace();
        var exe = workspace.WritePe("game.exe", CpuArch.X64);
        var game = new GameCandidate(workspace.Root,exe,"Fixture","game.exe",CpuArch.X64,
            GraphicsApi.DirectX11,"fixture",160*1024);
        var settings = workspace.FilePath("dlss5-feed.cfg");
        File.WriteAllText(settings,"Unrelated user data");
        Assert.DoesNotContain(settings,PackagedPhoenixPlan.RetiredComponents(game,"dxgi.dll"));
        File.WriteAllText(workspace.FilePath("dlss5-feed.addon64"),"Unknown file");
        Assert.DoesNotContain(settings,PackagedPhoenixPlan.RetiredComponents(game,"dxgi.dll"));
    }

    [Fact]
    public void WrongArchitectureCannotActivatePackagedBridge()
    {
        using var workspace = new TestWorkspace();
        var file = Dll(workspace, "wrong.addon32", CpuArch.X64,
            "CnrPHOENIXCompatibilityStatus", "AddonInit", "AddonUninit");
        Assert.False(PayloadScanner.HasPackagedPhoenixBridge(file, CpuArch.X86));
    }

    [Fact]
    public void InProcessCapabilityRequiresTheExplicitExportOnEitherArchitecture()
    {
        using var workspace = new TestWorkspace();
        var current = Dll(workspace, "current.addon64", CpuArch.X64, "CnrPHOENIXInProcessBackbufferVersion");
        var old = Dll(workspace, "old.addon64", CpuArch.X64, "CnrPHOENIXCompatibilityStatus");
        var x86 = Dll(workspace, "current.addon32", CpuArch.X86, "CnrPHOENIXInProcessBackbufferVersion");
        Assert.True(new PackagedPhoenixPlan(current, "dxgi.dll", false).SupportsInProcessBackbuffer);
        Assert.False(new PackagedPhoenixPlan(old, "dxgi.dll", false).SupportsInProcessBackbuffer);
        Assert.True(new PackagedPhoenixPlan(x86, "dxgi.dll", false).SupportsInProcessBackbuffer);
    }

    [Fact]
    public async Task LocalImportPublishesAndVerifiesItsSiblingX86Addon()
    {
        using var workspace = new TestWorkspace();
        var addon = Dll(workspace, "selected/Phoenix.addon64", CpuArch.X64,
            "CnrPHOENIXSummary", "CnrPHOENIXRegister", "CnrPHOENIXFeederVersion", "CnrPHOENIXFeederEvaluate");
        TestWorkspace.SetEmbeddedPhoenixRuntime(addon);
        Dll(workspace, "selected/Phoenix.addon32", CpuArch.X86,
            "CnrPHOENIXCompatibilityStatus", "AddonInit", "AddonUninit");
        var root = workspace.FilePath("published");
        var receipt = await PhoenixReleaseDownloader.ImportLocalAsync(addon, root, default);
        var companion = PhoenixCompanion32.Resolve(root, receipt);
        Assert.NotNull(companion);
        Assert.Equal(companion, PayloadScanner.Scan(root).PhoenixAddon32);
        Assert.True(await PhoenixReleaseCache.VerifyAddonAsync(root, receipt, default));
        using (var changed = new FileStream(companion!, FileMode.Open, FileAccess.ReadWrite))
        { changed.Position = changed.Length - 1; changed.WriteByte(1); }
        Assert.Null(PhoenixCompanion32.Resolve(root, receipt));
        Assert.Null(PayloadScanner.Scan(root).PhoenixAddon32);
        Assert.False(await PhoenixReleaseCache.VerifyAddonAsync(root, receipt, default));
    }

    [Fact]
    public async Task BadSiblingFailsBeforePublishingTheX64Addon()
    {
        using var workspace = new TestWorkspace();
        var addon = Dll(workspace, "selected/Phoenix.addon64", CpuArch.X64,
            "CnrPHOENIXSummary", "CnrPHOENIXRegister", "CnrPHOENIXFeederVersion", "CnrPHOENIXFeederEvaluate");
        TestWorkspace.SetEmbeddedPhoenixRuntime(addon);
        Dll(workspace, "selected/Phoenix.addon32", CpuArch.X64,
            "CnrPHOENIXCompatibilityStatus", "AddonInit", "AddonUninit");
        var root = workspace.FilePath("published");
        await Assert.ThrowsAsync<InvalidDataException>(() => PhoenixReleaseDownloader.ImportLocalAsync(addon, root, default));
        Assert.False(File.Exists(Path.Combine(root, "Phoenix.addon64")));
        Assert.False(File.Exists(Path.Combine(root, PhoenixReleaseDownloader.SourceManifestFileName)));
    }

    [Fact]
    public void HelperOnlyX64FrontendIsInstallableWithoutClaimingNativeGameInput()
    {
        using var workspace = new TestWorkspace();
        var addon = Dll(workspace, "payload/Phoenix.addon64", CpuArch.X64,
            "CnrPHOENIXCompatibilityStatus", "AddonInit", "AddonUninit");
        var payload = PayloadScanner.Scan(workspace.FilePath("payload"));
        Assert.Equal(Path.GetFullPath(addon), payload.PhoenixAddon);
        Assert.True(PayloadScanner.IsPhoenixAddon(addon));
        Assert.False(PayloadScanner.HasPhoenixFeederApi(addon));
        var exe = workspace.WritePe("game/game.exe", CpuArch.X64);
        var game = new GameCandidate(workspace.Root, exe, "Game", "game/game.exe",
            CpuArch.X64, GraphicsApi.DirectX12, "test", 160 * 1024, HasNativeDlssSignal: true);
        var plan = PackagedPhoenixPlan.For(game, payload);
        Assert.NotNull(plan);
        Assert.False(plan.NativeGameInput);
        Assert.False(plan.SupportsInProcessBackbuffer);
        Assert.Equal("dxgi.dll", plan.Proxy);
    }

    static string Dll(TestWorkspace workspace, string name, CpuArch arch, params string[] exports)
    {
        var path = workspace.WritePe(name, arch);
        var bytes = File.ReadAllBytes(path);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(0x96), 0x2022);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0x98 + (arch == CpuArch.X64 ? 108 : 92)), 16);
        File.WriteAllBytes(path, bytes);
        TestWorkspace.SetPhoenixExports(path, exports);
        return path;
    }
}
