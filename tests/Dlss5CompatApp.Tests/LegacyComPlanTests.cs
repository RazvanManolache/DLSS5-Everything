using System.Text;
namespace Dlss5CompatApp.Tests;

public class LegacyComPlanTests
{
    [Theory]
    [InlineData((int)CpuArch.X86, "32")]
    [InlineData((int)CpuArch.X64, "64")]
    public void BundledProxyFillsMissingPayloadButDoesNotHideAnExplicitInvalidFile(int bits, string suffix)
    {
        using var workspace = new TestWorkspace();
        var name = "PhoenixLegacyDDraw" + suffix + ".dll";
        var payload = workspace.FilePath("payload");
        var app = workspace.FilePath("app");
        var bundled = workspace.FilePath("app/Runtime/legacy-com/" + name);
        Directory.CreateDirectory(Path.GetDirectoryName(bundled)!);
        File.WriteAllText(bundled, "bundled fixture");
        Assert.True(LocalPaths.Same(bundled, LegacyComPlan.ProxySource(payload, (CpuArch)bits, app)));
        var supplied = workspace.FilePath("payload/legacy-com/" + name);
        Directory.CreateDirectory(Path.GetDirectoryName(supplied)!);
        File.WriteAllText(supplied, "invalid payload remains visible to validation");
        Assert.True(LocalPaths.Same(supplied, LegacyComPlan.ProxySource(payload, (CpuArch)bits, app)));
        Assert.False(PayloadScanner.IsPeDll(supplied, (CpuArch)bits));
        var missingPayload = workspace.FilePath("absent-payload");
        var missing = LegacyComPlan.ProxySource(missingPayload, (CpuArch)bits, workspace.FilePath("absent-app"));
        Assert.True(LocalPaths.Same(Path.Combine(missingPayload, "legacy-com", name), missing));
        Assert.False(File.Exists(missing));
    }

    [Theory]
    [InlineData("d7b70ee0-4340-11cf-b063-0020afc2cd35", true, false)]
    [InlineData("3c305196-50db-11d3-9cfe-00c04fd930c5", true, false)]
    [InlineData("2fe8f810-b2a5-11d0-a787-0000f803abfc", false, true)]
    public void ClassIdentitiesSelectOnlyRequiredBridge(string clsid, bool draw, bool lobby)
    {
        byte[] bytes = [..Encoding.ASCII.GetBytes("CoCreateInstance\0"), ..new Guid(clsid).ToByteArray()];
        Assert.Equal(new LegacyComPlan(draw,lobby), LegacyComPlan.Detect(bytes));
        Assert.Null(LegacyComPlan.Detect(new Guid(clsid).ToByteArray()));
    }
    [Fact]
    public void BothClassesEnableBothIndependentOptions()
    {
        byte[] bytes = [..Encoding.ASCII.GetBytes("CoCreateInstance\0"), ..new Guid("d7b70ee0-4340-11cf-b063-0020afc2cd35").ToByteArray(), ..new Guid("2fe8f810-b2a5-11d0-a787-0000f803abfc").ToByteArray()];
        Assert.Equal(new LegacyComPlan(true,true), LegacyComPlan.Detect(bytes));
    }
    [Fact]
    public void OtherRoutesDoNotReadOrInstallCompatibility()
    {
        Assert.Null(LegacyComPlan.For("does-not-exist.exe",InstallRoute.Unsupported));
        Assert.Null(LegacyComPlan.Detect(Encoding.ASCII.GetBytes("CoCreateInstance")));
    }
}
