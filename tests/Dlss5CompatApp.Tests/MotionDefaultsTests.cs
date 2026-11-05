using System.Reflection;

namespace Dlss5CompatApp.Tests;

public class MotionDefaultsTests
{
    [Theory]
    [InlineData(null, "1")]
    [InlineData("0", "0")]
    [InlineData("1", "1")]
    public void InstallerEnablesMissingMotionButPreservesSavedChoice(string? existing, string expected)
    {
        var folder = Path.Combine(Path.GetTempPath(), "phoenix-motion-default-" + Guid.NewGuid());
        Directory.CreateDirectory(folder);
        try
        {
            var ini = Path.Combine(folder, "ReShade.ini");
            if (existing is not null) IniEditor.SetValue(ini, "Phoenix", "MotionEstimation", existing);
            typeof(InstallerEngine).GetMethod("ConfigurePackagedPhoenixReShade", BindingFlags.NonPublic | BindingFlags.Static)!
                .Invoke(null, [ini]);
            Assert.Equal(expected, IniEditor.GetValue(ini, "Phoenix", "MotionEstimation"));
            Assert.Equal("0", IniEditor.GetValue(ini, "Phoenix", "MotionProvider"));
        }
        finally { Directory.Delete(folder, true); }
    }
}
