using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Dlss5CompatApp;

static class SmokeTestRunner
{
    static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static bool IsSmokeTestCommand(string[] args) =>
        args.Length > 0 &&
        (args[0].Equals("--smoke-test", StringComparison.OrdinalIgnoreCase) ||
         args[0].Equals("smoke-test", StringComparison.OrdinalIgnoreCase));

    public static async Task<int> RunFromArgsAsync(string[] args)
    {
        var configPath = args.Length > 1 ? args[1] : "";
        if (string.IsNullOrWhiteSpace(configPath) || !File.Exists(configPath))
            return 2;

        SmokeTestReport? report = null;
        try
        {
            var configDir = Path.GetDirectoryName(Path.GetFullPath(configPath)) ?? Environment.CurrentDirectory;
            var config = JsonSerializer.Deserialize<SmokeTestConfig>(await File.ReadAllTextAsync(configPath), JsonOptions)
                ?? throw new InvalidOperationException("Smoke-test config is empty.");
            report = await RunAsync(config, configDir);
            await WriteReportAsync(config, configDir, report);
            return report.Tests.All(t => t.Passed) ? 0 : 1;
        }
        catch (Exception ex)
        {
            report ??= new SmokeTestReport();
            report.StartedAt = DateTimeOffset.Now;
            report.CompletedAt = DateTimeOffset.Now;
            report.Error = ex.ToString();
            try
            {
                var config = File.Exists(configPath)
                    ? JsonSerializer.Deserialize<SmokeTestConfig>(await File.ReadAllTextAsync(configPath), JsonOptions)
                    : new SmokeTestConfig();
                await WriteReportAsync(config ?? new SmokeTestConfig(), Path.GetDirectoryName(Path.GetFullPath(configPath)) ?? Environment.CurrentDirectory, report);
            }
            catch
            {
            }
            return 3;
        }
    }

    static async Task<SmokeTestReport> RunAsync(SmokeTestConfig config, string configDir)
    {
        var report = new SmokeTestReport
        {
            StartedAt = DateTimeOffset.Now,
            PayloadRoot = ResolvePath(config.PayloadRoot, AppContext.BaseDirectory),
            AppBaseDirectory = AppContext.BaseDirectory
        };

        Directory.CreateDirectory(report.PayloadRoot);
        var progress = new Progress<BootstrapProgress>(p => report.PayloadProgress.Add($"[{DateTime.Now:HH:mm:ss}] {p.Percent}% {p.Message}"));
        if (config.UpdatePayload)
            await PayloadBootstrapper.EnsureCurrentAsync(report.PayloadRoot, progress, CancellationToken.None);

        var payload = PayloadScanner.Scan(report.PayloadRoot);
        report.PayloadSummary = payload.Summary;

        foreach (var test in config.Tests)
        {
            report.Tests.Add(await RunOneAsync(test, config, payload));
            report.CompletedAt = DateTimeOffset.Now;
            await WriteReportAsync(config, configDir, report);
        }

        report.CompletedAt = DateTimeOffset.Now;
        return report;
    }

