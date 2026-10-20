using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Dlss5CompatApp.Tests;

public sealed class PhoenixControlTests
{
    [Fact]
    public void RendererDisplayNamesDoNotChangeSavedBackendValues()
    {
        var choices = PhoenixControlContract.Definition(PhoenixSetting.Backend).Choices!;
        Assert.Equal("NVIDIA DLSS5 DLL", choices[1].Label);
        Assert.Equal("Phoenix DLSS5", choices[3].Label);
        Assert.Equal(1, choices[1].Value);
        Assert.Equal(3, choices[3].Value);
        foreach (var key in new[] { PhoenixSetting.SourceA, PhoenixSetting.SourceB })
            Assert.Equal(new[] { "Input", "NVIDIA DLSS5", "Phoenix DLSS5" },
                PhoenixControlContract.Definition(key).Choices!.Select(choice => choice.Label));
        Assert.Equal("phoenix-control/1", PhoenixControlContract.Schema);
    }

    [Theory]
    [InlineData("123-456", true)]
    [InlineData("123-456-789-1000", true)]
    [InlineData("123-456-0-0", true)]
    [InlineData("123-457-789-1000", false)]
    [InlineData("123-456-789", false)]
    [InlineData("123-456-789-1000-extra", false)]
    [InlineData("123-456-text-1000", false)]
    [InlineData("123-456-+789-1000", false)]
    [InlineData("123-456-0789-1000", false)]
    public void RuntimeGenerationSuffixKeepsExactProcessIdentity(string session, bool valid) =>
        Assert.Equal(valid, PhoenixControlClient.SessionMatchesProcess(session, "123-456"));

    [Fact]
    public async Task CompatibilityRuntimeSessionsRoundTripAndRejectEditsAfterReset()
    {
        using var bridge = new FakeBridge();
        var original = bridge.Session + "-123-456";
        bridge.WriteEndpoint(original);
        bridge.StartServer(2);
        var client = bridge.Client();
        var beforeReset = await client.StatusAsync();
        Assert.Equal(original, beforeReset.Session);
        bridge.WriteEndpoint(bridge.Session + "-123-457");
        await Assert.ThrowsAsync<PhoenixSessionChangedException>(() =>
            client.SendAsync(PhoenixOperation.Set, new PhoenixControlSettings().Set(PhoenixSetting.Tone, .5f), expectedSession: original));
        var afterReset = await client.StatusAsync();
        Assert.NotEqual(beforeReset.Session, afterReset.Session);
        await bridge.ServerFinished();
        Assert.All(bridge.Requests, request => Assert.Equal("status", request.GetProperty("op").GetString()));
    }

    [Fact]
    public async Task StatusUsesBoundedTickDeadlineAndReturnsDetachedReadonlySnapshot()
    {
        using var bridge = new FakeBridge();
        bridge.StartServer(1);
        var response = await bridge.Client().StatusAsync();
        var request = Assert.Single(bridge.Requests);
        Assert.Equal("phoenix-control/1", request.GetProperty("schema").GetString());
        Assert.Equal(bridge.Session, request.GetProperty("session").GetString());
        Assert.Equal(bridge.Identity.Pid, request.GetProperty("pid").GetInt32());
        Assert.InRange(request.GetProperty("expires_tick_ms").GetUInt64(), FakeBridge.Tick + 1, FakeBridge.Tick + 5000);
        Assert.Equal("status", request.GetProperty("op").GetString());
        Assert.False(request.TryGetProperty("settings", out _));
        Assert.False(request.TryGetProperty("frames", out _));
        Assert.True(response.Ok);
        Assert.Equal(0, response.Snapshot.GetProperty("phoenix").GetProperty("recorded_frames").GetInt32());
        File.Delete(bridge.PathFor("response.json"));
        Assert.Equal("not_started", response.Snapshot.GetProperty("execution").GetProperty("kind").GetString());
        Assert.Empty(Directory.GetFiles(bridge.Directory, "*.tmp"));
    }

    [Fact]
    public async Task SetUsesExactVisitSettingsKeysAndCopiesEditsBeforeScheduling()
    {
        using var bridge = new FakeBridge();
        bridge.StartServer(1);
        var settings = new PhoenixControlSettings().Set(PhoenixSetting.Backend, 3)
            .Set(PhoenixSetting.ExecutionDevice, "cpu").Set(PhoenixSetting.Tone, 1.25f)
            .Set(PhoenixSetting.Structure, .8f).Set(PhoenixSetting.OverrideStrengths, true);
        var command = bridge.Client().SetAsync(settings);
        settings.Set(PhoenixSetting.Tone, 0f);
        await command;
        var wire = Assert.Single(bridge.Requests).GetProperty("settings");
        Assert.Equal(1.25f, wire.GetProperty("Tone").GetSingle());
        Assert.Equal(.8f, wire.GetProperty("Structure").GetSingle());
        Assert.True(wire.GetProperty("OverrideStrengths").GetBoolean());
        Assert.False(wire.TryGetProperty("LocalTone", out _));
        Assert.False(wire.TryGetProperty("LocalStructure", out _));
    }

    [Theory]
    [InlineData("ResetHistory", "reset_history")]
    [InlineData("Screenshot", "screenshot")]
    [InlineData("Save", "save")]
    [InlineData("ValidateModel", "validate_model")]
    public async Task CommandsUseExactOperationsWithoutInventingCompletion(string operationName, string expected)
    {
        var operation = Enum.Parse<PhoenixOperation>(operationName);
        using var bridge = new FakeBridge();
        bridge.StartServer(1);
        var response = await bridge.Client().SendAsync(operation);
        Assert.Equal(expected, Assert.Single(bridge.Requests).GetProperty("op").GetString());
        Assert.Equal("queued", response.Phase);
        Assert.Equal("not_started", response.Snapshot.GetProperty("execution").GetProperty("kind").GetString());
        Assert.Equal(operation == PhoenixOperation.Screenshot ? "Phoenix-bridge-" + response.Id : null, response.ScreenshotPostfix);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(8)]
    public async Task CapturePublishesOnlyBoundedIntegerFrames(int frames)
    {
        using var bridge = new FakeBridge();
        bridge.StartServer(1);
        await bridge.Client().SendAsync(PhoenixOperation.Capture, frames: frames);
        var request = Assert.Single(bridge.Requests);
        Assert.Equal(frames, request.GetProperty("frames").GetInt32());
        Assert.False(request.TryGetProperty("settings", out _));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(9)]
    [InlineData(-1)]
    [InlineData(null)]
    public void InvalidCaptureNeverPublishes(int? frames)
    {
        using var bridge = new FakeBridge();
        Assert.Throws<ArgumentOutOfRangeException>(() => { _ = bridge.Client().SendAsync(PhoenixOperation.Capture, frames: frames); });
        Assert.False(File.Exists(bridge.PathFor("request.json")));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(30001)]
    public void InvalidExpiryNeverPublishes(int milliseconds)
    {
        using var bridge = new FakeBridge();
        Assert.Throws<ArgumentOutOfRangeException>(() => { _ = bridge.Client().SendAsync(PhoenixOperation.Status, timeout: TimeSpan.FromMilliseconds(milliseconds)); });
        Assert.False(File.Exists(bridge.PathFor("request.json")));
    }

