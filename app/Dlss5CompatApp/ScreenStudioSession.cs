using System.Diagnostics;
using System.Drawing.Imaging;
using System.Text.Json;

namespace Dlss5CompatApp;

internal sealed record ScreenStudioStatus(int Schema, string State, string Error, ulong Frames, uint Width, uint Height,
    uint ProcessingWidth, uint ProcessingHeight, double AverageFps, long AppliedRevision, int Screenshot,
    bool Hdr = false, bool Spout = false, int Converted = 0,
    ulong PresentedFrames = 0, bool OverlayVisible = false, uint LastPresent = 0, bool TargetForeground = false,
    bool CaptureExcluded = false, double CameraFps = 0, bool VirtualCamera = false);

internal sealed class ScreenStudioSession : IDisposable
{
    public string DirectoryPath { get; }
    public string HostDirectory => Path.Combine(DirectoryPath, "host64");
    internal string RecoveryDirectory => recoveryDirectory;
    internal string? PendingScreenshotRecoveryDirectory
    {
        get
        {
            lock (exportsGate)
                return Directory.Exists(recoveryDirectory) && Directory.EnumerateFiles(recoveryDirectory, "*.png").Any()
                    ? recoveryDirectory : null;
        }
    }
    internal Func<CancellationToken, Task>? StagingTestHook { get; set; }
    internal IReadOnlyList<string>? ConversionInputs { get; set; }
    public string? ShutdownWarning { get; private set; }
    internal Action<string>? ReportShutdownWarning { get; init; }
    public bool Running { get { lock (lifecycleGate) return !disposed && !stopped && worker is not null && !worker.HasExited; } }
    internal bool WorkerExited { get { lock (lifecycleGate) return worker is null || worker.HasExited; } }
    internal long WorkerRevision => Interlocked.Read(ref revision);
    Process? worker;
    long revision;
    int screenshot, exported;
    ScreenStudioSettings settings = new();
    readonly SemaphoreSlim gate = new(1);
    readonly object exportsGate = new();
    readonly object lifecycleGate = new();
    readonly CancellationTokenSource sessionCancellation = new();
    readonly string sessionRoot;
    readonly string recoveryDirectory;
    readonly bool keepSessionFiles;
    readonly string? diagnosticRoot;
    static readonly string[] screenshotSuffixes = ["normal", "enhanced", "view"];
    Task? startupTask, stopTask;
    bool disposed, stopped;
    public ScreenStudioSession(string? sessionRoot = null, bool keepSessionFiles = false, string? diagnosticRoot = null)
    {
        var root = LocalPaths.Normalize(sessionRoot ?? Path.Combine(LocalData.DirectoryPath, "ScreenSessions"));
        this.sessionRoot = root; this.keepSessionFiles = keepSessionFiles;
        this.diagnosticRoot = diagnosticRoot is null ? null : LocalPaths.Normalize(diagnosticRoot);
        DirectoryPath = Path.Combine(root, Guid.NewGuid().ToString("N"));
        recoveryDirectory = Path.Combine(root, "recovered", Path.GetFileName(DirectoryPath));
    }

    internal static IReadOnlyList<(string Source, string Target)> RequiredFiles(string appRoot, PayloadInfo payload, bool includeCaptureWorker = true)
    {
        if (!payload.HasReShade64)
            throw new InvalidOperationException("ReShade x64 is missing. Open Components to download it, or check the component download error.");
        if (payload.PhoenixAddon is null)
            throw new InvalidOperationException("Phoenix DLSS5 is missing. Open Components to download a compatible build, or check the component download error.");
        if (!payload.HasPhoenixFeederApi)
            throw new InvalidOperationException("The installed Phoenix DLSS5 build is incompatible with Screen Studio: its direct feeder API is missing. " +
                "Open Components to update it or import a compatible local build. Downloading ReShade again will not fix this.");
        if (!payload.HasEmbeddedPhoenixRuntime)
            throw new InvalidOperationException("Screen Studio requires a Phoenix DLSS5 build with its embedded C++ runtime.");
        var result = new List<(string Source, string Target)> {
            (Path.Combine(appRoot,"Runtime","host64","dlss5-feed-host64.exe"), "host64/dlss5-feed-host64.exe"),
            (payload.ReShade64Dll!, "host64/dxgi.dll"), (payload.PhoenixAddon!, "host64/Phoenix.addon64") };
        if (includeCaptureWorker) result.Insert(0, (Path.Combine(appRoot,"Runtime","screen","dlss5-screen.exe"), "dlss5-screen.exe"));
        foreach (var (source, _) in result)
            if (!File.Exists(source)) throw new FileNotFoundException("Screen Studio runtime is missing.", source);
        if (!PayloadScanner.IsDirectPhoenixHost(result.First(file => file.Target.EndsWith("dlss5-feed-host64.exe", StringComparison.Ordinal)).Source))
            throw new InvalidOperationException("The screen host is not the direct Phoenix DLSS5 host.");
        return result;
    }