    static async Task<SmokeTestResult> RunOneAsync(SmokeTestTarget test, SmokeTestConfig config, PayloadInfo payload)
    {
        var result = new SmokeTestResult
        {
            Name = string.IsNullOrWhiteSpace(test.Name) ? Path.GetFileNameWithoutExtension(test.Exe) : test.Name,
            Exe = test.Exe,
            StartedAt = DateTimeOffset.Now
        };
        var log = new List<string>();
        Process? process = null;
        var waitForExit = test.WaitForExit ?? config.WaitForExit;

        void Log(string message)
        {
            var line = $"[{DateTime.Now:HH:mm:ss}] {message}";
            log.Add(line);
            result.Log.Add(line);
        }

        try
        {
            if (string.IsNullOrWhiteSpace(test.Exe) || !File.Exists(test.Exe))
                throw new FileNotFoundException("Target executable does not exist.", test.Exe);

            var game = GameScanner.ScanSingleExe(test.Exe)
                ?? throw new InvalidOperationException("Target executable was not accepted by the scanner.");
            if (!string.IsNullOrWhiteSpace(test.Api))
            {
                if (!Enum.TryParse<GraphicsApi>(test.Api, ignoreCase: true, out var api))
                    throw new InvalidOperationException("Unknown API override: " + test.Api);
                if (!game.AllApis.Contains(api))
                    throw new InvalidOperationException($"API override {api} was not detected for this executable. Detected: {game.DisplayPossibleApis}");

                game = game.WithApi(api);
            }
            result.GameRoot = game.Root;
            result.DetectedName = game.DisplayName;
            result.Arch = game.Arch.ToString();
            result.Api = game.DisplayApi;
            result.Route = game.DisplayRoute;

            if (game.Route == InstallRoute.Unsupported)
                throw new InvalidOperationException("Unsupported route: " + game.DisplayRoute);
            if (!payload.IsReadyFor(game.Route))
                throw new InvalidOperationException("Payload is missing: " + payload.MissingFor(game.Route));

            var engine = new InstallerEngine(Log);
            await StopAppManagedHelpersAsync(game.Root, test.CloseSeconds ?? config.CloseSeconds, Log);
            if (test.RestoreBeforeInstall ?? config.RestoreBeforeInstall)
            {
                try
                {
                    await engine.RestoreAsync(game.Root);
                    result.Restore = "restored";
                }
                catch (InvalidOperationException ex) when (ex.Message.Contains("No restore manifest", StringComparison.OrdinalIgnoreCase))
                {
                    result.Restore = "no manifest";
                    Log("No previous install manifest; restore skipped.");
                }
            }

            await ClearLogsAsync(game, test.ClearLogsBeforeRun ?? config.ClearLogsBeforeRun);
            await engine.InstallAsync(game, payload, test.ForceVrEyeSplit ?? config.ForceVrEyeSplit);
            var gameDir = Path.GetDirectoryName(game.ExePath)!;
            var arguments = string.IsNullOrWhiteSpace(test.Arguments) ? game.SuggestedArguments : test.Arguments;

            result.InstallCompletedAt = DateTimeOffset.Now;
            process = await LaunchTargetAsync(test, game, gameDir, arguments, Log);
            result.ProcessId = process.Id;
            result.ProcessStartedAt = DateTimeOffset.Now;
            Log("Monitoring process " + process.Id + ".");

            var deadline = DateTimeOffset.Now.AddSeconds(test.RunSeconds ?? config.RunSeconds);
            var stableUntil = DateTimeOffset.Now.AddSeconds(test.MinRunSeconds ?? config.MinRunSeconds);
            var success = false;
            while (waitForExit || DateTimeOffset.Now < deadline)
            {
                await Task.Delay(TimeSpan.FromSeconds(1));
                if (process.HasExited)
                {
                    result.ExitCode = process.ExitCode;
                    result.ProcessExitedAt = DateTimeOffset.Now;
                    break;
                }

                var evidence = ReadEvidence(game);
                result.LastEvidence = evidence.Summary;
                if (HasFailure(evidence.Text, test.FailurePatterns ?? config.FailurePatterns, out var failure))
                {
                    result.Error ??= "Failure evidence matched: " + failure;
                    if (!waitForExit)
                        throw new InvalidOperationException(result.Error);
                }
                if (HasSuccess(evidence.Text, test.SuccessPatterns ?? config.SuccessPatterns, out var matched))
                {
                    result.SuccessPattern = matched;
                    success = true;
                    if (!waitForExit && DateTimeOffset.Now >= stableUntil)
                        break;
                }
            }

            result.Passed = success;
            if (!success && string.IsNullOrWhiteSpace(result.Error))
                result.Error = waitForExit
                    ? "Game exited before success evidence appeared."
                    : "Timed out before success evidence appeared.";

            result.LastEvidence = ReadEvidence(game).Summary;
            if (!waitForExit)
                await StopProcessAsync(process, test.CloseSeconds ?? config.CloseSeconds, Log);
            process.Dispose();
            process = null;
            await StopAppManagedHelpersAsync(game.Root, test.CloseSeconds ?? config.CloseSeconds, Log);
            if (test.RestoreAfterRun ?? config.RestoreAfterRun)
            {
                await engine.RestoreAsync(game.Root);
                result.RestoreAfterRun = "restored";
            }
        }
        catch (Exception ex)
        {
            result.Passed = false;
            result.Error = ex.Message;
            result.Exception = ex.ToString();
        }
        finally
        {
            if (process is not null)
            {
                if (!waitForExit)
                    await StopProcessAsync(process, test.CloseSeconds ?? config.CloseSeconds, Log);
                process.Dispose();
            }
            if (!string.IsNullOrWhiteSpace(result.GameRoot))
                await StopAppManagedHelpersAsync(result.GameRoot, test.CloseSeconds ?? config.CloseSeconds, Log);
            result.CompletedAt = DateTimeOffset.Now;
        }

        return result;
    }

