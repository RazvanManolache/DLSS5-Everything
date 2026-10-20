using System.Text.Json;
using System.Text.Json.Serialization;

namespace Dlss5CompatApp;

/// <summary>Runs the normal automatic installer/bridge check serially over a
/// detected library. A blocked title is recorded without blocking other games.
/// Each individual check retains its preflight, rollback and process cleanup.</summary>
static class LibraryVerification
{
    static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true, Converters = { new JsonStringEnumConverter() }
    };

    public static async Task<int> RunFromArgsAsync(string[] args)
    {
        if (args.Length != 6 || args[2] != "--payload" || args[4] != "--output") return 2;
        var root = Path.GetFullPath(args[1]);
        var payload = Path.GetFullPath(args[3]);
        var output = Path.GetFullPath(args[5]);
        LocalPaths.RequireNoLinks(output);
        if (Directory.Exists(output) || File.Exists(output))
            throw new IOException("Choose a new verification report folder; previous evidence is preserved.");
        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler cancel = (_, eventArgs) => { eventArgs.Cancel = true; cancellation.Cancel(); };
        Console.CancelKeyPress += cancel;
        try
        {
            var warnings = new List<string>();
            var games = await GameScanner.ScanAsync(root, cancellationToken: cancellation.Token, warning: warnings.Add);
            Directory.CreateDirectory(output);
            await File.WriteAllTextAsync(Path.Combine(output, "inventory.json"),
                JsonSerializer.Serialize(new { Root = root, Games = games, Warnings = warnings }, Json));
            var results = await RunCandidatesAsync(games, output, async (game, folder) =>
            {
                var config = new SmokeTestConfig
                {
                    PayloadRoot = payload, UpdatePayload = false, RestoreBeforeInstall = false,
                    RestoreAfterRun = false, ClearLogsBeforeRun = false,
                    CaptureScreenshot = true, AutoResolve = true, RunSeconds = 70, MinRunSeconds = 20,
                    CancellationToken = cancellation.Token, ReportPath = Path.Combine(folder, "report.json"),
                    Tests = [new SmokeTestTarget { Exe = game.ExePath }]
                };
                return await SmokeTestRunner.RunAsync(config, folder);
            }, cancellation.Token);
            return cancellation.IsCancellationRequested ? 130 : results.Count > 0 && results.All(result => result.Passed) ? 0 : 1;
        }
        finally { Console.CancelKeyPress -= cancel; }
    }

    internal static async Task<IReadOnlyList<LibraryVerificationResult>> RunCandidatesAsync(
        IEnumerable<GameCandidate> games, string output,
        Func<GameCandidate, string, Task<SmokeTestReport>> verify, CancellationToken cancellationToken = default)
    {
        LocalPaths.RequireNoLinks(output);
        Directory.CreateDirectory(output);
        var results = new List<LibraryVerificationResult>();
        foreach (var game in games.DistinctBy(game => Path.GetFullPath(game.ExePath), StringComparer.OrdinalIgnoreCase))
        {
            if (cancellationToken.IsCancellationRequested) break;
            var folder = Path.Combine(output, results.Count.ToString("D4"));
            Directory.CreateDirectory(folder);
            Console.WriteLine("Verifying " + game.ExePath);
            LibraryVerificationResult result;
            try
            {
                var report = await verify(game, folder);
                result = new(game.ExePath, report.Tests.Count > 0 && report.Tests.All(test => test.Passed),
                    report.Tests.FirstOrDefault(test => !test.Passed)?.Error ?? report.Error,
                    Path.Combine(folder, "report.json"));
                // Preflight refusals deliberately do not write into a game folder;
                // this independent report destination retains those refusals too.
                await File.WriteAllTextAsync(result.Report, JsonSerializer.Serialize(report, Json));
            }
            catch (Exception error)
            {
                result = new(game.ExePath, false, error.Message, Path.Combine(folder, "failure.json"));
                await File.WriteAllTextAsync(result.Report, JsonSerializer.Serialize(result, Json));
            }
            results.Add(result);
            await File.WriteAllTextAsync(Path.Combine(output, "results.json"), JsonSerializer.Serialize(results, Json));
        }
        return results;
    }
}

sealed record LibraryVerificationResult(string Exe, bool Passed, string? Error, string Report);
