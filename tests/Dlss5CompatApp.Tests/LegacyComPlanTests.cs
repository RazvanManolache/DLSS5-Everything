using System.Text;
namespace Dlss5CompatApp.Tests;

public class LegacyComPlanTests
{
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