    public Task StartAsync(ScreenCaptureSource source, ScreenStudioSettings value, PayloadInfo payload, string modelPath,
        string? appRoot = null, CancellationToken cancellationToken = default)
    {
        lock (lifecycleGate)
        {
            ThrowIfStopped(cancellationToken);
            if (startupTask is not null) throw new InvalidOperationException("This capture session has already been started.");
            return startupTask = PrepareAndStartAsync(source, value, payload, modelPath, appRoot, cancellationToken);
        }
    }

    async Task PrepareAndStartAsync(ScreenCaptureSource source, ScreenStudioSettings value, PayloadInfo payload, string modelPath,
        string? appRoot, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(sessionCancellation.Token, cancellationToken);
        var token = linked.Token;
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            await Task.Run(async () =>
            {
                token.ThrowIfCancellationRequested();
                if (StagingTestHook is { } hook) await hook(token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                value.Validate();
                if (source.Kind == "files")
                {
                    if (ConversionInputs is not { Count: > 0 and <= 10000 } || value.Hdr || value.Spout)
                        throw new InvalidDataException("Invalid SDR conversion input list.");
                    foreach (var input in ConversionInputs) { LocalPaths.RequireNoLinks(LocalPaths.Normalize(input)); if (!File.Exists(input)) throw new FileNotFoundException("Conversion input missing."); }
                }
                else if (source.Kind == "camera") CameraDevices.Validate(source, value);
                else if (source.Kind is not ("window" or "monitor") || source.Handle == 0 || source.Kind == "window" && source.ProcessId <= 0)
                    throw new InvalidDataException("Choose a valid screen capture source.");
                if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041)) throw new PlatformNotSupportedException("Screen Studio needs Windows 10 2004 or newer.");
                var files = RequiredFiles(appRoot ?? AppContext.BaseDirectory, payload);
                // Validate the separately supplied model before copying or starting any executable.
                _ = PhoenixConfiguration.Create(HostDirectory, modelPath, value.Phoenix(), PhoenixConfiguration.FeederInputProvider);
                token.ThrowIfCancellationRequested();
                LocalPaths.RequireNoLinks(DirectoryPath); Directory.CreateDirectory(HostDirectory);
                foreach (var (file, target) in files)
                {
                    token.ThrowIfCancellationRequested();
                    var dest = Path.Combine(DirectoryPath, target);
                    RequireOwnedPath(dest, DirectoryPath); LocalPaths.RequireNoLinks(file);
                    using var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
                    using var output = new FileStream(dest, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
                    await input.CopyToAsync(output, token).ConfigureAwait(false);
                }
                token.ThrowIfCancellationRequested();
                PhoenixConfiguration.Write(HostDirectory, modelPath, value.Phoenix(), PhoenixConfiguration.FeederInputProvider);
                token.ThrowIfCancellationRequested();
                IniEditor.SetValue(Path.Combine(HostDirectory, "ReShade.ini"), "GENERAL", "EffectSearchPaths", ".\\empty-effects");
                IniEditor.SetValue(Path.Combine(HostDirectory, "ReShade.ini"), "GENERAL", "TextureSearchPaths", ".\\empty-textures");
                IniEditor.SetValue(Path.Combine(HostDirectory, "ReShade.ini"), "OVERLAY", "TutorialProgress", "4");
                Directory.CreateDirectory(Path.Combine(HostDirectory, "empty-effects"));
                Directory.CreateDirectory(Path.Combine(HostDirectory, "empty-textures"));
                LocalData.WriteAtomic(Path.Combine(DirectoryPath, "dlss5-feed.cfg"), "vr_eye_split=0\n");
                var wire = source.Kind == "files" ? new Dictionary<string, object> { ["schema"] = 1, ["kind"] = "files", ["parent_pid"] = Environment.ProcessId, ["files"] = ConversionInputs! } : source.Wire(value.Cursor, Environment.ProcessId);
                LocalData.WriteAtomic(Path.Combine(DirectoryPath, "source.json"), JsonSerializer.Serialize(wire));
                lock (lifecycleGate)
                {
                    ThrowIfStopped(token);
                    settings = value; WriteSettings();
                    worker = Process.Start(new ProcessStartInfo(Path.Combine(DirectoryPath, "dlss5-screen.exe"))
                    {
                        WorkingDirectory = DirectoryPath,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        ArgumentList = { DirectoryPath }
                    }) ?? throw new IOException("Screen Studio could not start its capture worker.");
                }
            }, token).ConfigureAwait(false);
        }
        catch
        {
            // The staging task has finished; no writer can recreate files after this cleanup.
            try { Cleanup(); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or AggregateException) { }
            throw;
        }
        finally { gate.Release(); }
    }

    public async Task ApplyAsync(ScreenStudioSettings value, CancellationToken cancellationToken = default)
    {
        value.Validate();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(sessionCancellation.Token, cancellationToken);
        var token = linked.Token;
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ScreenStudioSettings previous;
            lock (lifecycleGate) { RequireRunning(token); previous = settings; }
            if (value.Cursor != previous.Cursor) throw new InvalidOperationException("Stop and restart capture to change cursor capture.");
            if (value.Hdr != previous.Hdr) throw new InvalidOperationException("Stop and restart capture to change HDR transport.");
            if (value.Tone != previous.Tone || value.Structure != previous.Structure || value.Skin != previous.Skin || value.AutoMask != previous.AutoMask)
            {
                var response = await new PhoenixControlClient(HostDirectory).SetAsync(value.LiveControls(), token).ConfigureAwait(false);
                lock (lifecycleGate) RequireRunning(token);
                if (!response.Ok) throw new IOException(response.Error ?? "Phoenix DLSS5 rejected the strength settings.");
            }
            lock (lifecycleGate) { RequireRunning(token); settings = value; WriteSettings(); }
        }
        finally { gate.Release(); }
    }
    void WriteSettings()
    {
        var wire = settings.Worker(++revision, screenshot);
        LocalData.WriteAtomic(Path.Combine(DirectoryPath, "settings.json"), JsonSerializer.Serialize(wire));
    }

    public async Task<ScreenStudioSettings> SynchronizeStrengthsAsync(CancellationToken cancellationToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(sessionCancellation.Token, cancellationToken);
        var token = linked.Token;
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            lock (lifecycleGate) RequireRunning(token);
            var response = await new PhoenixControlClient(HostDirectory).StatusAsync(token).ConfigureAwait(false);
            lock (lifecycleGate)
            {
                RequireRunning(token);
                if (!response.Ok) throw new IOException(response.Error ?? "Phoenix DLSS5 status failed.");
                var current = response.Snapshot.GetProperty("settings");
                var next = settings with
                {
                    Tone = current.GetProperty("Tone").GetSingle(),
                    Structure = current.GetProperty("Structure").GetSingle(),
                    Skin = current.GetProperty("Skin").GetSingle(),
                    AutoMask = current.GetProperty("AutoMask").GetBoolean()
                };
                next.Validate(); settings = next; WriteSettings(); return settings;
            }
        }
        finally { gate.Release(); }
    }

    public async Task RequestScreenshotAsync(CancellationToken cancellationToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(sessionCancellation.Token, cancellationToken);
        var token = linked.Token;
        await gate.WaitAsync(token).ConfigureAwait(false);
        try { lock (lifecycleGate) { RequireRunning(token); ++screenshot; WriteSettings(); } }
        finally { gate.Release(); }
    }

    void ThrowIfStopped(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(disposed, this);
        if (stopped) throw new OperationCanceledException("Screen Studio has been stopped.", sessionCancellation.Token);
    }

    void RequireRunning(CancellationToken token)
    {
        ThrowIfStopped(token);
        if (worker is null || worker.HasExited) throw new InvalidOperationException("Screen Studio is not running.");
    }
    public ScreenStudioStatus? ReadStatus()
    {
        var path = Path.Combine(DirectoryPath, "status.json");
        if (!File.Exists(path)) return null;
        var status = JsonSerializer.Deserialize<ScreenStudioStatus>(LocalData.ReadText(path, 16384), new JsonSerializerOptions
        { PropertyNameCaseInsensitive = true, PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower });
        if (status is null || status.Schema != 1 || status.State is null || !double.IsFinite(status.AverageFps))
            throw new InvalidDataException("Invalid screen status response.");
        return status;
    }
    public IReadOnlyList<string> ExportScreenshots(ScreenStudioStatus status)
    {
        lock (exportsGate)
        {
            if (status.Screenshot <= exported) return [];
            var result = new List<string>();
            ScreenStudioSettings mediaSettings;
            lock (lifecycleGate)
            {
                if (status.Screenshot > screenshot) throw new InvalidDataException("Unknown screenshot acknowledgement.");
                mediaSettings = settings;
            }
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + "-" + Guid.NewGuid().ToString("N")[..6];
            var written = new List<string>();
            var completed = new List<string>();
            try
            {
                var destination = LocalPaths.Normalize(mediaSettings.ScreenshotDirectory);
                if (LocalPaths.IsInside(destination, DirectoryPath) || LocalPaths.IsInside(destination, recoveryDirectory))
                    throw new IOException("Screenshot exports must be outside the runtime and recovery directories.");
                LocalPaths.RequireNoLinks(destination); Directory.CreateDirectory(destination);
                for (var index = exported + 1; index <= status.Screenshot; ++index)
                {
                    var sources = ScreenshotSources(index);
                    if (sources.All(path => !File.Exists(path))) continue;
                    if (sources.Any(path => !File.Exists(path))) throw new IOException("Screenshot triplet is incomplete.");
                    for (var part = 0; part < screenshotSuffixes.Length; ++part)
                    {
                        var source = sources[part];
                        LocalPaths.RequireNoLinks(source);
                        var target = Path.Combine(destination, $"screen-{stamp}-{index}-{screenshotSuffixes[part]}." + (mediaSettings.ScreenshotFormat == "png" ? "png" : "jpg"));
                        LocalPaths.RequireNoLinks(target);
                        using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                        // Register ownership before writing so a failed encoder/copy is also rolled back.
                        written.Add(target);
                        if (mediaSettings.ScreenshotFormat == "png")
                        {
                            using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
                            input.CopyTo(output);
                        }
                        else { using var image = Image.FromFile(source); image.Save(output, ImageFormat.Jpeg); }
                        output.Flush(true);
                    }
                    completed.AddRange(sources);
                }
            }
            catch (Exception error)
            {
                var failures = new List<Exception> { error };
                foreach (var path in written)
                    try { LocalPaths.RequireNoLinks(path); File.Delete(path); }
                    catch (Exception rollbackError) when (rollbackError is IOException or UnauthorizedAccessException) { failures.Add(rollbackError); }
                try { PreservePendingScreenshots(status.Screenshot); }
                catch (Exception recoveryError) when (recoveryError is IOException or UnauthorizedAccessException or AggregateException) { failures.Add(recoveryError); }
                if (failures.Count > 1) throw new AggregateException("Screenshot export failed; some rollback or recovery operations also failed.", failures);
                throw;
            }
            result.AddRange(written);
            exported = status.Screenshot;
            if (!keepSessionFiles)
                foreach (var source in completed)
                    try { LocalPaths.RequireNoLinks(source); File.Delete(source); }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            return result;
        }
    }

    string[] ScreenshotSources(int index)
    {
        var runtime = screenshotSuffixes.Select(suffix => Path.Combine(DirectoryPath, "screenshots", $"{index}-{suffix}.png")).ToArray();
        var recovered = screenshotSuffixes.Select(suffix => Path.Combine(recoveryDirectory, $"{index}-{suffix}.png")).ToArray();
        for (var part = 0; part < runtime.Length; ++part)
        {
            RequireOwnedPath(runtime[part], DirectoryPath);
            RequireOwnedPath(recovered[part], recoveryDirectory);
        }
        return recovered.All(File.Exists) ? recovered : runtime;
    }

    void PreservePendingScreenshots(int through = int.MaxValue)
    {
        var screenshots = Path.Combine(DirectoryPath, "screenshots");
        RequireOwnedPath(screenshots, DirectoryPath);
        if (!Directory.Exists(screenshots)) return;
        // Only the worker's exact PNG triplet names may leave the runtime tree.
        foreach (var normal in Directory.GetFiles(screenshots, "*-normal.png"))
        {
            var name = Path.GetFileName(normal);
            if (!int.TryParse(name.AsSpan(0, name.Length - "-normal.png".Length), System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var index) || index <= exported || index > through) continue;
            var sources = screenshotSuffixes.Select(suffix => Path.Combine(screenshots, $"{index}-{suffix}.png")).ToArray();
            foreach (var source in sources) RequireOwnedPath(source, DirectoryPath);
            if (!sources.All(File.Exists)) continue;
            RequireOwnedPath(recoveryDirectory, Path.Combine(sessionRoot, "recovered"));
            Directory.CreateDirectory(recoveryDirectory);
            var moved = new List<(string Source, string Target)>();
            try
            {
                foreach (var source in sources)
                {
                    var target = Path.Combine(recoveryDirectory, Path.GetFileName(source));
                    RequireOwnedPath(target, recoveryDirectory);
                    File.Move(source, target, false);
                    moved.Add((source, target));
                }
            }
            catch (Exception error)
            {
                var failures = new List<Exception> { error };
                foreach (var (source, target) in moved.AsEnumerable().Reverse())
                    try { RequireOwnedPath(source, DirectoryPath); RequireOwnedPath(target, recoveryDirectory); File.Move(target, source, false); }
                    catch (Exception rollbackError) when (rollbackError is IOException or UnauthorizedAccessException) { failures.Add(rollbackError); }
                if (failures.Count > 1) throw new AggregateException("Screenshot recovery could not be rolled back; runtime files have been retained.", failures);
                throw;
            }
        }
    }

    static void RequireOwnedPath(string path, string root)
    {
        if (!LocalPaths.IsInside(path, root) || LocalPaths.Same(path, root)) throw new IOException("Invalid capture file scope.");
        LocalPaths.RequireNoLinks(path);
    }

    public Task StopAsync()
    {
        TaskCompletionSource completion;
        Task? preparation;
        Process? owned;
        lock (lifecycleGate)
        {
            if (stopTask is not null) return stopTask;
            stopped = true;
            preparation = startupTask; owned = worker;
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            stopTask = completion.Task;
        }
        _ = CompleteStopAsync(completion, preparation, owned);
        return completion.Task;
    }

    async Task CompleteStopAsync(TaskCompletionSource completion, Task? preparation, Process? owned)
    {
        Exception? failure = null;
        try
        {
            // Cancel immediately, without acquiring the command gate or waiting for preparation.
            sessionCancellation.Cancel();
            if (owned is not null && !owned.HasExited)
            {
                try { LocalData.WriteAtomic(Path.Combine(DirectoryPath, "stop.signal"), ""); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { if (!owned.HasExited) owned.Kill(entireProcessTree: true); }
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(4));
                try { await owned.WaitForExitAsync(deadline.Token).ConfigureAwait(false); }
                catch (OperationCanceledException)
                {
                    if (!owned.HasExited) owned.Kill(entireProcessTree: true); await owned.WaitForExitAsync().ConfigureAwait(false);
                    throw new IOException("Capture shutdown timed out.");
                }
            }
        }
        catch (Exception error) { failure = error; }
        if (preparation is not null)
            try { await preparation.ConfigureAwait(false); }
            catch (Exception) { /* The StartAsync caller observes preparation failures. */ }
        if (failure is null) completion.SetResult();
        else completion.SetException(failure);
    }
    void Cleanup()
    {
        if (keepSessionFiles || !Directory.Exists(DirectoryPath)) return;
        lock (exportsGate)
        {
            if (!LocalPaths.IsInside(DirectoryPath, sessionRoot) || !Guid.TryParseExact(Path.GetFileName(DirectoryPath), "N", out _))
                throw new IOException("Invalid capture cleanup scope.");
            LocalPaths.RequireNoLinks(DirectoryPath);
            PreservePendingScreenshots();
            ValidateTree(DirectoryPath);
            Directory.Delete(DirectoryPath, true);
        }
    }
    static void ValidateTree(string root)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(root))
        {
            LocalPaths.RequireNoLinks(entry);
            if (Directory.Exists(entry)) ValidateTree(entry);
        }
    }
    public void Dispose()
    {
        lock (lifecycleGate)
        {
            if (disposed) return;
            disposed = true;
        }
        // Preparation has no UI-context continuations and must finish before deleting its files.
        try { StopAsync().GetAwaiter().GetResult(); }
        catch (IOException error) { ShutdownWarning = error.Message; }
        lock (exportsGate)
        {
            WriteDiagnosticSummary();
            try { Cleanup(); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or AggregateException) { }
            lock (lifecycleGate) worker?.Dispose();
        }
        if (ShutdownWarning is { } warning) ReportShutdownWarning?.Invoke(warning);
        // Pending operations still own linked tokens and may release the managed semaphore.
    }
    void WriteDiagnosticSummary()
    {
        if (diagnosticRoot is null) return;
        try
        {
            var live = new Dictionary<string, JsonElement>();
            ScreenStudioStatus? status = null;
            var statusReadFailed = false;
            var phoenixSummaryReadFailed = false;
            try { status = ReadStatus(); } catch { statusReadFailed = true; }
            var failureCode = DiagnosticFailureCode(status?.Error);
            if (status is not null) status = status with {
                Error = string.IsNullOrEmpty(status.Error) ? "" : "capture-error"
            };
            var summary = Path.Combine(HostDirectory, "phoenix-host-output-summary.json");
            try
            {
                if (File.Exists(summary))
                {
                    using var document = JsonDocument.Parse(LocalData.ReadText(summary, 1024 * 1024));
                    if (document.RootElement.TryGetProperty("phoenix_live", out var controls))
                        foreach (var key in new[] { "interactive", "settings_loaded", "override_strengths", "tone", "structure", "reset_pending", "resets_recorded" })
                            if (controls.TryGetProperty(key, out var value) && value.ValueKind is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False)
                                live[key] = value.Clone();
                }
            }
            catch { phoenixSummaryReadFailed = true; }
            var diagnostic = new { Schema = 1, SessionId = Path.GetFileName(DirectoryPath), Status = status, FailureCode = failureCode, StatusReadFailed = statusReadFailed,
                PhoenixSummaryReadFailed = phoenixSummaryReadFailed, PhoenixControls = live,
                Settings = new { settings.Enabled, settings.Scale, settings.Iterations, settings.Comparison, settings.Tone, settings.Structure, settings.Skin },
                WorkerExitCode = worker is { HasExited: true } ? (int?)worker.ExitCode : null };
            LocalPaths.RequireNoLinks(diagnosticRoot); Directory.CreateDirectory(diagnosticRoot);
            LocalData.WriteAtomic(Path.Combine(diagnosticRoot, "last-session.json"), JsonSerializer.Serialize(diagnostic));
        }
        catch { /* Optional local diagnostics must never prevent capture cleanup. */ }
    }

    internal static string DiagnosticFailureCode(string? error)
    {
        if (string.IsNullOrEmpty(error)) return "none";
        if (error.StartsWith("This camera does not offer", StringComparison.Ordinal)) return "camera-mode-unsupported";
        if (error.StartsWith("Open camera.", StringComparison.Ordinal)) return "camera-open-failed";
        if (error.StartsWith("No camera frames", StringComparison.Ordinal)) return "camera-frames-timeout";
        if (error.StartsWith("Camera read failed", StringComparison.Ordinal)) return "camera-read-failed";
        if (error.StartsWith("Convert camera to RGB32", StringComparison.Ordinal)) return "camera-color-conversion-failed";
        return "capture-error"; // Retain a useful bounded reason without paths or camera imagery.
    }
}
