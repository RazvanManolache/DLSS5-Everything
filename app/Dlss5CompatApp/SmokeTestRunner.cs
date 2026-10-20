using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Dlss5CompatApp;

static partial class SmokeTestRunner
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
            if (!report.PreflightApproved)
            {
                Console.Error.WriteLine(report.Error + Environment.NewLine + string.Join(Environment.NewLine, report.Tests.Select(t => t.Error)));
                return 1;
            }
            await WriteReportAsync(config, configDir, report);
            return report.Tests.Count > 0 && report.Tests.All(t => t.Passed) ? 0 : 1;
        }
        catch (Exception ex)
        {
            report ??= new SmokeTestReport();
            report.StartedAt = DateTimeOffset.Now;
            report.CompletedAt = DateTimeOffset.Now;
            report.Error = ex.ToString();
            try
            {
                if (report.PreflightApproved)
                {
                    var config = File.Exists(configPath)
                        ? JsonSerializer.Deserialize<SmokeTestConfig>(await File.ReadAllTextAsync(configPath), JsonOptions)
                        : new SmokeTestConfig();
                    await WriteReportAsync(config ?? new SmokeTestConfig(), Path.GetDirectoryName(Path.GetFullPath(configPath)) ?? Environment.CurrentDirectory, report);
                }
            }
            catch
            {
            }
            return 3;
        }
    }

    internal static async Task<SmokeTestReport> RunAsync(SmokeTestConfig config, string configDir,
        Func<string, int, Action<string>, Task>? stopHelpers = null)
    {
        config.CancellationToken.ThrowIfCancellationRequested();
        if (config.VerifyExistingInstall && (config.UpdatePayload || config.RestoreBeforeInstall || config.RestoreAfterRun || config.AutoResolve ||
            config.Tests.Any(test => test.RestoreBeforeInstall == true || test.RestoreAfterRun == true ||
                test.Api is not null || test.PreferredRoute is not null || test.ProxyName is not null)))
            throw new InvalidOperationException("Verification of an existing installation cannot update, restore or try alternative installations.");
        config.ReportPath = ResolvePath(config.ReportPath, configDir);
        var report = new SmokeTestReport
        {
            StartedAt = DateTimeOffset.Now,
            PayloadRoot = ResolvePath(config.PayloadRoot, AppContext.BaseDirectory),
            AppBaseDirectory = AppContext.BaseDirectory
        };

        // Refuse the whole plan before payload setup, which could also target a game folder.
        foreach (var test in config.Tests)
        {
            try
            {
                var game = DetectTarget(test, config.VerifyExistingInstall);
                RequirePreflightAcknowledgement(InstallerEngine.CheckPreflight(game, PayloadScanner.Scan(report.PayloadRoot)), config.AcknowledgeAntiCheatRisk,
                    message => report.PayloadProgress.Add(message));
            }
            catch (Exception ex)
            {
                report.Tests.Add(new SmokeTestResult
                {
                    Name = test.Name ?? Path.GetFileNameWithoutExtension(test.Exe), Exe = test.Exe,
                    StartedAt = DateTimeOffset.Now, CompletedAt = DateTimeOffset.Now,
                    Error = ex.Message, Exception = ex.ToString()
                });
            }
        }
        if (report.Tests.Count > 0 || config.Tests.Count == 0)
        {
            report.Error = "Smoke preflight refused; no payload updates or target actions were started.";
            report.CompletedAt = DateTimeOffset.Now;
            return report;
        }

        report.PreflightApproved = true;
        Directory.CreateDirectory(report.PayloadRoot);
        var progress = new Progress<BootstrapProgress>(p => report.PayloadProgress.Add($"[{DateTime.Now:HH:mm:ss}] {p.Percent}% {p.Message}"));
        if (config.UpdatePayload)
            await PayloadBootstrapper.EnsureCurrentAsync(report.PayloadRoot, progress, config.CancellationToken);

        var payload = PayloadScanner.Scan(report.PayloadRoot);
        report.PayloadSummary = payload.Summary;

        foreach (var test in config.Tests)
        {
            config.CancellationToken.ThrowIfCancellationRequested();
            report.Tests.Add(await RunOneAsync(test, config, payload, stopHelpers));
            report.CompletedAt = DateTimeOffset.Now;
            await WriteReportAsync(config, configDir, report);
        }

        report.CompletedAt = DateTimeOffset.Now;
        return report;
    }

    internal static async Task<SmokeTestResult> RunAttemptAsync(SmokeTestTarget test, SmokeTestConfig config, PayloadInfo payload,
        Func<string, int, Action<string>, Task>? stopHelpers = null)
    {
        var result = new SmokeTestResult
        {
            Name = string.IsNullOrWhiteSpace(test.Name) ? Path.GetFileNameWithoutExtension(test.Exe) : test.Name,
            Exe = test.Exe,
            StartedAt = DateTimeOffset.Now
        };
        var log = new List<string>();
        Process? process = null;
        ProcessJob? gameLifetime = null;
        GameLaunchLifetime? launcherLifetime = null;
        GameCandidate? game = null;
        InstallerEngine? engine = null;
        var installed = false;
        PhoenixSmokeEvidence? liveBridgeEvidence = null;
        PhoenixControlResponse? lastBridgeReply = null;
        var nextScreenshotAttempt = DateTimeOffset.MinValue;
        var helperCleanupAllowed = false;
        var waitForExit = test.WaitForExit ?? config.WaitForExit;
        stopHelpers ??= (root, seconds, logLine) => StopAppManagedHelpersAsync(root, seconds, logLine,
            result.ProcessId, result.ProcessStartedAt);

        void Log(string message)
        {
            var line = $"[{DateTime.Now:HH:mm:ss}] {message}";
            log.Add(line);
            result.Log.Add(line);
            Console.WriteLine(result.Name + ": " + line);
            config.Progress?.Invoke(line);
        }

        try
        {
            config.CancellationToken.ThrowIfCancellationRequested();
            game = DetectTarget(test, config.VerifyExistingInstall);
            result.GameRoot = game.Root;
            result.DetectedName = game.DisplayName;
            result.Arch = game.Arch.ToString();
            result.Api = game.DisplayApi;
            result.Route = PackagedPhoenixPlan.For(game, payload)?.Description ?? game.DisplayRoute;

            RequirePreflightAcknowledgement(InstallerEngine.CheckPreflight(game, payload), config.AcknowledgeAntiCheatRisk, Log);
            if (!config.VerifyExistingInstall && !payload.IsReadyFor(game))
                throw new InvalidOperationException("Payload is missing: " + payload.MissingFor(game));

            engine = new InstallerEngine(Log);
            helperCleanupAllowed = true;
            await stopHelpers(game.Root, test.CloseSeconds ?? config.CloseSeconds, Log);
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

            if (!config.VerifyExistingInstall)
            {
                await ClearLogsAsync(game, test.ClearLogsBeforeRun ?? config.ClearLogsBeforeRun);
                var install = await engine.InstallAsync(game, payload, test.ForceVrEyeSplit ?? config.ForceVrEyeSplit);
                installed = true;
                install.RequireComplete();
            }
            else Log("Verifying existing installation; keeping installed files, settings and route.");
            config.CancellationToken.ThrowIfCancellationRequested();
            var gameDir = Path.GetDirectoryName(game.ExePath)!;
            var arguments = test.Arguments ?? game.SuggestedArguments;

            if (installed) result.InstallCompletedAt = DateTimeOffset.Now;
            var baseline = CapturePhoenixSummaries(game);
            var logBaseline = CaptureLogCursors(game);
            var evidenceSince = DateTimeOffset.UtcNow;
            if (!string.IsNullOrWhiteSpace(test.LaunchUri))
                launcherLifetime = new GameLaunchLifetime(game.Root, Log);
            process = await LaunchTargetAsync(test, game, gameDir, arguments, Log,
                launcherLifetime, config.CancellationToken);
            result.ProcessId = process.Id;
            result.ProcessStartedAt = DateTimeOffset.Now;
            // Jobs include children born during shutdown (such as crash reporters),
            // which a later PID walk cannot reliably attribute after their parent exits.
            gameLifetime = new ProcessJob();
            gameLifetime.Assign(process);
            _ = process.SafeHandle; // Keep a query handle even if an attached short-lived launcher exits.
            Log("Monitoring process " + process.Id + ".");

            var deadline = DateTimeOffset.Now.AddSeconds(test.RunSeconds ?? config.RunSeconds);
            var stableUntil = DateTimeOffset.Now.AddSeconds(test.MinRunSeconds ?? config.MinRunSeconds);
            while (waitForExit || DateTimeOffset.Now < deadline)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), config.CancellationToken);
                if (process.HasExited)
                {
                    result.ExitCode = process.ExitCode;
                    if (result.ExitCode != 0) result.Error ??= StartupDiagnostics.DescribeExit(result.ExitCode.Value, game.ExePath);
                    result.ProcessExitedAt = DateTimeOffset.Now;
                    break;
                }

                var evidence = ReadEvidence(game, baseline, evidenceSince, process.Id, logBaseline);
                if (PackagedPhoenixPlan.For(game, payload) is not null)
                {
                    try
                    {
                        var client = new PhoenixControlClient(gameDir, game.ExePath, process);
                        var reply = await client.SendAsync(PhoenixOperation.Status, timeout: TimeSpan.FromSeconds(10), cancellationToken: config.CancellationToken);
                        var compatibility = reply.Snapshot.TryGetProperty("helper", out _);
                        if (compatibility) liveBridgeEvidence = PhoenixBridgeEvidence.Evaluate(reply, process.Id);
                        result.BridgeSnapshot = reply.Snapshot.Clone();
                        lastBridgeReply = reply;
                        var captureReady = compatibility ? liveBridgeEvidence!.OutputCompleted :
                            PhoenixBridgeEvidence.NativeFramesRecorded(reply, process.Id);
                        if (config.CaptureScreenshot && captureReady && !result.ScreenshotHasVariation &&
                            DateTimeOffset.Now >= nextScreenshotAttempt)
                        {
                            // An early loading frame may be black. Retake it once
                            // the game advances instead of retaining that first image.
                            nextScreenshotAttempt = DateTimeOffset.Now.AddSeconds(3);
                            await CaptureBridgeScreenshotAsync(client, process.Id, config, result);
                            reply = await client.SendAsync(PhoenixOperation.Status, timeout: TimeSpan.FromSeconds(10), cancellationToken: config.CancellationToken);
                            if (compatibility) liveBridgeEvidence = PhoenixBridgeEvidence.Evaluate(reply, process.Id);
                            result.BridgeSnapshot = reply.Snapshot.Clone();
                        }
                        lastBridgeReply = reply;
                    }
                    catch (Exception error) when (error is IOException or TimeoutException or InvalidOperationException or UnauthorizedAccessException)
                    { Log("Waiting for live game bridge: " + error.Message); }
                    if (liveBridgeEvidence is not null) evidence = evidence with { Phoenix = liveBridgeEvidence };
                }
                result.LastEvidence = evidence.Summary;
                if (!evidence.Phoenix.OutputCompleted && DateTimeOffset.Now >= stableUntil &&
                    StartupDiagnostics.ReadDialog(process.Id) is { Length: > 0 } dialog)
                {
                    result.StartupDialog = dialog;
                    throw new InvalidOperationException("Game startup is blocked by a dialog: " + dialog);
                }
                if (FindRecentCrash(game, result.StartedAt, process.Id, out var crash))
                {
                    result.Error ??= "Crash evidence matched: " + crash;
                    if (!waitForExit)
                        throw new InvalidOperationException(result.Error);
                }
                var success = EvaluateEvidence(result, evidence.Text, evidence.Phoenix,
                    test.SuccessPatterns ?? config.SuccessPatterns, test.FailurePatterns ?? config.FailurePatterns);
                if (!waitForExit && result.Error is not null)
                    throw new InvalidOperationException(result.Error);
                if (success && !waitForExit && DateTimeOffset.Now >= stableUntil &&
                    (!config.CaptureScreenshot || result.ScreenshotHasVariation))
                    break;
            }

            if (!process.HasExited) result.LoadedGraphicsModules = StartupDiagnostics.ReadGraphicsModules(process);
            if (!waitForExit)
                await StopProcessAsync(process, test.CloseSeconds ?? config.CloseSeconds, Log);
            process.Dispose();
            process = null;
            gameLifetime.Dispose();
            gameLifetime = null;
            await stopHelpers(game.Root, test.CloseSeconds ?? config.CloseSeconds, Log);
            await Task.Delay(TimeSpan.FromSeconds(2));
            // Completion summaries may only be flushed on exit. Recheck failures as well as success.
            var finalEvidence = ReadEvidence(game, baseline, evidenceSince, result.ProcessId!.Value, logBaseline);
            if (liveBridgeEvidence is not null) finalEvidence = finalEvidence with { Phoenix = liveBridgeEvidence };
            result.LastEvidence = finalEvidence.Summary;
            result.Passed = EvaluateEvidence(result, finalEvidence.Text, finalEvidence.Phoenix,
                test.SuccessPatterns ?? config.SuccessPatterns, test.FailurePatterns ?? config.FailurePatterns);
            if (!result.Passed && string.IsNullOrWhiteSpace(result.Error))
                result.Error = "Smoke evidence requirements not met: " + result.RuntimeEvidence;
            if (FindRecentCrash(game, result.StartedAt, result.ProcessId!.Value, out var finalCrash))
            {
                result.Passed = false;
                result.Error ??= "Crash evidence matched: " + finalCrash;
            }
            if (result.Passed)
                result.VerifiedBridgeReply = lastBridgeReply;
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
            if (helperCleanupAllowed && process is not null)
            {
                if (!process.HasExited) result.LoadedGraphicsModules = StartupDiagnostics.ReadGraphicsModules(process);
                if (!waitForExit || config.CancellationToken.IsCancellationRequested)
                    await StopProcessAsync(process, test.CloseSeconds ?? config.CloseSeconds, Log);
                process.Dispose();
            }
            gameLifetime?.Dispose();
            launcherLifetime?.Dispose();
            if (helperCleanupAllowed && !string.IsNullOrWhiteSpace(result.GameRoot))
                await stopHelpers(result.GameRoot, test.CloseSeconds ?? config.CloseSeconds, Log);
            if (helperCleanupAllowed && installed && engine is not null && game is not null && (test.RestoreAfterRun ?? config.RestoreAfterRun) && string.IsNullOrWhiteSpace(result.RestoreAfterRun))
            {
                try
                {
                    await engine.RestoreAsync(game.Root);
                    result.RestoreAfterRun = "restored";
                }
                catch (Exception ex)
                {
                    Log("Restore after failed run did not complete: " + ex.Message);
                }
            }
            result.CompletedAt = DateTimeOffset.Now;
        }

        return result;
    }

    static GameCandidate DetectTarget(SmokeTestTarget test, bool existingInstall = false)
    {
        if (string.IsNullOrWhiteSpace(test.Exe) || !File.Exists(test.Exe))
            throw new FileNotFoundException("Target executable does not exist.", test.Exe);
        var game = GameScanner.ScanSingleExe(test.Exe)
            ?? throw new InvalidOperationException("Target executable was not accepted by the scanner.");
        if (existingInstall) return InstallerEngine.InstalledGame(game);
        if (!string.IsNullOrWhiteSpace(test.Api))
        {
            if (!Enum.TryParse<GraphicsApi>(test.Api, ignoreCase: true, out var api))
                throw new InvalidOperationException("Unknown API override: " + test.Api);
            if (!game.AllApis.Contains(api))
                throw new InvalidOperationException($"API override {api} was not detected for this executable. Detected: {game.DisplayPossibleApis}");
            game = game.WithApi(api);
        }
        if (test.PreferredRoute is { } route)
        {
            if (!Enum.TryParse<GameRoutePreference>(route, true, out var preference) || !Enum.IsDefined(preference))
                throw new InvalidOperationException("Unknown input route: " + route);
            game = game with { PreferredRoute = preference };
        }
        if (test.ProxyName is not null) game = game with { ProxyName = test.ProxyName };
        game.ValidateOverrides();
        if (game.Route == InstallRoute.Unsupported)
            throw new InvalidOperationException("Unsupported route: " + game.DisplayRoute);
        return game;
    }

    internal static void RequirePreflightAcknowledgement(InstallPreflightResult checks, bool acknowledgeAntiCheatRisk,
        Action<string> log)
    {
        checks.ThrowIfBlocked();
        if (checks.HasWarnings) log(checks.DisplayText);
        if (checks.RequiresAntiCheatAcknowledgement && !acknowledgeAntiCheatRisk)
            throw new InvalidOperationException("Anti-cheat acknowledgement required. Review the game's policy and set "
                + "AcknowledgeAntiCheatRisk to true in the smoke-test config only if you accept the risk.\n" + checks.DisplayText);
    }

    static async Task<Process> LaunchTargetAsync(SmokeTestTarget test, GameCandidate game, string gameDir, string? arguments,
        Action<string> log, GameLaunchLifetime? launcherLifetime, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(test.LaunchUri))
        {
            var processName = string.IsNullOrWhiteSpace(test.ProcessName)
                ? Path.GetFileNameWithoutExtension(game.ExePath)
                : test.ProcessName;
            var startedAfter = DateTimeOffset.Now;
            log("Launching " + test.LaunchUri + " and waiting for " + processName + ".");
            using var shellProcess = Process.Start(new ProcessStartInfo
            {
                FileName = test.LaunchUri,
                UseShellExecute = true
            });

            return await WaitForProcessAsync(processName, game.ExePath, startedAfter,
                TimeSpan.FromSeconds(test.LaunchWaitSeconds ?? 90), launcherLifetime, cancellationToken);
        }

        // Exercise exactly the app's Play path, including its architecture-bound
        // Vulkan environment. A command-shell launcher loses exit-code ownership.
        var start = GameLauncher.Create(game, arguments ?? "");
        if (test.WorkingDirectory is not null) start.WorkingDirectory = ResolvePath(test.WorkingDirectory, start.WorkingDirectory);
        var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start game process.");
        log("Started process " + process.Id + " in " + start.WorkingDirectory +
            (string.IsNullOrWhiteSpace(start.Arguments) ? "." : " with arguments: " + start.Arguments));
        return process;
    }

    static async Task<Process> WaitForProcessAsync(string processName, string? expectedPath, DateTimeOffset startedAfter,
        TimeSpan timeout, GameLaunchLifetime? launcherLifetime, CancellationToken cancellationToken)
    {
        var normalizedName = Path.GetFileNameWithoutExtension(processName);
        var deadline = DateTimeOffset.Now + timeout;
        while (DateTimeOffset.Now < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            launcherLifetime?.Observe();
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
                        string? path = null;
                        try { path = process.MainModule?.FileName; } catch { }
                        if (string.IsNullOrWhiteSpace(path) || !LocalPaths.Same(path, expectedPath))
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
                        process.Dispose();
                        continue;
                    }

                    return process;
                }
                catch
                {
                    process.Dispose();
                }
            }

            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
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

    static SmokeEvidence ReadEvidence(GameCandidate game, IReadOnlyDictionary<string, string?> baseline,
        DateTimeOffset since, int processId, IReadOnlyDictionary<string, LogCursor>? logBaseline = null)
    {
        var chunks = new List<string>();
        var summary = new List<string>();
        foreach (var path in LogPaths(game))
        {
            if (!File.Exists(path)) continue;
            var tail = FreshLogTail(path, logBaseline?.GetValueOrDefault(path));
            chunks.Add("### " + Path.GetFileName(path) + Environment.NewLine + tail);
            summary.Add(Path.GetRelativePath(game.Root, path) + ": " + SummarizeLogTail(tail));
        }
        var phoenix = ReadPhoenixEvidence(game, baseline, since, processId);
        summary.Add(phoenix.Summary);
        return new SmokeEvidence(string.Join(Environment.NewLine, chunks), string.Join(" | ", summary), phoenix);
    }

    static IEnumerable<string> LogPaths(GameCandidate game)
    {
        var gameDir = Path.GetDirectoryName(game.ExePath)!;
        yield return Path.Combine(gameDir, "dlss5-feed.log");
        yield return Path.Combine(gameDir, "dlss5-bridge.log");
        yield return Path.Combine(gameDir, "ReShade.log");
        yield return Path.Combine(gameDir, "host64", "dlss5-feed-host.log");
        yield return Path.Combine(gameDir, "host64", "ReShade.log");
        yield return Path.Combine(gameDir, "PhoenixOutput", "phoenix.log");
        yield return Path.Combine(gameDir, "host64", "PhoenixOutput", "phoenix.log");
        yield return Path.Combine(gameDir, "phoenix-vulkan-feed.log");
        yield return Path.Combine(gameDir, "_DLSS5_OpenXR", "host64", "dlss5-feed-host.log");
        yield return Path.Combine(gameDir, "_DLSS5_OpenXR", "host64", "ReShade.log");
    }

    internal static IEnumerable<string> PhoenixSummaryPaths(GameCandidate game)
    {
        var directory = Path.GetDirectoryName(game.ExePath)!;
        if (game.Route != InstallRoute.X64DirectPhoenix)
            directory = game.Route == InstallRoute.X64OpenXrLayer
                ? Path.Combine(directory, "_DLSS5_OpenXR", "host64") : Path.Combine(directory, "host64");
        yield return Path.Combine(directory, "PhoenixOutput", "production_game_phoenix_summary.json");
        if (game.Route != InstallRoute.X64DirectPhoenix)
            yield return Path.Combine(directory, "phoenix-host-output-summary.json");
    }

    internal static Dictionary<string, string?> CapturePhoenixSummaries(GameCandidate game) =>
        PhoenixSummaryPaths(game).ToDictionary(path => path, ReadPhoenixSummary, StringComparer.OrdinalIgnoreCase);

    static string? ReadPhoenixSummary(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            LocalPaths.RequireNoLinks(path);
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length > 1024 * 1024) return "";
            using var reader = new StreamReader(stream);
            var buffer = new char[1024 * 1024 + 1];
            var length = reader.ReadBlock(buffer, 0, buffer.Length);
            return length > 1024 * 1024 ? "" : new string(buffer, 0, length);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return "";
        }
    }

    internal static PhoenixSmokeEvidence ReadPhoenixEvidence(GameCandidate game,
        IReadOnlyDictionary<string, string?> baseline, DateTimeOffset since, int processId)
    {
        var best = new PhoenixSmokeEvidence(false, false, "No fresh Phoenix DLSS5 completion summary.");
        foreach (var path in PhoenixSummaryPaths(game))
        {
            var text = ReadPhoenixSummary(path);
            if (text is null || File.GetLastWriteTimeUtc(path) < since.UtcDateTime) continue;
            // An unobserved baseline cannot establish whether counters advanced during this run.
            if (!baseline.TryGetValue(path, out var before)) continue;
            var evidence = EvaluatePhoenixSummary(before, text,
                game.Route == InstallRoute.X64DirectPhoenix ? processId : null);
            if (evidence.Failed) return evidence;
            if (!best.OutputCompleted) best = evidence;
        }
        return best;
    }

    internal static PhoenixSmokeEvidence EvaluatePhoenixSummary(string? before, string after, int? expectedProcessId = null)
    {
        try
        {
            using var current = JsonDocument.Parse(after);
            var root = current.RootElement;
            if (root.GetProperty("schema").GetString() != "production-game-phoenix/2" ||
                root.GetProperty("compiled_mode").GetString() != "production_candidate_only")
                return new(false, false, "Unsupported Phoenix DLSS5 summary schema or diagnostic build; output unverified.");
            if (root.GetProperty("terminal").GetBoolean() || !string.IsNullOrEmpty(root.GetProperty("error").GetString()))
                return new(false, true, "Phoenix DLSS5 summary reports terminal/error state.");
            var pid = root.GetProperty("process_id").GetInt32();
            if (pid <= 0 || (expectedProcessId is not null && pid != expectedProcessId))
                return new(false, false, "Phoenix DLSS5 summary belongs to a different or unknown process.");
            var completed = root.GetProperty("completed_frames").GetUInt64();
            var submitted = root.GetProperty("submitted_frames").GetUInt64();
            var launches = root.GetProperty("own_candidate_launches_attempted").GetUInt64();
            var route = root.GetProperty("phoenix_routing");
            var routed = route.GetProperty("frames").GetUInt64();
            ulong oldCompleted = 0, oldLaunches = 0, oldRouted = 0;
            if (before is not null)
            {
                using var previous = JsonDocument.Parse(before);
                var old = previous.RootElement;
                if (old.GetProperty("process_id").GetInt32() == pid)
                {
                    oldCompleted = old.GetProperty("completed_frames").GetUInt64();
                    oldLaunches = old.GetProperty("own_candidate_launches_attempted").GetUInt64();
                    oldRouted = old.GetProperty("phoenix_routing").GetProperty("frames").GetUInt64();
                }
            }
            var direct = root.GetProperty("direct_game");
            if (!direct.GetProperty("enabled").GetBoolean() ||
                direct.GetProperty("status").GetString() != "Own network output written back to the game DLSS output" ||
                route.GetProperty("active_mode").GetInt32() is not (2 or 3))
                return new(false, false, "No own-network output writeback evidence; native NR/SR is not Phoenix DLSS5 completion.");
            if (root.GetProperty("worker_execution").GetProperty("kind").GetString() != "native")
                return new(false, false, "Portable worker completion is not validated by this smoke gate; output unverified.");
            if (completed <= oldCompleted || launches <= oldLaunches || routed <= oldRouted || submitted < completed)
                return new(false, false, "Phoenix DLSS5 completion, own-launch and routed-output counters did not advance consistently.");
            return new(true, false, $"Phoenix DLSS5 reports fresh own-network completed output ({completed} completed frames, process {pid}).");
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException or FormatException or OverflowException)
        {
            return new(false, false, "Phoenix DLSS5 summary is incomplete, unreadable or unsupported; output unverified.");
        }
    }

    internal static bool EvaluateEvidence(SmokeTestResult result, string text, PhoenixSmokeEvidence phoenix,
        IReadOnlyList<string> successPatterns, IReadOnlyList<string> failurePatterns)
    {
        result.RuntimeOutputCompleted = phoenix.OutputCompleted;
        result.RuntimeEvidence = phoenix.Summary;
        result.Warning = "Log/NGX success is not Phoenix DLSS5 proof. Summary completion does not verify displayed delivery, visual quality or performance.";
        if (phoenix.Failed) result.Error ??= phoenix.Summary;
        if (HasFailure(text, failurePatterns, out var failure)) result.Error ??= "Failure evidence matched: " + failure;
        var matched = "";
        var patternsSatisfied = successPatterns.Count == 0 || HasSuccess(text, successPatterns, out matched);
        if (!patternsSatisfied) result.RuntimeEvidence += " Additional configured log requirement not observed.";
        result.SuccessPattern = string.IsNullOrEmpty(matched) ? null : matched;
        // Configured log patterns can add a requirement, but can never bypass the Phoenix gate.
        result.Passed = phoenix.OutputCompleted && patternsSatisfied && string.IsNullOrWhiteSpace(result.Error);
        return result.Passed;
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

    static bool FindRecentCrash(GameCandidate game, DateTimeOffset since, int launchedPid, out string summary)
    {
        summary = "";
        if (!OperatingSystem.IsWindows())
            return false;

        try
        {
            var query = new EventLogQuery("Application", PathType.LogName,
                "*[System[(EventID=1000 or EventID=1001 or EventID=1002)]]")
            {
                ReverseDirection = true
            };

            using var reader = new EventLogReader(query);
            for (var i = 0; i < 80; i++)
            {
                using var record = reader.ReadEvent();
                if (record is null) break;
                if (record.TimeCreated is null) continue;
                if (record.TimeCreated.Value < since.LocalDateTime)
                    break;

                var message = "";
                try { message = record.FormatDescription() ?? ""; } catch { }
                // WER can publish queued reports from old runs with a new event
                // timestamp. A filename alone does not identify this launch.
                if (!CrashBelongsToLaunch(record.ToXml(), launchedPid, game.ExePath))
                    continue;

                summary = $"{record.TimeCreated:HH:mm:ss} {record.ProviderName}/{record.Id}: {FirstLine(message)}";
                return true;
            }
        }
        catch
        {
        }

        return false;
    }

    internal static bool CrashBelongsToLaunch(string xml, int launchedPid, string executable)
    {
        try
        {
            var document = System.Xml.Linq.XDocument.Parse(xml);
            var data = document.Descendants().Where(element => element.Name.LocalName == "Data" && element.Attribute("Name") is not null)
                .ToDictionary(element => element.Attribute("Name")!.Value, element => element.Value, StringComparer.OrdinalIgnoreCase);
            if (!data.TryGetValue("ProcessId", out var process)) return false;
            var hexadecimal = process.StartsWith("0x", StringComparison.OrdinalIgnoreCase);
            if (!int.TryParse(hexadecimal ? process[2..] : process,
                hexadecimal ? System.Globalization.NumberStyles.HexNumber : System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var pid) || pid != launchedPid) return false;
            var path = data.GetValueOrDefault("AppPath") ?? data.GetValueOrDefault("ExeFileName");
            return path is not null && LocalPaths.Same(path, executable);
        }
        catch (Exception error) when (error is System.Xml.XmlException or ArgumentException or IOException) { return false; }
    }

    static string FirstLine(string text)
    {
        var line = text.Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        return line.Length <= 220 ? line : line[..220];
    }

    static IReadOnlyList<string> EffectiveSuccessPatterns(IReadOnlyList<string> patterns) =>
        patterns.Count == 0
            ? SmokeTestConfig.DefaultSuccessPatterns
            : patterns;

    static async Task StopProcessAsync(Process process, int closeSeconds, Action<string> log)
    {
        var processId = 0;
        try { processId = process.Id; } catch { }
        try
        {
            if (process.HasExited) return;
            log("Closing process " + processId + ".");
            try { process.CloseMainWindow(); } catch { }
            if (await WaitForExitAsync(process, TimeSpan.FromSeconds(closeSeconds))) return;
            log("Killing process " + processId + ".");
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
        }
        catch
        {
            log("Could not stop process " + processId + ".");
        }
    }

    static async Task StopAppManagedHelpersAsync(string gameRoot, int closeSeconds, Action<string> log,
        int? ownerPid = null, DateTimeOffset? launchedAt = null)
    {
        var root = Path.GetFullPath(gameRoot);
        foreach (var helper in Process.GetProcessesByName("dlss5-feed-host64")
            .Concat(Process.GetProcessesByName("phoenix_graphics_host")))
        {
            int helperId;
            try { helperId = helper.Id; }
            catch
            {
                helper.Dispose();
                continue;
            }

            try
            {
                using (helper)
                {
                    if (helper.HasExited) continue;
                    string? path = null;
                    try { path = helper.MainModule?.FileName; } catch { }
                    // A filename alone never establishes ownership of another process.
                    if (string.IsNullOrWhiteSpace(path)) continue;
                    if (helper.ProcessName.Equals("phoenix_graphics_host", StringComparison.OrdinalIgnoreCase))
                    {
                        if (ownerPid is null || launchedAt is null || !IsOwnedPrivateHelper(path, ownerPid.Value,
                            new DateTimeOffset(helper.StartTime), launchedAt.Value)) continue;
                    }
                    else if (!IsPathUnderRoot(path, root)) continue;

                    log("Closing stale helper " + helperId + ".");
                    try { helper.CloseMainWindow(); } catch { }
                    if (await WaitForExitAsync(helper, TimeSpan.FromSeconds(closeSeconds))) continue;
                    log("Killing stale helper " + helperId + ".");
                    helper.Kill(entireProcessTree: true);
                    await helper.WaitForExitAsync();
                }
            }
            catch
            {
                log("Could not stop stale helper " + helperId + ".");
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

    internal sealed record PhoenixSmokeEvidence(bool OutputCompleted, bool Failed, string Summary);
    sealed record SmokeEvidence(string Text, string Summary, PhoenixSmokeEvidence Phoenix);
}

sealed class SmokeTestConfig
{
    // Log patterns are optional additional checks. Structured Phoenix completion is mandatory.
    public static readonly string[] DefaultSuccessPatterns = [];

    public string PayloadRoot { get; set; } = @".\Payload";
    public bool UpdatePayload { get; set; } = true;
    public bool AcknowledgeAntiCheatRisk { get; set; } = false;
    public bool RestoreBeforeInstall { get; set; } = true;
    public bool RestoreAfterRun { get; set; }
    public bool ClearLogsBeforeRun { get; set; } = true;
    public bool ForceVrEyeSplit { get; set; }
    public int RunSeconds { get; set; } = 60;
    public int MinRunSeconds { get; set; } = 12;
    public int CloseSeconds { get; set; } = 8;
    public bool WaitForExit { get; set; }
    public bool CaptureScreenshot { get; set; }
    public bool AutoResolve { get; set; } = true;
    public bool VerifyExistingInstall { get; set; }
    [JsonIgnore] public Action<string>? Progress { get; set; }
    [JsonIgnore] public CancellationToken CancellationToken { get; set; }
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
    public string? PreferredRoute { get; set; }
    public string? ProxyName { get; set; }
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
    public bool PreflightApproved { get; set; }
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
    public string ValidationScope => "Phoenix-reported own-network completion only; displayed output, quality and performance unverified.";
    public bool RuntimeOutputCompleted { get; set; }
    public bool DisplayedOutputVerified => false;
    public string? RuntimeEvidence { get; set; }
    public string Warning { get; set; } = "Phoenix DLSS5 runtime output unverified.";
    public string? SuccessPattern { get; set; }
    public string? Error { get; set; }
    public string? Exception { get; set; }
    public string? LastEvidence { get; set; }
    public string? StartupDialog { get; set; }
    public string[] LoadedGraphicsModules { get; set; } = [];
    public JsonElement? BridgeSnapshot { get; set; }
    public string? PresentedScreenshot { get; set; }
    public bool ScreenshotHasVariation { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset CompletedAt { get; set; }
    public List<string> Log { get; set; } = [];
    public List<SmokeTestResult> Attempts { get; set; } = [];
    [JsonIgnore] public PhoenixControlResponse? VerifiedBridgeReply { get; set; }
}