    static async Task<Process> LaunchTargetAsync(SmokeTestTarget test, GameCandidate game, string gameDir, string? arguments, Action<string> log)
    {
        if (!string.IsNullOrWhiteSpace(test.LaunchUri))
        {
            var processName = string.IsNullOrWhiteSpace(test.ProcessName)
                ? Path.GetFileNameWithoutExtension(game.ExePath)
                : test.ProcessName;
            var startedAfter = DateTimeOffset.Now.AddSeconds(-2);
            log("Launching " + test.LaunchUri + " and waiting for " + processName + ".");
            Process.Start(new ProcessStartInfo
            {
                FileName = test.LaunchUri,
                UseShellExecute = true
            });

            return await WaitForProcessAsync(processName, game.ExePath, startedAfter, TimeSpan.FromSeconds(test.LaunchWaitSeconds ?? 90));
        }

        var launchTarget = InstallerEngine.LaunchTargetFor(game);
        var start = new ProcessStartInfo
        {
            FileName = launchTarget,
            WorkingDirectory = ResolvePath(test.WorkingDirectory, gameDir),
            UseShellExecute = true
        };
        if (launchTarget.Equals(game.ExePath, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(arguments))
            start.Arguments = arguments;

        var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start game process.");
        log("Started process " + process.Id + (string.IsNullOrWhiteSpace(arguments) ? "." : " with arguments: " + arguments));
        return process;
    }

    static async Task<Process> WaitForProcessAsync(string processName, string? expectedPath, DateTimeOffset startedAfter, TimeSpan timeout)
    {
        var normalizedName = Path.GetFileNameWithoutExtension(processName);
        var deadline = DateTimeOffset.Now + timeout;
        while (DateTimeOffset.Now < deadline)
        {
            foreach (var process in Process.GetProcessesByName(normalizedName))
            {
                try
                {
                    if (process.HasExited)
                    {
                        process.Dispose();
                        continue;
                    }

                    if (!string.IsNullOrWhiteSpace(expectedPath))
                    {
                        var path = process.MainModule?.FileName;
                        if (!string.IsNullOrWhiteSpace(path) &&
                            !path.Equals(expectedPath, StringComparison.OrdinalIgnoreCase))
                        {
                            process.Dispose();
                            continue;
                        }
                    }

                    try
                    {
                        if (process.StartTime < startedAfter.LocalDateTime)
                        {
                            process.Dispose();
                            continue;
                        }
                    }
                    catch
                    {
                    }

                    return process;
                }
                catch
                {
                    process.Dispose();
                }
            }

            await Task.Delay(TimeSpan.FromSeconds(1));
        }

        throw new TimeoutException("Timed out waiting for process " + normalizedName + " to start.");
    }

    static async Task ClearLogsAsync(GameCandidate game, bool clear)
    {
        if (!clear) return;
        foreach (var path in LogPaths(game))
        {
            try
            {
                if (File.Exists(path))
                    await File.WriteAllTextAsync(path, "");
            }
            catch
            {
            }
        }
    }

    static SmokeEvidence ReadEvidence(GameCandidate game)
    {
        var chunks = new List<string>();
        var summary = new List<string>();
        foreach (var path in LogPaths(game))
        {
            if (!File.Exists(path)) continue;
            var tail = TailText(path, 128 * 1024);
            chunks.Add("### " + Path.GetFileName(path) + Environment.NewLine + tail);
            summary.Add(Path.GetRelativePath(game.Root, path) + ": " + SummarizeLogTail(tail));
        }
        return new SmokeEvidence(string.Join(Environment.NewLine, chunks), string.Join(" | ", summary));
    }

    static IEnumerable<string> LogPaths(GameCandidate game)
    {
        var gameDir = Path.GetDirectoryName(game.ExePath)!;
        yield return Path.Combine(gameDir, "dlss5-feed.log");
        yield return Path.Combine(gameDir, "dlss5-bridge.log");
        yield return Path.Combine(gameDir, "ReShade.log");
        yield return Path.Combine(gameDir, "host64", "dlss5-feed-host.log");
        yield return Path.Combine(gameDir, "host64", "ReShade.log");
    }

    static string TailText(string path, int maxBytes)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var toRead = (int)Math.Min(maxBytes, fs.Length);
            fs.Seek(-toRead, SeekOrigin.End);
            var buffer = new byte[toRead];
            _ = fs.Read(buffer, 0, toRead);
            return System.Text.Encoding.UTF8.GetString(buffer);
        }
        catch
        {
            return "";
        }
    }

    static string SummarizeLogTail(string text)
    {
        var lines = text.Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries);
        return lines.Length == 0 ? "empty" : string.Join(" / ", lines.TakeLast(Math.Min(3, lines.Length)));
    }

    static bool HasSuccess(string text, IReadOnlyList<string> patterns, out string matched)
    {
        foreach (var pattern in EffectiveSuccessPatterns(patterns))
        {
            if (!Regex.IsMatch(text, pattern, RegexOptions.IgnoreCase | RegexOptions.Multiline)) continue;
            matched = pattern;
            return true;
        }

        matched = "";
        return false;
    }

    static bool HasFailure(string text, IReadOnlyList<string> patterns, out string matched)
    {
        foreach (var pattern in patterns)
        {
            if (!Regex.IsMatch(text, pattern, RegexOptions.IgnoreCase | RegexOptions.Multiline)) continue;
            matched = pattern;
            return true;
        }

        matched = "";
        return false;
    }

    static IReadOnlyList<string> EffectiveSuccessPatterns(IReadOnlyList<string> patterns) =>
        patterns.Count == 0
            ? SmokeTestConfig.DefaultSuccessPatterns
            : patterns;

    static async Task StopProcessAsync(Process process, int closeSeconds, Action<string> log)
    {
        try
        {
            if (process.HasExited) return;
            log("Closing process " + process.Id + ".");
            try { process.CloseMainWindow(); } catch { }
            if (await WaitForExitAsync(process, TimeSpan.FromSeconds(closeSeconds))) return;
            log("Killing process " + process.Id + ".");
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
        }
        catch
        {
        }
    }

    static async Task StopAppManagedHelpersAsync(string gameRoot, int closeSeconds, Action<string> log)
    {
        var root = Path.GetFullPath(gameRoot);
        foreach (var helper in Process.GetProcessesByName("dlss5-feed-host64"))
        {
            try
            {
                using (helper)
                {
                    if (helper.HasExited) continue;
                    var path = helper.MainModule?.FileName;
                    if (string.IsNullOrWhiteSpace(path)) continue;
                    if (!IsPathUnderRoot(path, root)) continue;

                    log("Closing stale helper " + helper.Id + ".");
                    try { helper.CloseMainWindow(); } catch { }
                    if (await WaitForExitAsync(helper, TimeSpan.FromSeconds(closeSeconds))) continue;
                    log("Killing stale helper " + helper.Id + ".");
                    helper.Kill(entireProcessTree: true);
                    await helper.WaitForExitAsync();
                }
            }
            catch
            {
            }
        }
    }

    static bool IsPathUnderRoot(string path, string root)
    {
        var fullPath = Path.GetFullPath(path);
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        return fullPath.Equals(fullRoot, StringComparison.OrdinalIgnoreCase) ||
               fullPath.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
               fullPath.StartsWith(fullRoot + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    static async Task<bool> WaitForExitAsync(Process process, TimeSpan timeout)
    {
        try
        {
            using var cts = new CancellationTokenSource(timeout);
            await process.WaitForExitAsync(cts.Token);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    static string ResolvePath(string? path, string baseDir)
    {
        if (string.IsNullOrWhiteSpace(path)) return baseDir;
        return Path.IsPathRooted(path) ? path : Path.GetFullPath(Path.Combine(baseDir, path));
    }

    static async Task WriteReportAsync(SmokeTestConfig config, string configDir, SmokeTestReport report)
    {
        var path = ResolvePath(config.ReportPath, configDir);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(report, JsonOptions));
    }

    sealed record SmokeEvidence(string Text, string Summary);
}

sealed class SmokeTestConfig
{
    public static readonly string[] DefaultSuccessPatterns =
    [
        @"frame\s+\d+\s+delivered",
        @"D3D11\s+CPU\s+fallback\s+frame\s+\d+\s+delivered",
        @"native\s+D3D9\s+frame\s+\d+\s+delivered",
        @"native\s+D3D12\s+frame\s+\d+\s+delivered",
        @"native\s+OpenGL\s+frame\s+\d+\s+delivered",
        @"native\s+OpenGL\s+CPU\s+bridge\s+ready",
        @"bridge.*delivered",
        @"vkmirror.*delivered",
        @"synthetic.*delivered",
        @"feature\s+18\s+created",
        @"NGX.*initialized"
    ];

    public string PayloadRoot { get; set; } = @".\Payload";
    public bool UpdatePayload { get; set; } = true;
    public bool RestoreBeforeInstall { get; set; } = true;
    public bool RestoreAfterRun { get; set; }
    public bool ClearLogsBeforeRun { get; set; } = true;
    public bool ForceVrEyeSplit { get; set; }
    public int RunSeconds { get; set; } = 60;
    public int MinRunSeconds { get; set; } = 12;
    public int CloseSeconds { get; set; } = 8;
    public bool WaitForExit { get; set; }
    public string ReportPath { get; set; } = @".\smoke-report.json";
    public List<string> SuccessPatterns { get; set; } = [];
    public List<string> FailurePatterns { get; set; } =
    [
        @"stopped:",
        @"host\s+returned\s+no\s+output",
        @"ReShade setup did not create",
        @"Payload is missing"
    ];
    public List<SmokeTestTarget> Tests { get; set; } = [];
}

sealed class SmokeTestTarget
{
    public string? Name { get; set; }
    public string Exe { get; set; } = "";
    public string? LaunchUri { get; set; }
    public string? ProcessName { get; set; }
    public string? Api { get; set; }
    public string? Arguments { get; set; }
    public string? WorkingDirectory { get; set; }
    public int? LaunchWaitSeconds { get; set; }
    public int? RunSeconds { get; set; }
    public int? MinRunSeconds { get; set; }
    public int? CloseSeconds { get; set; }
    public bool? WaitForExit { get; set; }
    public bool? RestoreBeforeInstall { get; set; }
    public bool? RestoreAfterRun { get; set; }
    public bool? ClearLogsBeforeRun { get; set; }
    public bool? ForceVrEyeSplit { get; set; }
    public List<string>? SuccessPatterns { get; set; }
    public List<string>? FailurePatterns { get; set; }
}

sealed class SmokeTestReport
{
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset CompletedAt { get; set; }
    public string AppBaseDirectory { get; set; } = "";
    public string PayloadRoot { get; set; } = "";
    public string PayloadSummary { get; set; } = "";
    public string? Error { get; set; }
    public List<string> PayloadProgress { get; set; } = [];
    public List<SmokeTestResult> Tests { get; set; } = [];
}

sealed class SmokeTestResult
{
    public string Name { get; set; } = "";
    public string Exe { get; set; } = "";
    public string? GameRoot { get; set; }
    public string? DetectedName { get; set; }
    public string? Arch { get; set; }
    public string? Api { get; set; }
    public string? Route { get; set; }
    public string? Restore { get; set; }
    public string? RestoreAfterRun { get; set; }
    public int? ProcessId { get; set; }
    public int? ExitCode { get; set; }
    public DateTimeOffset? InstallCompletedAt { get; set; }
    public DateTimeOffset? ProcessStartedAt { get; set; }
    public DateTimeOffset? ProcessExitedAt { get; set; }
    public bool Passed { get; set; }
    public string? SuccessPattern { get; set; }
    public string? Error { get; set; }
    public string? Exception { get; set; }
    public string? LastEvidence { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset CompletedAt { get; set; }
    public List<string> Log { get; set; } = [];
}
