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
        Process? after = null;
        try
        {
            await Ready(before);
            var adoptionLog = new List<string>();
            using var lifetime = new GameLaunchLifetime(Path.GetDirectoryName(image)!, adoptionLog.Add);
            after = Start();
            Assert.Equal(image, GameLaunchLifetime.ProcessImagePath(after), ignoreCase: true);
            lifetime.Observe();
            Assert.Contains("Guarding game bootstrap process " + after.Id + ".", adoptionLog);
            // Start() can return before the child's PEB/image is queryable.
            // The kernel identity above is already usable; let the fixture
            // reach its input loop before asserting the unchanged exit deadline.
            await Ready(after);
            Assert.False(before.HasExited);
            Assert.False(after.HasExited);
            lifetime.Dispose();
            await after.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(before.HasExited);
        }
        finally
        {
            if (!before.HasExited) before.Kill(entireProcessTree: true);
            if (after is { HasExited: false }) after.Kill(entireProcessTree: true);
            await before.WaitForExitAsync();
            if (after is not null) await after.WaitForExitAsync();
            before.Dispose();
            after?.Dispose();
            // Both exit assertions above stay strict. On a busy Windows CI
            // host, image teardown or a scanner can briefly retain this copied
            // executable after process exit. Bound only this fixture's cleanup.
            var cleanupDeadline = Stopwatch.StartNew();
            while (true)
            {
                try { File.Delete(image); break; }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException &&
                    cleanupDeadline.Elapsed < TimeSpan.FromSeconds(5))
                { await Task.Delay(50); }
            }
        }
    }

    static async Task Ready(Process process)
    {
        const string marker = "GAME_LIFETIME_FIXTURE_READY";
        await process.StandardInput.WriteLineAsync("echo " + marker);
        await process.StandardInput.FlushAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (await process.StandardOutput.ReadLineAsync(deadline.Token) is { } line)
            if (line.Contains(marker, StringComparison.Ordinal)) return;
        throw new InvalidOperationException("The copied bootstrap exited before its ready handshake.");
    }
}
