namespace Dlss5CompatApp;

static partial class SmokeTestRunner
{
    internal static async Task<SmokeTestResult> RunOneAsync(SmokeTestTarget test, SmokeTestConfig config,
        PayloadInfo payload, Func<string, int, Action<string>, Task>? stopHelpers = null)
    {
        config.CancellationToken.ThrowIfCancellationRequested();
        var first = await RunAttemptAsync(test, config, payload, stopHelpers);
        if (config.CancellationToken.IsCancellationRequested) return first;
        if (config.VerifyExistingInstall || !config.AutoResolve || (test.WaitForExit ?? config.WaitForExit) || test.Api is not null ||
            test.PreferredRoute is not null || test.ProxyName is not null || first.InstallCompletedAt is null || first.StartupDialog is not null || IsHealthy(first))
        {
            if (first.Passed) SaveRendererProfile(DetectTarget(test, config.VerifyExistingInstall), first);
            return first;
        }
        var game = DetectTarget(test);
        var attempts = new List<SmokeTestResult> { first };
        var choices = RetryChoices(game).Where(choice => payload.IsReadyFor(choice)).Take(12).ToArray();
        var best = first;
        GameCandidate bestGame = game;
        foreach (var choice in choices)
        {
            if (config.CancellationToken.IsCancellationRequested) break;
            await WaitForPostExitFileReleaseAsync(() => InstallerEngine.CheckPreflight(choice, payload),
                message => first.Log.Add(message), config.CancellationToken);
            var retry = System.Text.Json.JsonSerializer.Deserialize<SmokeTestTarget>(
                System.Text.Json.JsonSerializer.Serialize(test))!;
            retry.Api = choice.Api.ToString();
            retry.PreferredRoute = choice.PreferredRoute?.ToString();
            retry.ProxyName = choice.ProxyName;
            var result = await RunAttemptAsync(retry, config, payload, stopHelpers);
            attempts.Add(result);
            if (Score(result) > Score(best)) { best = result; bestGame = choice; }
            if (IsHealthy(result)) break;
        }
        // Report every failed experiment, but leave the strongest verified route
        // installed. Never leave the final failed retry over a successful route.
        if (best.Passed && !ReferenceEquals(best, attempts[^1]) && !(test.RestoreAfterRun ?? config.RestoreAfterRun))
            try
            {
                var install = await new InstallerEngine(message => best.Log.Add(message)).InstallAsync(bestGame, payload,
                    test.ForceVrEyeSplit ?? config.ForceVrEyeSplit);
                install.RequireComplete();
            }
            catch (Exception error)
            {
                best.Passed = false;
                best.Error = "The verified route could not be restored after testing alternatives: " + error.Message;
            }
        if (best.Passed) SaveRendererProfile(bestGame, best);
        var report = System.Text.Json.JsonSerializer.Deserialize<SmokeTestResult>(
            System.Text.Json.JsonSerializer.Serialize(best))!;
        report.Attempts = attempts;
        report.StartedAt = first.StartedAt;
        report.CompletedAt = DateTimeOffset.Now;
        return report;
    }

    internal static async Task WaitForPostExitFileReleaseAsync(Func<InstallPreflightResult> inspect,
        Action<string> log, CancellationToken cancellationToken)
    {
        var deadline = System.Diagnostics.Stopwatch.StartNew();
        bool announced = false;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var errors = inspect().Findings.Where(finding => finding.Severity == PreflightSeverity.Error).ToArray();
            // The previous owned game is already closed. Only wait for transient
            // sharing locks; mods, another live process or unsafe paths still
            // fail the normal preflight immediately.
            if (errors.Length == 0 || errors.Any(error => error.Code != "file-unavailable") ||
                deadline.Elapsed >= TimeSpan.FromSeconds(15)) return;
            if (!announced) { log("Waiting for graphics files to be released after the previous game exit."); announced = true; }
            await Task.Delay(250, cancellationToken);
        }
    }

    static void SaveRendererProfile(GameCandidate game, SmokeTestResult result)
    {
        if (result.VerifiedBridgeReply is not { } reply) return;
        try
        {
            if (VerifiedRendererProfiles.Record(game, reply, result.ProcessId!.Value))
                result.Log.Add("Saved the verified renderer profile for this executable's content and architecture.");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { result.Log.Add("Renderer profile could not be saved: " + error.Message); }
    }

    internal static IEnumerable<GameCandidate> RetryChoices(GameCandidate game)
    {
        if (game.Route == InstallRoute.X64DirectPhoenix)
            yield return game with { PreferredRoute = GameRoutePreference.Feeder };
        if (game.Api == GraphicsApi.DirectX11)
            yield return game with { ProxyName = game.ProxyName == "d3d11.dll" ? "dxgi.dll" : "d3d11.dll",
                PreferredRoute = GameRoutePreference.Feeder };
        if (game.Arch == CpuArch.X86 && game.Api == GraphicsApi.DirectX8)
            yield return game.WithApi(GraphicsApi.DirectX8DgVoodoo) with { PreferredRoute = GameRoutePreference.Feeder, ProxyName = null };
        foreach (var api in game.AllApis.Where(api => api != game.Api && api is
            GraphicsApi.Vulkan or GraphicsApi.DirectX12 or GraphicsApi.DirectX11 or GraphicsApi.DirectX10 or GraphicsApi.DirectX10_1 or GraphicsApi.DirectX9 or GraphicsApi.OpenGl
                or GraphicsApi.DirectX7OrOlder or GraphicsApi.DirectX8 or GraphicsApi.DirectX8DgVoodoo
                or GraphicsApi.Glide211 or GraphicsApi.Glide245 or GraphicsApi.Glide31 or GraphicsApi.Glide31Napalm)
            .Where(api => game.Api != GraphicsApi.DirectX8 || api != GraphicsApi.DirectX8DgVoodoo)
            .OrderBy(api => api == GraphicsApi.Vulkan ? 0 : api == GraphicsApi.DirectX11 ? 1 : 2))
            yield return game.WithApi(api) with { PreferredRoute = GameRoutePreference.Feeder, ProxyName = null };
    }

    static double Score(SmokeTestResult result) => result.Passed
        ? 1000 + (result.ScreenshotHasVariation ? 100 : 0) + Math.Min(240, PresentFps(result)) : 0;

    static bool IsHealthy(SmokeTestResult result) => result.Passed &&
        (result.BridgeSnapshot is null || PresentFps(result) >= 10);

    static double PresentFps(SmokeTestResult result) => result.BridgeSnapshot is { } snapshot &&
        snapshot.TryGetProperty("present_fps", out var fps) && fps.TryGetDouble(out var value) && double.IsFinite(value)
        ? Math.Max(0, value) : 0;
}
