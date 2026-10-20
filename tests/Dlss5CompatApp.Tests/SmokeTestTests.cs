using System.Text.Json;
using System.Text.Json.Nodes;

namespace Dlss5CompatApp.Tests;

public sealed class SmokeTestTests
{
    static JsonObject Summary(int completed = 2, int pid = 123) => new()
    {
        ["schema"] = "production-game-phoenix/2",
        ["compiled_mode"] = "production_candidate_only",
        ["terminal"] = false,
        ["error"] = "",
        ["process_id"] = pid,
        ["completed_frames"] = completed,
        ["submitted_frames"] = completed,
        ["own_candidate_launches_attempted"] = completed * 154,
        ["phoenix_routing"] = new JsonObject { ["frames"] = completed, ["active_mode"] = 2 },
        ["direct_game"] = new JsonObject
        {
            ["enabled"] = true,
            ["status"] = "Own network output written back to the game DLSS output"
        },
        ["worker_execution"] = new JsonObject { ["kind"] = "native" }
    };

    static SmokeTestRunner.PhoenixSmokeEvidence Evaluate(JsonObject after, JsonObject? before = null, int? pid = null) =>
        SmokeTestRunner.EvaluatePhoenixSummary(before?.ToJsonString(), after.ToJsonString(), pid);

    [Theory]
    [InlineData("NGX initialized")]
    [InlineData("feature 18 created")]
    [InlineData("native D3D12 frame 30 delivered")]
    [InlineData("D3D11 CPU fallback frame 20 delivered")]
    [InlineData("native OpenGL CPU bridge ready")]
    [InlineData("bridge delivered")]
    [InlineData("vkmirror delivered")]
    [InlineData("synthetic frame delivered")]
    [InlineData("RenoDX initialized; frame 50 delivered")]
    public void LegacyAndTransportLogsCannotPassEvenWithCustomSuccessPattern(string text)
    {
        var result = new SmokeTestResult();
        var missing = new SmokeTestRunner.PhoenixSmokeEvidence(false, false, "No fresh Phoenix DLSS5 completion summary.");
        Assert.False(SmokeTestRunner.EvaluateEvidence(result, text, missing, [".*"], []));
        Assert.False(result.RuntimeOutputCompleted);
        Assert.False(result.DisplayedOutputVerified);
        Assert.Contains("not Phoenix DLSS5 proof", result.Warning);
        Assert.Equal(".*", result.SuccessPattern);
    }

