using System.Diagnostics;

namespace Dlss5CompatApp.Tests;

internal static class SwapperTestLinks
{
    // Directory junctions exercise reparse-point protection without Windows symlink privileges.
    public static void CreateJunction(string link, string target)
    {
        static string Quote(string value) => "'" + value.Replace("'", "''") + "'";
        var start = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true,
            ArgumentList = { "-NoProfile", "-NonInteractive", "-Command",
                "$ErrorActionPreference='Stop'; New-Item -ItemType Junction -Path " + Quote(link) + " -Target " + Quote(target) + " | Out-Null" }
        };
        using var process = Process.Start(start) ?? throw new IOException("Could not create test junction.");
        if (!process.WaitForExit(10000))
        {
            process.Kill(true); process.WaitForExit();
            throw new IOException("Test junction creation timed out.");
        }
        if (process.ExitCode != 0) throw new IOException("Test junction creation failed: " + process.StandardError.ReadToEnd());
        Assert.True((File.GetAttributes(link) & FileAttributes.ReparsePoint) != 0);
    }
}
