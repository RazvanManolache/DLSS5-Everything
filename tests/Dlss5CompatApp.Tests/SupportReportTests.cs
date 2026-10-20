namespace Dlss5CompatApp.Tests;

public class SupportReportTests
{
    [Fact]
    public void ExportRemovesCommonSensitiveInformation()
    {
        var text = @"C:\Private\Games\Example\host64\Phoenix.json
Other: D:\Private\Other Game\config.ini
token=do-not-share
someone@example.com 192.168.0.4
https://example.com/download?token=secret
Runtime: not verified";
        var report = SupportReport.Redact(text, @"C:\Private\Games\Example");
        Assert.DoesNotContain("Private", report);
        Assert.DoesNotContain("do-not-share", report);
        Assert.DoesNotContain("someone@", report);
        Assert.DoesNotContain("192.168", report);
        Assert.DoesNotContain("token=secret", report);
        Assert.Contains("Runtime: not verified", report);
        Assert.Contains("Review", report);
    }

    [Fact]
    public void OversizedReportFailsExplicitly() => Assert.Throws<InvalidDataException>(() => SupportReport.Redact(new string('x', 8 * 1024 * 1024 + 1), @"C:\Games"));
}