    [Fact]
    public void TypedSettingsRejectUnknownKeysTypesNonfiniteAndOutOfRangeValues()
    {
        var settings = new PhoenixControlSettings();
        Assert.Throws<ArgumentOutOfRangeException>(() => settings.Set((PhoenixSetting)999, 1));
        Assert.Throws<ArgumentException>(() => settings.Set(PhoenixSetting.Backend, "3"));
        Assert.Throws<ArgumentException>(() => settings.Set(PhoenixSetting.Tone, 1));
        Assert.Throws<ArgumentException>(() => settings.Set(PhoenixSetting.OverrideStrengths, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => settings.Set(PhoenixSetting.Tone, float.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() => settings.Set(PhoenixSetting.Structure, float.PositiveInfinity));
        Assert.Throws<ArgumentOutOfRangeException>(() => settings.Set(PhoenixSetting.Intensity, 2.01f));
        Assert.Throws<ArgumentException>(() => settings.Set(PhoenixSetting.NrFeature, 14));
        Assert.Throws<ArgumentException>(() => settings.Set(PhoenixSetting.ExecutionDevice, "cuda"));
        Assert.Throws<ArgumentException>(() => settings.Set(PhoenixSetting.ModelFolder, "bad\0path"));
        Assert.Throws<ArgumentException>(() => settings.Set(PhoenixSetting.ModelDll, new string('x', 4096)));
        Assert.Throws<ArgumentException>(() => settings.Set(PhoenixSetting.WorkerEngine, "custom"));
        Assert.DoesNotContain(PhoenixControlContract.Settings, item => item.WireKey is "ScreenshotRequest" or "CaptureBurst" or "LocalTone" or "LocalStructure");
        Assert.Equal(Enum.GetValues<PhoenixSetting>().Length, PhoenixControlContract.Settings.Count);
        Assert.Equal(PhoenixControlContract.Settings.Count, PhoenixControlContract.Settings.Select(item => item.WireKey).Distinct().Count());
    }

    [Theory]
    [InlineData("native")]
    [InlineData("cpu")]
    [InlineData("sycl:cpu")]
    [InlineData("cuda:GPU-01234567-89ab-cdef-0123-456789abcdef")]
    [InlineData("vulkan:0123456789abcdef0123456789abcdef")]
    [InlineData("hip:0123456789abcdef0123456789abcdef")]
    [InlineData("sycl:0123456789abcdef0123456789abcdef")]
    public void ExactNativeDeviceIdsAreAccepted(string value) => new PhoenixControlSettings().Set(PhoenixSetting.ExecutionDevice, value);

    [Fact]
    public void WrongOperationPayloadAndEmptyOrInconsistentSettingsAreRejected()
    {
        using var bridge = new FakeBridge();
        var client = bridge.Client();
        Assert.Throws<ArgumentException>(() => { _ = client.SendAsync(PhoenixOperation.Set); });
        Assert.Throws<ArgumentException>(() => { _ = client.SetAsync(new()); });
        Assert.Throws<ArgumentException>(() => { _ = client.SendAsync(PhoenixOperation.Status, new PhoenixControlSettings().Set(PhoenixSetting.Backend, 0)); });
        Assert.Throws<ArgumentOutOfRangeException>(() => { _ = client.SendAsync(PhoenixOperation.Status, frames: 1); });
        Assert.Throws<ArgumentOutOfRangeException>(() => { _ = client.SendAsync((PhoenixOperation)999); });
        Assert.Throws<ArgumentException>(() => { _ = client.SetAsync(new PhoenixControlSettings().Set(PhoenixSetting.MinimumBlocks, 8).Set(PhoenixSetting.TransformerBlocks, 2)); });
    }

    [Fact]
    public async Task StaleCreationFiletimeRejectsPidReuseBeforePublication()
    {
        using var bridge = new FakeBridge();
        bridge.WriteEndpoint(bridge.Identity.Pid + "-" + (bridge.Identity.CreationFileTime - 1));
        await Assert.ThrowsAsync<PhoenixSessionChangedException>(() => bridge.Client().StatusAsync());
        Assert.False(File.Exists(bridge.PathFor("request.json")));
    }

    [Fact]
    public async Task WrongProcessPathAndInspectorPidAreRejected()
    {
        using var bridge = new FakeBridge();
        var wrongPath = new PhoenixControlClient(bridge.Root, null, _ => bridge.Identity with { ImagePath = Path.Combine(bridge.Root + "-other", "game.exe") }, () => FakeBridge.Tick);
        await Assert.ThrowsAsync<InvalidDataException>(() => wrongPath.StatusAsync());
        var wrongPid = new PhoenixControlClient(bridge.Root, null, _ => bridge.Identity with { Pid = 999 }, () => FakeBridge.Tick);
        await Assert.ThrowsAsync<PhoenixSessionChangedException>(() => wrongPid.StatusAsync());
        Assert.False(File.Exists(bridge.PathFor("request.json")));
    }

    [Fact]
    public async Task ProcessExitAndMissingOrOversizedEndpointFailWithoutPublication()
    {
        using var bridge = new FakeBridge();
        var exited = new PhoenixControlClient(bridge.Root, null, _ => throw new IOException("process exited"), () => FakeBridge.Tick);
        await Assert.ThrowsAsync<IOException>(() => exited.StatusAsync());
        File.WriteAllText(bridge.PathFor("endpoint.json"), new string('x', 16385));
        await Assert.ThrowsAsync<InvalidDataException>(() => bridge.Client().StatusAsync());
        File.Delete(bridge.PathFor("endpoint.json"));
        await Assert.ThrowsAsync<FileNotFoundException>(() => bridge.Client().StatusAsync());
        Assert.False(File.Exists(bridge.PathFor("request.json")));
    }

    [Fact]
    public async Task SessionChangeDuringExchangeFailsAndNextStatusCanReconnect()
    {
        using var bridge = new FakeBridge();
        var client = bridge.Client();
        bridge.StartServer(1, _ =>
        {
            bridge.Identity = bridge.Identity with { CreationFileTime = bridge.Identity.CreationFileTime + 1 };
            bridge.WriteEndpoint();
            return Task.CompletedTask;
        });
        await Assert.ThrowsAsync<PhoenixSessionChangedException>(() => client.StatusAsync());
        await bridge.ServerFinished();
        bridge.StartServer(1);
        var response = await client.StatusAsync();
        Assert.Equal(bridge.Session, response.Session);
    }

    [Fact]
    public async Task PendingEditsCannotBeAppliedToANewSession()
    {
        using var bridge = new FakeBridge();
        var previous = bridge.Session;
        bridge.Identity = bridge.Identity with { CreationFileTime = bridge.Identity.CreationFileTime + 1 };
        bridge.WriteEndpoint();
        await Assert.ThrowsAsync<PhoenixSessionChangedException>(() => bridge.Client().SendAsync(PhoenixOperation.Set,
            new PhoenixControlSettings().Set(PhoenixSetting.Backend, 3), expectedSession: previous));
        Assert.False(File.Exists(bridge.PathFor("request.json")));
    }

    [Fact]
    public async Task StaleResponseIdIsIgnoredUntilMatchingResponseArrives()
    {
        using var bridge = new FakeBridge();
        bridge.Publish("response.json", new { id = "stale", ok = true });
        bridge.StartServer(1, async _ => await Task.Delay(80));
        var response = await bridge.Client().StatusAsync();
        Assert.NotEqual("stale", response.Id);
    }

    [Theory]
    [InlineData("session")]
    [InlineData("pid")]
    [InlineData("schema")]
    public async Task MatchingResponseWithWrongIdentityIsRejected(string field)
    {
        using var bridge = new FakeBridge();
        bridge.StartServer(1, transform: response => response[field] = field == "pid" ? 999 : "wrong");
        await Assert.ThrowsAsync<PhoenixSessionChangedException>(() => bridge.Client().StatusAsync());
    }

    [Fact]
    public async Task RejectionPreservesErrorAndSnapshotWithoutReportingSuccess()
    {
        using var bridge = new FakeBridge();
        bridge.StartServer(1, transform: response => { response["ok"] = false; response["phase"] = "rejected"; response["error"] = "Capture unavailable: no direct runner"; });
        var response = await bridge.Client().SendAsync(PhoenixOperation.Capture, frames: 1);
        Assert.False(response.Ok);
        Assert.Equal("rejected", response.Phase);
        Assert.Contains("no direct runner", response.Error);
        Assert.Equal(JsonValueKind.Object, response.Snapshot.ValueKind);
    }

    [Theory]
    [InlineData("request.json")]
    [InlineData("processing.json")]
    public async Task ActiveMailboxIsNeverOverwrittenOrDeletedOnTimeout(string name)
    {
        using var bridge = new FakeBridge();
        var content = "{\"id\":\"other-client\",\"expires_tick_ms\":0}";
        File.WriteAllText(bridge.PathFor(name), content);
        await Assert.ThrowsAsync<TimeoutException>(() => bridge.Client().SendAsync(PhoenixOperation.Status, timeout: TimeSpan.FromMilliseconds(100)));
        Assert.Equal(content, File.ReadAllText(bridge.PathFor(name)));
        Assert.False(File.Exists(bridge.PathFor(name == "request.json" ? "processing.json" : "request.json")));
    }

    [Fact]
    public async Task CancellationBeforePublicationWritesNothing()
    {
        using var bridge = new FakeBridge();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => bridge.Client().StatusAsync(cancelled.Token));
        Assert.False(File.Exists(bridge.PathFor("request.json")));
    }

    [Theory]
    [InlineData("request.json")]
    [InlineData("processing.json")]
    public async Task ExitedProcessMailboxIsRetainedAndNewSessionCanReconnect(string name)
    {
        using var bridge = new FakeBridge();
        var old = JsonSerializer.Serialize(new { schema = "phoenix-control/1", pid = 5678, session = "5678-123", id = "old-command", op = "status", expires_tick_ms = 0 });
        File.WriteAllText(bridge.PathFor(name), old);
        var client = new PhoenixControlClient(bridge.Root, null, pid => pid == bridge.Identity.Pid
            ? bridge.Identity : throw new PhoenixProcessExitedException(), () => FakeBridge.Tick);
        // Start only after recovery: the fake server must not claim the old request.
        var pending = client.StatusAsync();
        await Until(() => System.IO.Directory.GetFiles(bridge.Directory, name + ".retired-*").Length == 1);
        bridge.StartServer(1);
        Assert.True((await pending).Ok);
        Assert.Equal(old, File.ReadAllText(Assert.Single(System.IO.Directory.GetFiles(bridge.Directory, name + ".retired-*"))));
        await bridge.ServerFinished();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OtherLiveProcessOrUnreadableIdentityCannotBeRetired(bool unreadable)
    {
        using var bridge = new FakeBridge();
        var old = JsonSerializer.Serialize(new { schema = "phoenix-control/1", pid = 5678, session = "5678-123", id = "other-command" });
        File.WriteAllText(bridge.PathFor("processing.json"), old);
        var client = new PhoenixControlClient(bridge.Root, null, pid => pid == bridge.Identity.Pid ? bridge.Identity
            : unreadable ? throw new IOException("Access denied") : new PhoenixProcessIdentity(5678, 123, bridge.Identity.ImagePath), () => FakeBridge.Tick);
        if (unreadable) await Assert.ThrowsAsync<IOException>(() => client.StatusAsync());
        else await Assert.ThrowsAsync<TimeoutException>(() => client.SendAsync(PhoenixOperation.Status, timeout: TimeSpan.FromMilliseconds(100)));
        Assert.Equal(old, File.ReadAllText(bridge.PathFor("processing.json")));
        Assert.Empty(System.IO.Directory.GetFiles(bridge.Directory, "*.retired-*"));
    }

    [Fact]
    public async Task CancellationAfterPublicationPreservesRequestAndReleasesMutex()
    {
        using var bridge = new FakeBridge();
        using var cancelled = new CancellationTokenSource();
        var pending = bridge.Client().StatusAsync(cancelled.Token);
        await Until(() => File.Exists(bridge.PathFor("request.json")));
        var original = File.ReadAllText(bridge.PathFor("request.json"));
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal(original, File.ReadAllText(bridge.PathFor("request.json")));
        bridge.StartServer(2);
        var next = await bridge.Client().StatusAsync();
        Assert.NotEqual(JsonDocument.Parse(original).RootElement.GetProperty("id").GetString(), next.Id);
        await bridge.ServerFinished();
        Assert.Equal(2, bridge.Requests.Count);
    }

    [Fact]
    public async Task CancellationWhileWaitingForAnActiveMailboxPreservesIt()
    {
        using var bridge = new FakeBridge();
        File.WriteAllText(bridge.PathFor("processing.json"), "other");
        using var cancelled = new CancellationTokenSource(80);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => bridge.Client().StatusAsync(cancelled.Token));
        Assert.Equal("other", File.ReadAllText(bridge.PathFor("processing.json")));
        Assert.False(File.Exists(bridge.PathFor("request.json")));
    }

    [Fact]
    public async Task ParallelClientsSerializeRequestsAndCorrelateEveryResponse()
    {
        using var bridge = new FakeBridge();
        bridge.StartServer(12, async _ => await Task.Delay(15));
        var tasks = Enumerable.Range(0, 12).Select(index => bridge.Client().SendAsync(PhoenixOperation.Capture, frames: index % 8 + 1)).ToArray();
        var results = await Task.WhenAll(tasks);
        Assert.Equal(12, bridge.Requests.Count);
        Assert.Equal(12, results.Select(result => result.Id).Distinct().Count());
        Assert.All(results, result => Assert.True(result.Ok));
        Assert.Equal(bridge.Requests.Select(request => request.GetProperty("id").GetString()).Order(), results.Select(result => result.Id).Order());
        Assert.All(bridge.Requests, request => Assert.InRange(request.GetProperty("frames").GetInt32(), 1, 8));
    }

    [Fact]
    public async Task NamedMutexSerializesWithAnotherWindowsProcess()
    {
        using var bridge = new FakeBridge();
        var mutexName = @"Local\PhoenixControl-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(bridge.Directory.ToUpperInvariant())));
        var ready = bridge.PathFor("lock-ready");
        var release = bridge.PathFor("lock-release");
        static string Literal(string text) => "'" + text.Replace("'", "''") + "'";
        var script = "$m = [Threading.Mutex]::new($false, " + Literal(mutexName) + "); " +
            "try { [void]$m.WaitOne(); [IO.File]::WriteAllText(" + Literal(ready) + ", 'ready'); " +
            "while (-not [IO.File]::Exists(" + Literal(release) + ")) { Start-Sleep -Milliseconds 10 } } " +
            "finally { $m.ReleaseMutex(); $m.Dispose() }";
        var start = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true };
        foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-Command", script }) start.ArgumentList.Add(arg);
        using var helper = Process.Start(start)!;
        try
        {
            await Until(() => File.Exists(ready));
            await Assert.ThrowsAsync<TimeoutException>(() => bridge.Client().SendAsync(PhoenixOperation.Status, timeout: TimeSpan.FromMilliseconds(100)));
            Assert.False(File.Exists(bridge.PathFor("request.json")));
            using var cancelled = new CancellationTokenSource(80);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => bridge.Client().StatusAsync(cancelled.Token));
            File.WriteAllText(release, "release");
            await helper.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(0, helper.ExitCode);
            bridge.StartServer(1);
            Assert.True((await bridge.Client().StatusAsync()).Ok);
        }
        finally { if (!helper.HasExited) { helper.Kill(true); await helper.WaitForExitAsync(); } }
    }

    [Fact]
    public async Task ConfiguredSandboxAndExplicitBridgeDirectoryAreResolved()
    {
        using var bridge = new FakeBridge();
        var game = Path.Combine(bridge.Root, "game");
        Directory.CreateDirectory(game);
        File.WriteAllText(Path.Combine(game, "Phoenix.json"), JsonSerializer.Serialize(new { sandbox = ".." }));
        var configured = new PhoenixControlClient(game, bridge.Identity.ImagePath, _ => bridge.Identity, () => FakeBridge.Tick);
        Assert.Equal(bridge.Directory, configured.BridgeDirectory);
        var direct = new PhoenixControlClient(bridge.Directory, null, _ => bridge.Identity, () => FakeBridge.Tick);
        bridge.StartServer(2);
        Assert.True((await configured.StatusAsync()).Ok);
        Assert.True((await direct.StatusAsync()).Ok);
    }

    [Fact]
    public async Task RealWindowsProcessCreationTimeAndImagePathMatchSessionContract()
    {
        using var bridge = new FakeBridge();
        bridge.Identity = PhoenixControlClient.InspectProcess(Environment.ProcessId);
        bridge.WriteEndpoint();
        var client = new PhoenixControlClient(bridge.Root, Environment.ProcessPath);
        bridge.StartServer(1);
        var response = await client.StatusAsync();
        Assert.Equal(Environment.ProcessId, response.Pid);
        Assert.Equal(bridge.Session, response.Session);
        Assert.True(bridge.Identity.CreationFileTime > 0);
    }

    [Fact]
    public async Task RetainedLaunchHandleStillRequiresTheExactProcessCreationIdentity()
    {
        using var bridge = new FakeBridge();
        using var process = Process.GetCurrentProcess();
        bridge.Identity = PhoenixControlClient.InspectProcess(process.Id);
        bridge.WriteEndpoint();
        var client = new PhoenixControlClient(bridge.Root, Environment.ProcessPath!, process);
        bridge.StartServer(1);
        Assert.True((await client.StatusAsync()).Ok);
        await bridge.ServerFinished();
        bridge.WriteEndpoint(process.Id + "-" + (bridge.Identity.CreationFileTime + 1));
        await Assert.ThrowsAsync<PhoenixSessionChangedException>(() => client.StatusAsync());
        Assert.False(File.Exists(bridge.PathFor("request.json")));
        Assert.False(process.HasExited);
    }

    [Fact]
    public void FormHasTypedSettingsAndSingleApplyWithoutPropertyGrid()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var bridge = new FakeBridge();
                File.Delete(bridge.PathFor("endpoint.json"));
                using var form = new PhoenixLiveForm(bridge.Root);
                Control[] Descendants(Control control) => control.Controls.Cast<Control>().SelectMany(child => new[] { child }.Concat(Descendants(child))).ToArray();
                var controls = Descendants(form);
                Assert.DoesNotContain(controls, control => control is PropertyGrid);
                Assert.IsAssignableFrom<ComboBox>(Assert.Single(controls, control => control.Name == "Backend"));
                Assert.IsAssignableFrom<ComboBox>(Assert.Single(controls, control => control.Name == "ExecutionDevice"));
                Assert.IsType<CheckBox>(Assert.Single(controls, control => control.Name == "OverrideStrengths"));
                Assert.IsAssignableFrom<NumericUpDown>(Assert.Single(controls, control => control.Name == "Tone"));
                Assert.Single(controls, control => control is Button && control.Text == "Apply & save");
                Assert.True(controls.Single(control => control.Name == "OverrideStrengths").Enabled);
                Assert.False(controls.Single(control => control.Name == "Tone").Enabled);
            }
            catch (Exception exception) { failure = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "Form construction timed out.");
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    [Fact]
    public void OfflineApplyPreservesLaunchConfigUneditedControlsPresetsAndOtherSections()
    {
        using var workspace = new TestWorkspace();
        var launch = "{\"schema\":\"phoenix-live/1\",\"custom\":{\"retain\":true},\"sandbox\":\"somewhere\"}";
        var original = "; retained comment\r\n[Other]\r\nKey=value\r\n[Phoenix]\r\nBackend=1\r\nTone=0.7\r\nStructure=1.8\r\nHotkey0=120\r\nFutureSetting=retain\r\nScreenshotRequest=7\r\n[Phoenix.Preset1]\r\nTone=1.9\r\n";
        File.WriteAllText(workspace.FilePath("Phoenix.json"), launch);
        File.WriteAllText(workspace.FilePath("ReShade.ini"), original);
        File.WriteAllText(workspace.FilePath("ReShadeVR.ini"), original);
        var offline = new PhoenixOfflineControls(workspace.Root);
        var observed = offline.Read().GetProperty("settings");
        Assert.Equal(1, observed.GetProperty("Backend").GetInt32());
        Assert.Equal(.7f, observed.GetProperty("Tone").GetSingle());
        Assert.Equal(1.8f, observed.GetProperty("Structure").GetSingle());
        offline.Apply(new PhoenixControlSettings().Set(PhoenixSetting.Tone, 1.2f), () => true);
        Assert.Equal(launch, File.ReadAllText(workspace.FilePath("Phoenix.json")));
        Assert.Equal(original.Replace("Tone=0.7", "Tone=1.2", StringComparison.Ordinal), File.ReadAllText(workspace.FilePath("ReShade.ini")));
        Assert.Equal(original.Replace("Tone=0.7", "Tone=1.2", StringComparison.Ordinal), File.ReadAllText(workspace.FilePath("ReShadeVR.ini")));
        Assert.Empty(Directory.GetFiles(workspace.Root, "*.tmp"));
    }

    [Fact]
    public void OfflineApplyRejectsConcurrentDiskChangesLiveEndpointAndInvalidMergedBlocks()
    {
        using var workspace = new TestWorkspace();
        var path = workspace.FilePath("ReShade.ini");
        File.WriteAllText(path, "[Phoenix]\nTone=1\nTransformerBlocks=8\nMinimumBlocks=7\n");
        var offline = new PhoenixOfflineControls(workspace.Root);
        offline.Read();
        Assert.Throws<ArgumentException>(() => offline.Apply(new PhoenixControlSettings().Set(PhoenixSetting.TransformerBlocks, 2), () => true));
        Assert.Throws<IOException>(() => offline.Apply(new PhoenixControlSettings().Set(PhoenixSetting.Tone, .5f), () => false));
        File.AppendAllText(path, "; changed outside the form\n");
        var changed = File.ReadAllText(path);
        Assert.Throws<IOException>(() => offline.Apply(new PhoenixControlSettings().Set(PhoenixSetting.Tone, .5f), () => true));
        Assert.Equal(changed, File.ReadAllText(path));
        Assert.Empty(Directory.GetFiles(workspace.Root, "*.tmp"));
    }

    [Theory]
    [InlineData(FileShare.Read)]
    [InlineData(FileShare.ReadWrite)]
    public void OfflineLockedVrDestinationLeavesBothConfigurationsUnchanged(FileShare share)
    {
        using var workspace = new TestWorkspace();
        var ordinary = workspace.FilePath("ReShade.ini");
        var vr = workspace.FilePath("ReShadeVR.ini");
        var original = Encoding.UTF8.GetBytes("[Phoenix]\r\nTone=1\r\n");
        File.WriteAllBytes(ordinary, original);
        File.WriteAllBytes(vr, original);
        var offline = new PhoenixOfflineControls(workspace.Root);
        offline.Read();
        using (var locked = new FileStream(vr, FileMode.Open, FileAccess.Read, share))
            Assert.Throws<IOException>(() => offline.Apply(new PhoenixControlSettings().Set(PhoenixSetting.Tone, .5f), () => true));
        Assert.Equal(original, File.ReadAllBytes(ordinary));
        Assert.Equal(original, File.ReadAllBytes(vr));
        Assert.Empty(Directory.GetFiles(workspace.Root, "*.tmp"));
    }

    [Fact]
    public void OfflineLaterCommitFailureRollsBackExactOriginalBytesWhileKeepingDestinationsLocked()
    {
        using var workspace = new TestWorkspace();
        var ordinary = workspace.FilePath("ReShade.ini");
        var vr = workspace.FilePath("ReShadeVR.ini");
        var original = Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes("; original\r\n[Phoenix]\r\nTone=1\r\n")).ToArray();
        File.WriteAllBytes(ordinary, original);
        File.WriteAllBytes(vr, original);
        var offline = new PhoenixOfflineControls(workspace.Root);
        offline.Read();
        var writes = 0;
        var failure = Assert.Throws<IOException>(() => offline.Apply(new PhoenixControlSettings().Set(PhoenixSetting.Tone, .5f), () => true, path =>
        {
            Assert.Throws<IOException>(() => File.WriteAllText(ordinary, "external writer"));
            Assert.Throws<IOException>(() => File.Move(ordinary, ordinary + ".moved"));
            Assert.Throws<IOException>(() => File.WriteAllText(vr, "external writer"));
            if (++writes == 2) throw new IOException("injected later commit failure");
        }));
        Assert.Equal("injected later commit failure", failure.Message);
        Assert.Equal(2, writes);
        Assert.Equal(original, File.ReadAllBytes(ordinary));
        Assert.Equal(original, File.ReadAllBytes(vr));
        Assert.Empty(Directory.GetFiles(workspace.Root, "*.tmp"));
        offline.Apply(new PhoenixControlSettings().Set(PhoenixSetting.Tone, .5f), () => true);
        Assert.Equal("; original\r\n[Phoenix]\r\nTone=0.5\r\n", File.ReadAllText(ordinary));
        Assert.Equal(Encoding.Unicode.GetPreamble(), File.ReadAllBytes(ordinary)[..2]);
    }

    [Fact]
    public async Task OfflineConcurrentApplySerializesAndRejectsStaleChanges()
    {
        using var workspace = new TestWorkspace();
        var path = workspace.FilePath("ReShade.ini");
        File.WriteAllText(path, "[Phoenix]\nTone=1\nStructure=1\n");
        var first = new PhoenixOfflineControls(workspace.Root);
        var second = new PhoenixOfflineControls(workspace.Root + Path.DirectorySeparatorChar);
        first.Read(); second.Read();
        using var firstEntered = new ManualResetEventSlim();
        using var allowFirst = new ManualResetEventSlim();
        using var secondStarting = new ManualResetEventSlim();
        using var secondEntered = new ManualResetEventSlim();
        var firstApply = Task.Run(() => first.Apply(new PhoenixControlSettings().Set(PhoenixSetting.Tone, .5f), () =>
        {
            firstEntered.Set();
            if (!allowFirst.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("First apply was not released.");
            return true;
        }));
        Task<Exception?>? secondApply = null;
        try
        {
            Assert.True(firstEntered.Wait(TimeSpan.FromSeconds(5)));
            secondApply = Task.Run<Exception?>(() =>
            {
                secondStarting.Set();
                return Record.Exception(() => second.Apply(new PhoenixControlSettings().Set(PhoenixSetting.Structure, 1.5f), () =>
                { secondEntered.Set(); return true; }));
            });
            Assert.True(secondStarting.Wait(TimeSpan.FromSeconds(5)));
            Assert.False(secondEntered.Wait(TimeSpan.FromMilliseconds(150)));
        }
        finally { allowFirst.Set(); }
        await firstApply.WaitAsync(TimeSpan.FromSeconds(5));
        var stale = Assert.IsType<IOException>(await secondApply!.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Contains("changed on disk", stale.Message);
        Assert.Equal("[Phoenix]\nTone=0.5\nStructure=1\n", File.ReadAllText(path));
        second.Read();
        second.Apply(new PhoenixControlSettings().Set(PhoenixSetting.Structure, 1.5f), () => true);
        Assert.Equal("[Phoenix]\nTone=0.5\nStructure=1.5\n", File.ReadAllText(path));
    }

    [Fact]
    public async Task OfflineDirectoryMutexSerializesWithAnotherWindowsProcess()
    {
        using var workspace = new TestWorkspace();
        var path = workspace.FilePath("ReShade.ini");
        File.WriteAllText(path, "[Phoenix]\nTone=1\nStructure=1\n");
        var offline = new PhoenixOfflineControls(workspace.Root);
        offline.Read();
        var mutexName = @"Local\DLSS5-Everything-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(workspace.Root.ToUpperInvariant())));
        var ready = workspace.FilePath("lock-ready");
        var release = workspace.FilePath("lock-release");
        static string Literal(string text) => "'" + text.Replace("'", "''") + "'";
        var script = "$m = [Threading.Mutex]::new($false, " + Literal(mutexName) + "); " +
            "try { [void]$m.WaitOne(); [IO.File]::WriteAllText(" + Literal(path) + ", \"[Phoenix]`nTone=0.5`nStructure=1`n\"); " +
            "[IO.File]::WriteAllText(" + Literal(ready) + ", 'ready'); " +
            "while (-not [IO.File]::Exists(" + Literal(release) + ")) { Start-Sleep -Milliseconds 10 } } " +
            "finally { $m.ReleaseMutex(); $m.Dispose() }";
        var start = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true };
        foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-Command", script }) start.ArgumentList.Add(arg);
        using var helper = Process.Start(start)!;
        using var starting = new ManualResetEventSlim();
        using var entered = new ManualResetEventSlim();
        Task<Exception?>? apply = null;
        try
        {
            await Until(() => File.Exists(ready));
            apply = Task.Run<Exception?>(() =>
            {
                starting.Set();
                return Record.Exception(() => offline.Apply(new PhoenixControlSettings().Set(PhoenixSetting.Structure, 1.5f), () =>
                { entered.Set(); return true; }));
            });
            Assert.True(starting.Wait(TimeSpan.FromSeconds(5)));
            Assert.False(entered.Wait(TimeSpan.FromMilliseconds(150)));
            File.WriteAllText(release, "release");
            await helper.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(0, helper.ExitCode);
            Assert.IsType<IOException>(await apply.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal("[Phoenix]\nTone=0.5\nStructure=1\n", File.ReadAllText(path));
        }
        finally
        {
            if (!helper.HasExited) { helper.Kill(true); await helper.WaitForExitAsync(); }
            if (apply is not null) await apply.WaitAsync(TimeSpan.FromSeconds(6));
        }
    }

    [Theory]
    [InlineData("ReShade.ini")]
    [InlineData("ReShadeVR.ini")]
    public void OfflineReparseTargetRejectsReadAndApplyWithoutChangingEitherOriginal(string name)
    {
        using var workspace = new TestWorkspace();
        using var outside = new TestWorkspace();
        var original = "[Phoenix]\nTone=1\n";
        File.WriteAllText(workspace.FilePath("ReShade.ini"), original);
        File.WriteAllText(workspace.FilePath("ReShadeVR.ini"), original);
        File.WriteAllText(outside.FilePath("keep.ini"), "outside original");
        var offline = new PhoenixOfflineControls(workspace.Root);
        offline.Read();
        var target = workspace.FilePath(name);
        File.Delete(target);
        SwapperTestLinks.CreateJunction(target, outside.Root);
        try
        {
            Assert.Throws<IOException>(() => new PhoenixOfflineControls(workspace.Root).Read());
            Assert.Throws<IOException>(() => offline.Apply(new PhoenixControlSettings().Set(PhoenixSetting.Tone, .5f), () => true));
            Assert.Equal(original, File.ReadAllText(workspace.FilePath(name == "ReShade.ini" ? "ReShadeVR.ini" : "ReShade.ini")));
            Assert.Equal("outside original", File.ReadAllText(outside.FilePath("keep.ini")));
            Assert.Empty(Directory.GetFiles(workspace.Root, "*.tmp"));
        }
        finally { Directory.Delete(target); }
    }

    [Fact]
    public void OfflineReparseDirectoryIsRejectedWithoutChangingOutsideConfiguration()
    {
        using var workspace = new TestWorkspace();
        using var outside = new TestWorkspace();
        var path = outside.FilePath("ReShade.ini");
        File.WriteAllText(path, "[Phoenix]\nTone=1\n");
        var link = workspace.FilePath("linked");
        SwapperTestLinks.CreateJunction(link, outside.Root);
        try
        {
            Assert.Throws<IOException>(() => new PhoenixOfflineControls(link).Read());
            Assert.Equal("[Phoenix]\nTone=1\n", File.ReadAllText(path));
        }
        finally { Directory.Delete(link); }
    }

    [Fact]
    public void OfflineAppearingOrDisappearingConfigurationIsAConflictEvenWhenEmpty()
    {
        using var workspace = new TestWorkspace();
        var path = workspace.FilePath("ReShade.ini");
        var offline = new PhoenixOfflineControls(workspace.Root);
        offline.Read();
        File.WriteAllText(path, "");
        Assert.Throws<IOException>(() => offline.Apply(new PhoenixControlSettings().Set(PhoenixSetting.Tone, .5f), () => true));
        offline.Read();
        File.Delete(path);
        Assert.Throws<IOException>(() => offline.Apply(new PhoenixControlSettings().Set(PhoenixSetting.Tone, .5f), () => true));
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void OfflineNewConfigurationNeverOverwritesAnOutsideWriterAfterValidation()
    {
        using var workspace = new TestWorkspace();
        var offline = new PhoenixOfflineControls(workspace.Root);
        offline.Read();
        Assert.Throws<IOException>(() => offline.Apply(new PhoenixControlSettings().Set(PhoenixSetting.Tone, .5f), () => true,
            target => File.WriteAllText(target, "outside writer")));
        Assert.Equal("outside writer", File.ReadAllText(workspace.FilePath("ReShade.ini")));
        Assert.Empty(Directory.GetFiles(workspace.Root, "*.tmp"));
    }

    [Theory]
    [InlineData("ReShade.ini")]
    [InlineData("ReShadeVR.ini")]
    public void OfflineOversizedReadOrChangedDestinationIsRejectedBeforeAnyWrite(string name)
    {
        using var workspace = new TestWorkspace();
        var ordinary = workspace.FilePath("ReShade.ini");
        var original = "[Phoenix]\nTone=1\n";
        File.WriteAllText(ordinary, original);
        File.WriteAllText(workspace.FilePath("ReShadeVR.ini"), original);
        var offline = new PhoenixOfflineControls(workspace.Root);
        offline.Read();
        var path = workspace.FilePath(name);
        File.WriteAllBytes(path, new byte[PhoenixOfflineControls.MaximumConfigurationBytes + 1]);
        Assert.Throws<InvalidDataException>(() => new PhoenixOfflineControls(workspace.Root).Read());
        Assert.Throws<InvalidDataException>(() => offline.Apply(new PhoenixControlSettings().Set(PhoenixSetting.Tone, .5f), () => true));
        Assert.Equal(original, File.ReadAllText(workspace.FilePath(name == "ReShade.ini" ? "ReShadeVR.ini" : "ReShade.ini")));
        Assert.Empty(Directory.GetFiles(workspace.Root, "*.tmp"));
    }

    [Fact]
    public void OfflineOversizedStagedConfigurationLeavesOriginalUntouched()
    {
        using var workspace = new TestWorkspace();
        var path = workspace.FilePath("ReShade.ini");
        var original = Encoding.UTF8.GetBytes(new string(';', PhoenixOfflineControls.MaximumConfigurationBytes));
        File.WriteAllBytes(path, original);
        var offline = new PhoenixOfflineControls(workspace.Root);
        offline.Read();
        Assert.Throws<InvalidDataException>(() => offline.Apply(new PhoenixControlSettings().Set(PhoenixSetting.Tone, .5f), () => true));
        Assert.Equal(original, File.ReadAllBytes(path));
        Assert.Empty(Directory.GetFiles(workspace.Root, "*.tmp"));
    }

    [Fact]
    public void OfflineMissingConfigurationIsCreatedAndStaleInstanceMustRefresh()
    {
        using var workspace = new TestWorkspace();
        var offline = new PhoenixOfflineControls(workspace.Root);
        offline.Read();
        offline.Apply(new PhoenixControlSettings().Set(PhoenixSetting.Tone, .5f), () => true);
        Assert.Equal("[Phoenix]\nTone=0.5\n", File.ReadAllText(workspace.FilePath("ReShade.ini")));
        Assert.False(File.Exists(workspace.FilePath("ReShadeVR.ini")));
        Assert.Throws<IOException>(() => offline.Apply(new PhoenixControlSettings().Set(PhoenixSetting.Structure, 1.5f), () => true));
        Assert.Empty(Directory.GetFiles(workspace.Root, "*.tmp"));
    }

    [Fact]
    public void OfflineEndpointAppearingAfterStagingLeavesBothConfigurationsUnchanged()
    {
        using var workspace = new TestWorkspace();
        var original = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes("[Phoenix]\nTone=1\n")).ToArray();
        File.WriteAllBytes(workspace.FilePath("ReShade.ini"), original);
        File.WriteAllBytes(workspace.FilePath("ReShadeVR.ini"), original);
        var offline = new PhoenixOfflineControls(workspace.Root);
        offline.Read();
        var checks = 0;
        Assert.Throws<IOException>(() => offline.Apply(new PhoenixControlSettings().Set(PhoenixSetting.Tone, .5f), () => ++checks == 1));
        Assert.Equal(2, checks);
        Assert.Equal(original, File.ReadAllBytes(workspace.FilePath("ReShade.ini")));
        Assert.Equal(original, File.ReadAllBytes(workspace.FilePath("ReShadeVR.ini")));
        Assert.Empty(Directory.GetFiles(workspace.Root, "*.tmp"));
    }

    [Fact]
    public void OfflineDirectoryCannotBeRenamedDuringCommit()
    {
        using var workspace = new TestWorkspace();
        var directory = workspace.FilePath("settings");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "ReShade.ini");
        File.WriteAllText(path, "[Phoenix]\nTone=1\n");
        var offline = new PhoenixOfflineControls(directory);
        offline.Read();
        offline.Apply(new PhoenixControlSettings().Set(PhoenixSetting.Tone, .5f), () => true, _ =>
            Assert.Throws<IOException>(() => Directory.Move(directory, directory + ".moved")));
        Assert.Equal("[Phoenix]\nTone=0.5\n", File.ReadAllText(path));
        Assert.False(Directory.Exists(directory + ".moved"));
    }

    static async Task Until(Func<bool> condition)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            if (watch.Elapsed > TimeSpan.FromSeconds(5)) throw new TimeoutException("Fake bridge condition did not arrive.");
            await Task.Delay(10);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SaveAppliesPendingLiveEditsFirstAndDoesNotSaveARejectedSet(bool rejectSet)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var bridge = new FakeBridge();
                var defaults = new PhoenixOfflineControls(bridge.Root).Read().GetProperty("settings");
                bridge.StartServer(rejectSet ? 2 : 3, transform: response =>
                {
                    response["snapshot"] = new { settings = defaults };
                    response["phase"] = bridge.Requests.Last().GetProperty("op").GetString() switch { "set" => "applied", "save" => "saved", _ => "observed" };
                    if (rejectSet && bridge.Requests.Last().GetProperty("op").GetString() == "set")
                    { response["ok"] = false; response["error"] = "Rejected test setting"; }
                });
                using var form = new PhoenixLiveForm(bridge.Root, controlClient: bridge.Client());
                void Wait(Task task)
                {
                    var deadline = DateTime.UtcNow.AddSeconds(8);
                    while (!task.IsCompleted && DateTime.UtcNow < deadline) { Application.DoEvents(); Thread.Sleep(5); }
                    Assert.True(task.IsCompleted); task.GetAwaiter().GetResult();
                }
                var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
                Wait((Task)typeof(PhoenixLiveForm).GetMethod("RunAsync", flags)!.Invoke(form, [PhoenixOperation.Status, null, false])!);
                var editors = (Dictionary<PhoenixSetting, Control>)typeof(PhoenixLiveForm).GetField("editors", flags)!.GetValue(form)!;
                ((CheckBox)editors[PhoenixSetting.OverrideStrengths]).Checked = true;
                ((NumericUpDown)editors[PhoenixSetting.Tone]).Value = 1.7m;
                Wait((Task)typeof(PhoenixLiveForm).GetMethod("ApplyAndSaveAsync", flags)!.Invoke(form, null)!);
                var operations = bridge.Requests.Select(request => request.GetProperty("op").GetString()).ToArray();
                Assert.Equal(rejectSet ? new[] { "status", "set" } : new[] { "status", "set", "save" }, operations);
                Assert.Equal(1.7f, bridge.Requests.ElementAt(1).GetProperty("settings").GetProperty("Tone").GetSingle());
                Wait(bridge.ServerFinished());
            }
            catch (Exception error) { failure = error; }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(25)), "Live save test timed out.");
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    sealed class FakeBridge : IDisposable
    {
        readonly TestWorkspace workspace = new();
        readonly CancellationTokenSource stop = new();
        readonly List<Task> servers = new();
        public const ulong Tick = 900000;
        public string Root => workspace.Root;
        public string Directory => Path.Combine(Root, "PhoenixOutput", "bridge");
        public PhoenixProcessIdentity Identity { get; set; }
        public string Session => Identity.Pid + "-" + Identity.CreationFileTime;
        public ConcurrentQueue<JsonElement> Requests { get; } = new();
        public FakeBridge()
        {
            System.IO.Directory.CreateDirectory(Directory);
            Identity = new(1234, 133456789012345678, Path.Combine(Root, "game.exe"));
            WriteEndpoint();
        }
        public string PathFor(string name) => Path.Combine(Directory, name);
        public PhoenixControlClient Client() => new(Root, null, _ => Identity, () => Tick);
        public void WriteEndpoint(string? session = null) => Publish("endpoint.json", new
        {
            schema = "phoenix-control/1", session = session ?? Session, pid = Identity.Pid,
            operations = new[] { "status", "set", "reset_history", "screenshot", "capture", "save", "validate_model" }
        });
        public void Publish(string name, object value)
        {
            var temporary = PathFor(Guid.NewGuid().ToString("N") + ".tmp");
            File.WriteAllText(temporary, JsonSerializer.Serialize(value));
            // Rapid replacement can briefly meet a Windows scanner/reader handle.
            // Bound the retry; persistent publication errors must still fail the test.
            for (var attempt = 0; ; attempt++)
            {
                try { File.Move(temporary, PathFor(name), true); break; }
                catch (Exception error) when (attempt < 9 &&
                    (error is UnauthorizedAccessException || error is IOException io && (io.HResult & 0xffff) is 5 or 32 or 33))
                { Thread.Sleep(10); }
            }
        }
        public void StartServer(int count, Func<JsonElement, Task>? onRequest = null, Action<Dictionary<string, object?>>? transform = null)
        {
            servers.Add(Task.Run(async () =>
            {
                for (int index = 0; index < count; index++)
                {
                    while (!File.Exists(PathFor("request.json"))) await Task.Delay(5, stop.Token);
                    File.Move(PathFor("request.json"), PathFor("processing.json"));
                    var request = PhoenixControlClient.ReadJson(PathFor("processing.json"), 65536);
                    Requests.Enqueue(request);
                    if (onRequest is not null) await onRequest(request);
                    var id = request.GetProperty("id").GetString();
                    var op = request.GetProperty("op").GetString();
                    var response = new Dictionary<string, object?>
                    {
                        ["schema"] = "phoenix-control/1", ["id"] = id, ["session"] = request.GetProperty("session").GetString(),
                        ["pid"] = request.GetProperty("pid").GetInt32(), ["ok"] = true, ["phase"] = op == "status" ? "observed" : "queued",
                        ["snapshot"] = new { settings = new { Backend = 3, Tone = 1f, Structure = 1f }, phoenix = new { recorded_frames = 0 }, execution = new { kind = "not_started" } }
                    };
                    if (op == "screenshot") response["screenshot_postfix"] = "Phoenix-bridge-" + id;
                    transform?.Invoke(response);
                    Publish("response.json", response);
                    File.Delete(PathFor("processing.json"));
                }
            }, stop.Token));
        }
        public Task ServerFinished() => Task.WhenAll(servers).WaitAsync(TimeSpan.FromSeconds(5));
        public void Dispose()
        {
            stop.Cancel();
            try { Task.WhenAll(servers).GetAwaiter().GetResult(); }
            catch (OperationCanceledException) { }
            stop.Dispose();
            workspace.Dispose();
        }
    }
}
