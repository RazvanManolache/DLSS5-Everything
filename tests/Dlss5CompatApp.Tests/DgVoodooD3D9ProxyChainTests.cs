using System.Reflection;

namespace Dlss5CompatApp.Tests;

// CPU fixtures only: synthetic PE files are never loaded or executed.
public sealed class DgVoodooD3D9ProxyChainTests
{
    static PayloadInfo Payload(TestWorkspace workspace) => (PayloadInfo)typeof(InputBootstrapTests)
        .GetMethod("Payload", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [workspace])!;

    static GameCandidate Game(TestWorkspace workspace, params string[] imports) =>
        GameScanner.ScanSingleExe(workspace.WritePe("game/fixture.exe", CpuArch.X86,
            imports.Length == 0 ? ["d3d9.dll"] : imports))! with { TranslateD3D9 = true };

    [Fact]
    public void OrdinaryD3D9ImportWithoutInputAnchorSelectsOneChainedFrontend()
    {
        using var workspace = new TestWorkspace();
        var payload = Payload(workspace); var game = Game(workspace);
        var plan = PackagedPhoenixPlan.For(game, payload)!;
        Assert.True(plan.UsesDgVoodooD3D9ProxyChain);
        Assert.Equal("d3d9.dll", plan.Proxy);
        Assert.Equal(InstallRoute.X86Dx9ViaDgVoodoo, plan.Translation);
        var target = Assert.Single(plan.TranslationFiles(payload));
        Assert.Equal(PackagedPhoenixPlan.DgVoodooD3D9Library, target.Target);
        Assert.Equal(payload.DgVoodooD3D9, target.Source);
    }

    [Fact]
    public void SelectedDynamicD3D9TranslationStillHasOneWorkingFrontend()
    {
        using var workspace = new TestWorkspace(); var payload = Payload(workspace);
        var game = Game(workspace) with { StartupGraphicsApis = [], ImportedGraphicsApis = [] };
        Assert.DoesNotContain(GraphicsApi.DirectX9, game.StartupGraphicsApis);
        var plan = PackagedPhoenixPlan.For(game, payload)!;
        Assert.True(plan.UsesDgVoodooD3D9ProxyChain);
        Assert.Equal("d3d9.dll", plan.Proxy);
        Assert.Equal(PackagedPhoenixPlan.DgVoodooD3D9Library, Assert.Single(plan.TranslationFiles(payload)).Target);
    }

    [Theory]
    [InlineData("dinput8.dll")]
    [InlineData("dinput.dll")]
    public void ProvenOrdinaryInputAnchorKeepsExistingWorkingChain(string input)
    {
        using var workspace = new TestWorkspace(); var payload = Payload(workspace);
        var plan = PackagedPhoenixPlan.For(Game(workspace, "d3d9.dll", input), payload)!;
        Assert.False(plan.UsesDgVoodooD3D9ProxyChain); Assert.Equal(input, plan.Proxy);
        Assert.Equal("D3D9.dll", Assert.Single(plan.TranslationFiles(payload)).Target);
    }

    [Theory]
    [InlineData("dxgi.dll")]
    [InlineData("d3d11.dll")]
    public void ExplicitOutputProxyIsNotOverridden(string proxy)
    {
        using var workspace = new TestWorkspace(); var payload = Payload(workspace);
        var game = Game(workspace).WithApi(GraphicsApi.DirectX11) with
        { ProxyName = proxy, TranslateD3D9 = false, TranslatorKinds = ["dgVoodoo"] };
        var plan = PackagedPhoenixPlan.For(game, payload)!;
        Assert.False(plan.UsesDgVoodooD3D9ProxyChain); Assert.Equal(proxy, plan.Proxy);
    }

    [Fact]
    public void DelayOnlyD3D9IsNotAStartupAnchor()
    {
        using var workspace = new TestWorkspace(); var payload = Payload(workspace);
        var exe = workspace.WritePe("game/fixture.exe", CpuArch.X86, "d3d11.dll");
        TestWorkspace.SetDelayImports(exe, "d3d9.dll");
        var game = GameScanner.ScanSingleExe(exe)! with { TranslatorKinds = ["dgVoodoo"] };
        var plan = PackagedPhoenixPlan.For(game, payload)!;
        Assert.False(plan.UsesDgVoodooD3D9ProxyChain); Assert.Equal("dxgi.dll", plan.Proxy);
    }

    [Fact]
    public void DynamicInputMarkersDoNotBecomeAnAutomaticInputAnchor()
    {
        using var workspace = new TestWorkspace(); var payload = Payload(workspace); var game = Game(workspace);
        using (var file = File.Open(game.ExePath, FileMode.Append))
        {
            file.Write(System.Text.Encoding.Unicode.GetBytes("\\dinput8.dll\0"));
            file.Write(System.Text.Encoding.ASCII.GetBytes("DirectInput8Create\0"));
        }
        Assert.Null(InputBootstrap.AutomaticProxy(game));
        Assert.True(PackagedPhoenixPlan.For(game, payload)!.UsesDgVoodooD3D9ProxyChain);
    }

