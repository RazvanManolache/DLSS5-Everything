using System.Runtime.InteropServices;

namespace Dlss5CompatApp.Tests;

public sealed class StartupDiagnosticTests
{
    [Fact]
    public async Task PostExitRetryWaitsForSharingLocksButDoesNotWaitThroughOtherBlocks()
    {
        var checks = 0;
        var messages = new List<string>();
        await SmokeTestRunner.WaitForPostExitFileReleaseAsync(() => ++checks == 1
            ? new([new("file-unavailable", PreflightSeverity.Error, "Sharing violation")]) : new([]), messages.Add, default);
        Assert.Equal(2, checks);
        Assert.Single(messages);
        checks = 0;
        await SmokeTestRunner.WaitForPostExitFileReleaseAsync(() =>
        { checks++; return new([new("mod-conflict", PreflightSeverity.Error, "Unknown mod")]); }, messages.Add, default);
        Assert.Equal(1, checks);
        using var cancellation = new CancellationTokenSource(50);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => SmokeTestRunner.WaitForPostExitFileReleaseAsync(
            () => new([new("file-unavailable", PreflightSeverity.Error, "Sharing violation")]), _ => { }, cancellation.Token));
    }

    [Fact]
    public void OldWerReportsAndOtherExecutablesCannotFailTheCurrentLaunch()
    {
        const string current = "<Event><EventData><Data Name='ProcessId'>0x7b</Data><Data Name='AppPath'>C:/Games/test.exe</Data></EventData></Event>";
        Assert.True(SmokeTestRunner.CrashBelongsToLaunch(current, 123, "C:/Games/test.exe"));
        Assert.False(SmokeTestRunner.CrashBelongsToLaunch(current, 124, "C:/Games/test.exe"));
        Assert.False(SmokeTestRunner.CrashBelongsToLaunch(current, 123, "C:/Other/test.exe"));
        Assert.False(SmokeTestRunner.CrashBelongsToLaunch("<Event><EventData><Data Name='P1'>test.exe</Data></EventData></Event>", 123, "C:/Games/test.exe"));
    }

    [Fact]
    public void ReadsOnlyTheOwnedDialogWithoutAcceptingItsPrompt()
    {
        var dialog = CreateWindowExW(0, "#32770", "Startup fixture", 0x90000000, 0, 0, 200, 100, 0, 0, 0, 0);
        Assert.NotEqual(0, dialog);
        try
        {
            var text = CreateWindowExW(0, "STATIC", "Required game data is missing.", 0x50000000,
                0, 0, 180, 50, dialog, 0, 0, 0);
            Assert.NotEqual(0, text);
            var observed = StartupDiagnostics.ReadDialog(Environment.ProcessId);
            Assert.Contains("Startup fixture", observed);
            Assert.Contains("Required game data is missing.", observed);
            Assert.Null(StartupDiagnostics.ReadDialog(int.MaxValue));
            Assert.True(IsWindow(dialog));
        }
        finally { DestroyWindow(dialog); }
    }

    [Fact]
    public void RetryOrderUsesDetectedCapabilitiesAndNativeInputFallback()
    {
        var game = new GameCandidate("C:/Games/Any", "C:/Games/Any/unrelated.exe", "Any title", "unrelated.exe",
            CpuArch.X64, GraphicsApi.DirectX12, "imports", 200000,
            [GraphicsApi.DirectX12, GraphicsApi.DirectX11, GraphicsApi.Vulkan, GraphicsApi.OpenVr], HasNativeDlssSignal: true);
        var retries = SmokeTestRunner.RetryChoices(game).ToArray();
        Assert.Equal(new[] { GraphicsApi.DirectX12, GraphicsApi.Vulkan, GraphicsApi.DirectX11 }, retries.Select(item => item.Api));
        Assert.All(retries, item => Assert.Equal(GameRoutePreference.Feeder, item.PreferredRoute));
        Assert.DoesNotContain(retries, item => item.Api == GraphicsApi.OpenVr);
    }

    [Fact]
    public void LegacyEngineAlternatesAreRetriedWithoutAGameTitleRule()
    {
        var game = new GameCandidate("C:/Games/Any", "C:/Games/Any/arbitrary.exe", "Unrelated", "arbitrary.exe",
            CpuArch.X86, GraphicsApi.Glide31, "imports", 200000,
            [GraphicsApi.Glide31, GraphicsApi.DirectX7OrOlder, GraphicsApi.DirectX8, GraphicsApi.OpenGl]);
        var choices = SmokeTestRunner.RetryChoices(game).ToArray();
        Assert.Contains(choices, item => item.Api == GraphicsApi.DirectX7OrOlder);
        Assert.Contains(choices, item => item.Api == GraphicsApi.DirectX8);
        Assert.Contains(choices, item => item.Api == GraphicsApi.OpenGl);
        Assert.All(choices, item => Assert.Equal(GameRoutePreference.Feeder, item.PreferredRoute));
    }

    [Fact]
    public void DirectX8CanRetryADifferentTranslatorForTheSameObservedApi()
    {
        var game = new GameCandidate("C:/Games/Any", "C:/Games/Any/arbitrary.exe", "Unrelated", "arbitrary.exe",
            CpuArch.X86, GraphicsApi.DirectX8, "imports", 200000, [GraphicsApi.DirectX8]);
        var retry = Assert.Single(SmokeTestRunner.RetryChoices(game));
        Assert.Equal(InstallRoute.X86Dx8ViaDgVoodoo, retry.Route);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern nint CreateWindowExW(uint extendedStyle,
        string className, string caption, uint style, int x, int y, int width, int height,
        nint parent, nint menu, nint instance, nint parameter);
    [DllImport("user32.dll")] static extern bool DestroyWindow(nint window);
    [DllImport("user32.dll")] static extern bool IsWindow(nint window);
}
