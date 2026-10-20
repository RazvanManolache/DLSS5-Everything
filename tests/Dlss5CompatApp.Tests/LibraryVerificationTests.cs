namespace Dlss5CompatApp.Tests;

public sealed class LibraryVerificationTests
{
    [Fact]
    public async Task SerialSweepContinuesAfterRefusalAndDeduplicatesExactExecutables()
    {
        using var workspace = new TestWorkspace();
        var first = Candidate(workspace.WritePe("one/arbitrary.exe", CpuArch.X86), CpuArch.X86);
        var second = Candidate(workspace.WritePe("two/another.exe", CpuArch.X64), CpuArch.X64);
        var calls = new List<string>(); var active = 0;
        var output = workspace.FilePath("reports");
        var results = await LibraryVerification.RunCandidatesAsync([first, first, second], output, async (game, folder) =>
        {
            Assert.Equal(1, Interlocked.Increment(ref active));
            calls.Add(game.ExePath);
            try
            {
                await Task.Yield();
                return game.Arch == CpuArch.X86
                    ? new SmokeTestReport { Error = "Protected-game acknowledgement required." }
                    : new SmokeTestReport { PreflightApproved = true, Tests = [new SmokeTestResult { Passed = true }] };
            }
            finally { Interlocked.Decrement(ref active); }
        });
        Assert.Equal(new[] { first.ExePath, second.ExePath }, calls);
        Assert.False(results[0].Passed);
        Assert.True(results[1].Passed);
        Assert.All(results, result => Assert.True(File.Exists(result.Report)));
        Assert.True(File.Exists(Path.Combine(output, "results.json")));
    }

    [Fact]
    public async Task CancellationRetainsFailureAndDoesNotStartTheNextGame()
    {
        using var workspace = new TestWorkspace(); using var cancellation = new CancellationTokenSource();
        var game = Candidate(workspace.WritePe("any.exe", CpuArch.X64), CpuArch.X64);
        var second = game with { ExePath = workspace.WritePe("other.exe", CpuArch.X86), Arch = CpuArch.X86 };
        var calls = 0;
        var result = await LibraryVerification.RunCandidatesAsync([game, second], workspace.FilePath("reports"), (_, _) =>
        {
            calls++; cancellation.Cancel();
            throw new OperationCanceledException(cancellation.Token);
        }, cancellation.Token);
        Assert.Equal(1, calls);
        Assert.False(Assert.Single(result).Passed);
        Assert.True(File.Exists(result[0].Report));
    }

    static GameCandidate Candidate(string path, CpuArch architecture) => new(Path.GetDirectoryName(path)!, path,
        "Display names do not choose a route", Path.GetFileName(path), architecture, GraphicsApi.DirectX11, "imports", 200000);
}