    [Fact]
    public void NativeD3D9WithoutTranslationKeepsItsOriginalFrontend()
    {
        using var workspace = new TestWorkspace(); var payload = Payload(workspace);
        var game = Game(workspace) with { TranslateD3D9 = false };
        var plan = PackagedPhoenixPlan.For(game, payload)!;
        Assert.False(plan.UsesDgVoodooD3D9ProxyChain); Assert.Equal("d3d9.dll", plan.Proxy);
        Assert.Empty(plan.TranslationFiles(payload));
    }

    [Fact]
    public void ConfigReassertsExactChainAndClearsOnlyItsOwnOutgoingChain()
    {
        using var workspace = new TestWorkspace(); var payload = Payload(workspace); var game = Game(workspace);
        var chain = PackagedPhoenixPlan.For(game, payload)!;
        var native = PackagedPhoenixPlan.For(game with { TranslateD3D9 = false }, payload)!;
        var path = workspace.FilePath("ReShade.ini");
        File.WriteAllText(path, "[PROXY]\nEnableProxyLibrary=1\nProxyLibrary=other.dll\n");
        InstallerEngine.ConfigureDgVoodooD3D9Proxy(native, path);
        Assert.Equal("other.dll", IniEditor.GetValue(path, "PROXY", "ProxyLibrary"));
        InstallerEngine.ConfigureDgVoodooD3D9Proxy(chain, path);
        Assert.Equal("1", IniEditor.GetValue(path, "PROXY", "EnableProxyLibrary"));
        Assert.Equal(@".\PhoenixDgVoodooD3D9.dll", IniEditor.GetValue(path, "PROXY", "ProxyLibrary"));
        InstallerEngine.ConfigureDgVoodooD3D9Proxy(native, path);
        Assert.Equal("0", IniEditor.GetValue(path, "PROXY", "EnableProxyLibrary"));
        Assert.Equal("", IniEditor.GetValue(path, "PROXY", "ProxyLibrary"));
    }

    [Fact]
    public async Task SyntheticInstallReinstallAndRestorePreserveExactTranslatorAndRecipeBytes()
    {
        using var workspace = new TestWorkspace(); var payload = Payload(workspace); var game = Game(workspace);
        var exeBytes = File.ReadAllBytes(game.ExePath); var wrapper = File.ReadAllBytes(payload.DgVoodooD3D9!);
        var hook = File.ReadAllBytes(payload.ReShade32Dll!); var engine = new InstallerEngine(_ => { });
        for (var attempt = 0; attempt < 2; attempt++)
        {
            await engine.InstallAsync(game, payload);
            Assert.Equal(wrapper, File.ReadAllBytes(Path.Combine(game.Root, PackagedPhoenixPlan.DgVoodooD3D9Library)));
            Assert.Equal(hook, File.ReadAllBytes(Path.Combine(game.Root, "d3d9.dll")));
            Assert.False(File.Exists(Path.Combine(game.Root, "dxgi.dll")));
            foreach (var name in new[] { "ReShade.ini", "ReShadeVR.ini" })
                Assert.Equal(@".\PhoenixDgVoodooD3D9.dll", IniEditor.GetValue(Path.Combine(game.Root, name), "PROXY", "ProxyLibrary"));
        }
        await engine.RestoreAsync(game.Root);
        Assert.Equal(exeBytes, File.ReadAllBytes(game.ExePath));
        Assert.False(File.Exists(Path.Combine(game.Root, PackagedPhoenixPlan.DgVoodooD3D9Library)));
        Assert.False(File.Exists(Path.Combine(game.Root, "d3d9.dll")));
    }

    [Fact]
    public async Task ForeignChainDestinationFailsBeforeAnyInstallTransaction()
    {
        using var workspace = new TestWorkspace(); var payload = Payload(workspace); var game = Game(workspace);
        var path = Path.Combine(game.Root, PackagedPhoenixPlan.DgVoodooD3D9Library);
        var original = new byte[] { 1, 3, 5, 7 }; File.WriteAllBytes(path, original);
        await Assert.ThrowsAsync<InvalidOperationException>(() => new InstallerEngine(_ => { }).InstallAsync(game, payload));
        Assert.Equal(original, File.ReadAllBytes(path)); Assert.False(InstallerEngine.HasPending(game.Root));
        Assert.False(File.Exists(Path.Combine(game.Root, "d3d9.dll")));
    }
}
