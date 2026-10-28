namespace Dlss5CompatApp.Tests;

public sealed class LaunchPlanCliTests
{
    [Fact]
    public void InstalledPackedExecutableCanGenerateAPlanWithExplicitApiWithoutLaunching()
    {
        using var workspace=new TestWorkspace();
        var exe=workspace.FilePath("packed.exe");
        var bytes=TestWorkspace.PeBytes(CpuArch.X86,[])[..(96*1024)];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(0x96),0x22);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(0x98+16),0x1000);
        File.WriteAllBytes(exe,bytes);
        var root=Path.GetDirectoryName(exe)!;
        Directory.CreateDirectory(Path.Combine(root,"_DLSS5_Compat_Backup"));
        File.WriteAllText(Path.Combine(root,"installed-marker.bin"),"fixture");
        File.WriteAllText(Path.Combine(root,"_DLSS5_Compat_Backup","manifest.json"),System.Text.Json.JsonSerializer.Serialize(new InstallManifest
        {GameRoot=root,ExeRelativePath="packed.exe",Route=InstallRoute.X86Dx9NativeFeeder.ToString(),Api=GraphicsApi.DirectX9,Added=["installed-marker.bin"]}));
        var output=workspace.FilePath("plan.json");
        Assert.Equal(1,LaunchPlanCli.Run(["--launch-plan",exe,"--output",output]));
        Assert.False(File.Exists(output));
        Assert.Equal(0,LaunchPlanCli.Run(["--launch-plan",exe,"--api","DirectX9","--output",output,"--arguments","-window"]));
        using var plan=System.Text.Json.JsonDocument.Parse(File.ReadAllText(output));
        Assert.Equal(exe,plan.RootElement.GetProperty("Executable").GetString());
        Assert.Equal("-window",plan.RootElement.GetProperty("Arguments").GetString());
    }
    [Fact]
    public void RendererSelectionIsBoundToTheVerifiedLocalRemixBridge()
    {
        using var workspace=new TestWorkspace();
        var frontend=workspace.WritePe("game.exe",CpuArch.X86,"kernel32.dll");
        Directory.CreateDirectory(workspace.FilePath("bin/.trex"));
        var bridge=workspace.WritePe("bin/d3d9.dll",CpuArch.X86,"kernel32.dll");
        var renderer=workspace.WritePe("bin/.trex/NvRemixBridge.exe",CpuArch.X64,"kernel32.dll");
        Assert.Throws<InvalidOperationException>(()=>LaunchPlanCli.ValidateRemixRenderer(frontend,renderer));
        TestWorkspace.SetPhoenixExports(bridge,"Direct3DCreate9","remixapi_InitializeLibrary","remixapi_RegisterCallbacks");
        LaunchPlanCli.ValidateRemixRenderer(frontend,renderer);
        var other=workspace.WritePe("other.exe",CpuArch.X64,"kernel32.dll");
        Assert.Throws<InvalidOperationException>(()=>LaunchPlanCli.ValidateRemixRenderer(frontend,other));
    }
    [Fact]
    public void LaunchPlanNeverExportsInheritedEnvironmentValues()
    {
        var key="DLSS5_TEST_INHERITED_"+Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(key,"private-inherited-value");
        try
        {
            var result=LaunchPlanCli.EnvironmentChanges(new Dictionary<string,string?>{{key,"private-inherited-value"},{"SteamAppId","1174180"}});
            Assert.False(result.ContainsKey(key));
            Assert.Equal("1174180",result["SteamAppId"]);
        }
        finally{Environment.SetEnvironmentVariable(key,null);}
    }
}