    [Fact]
    public void AdvancingProductionOwnNetworkOutputPassesWithExplicitScope()
    {
        var evidence = Evaluate(Summary(), Summary(1), 123);
        Assert.True(evidence.OutputCompleted);
        var result = new SmokeTestResult();
        Assert.True(SmokeTestRunner.EvaluateEvidence(result, "", evidence, [], []));
        Assert.False(result.DisplayedOutputVerified);
        Assert.Contains("visual quality", result.Warning);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(result));
        Assert.True(json.RootElement.GetProperty("RuntimeOutputCompleted").GetBoolean());
        Assert.False(json.RootElement.GetProperty("DisplayedOutputVerified").GetBoolean());
        Assert.Contains("completion only", json.RootElement.GetProperty("ValidationScope").GetString());
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(2, 2)]
    [InlineData(1, 2)]
    public void MissingUnchangedOrRegressedCompletionCannotPass(int current, int previous)
    {
        Assert.False(Evaluate(Summary(current), Summary(previous)).OutputCompleted);
    }

    [Theory]
    [InlineData("own_candidate_launches_attempted")]
    [InlineData("submitted_frames")]
    public void CompletionAloneCannotPassWithoutConsistentOwnExecution(string key)
    {
        var after = Summary(); after[key] = 0;
        Assert.False(Evaluate(after).OutputCompleted);
    }

    [Fact]
    public void RoutedOutputMustAdvance()
    {
        var after = Summary(); after["phoenix_routing"]!["frames"] = 1;
        Assert.False(Evaluate(after, Summary(1)).OutputCompleted);
    }

    [Theory]
    [InlineData("schema", "production-game-phoenix-diagnostic/1")]
    [InlineData("schema", "production-game-y41/1")]
    [InlineData("compiled_mode", "diagnostic_native_and_candidate")]
    public void LegacyDiagnosticAndUnknownSchemasAreUnverified(string key, string value)
    {
        var after = Summary(); after[key] = value;
        Assert.False(Evaluate(after).OutputCompleted);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(4)]
    public void OriginalOrNativeNrRoutingIsNotPhoenixOwnNetworkCompletion(int mode)
    {
        var after = Summary(); after["phoenix_routing"]!["active_mode"] = mode;
        Assert.False(Evaluate(after).OutputCompleted);
    }

    [Fact]
    public void NativeNrWritebackAndDisabledDirectProviderCannotPass()
    {
        var after = Summary();
        after["direct_game"]!["status"] = "Native NR output (resolved) written back to the game DLSS output";
        Assert.False(Evaluate(after).OutputCompleted);
        after = Summary(); after["direct_game"]!["enabled"] = false;
        Assert.False(Evaluate(after).OutputCompleted);
    }

    [Fact]
    public void PortableWorkerIsExplicitlyUnverified()
    {
        var after = Summary(); after["worker_execution"]!["kind"] = "portable_cpp";
        var evidence = Evaluate(after);
        Assert.False(evidence.OutputCompleted);
        Assert.Contains("Portable worker", evidence.Summary);
    }

    [Theory]
    [InlineData("{")]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("[]")]
    public void MalformedOrIncompleteSummaryFailsClosed(string text)
    {
        Assert.False(SmokeTestRunner.EvaluatePhoenixSummary(null, text).OutputCompleted);
        Assert.False(SmokeTestRunner.EvaluatePhoenixSummary(text, Summary().ToJsonString()).OutputCompleted);
    }

    [Fact]
    public void IncorrectFieldTypesFailClosed()
    {
        var after = Summary(); after["completed_frames"] = "2";
        Assert.False(Evaluate(after).OutputCompleted);
        after = Summary(); after["terminal"] = "false";
        Assert.False(Evaluate(after).OutputCompleted);
    }

    [Fact]
    public void TerminalOrErrorSummaryOverridesCompletion()
    {
        var after = Summary(); after["terminal"] = true;
        Assert.True(Evaluate(after).Failed);
        after = Summary(); after["error"] = "GPU removed";
        var result = new SmokeTestResult();
        Assert.False(SmokeTestRunner.EvaluateEvidence(result, "", Evaluate(after), [], []));
        Assert.NotNull(result.Error);
    }

    [Fact]
    public void PriorOrLateFailuresCannotBeOverwrittenBySuccess()
    {
        var result = new SmokeTestResult(); var good = Evaluate(Summary());
        Assert.True(SmokeTestRunner.EvaluateEvidence(result, "", good, [], []));
        Assert.False(SmokeTestRunner.EvaluateEvidence(result, "host returned no output", good, [], ["host returned no output"]));
        Assert.False(SmokeTestRunner.EvaluateEvidence(result, "", good, [], []));
    }

    [Fact]
    public void CustomPatternsAddRequirementsWithoutReplacingCompletionGate()
    {
        var result = new SmokeTestResult(); var good = Evaluate(Summary());
        Assert.False(SmokeTestRunner.EvaluateEvidence(result, "", good, ["checkpoint"], []));
        Assert.Contains("Additional configured log requirement", result.RuntimeEvidence);
        Assert.True(SmokeTestRunner.EvaluateEvidence(result, "checkpoint", good, ["checkpoint"], []));
    }

    [Fact]
    public void NewProcessCountersCanRestartButDirectProcessMustMatch()
    {
        Assert.True(Evaluate(Summary(1, 456), Summary(50, 123), 456).OutputCompleted);
        Assert.False(Evaluate(Summary(1, 456), Summary(50, 123), 123).OutputCompleted);
    }

    static GameCandidate Game(string root, GraphicsApi api = GraphicsApi.DirectX12, bool native = true) =>
        new(root, Path.Combine(root, "game.exe"), "Fixture", "game.exe", CpuArch.X64, api, "fixture", 200000,
            HasNativeDlssSignal: native);

    [Theory]
    [InlineData("DirectX12", true, "PhoenixOutput/production_game_phoenix_summary.json")]
    [InlineData("DirectX11", false, "host64/PhoenixOutput/production_game_phoenix_summary.json")]
    [InlineData("OpenXr", false, "_DLSS5_OpenXR/host64/PhoenixOutput/production_game_phoenix_summary.json")]
    public void SummariesAreReadOnlyFromTheSelectedRoute(string api, bool native, string relative)
    {
        using var workspace = new TestWorkspace(); var game = Game(workspace.Root, Enum.Parse<GraphicsApi>(api), native);
        var paths = SmokeTestRunner.PhoenixSummaryPaths(game).ToArray();
        Assert.Contains(Path.Combine(workspace.Root, relative.Replace('/', Path.DirectorySeparatorChar)), paths);
        Assert.Equal(native ? 1 : 2, paths.Length);
    }

    [Fact]
    public void OldSummaryAndUnchangedFreshRewriteCannotPass()
    {
        using var workspace = new TestWorkspace(); var game = Game(workspace.Root);
        var path = SmokeTestRunner.PhoenixSummaryPaths(game).Single();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, Summary().ToJsonString());
        var since = DateTimeOffset.UtcNow;
        File.SetLastWriteTimeUtc(path, since.AddMinutes(-1).UtcDateTime);
        var emptyBaseline = new Dictionary<string, string?> { [path] = null };
        Assert.False(SmokeTestRunner.ReadPhoenixEvidence(game, emptyBaseline, since, 123).OutputCompleted);
        var baseline = SmokeTestRunner.CapturePhoenixSummaries(game);
        File.SetLastWriteTimeUtc(path, since.AddSeconds(1).UtcDateTime);
        Assert.False(SmokeTestRunner.ReadPhoenixEvidence(game, baseline, since, 123).OutputCompleted);
        File.WriteAllText(path, Summary(3).ToJsonString());
        File.SetLastWriteTimeUtc(path, since.AddSeconds(1).UtcDateTime);
        Assert.True(SmokeTestRunner.ReadPhoenixEvidence(game, baseline, since, 123).OutputCompleted);
        Assert.False(SmokeTestRunner.ReadPhoenixEvidence(game, new Dictionary<string, string?>(), since, 123).OutputCompleted);
    }

    [Fact]
    public void OversizedSummaryIsUnverified()
    {
        using var workspace = new TestWorkspace(); var game = Game(workspace.Root);
        var baseline = SmokeTestRunner.CapturePhoenixSummaries(game);
        var path = SmokeTestRunner.PhoenixSummaryPaths(game).Single();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var since = DateTimeOffset.UtcNow;
        File.WriteAllText(path, new string(' ', 1024 * 1024 + 1));
        File.SetLastWriteTimeUtc(path, since.AddSeconds(1).UtcDateTime);
        Assert.False(SmokeTestRunner.ReadPhoenixEvidence(game, baseline, since, 123).OutputCompleted);
    }

    [Fact]
    public void FreshHostTerminalStateOverridesAnotherSuccessfulSummary()
    {
        using var workspace = new TestWorkspace(); var game = Game(workspace.Root, GraphicsApi.DirectX11, false);
        var baseline = SmokeTestRunner.CapturePhoenixSummaries(game);
        var paths = SmokeTestRunner.PhoenixSummaryPaths(game).ToArray();
        var since = DateTimeOffset.UtcNow;
        foreach (var path in paths)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, Summary().ToJsonString());
            File.SetLastWriteTimeUtc(path, since.AddSeconds(1).UtcDateTime);
        }
        var failed = Summary(); failed["terminal"] = true;
        File.WriteAllText(paths[1], failed.ToJsonString());
        File.SetLastWriteTimeUtc(paths[1], since.AddSeconds(1).UtcDateTime);
        var evidence = SmokeTestRunner.ReadPhoenixEvidence(game, baseline, since, 123);
        Assert.False(evidence.OutputCompleted);
        Assert.True(evidence.Failed);
    }

    [Fact]
    public void AntiCheatAcknowledgementDefaultsFalseAndMustBeExplicitInConfig()
    {
        Assert.False(new SmokeTestConfig().AcknowledgeAntiCheatRisk);
        Assert.False(JsonSerializer.Deserialize<SmokeTestConfig>("{}")!.AcknowledgeAntiCheatRisk);
        Assert.True(JsonSerializer.Deserialize<SmokeTestConfig>("{\"AcknowledgeAntiCheatRisk\":true}")!.AcknowledgeAntiCheatRisk);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AntiCheatWarningRequiresAcknowledgementAndLogsTheWarning(bool acknowledged)
    {
        var checks = new InstallPreflightResult([new("anti-cheat", PreflightSeverity.Warning, "Fixture anti-cheat risk")]);
        var messages = new List<string>();
        if (acknowledged)
            SmokeTestRunner.RequirePreflightAcknowledgement(checks, true, messages.Add);
        else
        {
            var error = Assert.Throws<InvalidOperationException>(() =>
                SmokeTestRunner.RequirePreflightAcknowledgement(checks, false, messages.Add));
            Assert.Contains("AcknowledgeAntiCheatRisk", error.Message);
        }
        Assert.Contains(messages, message => message.Contains("Fixture anti-cheat risk"));
    }

    [Fact]
    public void AcknowledgementDoesNotOverrideBlockingPreflightFindings()
    {
        var checks = new InstallPreflightResult([
            new("anti-cheat", PreflightSeverity.Warning, "Fixture risk"),
            new("mod-conflict", PreflightSeverity.Error, "Fixture blocking conflict")]);
        var error = Assert.Throws<InvalidOperationException>(() =>
            SmokeTestRunner.RequirePreflightAcknowledgement(checks, true, _ => { }));
        Assert.Contains("Fixture blocking conflict", error.Message);
    }

    [Fact]
    public void ReadOnlyPreflightFindsAntiCheatWithoutAnyFolderWrites()
    {
        using var workspace = new TestWorkspace(); var target = PreflightTarget(workspace);
        var game = GameScanner.ScanSingleExe(target.Exe)!;
        var before = Snapshot(workspace.Root);
        var checks = InstallerEngine.CheckPreflight(game);
        Assert.True(checks.CanInstall, checks.DisplayText);
        Assert.True(checks.RequiresAntiCheatAcknowledgement);
        Assert.Equal(before, Snapshot(workspace.Root));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RefusedTargetNeverStopsHelpersRestoresClearsLogsOrWrites(bool waitForExit)
    {
        using var workspace = new TestWorkspace(); var target = PreflightTarget(workspace);
        var config = new SmokeTestConfig
        {
            UpdatePayload = false, RestoreBeforeInstall = true, RestoreAfterRun = true,
            ClearLogsBeforeRun = true, WaitForExit = waitForExit
        };
        var payload = PayloadScanner.Scan(workspace.FilePath("missing-payload"));
        var before = Snapshot(workspace.Root); var helperCalls = 0;
        var result = await SmokeTestRunner.RunOneAsync(target, config, payload, (_, _, _) =>
        {
            helperCalls++;
            return Task.CompletedTask;
        });
        Assert.Contains("Anti-cheat acknowledgement required", result.Error);
        Assert.NotNull(result.GameRoot);
        Assert.False(result.Passed);
        Assert.Null(result.Restore); Assert.Null(result.RestoreAfterRun); Assert.Null(result.ProcessId);
        Assert.Equal(0, helperCalls);
        Assert.Equal(before, Snapshot(workspace.Root));
    }

    [Fact]
    public async Task AcknowledgedTargetWithMissingPayloadStillSkipsHelperCleanupAndWrites()
    {
        using var workspace = new TestWorkspace(); var target = PreflightTarget(workspace);
        var payload = PayloadScanner.Scan(workspace.FilePath("missing-payload"));
        var before = Snapshot(workspace.Root); var helperCalls = 0;
        var result = await SmokeTestRunner.RunOneAsync(target, new SmokeTestConfig { AcknowledgeAntiCheatRisk = true },
            payload, (_, _, _) => { helperCalls++; return Task.CompletedTask; });
        Assert.Contains("Payload is missing", result.Error);
        Assert.Equal(0, helperCalls);
        Assert.Equal(before, Snapshot(workspace.Root));
    }

    [Fact]
    public async Task CancelledVerificationCannotInstallLaunchOrRetry()
    {
        using var workspace = new TestWorkspace();
        var target = PreflightTarget(workspace);
        var config = new SmokeTestConfig
        {
            CancellationToken = new CancellationToken(true),
            PayloadRoot = workspace.FilePath("must-not-create"), Tests = [target]
        };
        var before = Snapshot(workspace.Root);
        var helpers = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => SmokeTestRunner.RunAsync(config, workspace.Root));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => SmokeTestRunner.RunOneAsync(target, config,
            PayloadScanner.Scan(config.PayloadRoot), (_, _, _) => { helpers++; return Task.CompletedTask; }));
        Assert.Equal(0, helpers);
        Assert.Equal(before, Snapshot(workspace.Root));
    }

    [Fact]
    public async Task WholePlanRefusalPrecedesPayloadDirectoryCreationAndReportWrites()
    {
        using var workspace = new TestWorkspace(); var target = PreflightTarget(workspace);
        var config = new SmokeTestConfig
        {
            UpdatePayload = false, PayloadRoot = workspace.FilePath("game/must-not-create-payload"),
            ReportPath = workspace.FilePath("game/must-not-create-report.json"), Tests = [target]
        };
        var before = Snapshot(workspace.Root); var helperCalls = 0;
        var report = await SmokeTestRunner.RunAsync(config, workspace.Root,
            (_, _, _) => { helperCalls++; return Task.CompletedTask; });
        Assert.Contains("no payload updates or target actions", report.Error);
        Assert.False(report.PreflightApproved);
        Assert.Contains("Anti-cheat acknowledgement required", Assert.Single(report.Tests).Error);
        Assert.Equal(0, helperCalls);
        Assert.Equal(before, Snapshot(workspace.Root));
    }

    [Fact]
    public async Task RefusedCommandEntryPointDoesNotWriteEvenItsReportIntoGameFolder()
    {
        using var workspace = new TestWorkspace(); var target = PreflightTarget(workspace);
        var config = new SmokeTestConfig
        {
            UpdatePayload = false, PayloadRoot = workspace.FilePath("game/must-not-create-payload"),
            ReportPath = workspace.FilePath("game/must-not-create-report.json"), Tests = [target]
        };
        var configPath = workspace.FilePath("smoke-config.json");
        File.WriteAllText(configPath, JsonSerializer.Serialize(config));
        var before = Snapshot(workspace.Root);
        Assert.Equal(1, await SmokeTestRunner.RunFromArgsAsync(["--smoke-test", configPath]));
        Assert.Equal(before, Snapshot(workspace.Root));
    }

    static SmokeTestTarget PreflightTarget(TestWorkspace workspace)
    {
        var exe = workspace.WritePe("game/game.exe", CpuArch.X64, "d3d12.dll");
        File.WriteAllText(workspace.FilePath("game/EasyAntiCheat_EOS.dll"), "Fixture marker; not executable");
        foreach (var name in new[] { "dlss5-feed.log", "ReShade.log", "host64/dlss5-feed-host.log", "_DLSS5_Compat_Backup/keep.bin" })
        {
            var path = workspace.FilePath("game/" + name);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "Keep this fixture exactly.");
        }
        return new() { Exe = exe };
    }

    static string[] Snapshot(string root) => Directory.GetFileSystemEntries(root, "*", SearchOption.AllDirectories)
        .OrderBy(path => path, StringComparer.Ordinal).Select(path => Directory.Exists(path)
            ? "Directory: " + Path.GetRelativePath(root, path)
            : "File: " + Path.GetRelativePath(root, path) + " | " + File.GetLastWriteTimeUtc(path).Ticks + " | "
                + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path))))
        .ToArray();
}
