using System.Diagnostics;

namespace Dlss5CompatApp.Tests;

public sealed class GameLaunchLifetimeTests
{
    [Theory]
    [InlineData("C:\\Games\\Demo\\launcher.exe", false, true)]
    [InlineData("C:\\Games\\Demo\\bin\\game.exe", false, true)]
    [InlineData("C:\\Games\\Demo2\\launcher.exe", false, false)]
    [InlineData("C:\\Steam\\steam.exe", false, false)]
    [InlineData("C:\\Games\\Demo\\launcher.exe", true, false)]
    [InlineData(null, false, false)]
    public void OnlyNewProcessesInsideTheExactInstallationAreEligible(string? image, bool existed, bool expected)
    {
        var launch = new DateTime(2026, 10, 16, 2, 0, 0);
        Assert.Equal(expected, GameLaunchLifetime.IsNewLocalProcess("C:\\Games\\Demo", image,
            launch.AddSeconds(1), launch, existed));
        Assert.False(GameLaunchLifetime.IsNewLocalProcess("C:\\Games\\Demo", image,
            launch.AddSeconds(-1), launch, false));
    }

    [Fact]
    public async Task CleanupClosesANewBootstrapAndPreservesAnExistingLocalProcess()
    {
        using var workspace = new TestWorkspace();
        var image = workspace.FilePath("launcher.exe");
        File.Copy(Path.Combine(Environment.SystemDirectory, "cmd.exe"), image);
        Process Start() => Process.Start(new ProcessStartInfo(image, "/q")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        })!;
        using var before = Start();
        using var lifetime = new GameLaunchLifetime(Path.GetDirectoryName(image)!, _ => { });
        using var after = Start();
        try
        {
            lifetime.Observe();
            Assert.False(before.HasExited);
            Assert.False(after.HasExited);
            lifetime.Dispose();
            await after.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(before.HasExited);
        }
        finally
        {
            if (!before.HasExited) before.Kill(entireProcessTree: true);
            if (!after.HasExited) after.Kill(entireProcessTree: true);
            await before.WaitForExitAsync();
            await after.WaitForExitAsync();
        }
    }
}
