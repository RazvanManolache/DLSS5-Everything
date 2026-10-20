namespace Dlss5CompatApp.Tests;

public sealed class EmulatorProfilesTests
{
    [Fact]
    public void ReferenceTableHasExactlyNineteenProfilesWithUniqueExecutableAliases()
    {
        Assert.Equal(19, EmulatorProfiles.All.Count);
        Assert.Equal(19, EmulatorProfiles.All.Select(p => p.Key).Distinct().Count());
        var aliases = EmulatorProfiles.All.SelectMany(p => p.Executables).ToArray();
        Assert.Equal(aliases.Length, aliases.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        foreach (var profile in EmulatorProfiles.All)
        {
            Assert.NotEmpty(profile.System); Assert.NotEmpty(profile.Hint); Assert.NotEmpty(profile.Renderers);
            foreach (var exe in profile.Executables)
            {
                Assert.Same(profile, EmulatorProfiles.ProfileFor("C:\\emulators\\" + exe.ToUpperInvariant()));
                Assert.Same(profile, EmulatorProfiles.ProfileFor("/emulators/" + exe));
            }
        }
        Assert.Contains("9fb0b7c", EmulatorProfiles.SourceUrl);
    }

    [Theory]
    [InlineData("duckstation.exe", "dxgi,vulkan,opengl")]
    [InlineData("xenia_canary.exe", "dxgi,vulkan")]
    [InlineData("cemu.exe", "vulkan,opengl")]
    [InlineData("shadps4.exe", "vulkan")]
    [InlineData("melonds.exe", "opengl")]
    [InlineData("snes9x.exe", "dxgi")]
    public void RendererChoicesRetainReferenceOrderAndLabels(string exe, string apis)
    {
        var profile = EmulatorProfiles.ProfileFor(exe);
        Assert.NotNull(profile);
        Assert.Equal(apis, string.Join(',', EmulatorProfiles.ApiChoices(profile).Select(r => r.Api)));
        foreach (var renderer in profile.Renderers)
            Assert.Equal(renderer.Api switch { "dxgi" => "DirectX 11/12", "vulkan" => "Vulkan", "opengl" => "OpenGL", _ => "invalid" }, renderer.Label);
    }

    [Theory]
    [InlineData(null)] [InlineData("")] [InlineData("unknown.exe")] [InlineData("rpcs3.exe.bak")]
    [InlineData("not-rpcs3.exe")] [InlineData("C:\\rpcs3.exe\\other.exe")]
    public void UnrecognizedExecutablesDoNotReceiveMetadata(string? exe)
    {
        Assert.Null(EmulatorProfiles.ProfileFor(exe));
        Assert.Empty(EmulatorProfiles.ApiChoices(null));
    }

    [Fact]
    public void PublicMetadataCannotBeMutatedThroughCollectionInterfaces()
    {
        Assert.Throws<NotSupportedException>(() => ((IList<EmulatorProfile>)EmulatorProfiles.All).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<string>)EmulatorProfiles.All[0].Executables).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<EmulatorRenderer>)EmulatorProfiles.All[0].Renderers).Clear());
    }

    [Theory]
    [InlineData("duckstation.exe", "Dxgi")]
    [InlineData("cemu.exe", "Vulkan")]
    [InlineData("melonds.exe", "OpenGl")]
    public async Task ScannerRecognizesMetadataWithoutImportsAndLabelsItUnverified(string exe, string expected)
    {
        using var w = new TestWorkspace(); var path = w.WritePe(exe, CpuArch.X64);
        var selected = GameScanner.ScanSingleExe(path);
        var discovered = Assert.Single(await GameScanner.ScanAsync(w.Root, limits: new()));
        Assert.NotNull(selected);
        Assert.Equal(expected, selected.Api.ToString()); Assert.Equal(selected.Api, discovered.Api);
        Assert.Equal(selected.AllApis, discovered.AllApis);
        Assert.Contains("emulator-metadata", selected.Detection);
        Assert.Contains("runtime compatibility unverified", selected.Detection);
        Assert.Contains(EmulatorProfiles.ProfileFor(exe)!.Hint, selected.Detection);
        Assert.False(selected.HasNativeDlssSignal); Assert.Equal("", selected.SuggestedArguments);
    }

    [Theory]
    [InlineData("d3d11.dll", "DirectX11")]
    [InlineData("d3d12.dll", "DirectX12")]
    [InlineData("opengl32.dll", "OpenGl")]
    [InlineData("vulkan-1.dll", "Vulkan")]
    public void ObservedImportsStayPrimaryAndFirstWithMetadataChoicesAppended(string import, string expected)
    {
        using var w = new TestWorkspace(); var path = w.WritePe("duckstation.exe", CpuArch.X64, import);
        var game = GameScanner.ScanSingleExe(path);
        Assert.NotNull(game); Assert.Equal(expected, game.Api.ToString());
        Assert.Equal(game.Api, game.AllApis[0]);
        Assert.Contains(GraphicsApi.Dxgi, game.AllApis); Assert.Contains(GraphicsApi.Vulkan, game.AllApis);
        Assert.Contains(GraphicsApi.OpenGl, game.AllApis);
        Assert.Equal(game.AllApis.Count, game.AllApis.Distinct().Count());
        Assert.StartsWith("imports", game.Detection);
    }

    [Fact]
    public void ArchitectureAndWrapperEvidenceRemainAuthoritative()
    {
        using var w = new TestWorkspace();
        var x86 = GameScanner.ScanSingleExe(w.WritePe("cemu.exe", CpuArch.X86));
        Assert.NotNull(x86); Assert.Equal(GraphicsApi.Vulkan, x86.Api);
        var unsupported = GameScanner.ScanSingleExe(w.WritePe("shadps4.exe", CpuArch.X86));
        Assert.NotNull(unsupported); Assert.Equal(InstallRoute.X86PackagedBridge, unsupported.Route);
        var wrapped = w.WritePe("duckstation.exe", CpuArch.X64, "d3d11.dll");
        File.WriteAllBytes(w.FilePath("d3d11.dll"), TestWorkspace.PeBytes(CpuArch.X64, [], "DXVK vkGetInstanceProcAddr"));
        var detected = GameScanner.ScanSingleExe(wrapped);
        Assert.NotNull(detected); Assert.Equal(GraphicsApi.Vulkan, detected.Api); Assert.Contains("wrapper:", detected.Detection);
    }

    [Fact]
    public async Task MetadataDoesNotBypassScannerUtilitySizeArchitectureOrDirectoryExclusions()
    {
        using var w = new TestWorkspace();
        var utility = w.WritePe("tools/duckstation.exe", CpuArch.X64);
        Assert.Null(GameScanner.ScanSingleExe(utility));
        w.WritePe("duckstation-updater.exe", CpuArch.X64);
        var invalid = w.FilePath("pcsx2.exe"); File.WriteAllBytes(invalid, new byte[160 * 1024]);
        var tiny = w.FilePath("cemu.exe"); File.WriteAllBytes(tiny, new byte[1024]);
        w.WritePe("unknown.exe", CpuArch.X64);
        Assert.Null(GameScanner.ScanSingleExe(invalid)); Assert.Null(GameScanner.ScanSingleExe(tiny));
        Assert.Empty(await GameScanner.ScanAsync(w.Root, limits: new()));
        var explicitUnknown = GameScanner.ScanSingleExe(w.FilePath("unknown.exe"));
        Assert.NotNull(explicitUnknown); Assert.Equal(GraphicsApi.Unknown, explicitUnknown.Api);
        Assert.DoesNotContain("emulator", explicitUnknown.Detection);
    }
}
