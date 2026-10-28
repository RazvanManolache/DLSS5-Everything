using System.Reflection;
using System.Text.Json;

namespace Dlss5CompatApp.Tests;

public class ScreenStudioTests
{
    [Fact]
    public void DefaultsAreNativeOnePassAndNoExtraSr()
    {
        var settings = new ScreenStudioSettings(); settings.Validate();
        Assert.Equal(1, settings.Scale); Assert.Equal(1, settings.Iterations);
        Assert.Equal(0, settings.Phoenix().ResolutionMode); Assert.Equal(0, settings.Phoenix().Upscaler);
        Assert.Equal(Keys.End, settings.StopKey); Assert.Equal(Keys.F10, settings.ScreenshotKey);
        var wire = settings.Worker(4, 2); Assert.Equal(4L, wire["revision"]); Assert.Equal(2, wire["screenshot"]);
        Assert.DoesNotContain("frame_generation", wire.Keys); Assert.DoesNotContain("nr_dll", wire.Keys);
    }
    [Theory]
    [InlineData(0)]
    [InlineData(11)]
    [InlineData(-1)]
    public void RejectsInvalidIterationCount(int count) => Assert.Throws<InvalidDataException>(() => (new ScreenStudioSettings { Iterations = count }).Validate());
    [Theory]
    [InlineData(-.1f)]
    [InlineData(.24f)]
    [InlineData(1.01f)]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    public void RejectsInvalidProcessingScale(float scale) => Assert.Throws<InvalidDataException>(() => (new ScreenStudioSettings { Scale = scale }).Validate());
    [Theory]
    [InlineData(-1)]
    [InlineData(4)]
    public void RejectsInvalidComparison(int mode) => Assert.Throws<InvalidDataException>(() => (new ScreenStudioSettings { Comparison = mode }).Validate());
    [Fact]
    public void StrengthsMatchPhoenixWireContract()
    {
        var settings = new ScreenStudioSettings { Tone = 2, Structure = .5f, Skin = 1.5f, AutoMask = true };
        var wire = settings.LiveControls().Copy();
        Assert.Equal(2f, wire["Tone"]); Assert.Equal(.5f, wire["Structure"]); Assert.Equal(1.5f, wire["Skin"]); Assert.Equal(true, wire["AutoMask"]);
        Assert.Equal("3", PhoenixConfiguration.Controls(settings.Phoenix())["Backend"]);
    }
    [Fact]
    public void RejectsBadHotkeysPathsAndMediaOptions()
    {
        Assert.Throws<InvalidDataException>(() => (new ScreenStudioSettings { StopKey = Keys.F10 }).Validate());
        Assert.Throws<InvalidDataException>(() => (new ScreenStudioSettings { StopKey = Keys.A }).Validate());
        Assert.Throws<ArgumentException>(() => (new ScreenStudioSettings { ScreenshotDirectory = @"\\server\capture" }).Validate());
        Assert.Throws<InvalidDataException>(() => (new ScreenStudioSettings { ScreenshotFormat = "exe" }).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new ScreenStudioSettings().Worker(-1));
    }
    [Fact]
    public void PreferencesAndPresetsRoundTripOutsideThePackage()
    {
        using var workspace = new TestWorkspace(); var path = workspace.FilePath("screen-studio.json");
        var settings = new ScreenStudioSettings { Iterations = 3, Scale = .5f, Comparison = 2 };
        var preferences = new ScreenStudioPreferences { Settings = settings, Presets = new() { ["Custom"] = settings } };
        preferences.Save(path); var loaded = ScreenStudioPreferences.Load(path);
        Assert.Equal(settings, loaded.Settings); Assert.Equal(settings, loaded.Presets["Custom"]);
        preferences.Schema = 2; Assert.Throws<InvalidDataException>(() => preferences.Save(path));
        Assert.False(ScreenStudioPreferences.ValidName("bad\nname"));
    }
    [Fact]
    public async Task AnUnstartedSessionStopsWithoutCreatingFiles()
    {
        using var workspace = new TestWorkspace(); using var session = new ScreenStudioSession(workspace.Root);
        Assert.False(session.Running); Assert.False(Directory.Exists(session.DirectoryPath));
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.RequestScreenshotAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.ApplyAsync(new()));
        Assert.Null(session.ReadStatus()); await session.StopAsync();
        Assert.False(Directory.Exists(session.DirectoryPath));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.RequestScreenshotAsync());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.ApplyAsync(new()));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StopOrDisposeWaitsForCancelledPreparationAndLeavesNoLateFiles(bool dispose)
    {
        using var workspace = new TestWorkspace(); using var session = new ScreenStudioSession(workspace.Root);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.StagingTestHook = async token =>
        {
            Directory.CreateDirectory(session.DirectoryPath);
            using var registration = token.Register(() => cancelled.TrySetResult());
            entered.SetResult(); await release.Task;
            File.WriteAllText(Path.Combine(session.DirectoryPath, "staged.txt"), "late staging");
        };
        var start = session.StartAsync(new("window", 1, 1, "Synthetic"), new(), PayloadScanner.Scan(workspace.Root), "");
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var stop = dispose ? Task.Run(session.Dispose) : session.StopAsync();
            await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(stop.IsCompleted);
            release.TrySetResult(); await stop.WaitAsync(TimeSpan.FromSeconds(5));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => start);
            session.Dispose(); Assert.False(Directory.Exists(session.DirectoryPath)); Assert.False(session.Running);
        }
        finally { release.TrySetResult(); }
    }
    [Fact]
    public void StatusAndScreenshotExportValidateAcknowledgements()
    {
        using var workspace = new TestWorkspace(); using var session = new ScreenStudioSession(workspace.Root);
        Directory.CreateDirectory(session.DirectoryPath);
        var path = Path.Combine(session.DirectoryPath, "status.json");
        File.WriteAllText(path, "{\"schema\":2,\"state\":\"running\"}"); Assert.Throws<InvalidDataException>(() => session.ReadStatus());
        var fields = new { schema = 1, state = "running", error = "", frames = 3, width = 320, height = 240, processing_width = 160, processing_height = 120, average_fps = 25.0, applied_revision = 4, screenshot = 0 };
        File.WriteAllText(path, JsonSerializer.Serialize(fields)); var status = session.ReadStatus()!;
        Assert.Equal(160u, status.ProcessingWidth); Assert.Equal(4, status.AppliedRevision); Assert.Empty(session.ExportScreenshots(status));
        Assert.Throws<InvalidDataException>(() => session.ExportScreenshots(status with { Screenshot = 2 }));
    }
    [Fact]
    public void DisposingRemovesOnlyItsOwnedSessionFolder()
    {
        using var workspace = new TestWorkspace();
        var session = new ScreenStudioSession(workspace.Root);
        Directory.CreateDirectory(session.DirectoryPath); File.WriteAllText(Path.Combine(session.DirectoryPath, "private.png"), "private");
        var unrelated = workspace.FilePath("unrelated.txt"); File.WriteAllText(unrelated, "keep");
        session.Dispose(); Assert.False(Directory.Exists(session.DirectoryPath)); Assert.True(File.Exists(unrelated));
    }
    [Fact]
    public async Task InvalidWorkerConfigurationPublishesFailureWithoutStartingAHost()
    {
        using var workspace = new TestWorkspace(); using var session = new ScreenStudioSession(workspace.Root);
        Directory.CreateDirectory(session.DirectoryPath);
        File.WriteAllText(Path.Combine(session.DirectoryPath, "source.json"), "not json");
        using var worker = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
            Path.Combine(AppContext.BaseDirectory, "Runtime", "screen", "dlss5-screen.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            ArgumentList = { session.DirectoryPath }
        })!;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await worker.WaitForExitAsync(timeout.Token);
        Assert.NotEqual(0, worker.ExitCode);
        var status = session.ReadStatus()!;
        Assert.True(status is not null, $"Worker exit {worker.ExitCode:X8}: {await worker.StandardError.ReadToEndAsync()}");
        Assert.Equal("failed", status.State); Assert.NotEmpty(status.Error); Assert.Equal(0ul, status.Frames);
        Assert.False(Directory.Exists(session.HostDirectory));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DisposeRetainsUnexportedScreenshotsButNotRuntimeFiles(bool exportFailure)
    {
        using var workspace = new TestWorkspace();
        var session = new ScreenStudioSession(workspace.Root);
        var captures = Path.Combine(session.DirectoryPath, "screenshots"); Directory.CreateDirectory(captures);
        using (var image = new Bitmap(2, 2))
            foreach (var suffix in new[] { "normal", "enhanced", "view" })
                image.Save(Path.Combine(captures, $"1-{suffix}.png"), System.Drawing.Imaging.ImageFormat.Png);
        File.WriteAllText(Path.Combine(session.DirectoryPath, "payload.dll"), "runtime must not survive");
        if (exportFailure)
        {
            var blocked = workspace.FilePath("not-a-folder"); File.WriteAllText(blocked, "blocked");
            typeof(ScreenStudioSession).GetField("settings", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(session, new ScreenStudioSettings { ScreenshotDirectory = blocked });
            typeof(ScreenStudioSession).GetField("screenshot", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(session, 1);
            Assert.Throws<IOException>(() => session.ExportScreenshots(new(1, "stopped", "", 1, 2, 2, 2, 2, 1, 1, 1)));
        }
        session.Dispose();
        Assert.False(Directory.Exists(session.DirectoryPath));
        Assert.Equal(3, Directory.GetFiles(session.RecoveryDirectory, "*.png").Length);
        Assert.DoesNotContain(Directory.GetFiles(session.RecoveryDirectory), path => path.EndsWith(".dll"));
    }
    [Fact]
    public void PresetCapacityFailureDoesNotPoisonPreferences() => OnSta(() =>
    {
        using var view = new ScreenStudioView(() => null, () => "", () => UiPalette.Dark, preview: true);
        var preferences = Field<ScreenStudioPreferences>(view, "preferences");
        for (var i = 0; i < 40; i++) preferences.Presets.Add("Preset " + i, new());
        Field<TextBox>(view, "presetName").Text = "One too many";
        var error = Assert.Throws<TargetInvocationException>(() => Call(view, "SavePreset")); Assert.IsType<InvalidDataException>(error.InnerException);
        Assert.Equal(40, preferences.Presets.Count); preferences.Validate();
    });
    [Fact]
    public void StartupPollingDoesNotReportStoppedOrEnableAnotherStart() => OnSta(() =>
    {
        using var view = new ScreenStudioView(() => null, () => "", () => UiPalette.Dark, preview: true);
        using var session = new ScreenStudioSession();
        SetField(view, "session", session); SetField(view, "starting", true); SetField(view, "busy", true);
        Field<Label>(view, "state").Text = "Preparing capture";
        var poll = (Task)typeof(ScreenStudioView).GetMethod("PollAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(view, null)!;
        poll.GetAwaiter().GetResult(); Call(view, "UpdateActions");
        Assert.Equal("Preparing capture", Field<Label>(view, "state").Text);
        Assert.True(Field<Button>(view, "stop").Enabled); Assert.False(Field<Button>(view, "start").Enabled);
    });
    [Fact]
    public void OldOperationCompletionCannotUnlockNewOperation() => OnSta(() =>
    {
        using var view = new ScreenStudioView(() => null, () => "", () => UiPalette.Dark, preview: true);
        SetField(view, "operationId", 2L); SetField(view, "busy", true);
        typeof(ScreenStudioView).GetMethod("EndOperation", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(view, [1L]);
        Assert.True(Field<bool>(view, "busy"));
    });
    [Theory]
    [InlineData(1060, 740)]
    [InlineData(1400, 900)]
    [InlineData(1920, 1080)]
    public void ScreenStudioRendersAllTabsInBothThemesAndStagesEdits(int width, int height) => OnSta(() =>
    {
        using var form = new MainForm(preview: true, previewPage: "Screen Studio") { Size = new(width, height), StartPosition = FormStartPosition.Manual, Location = new(-20000, -20000), ShowInTaskbar = false };
        form.Show(); Application.DoEvents();
        var view = Field<ScreenStudioView>(form, "_screenStudio");
        var changed = new ScreenStudioSettings { Scale = .5f, Iterations = 4, Tone = 2, Structure = .75f, Comparison = 2, ResidualStrength = .5f, Noise = .2f, Lens = true, ScreenshotDirectory = @"C:\Captures\Images" };
        view.LoadSettings(changed); Assert.Equal(changed, view.ReadSettings());
        Assert.Equal("Unapplied changes", Field<Label>(view, "pending").Text);
        Assert.False(Field<Button>(view, "start").Enabled); Assert.False(Field<Button>(view, "screenshot").Enabled);
        var theme = Field<ComboBox>(form, "_themeChoice");
        foreach (var name in new[] { "Dark", "Light" })
        {
            theme.SelectedItem = name; Application.DoEvents();
            var settings = Field<Panel>(view, "settingsPanel");
            if (settings.Visible) Field<Button>(view, "settingsButton").PerformClick();
            Application.DoEvents();
            var folder = Environment.GetEnvironmentVariable("DLSS5_SCREEN_CAPTURE_DIR");
            if (!string.IsNullOrWhiteSpace(folder)) { Directory.CreateDirectory(folder); using var bitmap = new Bitmap(form.Width, form.Height); form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size)); bitmap.Save(Path.Combine(folder, $"screen-Simple-{name}-{width}.png")); }
            Field<Button>(view, "settingsButton").PerformClick(); Application.DoEvents(); Assert.True(settings.Visible);
            var tabs = Descendants(view).OfType<TabControl>().Single();
            foreach (TabPage page in tabs.TabPages)
            {
                tabs.SelectedTab = page; Application.DoEvents();
                var fields = page.Controls.OfType<TableLayoutPanel>().Single();
                Assert.True(fields.Width <= page.ClientSize.Width, $"{page.Text}: fields={fields.Width}, page={page.ClientSize.Width}");
                foreach (Control editor in fields.Controls) Assert.True(editor.Right <= fields.ClientSize.Width,
                    $"{page.Text}: {editor.AccessibleName ?? editor.Text} right={editor.Right}, fields={fields.ClientSize.Width}");
                if (!string.IsNullOrWhiteSpace(folder)) { Directory.CreateDirectory(folder); using var bitmap = new Bitmap(form.Width, form.Height); form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size)); bitmap.Save(Path.Combine(folder, $"screen-{page.Text.Replace("&", "")}-{name}-{width}.png")); }
            }
        }
        Field<Button>(view, "apply").PerformClick(); Application.DoEvents();
        Assert.Equal("Saved for next capture", Field<Label>(view, "pending").Text);
    });
    [Fact]
    public void SimpleViewDefaultsToDisplayAndKeepsAdvancedToolsOutOfTheWay() => OnSta(() =>
    {
        using var form = new MainForm(preview: true, previewPage: "Screen Studio") { Location = new(-20000, -20000), ShowInTaskbar = false };
        form.Show(); Application.DoEvents();
        var view = Field<ScreenStudioView>(form, "_screenStudio");
        Assert.False(Field<Panel>(view, "settingsPanel").Visible);
        Assert.True(Field<RadioButton>(view, "wholeDisplay").Checked);
        var source = Field<ComboBox>(view, "source");
        Assert.All(source.Items.Cast<ScreenCaptureSource>(), item => Assert.Equal("monitor", item.Kind));
        Assert.Equal("Enable Phoenix DLSS5", Field<Button>(view, "start").Text);
        Assert.False(Field<Button>(view, "convert").Visible);
        var hotkeys = Field<Dictionary<string, ComboBox>>(view, "hotkeyEditors");
        Assert.Equal(4, hotkeys.Count); Assert.DoesNotContain("Recording", hotkeys.Keys);
        Assert.DoesNotContain(Descendants(view), control =>
            (control.AccessibleName ?? control.Text).Contains("record", StringComparison.OrdinalIgnoreCase));
        Assert.True(Field<Button>(view, "apply").Visible);
        Assert.True(Field<Button>(view, "screenshot").Visible);
        var notice = Field<Label>(view, "notice"); notice.Text = "Screenshot export failed";
        Assert.True(notice.Visible); Assert.False(Field<Panel>(view, "settingsPanel").Contains(notice));
        Field<RadioButton>(view, "specificWindow").Checked = true; Application.DoEvents();
        Assert.All(source.Items.Cast<ScreenCaptureSource>(), item => Assert.Equal("window", item.Kind));
        Assert.Equal("window", Assert.IsType<ScreenCaptureSource>(source.SelectedItem).Kind);
        Assert.Equal("Window", source.AccessibleName);
        var window = source.SelectedItem;
        Field<RadioButton>(view, "wholeDisplay").Checked = true;
        Field<RadioButton>(view, "specificWindow").Checked = true;
        Assert.Equal(window, source.SelectedItem);
        Field<Button>(view, "settingsButton").PerformClick(); Application.DoEvents();
        Assert.True(Field<Panel>(view, "settingsPanel").Visible);
        Assert.True(Field<Button>(view, "apply").Visible);
        Field<Button>(view, "settingsButton").PerformClick(); Application.DoEvents();
        Assert.False(Field<Panel>(view, "settingsPanel").Visible);
        Assert.Equal("Advanced settings", Field<Button>(view, "settingsButton").Text);
    });

    [Fact]
    public void EnablePhoenixCannotAccidentallyStartWithASavedBypassOrDifferenceView() => OnSta(() =>
    {
        using var view = new ScreenStudioView(() => null, () => "", () => UiPalette.Dark);
        view.LoadSettings(new() { Enabled = false, Comparison = 3, Tone = .75f, Iterations = 2 });
        var source = Field<ComboBox>(view, "source");
        source.Items.Clear(); source.Items.Add(new ScreenCaptureSource("monitor", 1, 0, "Test display")); source.SelectedIndex = 0;
        var start = (Task)view.GetType().GetMethod("StartViewingAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(view, null)!;
        Assert.Throws<InvalidOperationException>(() => start.GetAwaiter().GetResult());
        var settings = view.ReadSettings();
        Assert.True(settings.Enabled); Assert.Equal(0, settings.Comparison);
        Assert.Equal(.75f, settings.Tone); Assert.Equal(2, settings.Iterations);
        Assert.Null(Field<ScreenStudioSession?>(view, "session"));
    });

    [Fact]
    public void WindowActivationRejectsDisplaysAndStaleProcessIdentity() => OnSta(() =>
    {
        Assert.False(ScreenCaptureSources.TryActivate(new("monitor", 1, 0, "Display")));
        Assert.False(ScreenCaptureSources.TryActivate(new("window", 0, 1, "Missing window")));
        using var window = new Form { ShowInTaskbar = false };
        Assert.False(ScreenCaptureSources.TryActivate(new("window", (ulong)window.Handle, int.MaxValue, "Stale window")));
    });
    [Fact]
    public void CaptureTargetsUseWindowIdentityNotChangingTitles()
    {
        var window = new ScreenCaptureSource("window", 123, 456, "Document A");
        Assert.True(ScreenStudioView.SameTarget(window, window with { Name = "Document B" }));
        Assert.False(ScreenStudioView.SameTarget(window, window with { ProcessId = 789 }));
        Assert.False(ScreenStudioView.SameTarget(window, window with { Kind = "monitor" }));
        Assert.False(ScreenStudioView.SameTarget(window, null));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LocalDiagnosticSummarySurvivesCleanupWithoutCopyingPathsOrPixels(bool malformedBackend)
    {
        using var workspace = new TestWorkspace();
        using var session = new ScreenStudioSession(workspace.FilePath("sessions"), diagnosticRoot: workspace.FilePath("diagnostics"));
        Directory.CreateDirectory(session.DirectoryPath);
        Directory.CreateDirectory(session.HostDirectory);
        File.WriteAllText(Path.Combine(session.HostDirectory,"phoenix-host-output-summary.json"),malformedBackend ? "{broken" : "{\"phoenix_live\":{\"tone\":2,\"user_path\":\"Private-MediaFolder\"}}");
        File.WriteAllText(Path.Combine(session.DirectoryPath,"status.json"),JsonSerializer.Serialize(new ScreenStudioStatus(1,"stopped","Cannot open Private-MediaFolder",10,640,384,640,384,60,1,0,PresentedFrames:10), new JsonSerializerOptions { PropertyNamingPolicy=JsonNamingPolicy.SnakeCaseLower }));
        session.Dispose();
        Assert.False(Directory.Exists(session.DirectoryPath));
        var json=File.ReadAllText(workspace.FilePath("diagnostics/last-session.json"));
        using var summary=JsonDocument.Parse(json);
        Assert.Equal(10UL,summary.RootElement.GetProperty("Status").GetProperty("PresentedFrames").GetUInt64());
        Assert.Equal(malformedBackend,summary.RootElement.GetProperty("PhoenixSummaryReadFailed").GetBoolean());
        Assert.DoesNotContain("Private-MediaFolder",json); Assert.Equal("capture-error",summary.RootElement.GetProperty("Status").GetProperty("Error").GetString());
        Assert.DoesNotContain("ScreenshotDirectory",json); Assert.DoesNotContain("RecordingDirectory",json); Assert.DoesNotContain(workspace.Root,json);
    }

    [Theory]
    [InlineData(true, false, "Phoenix DLSS5 on")]
    [InlineData(true, true, "Phoenix DLSS5 on - window in background")]
    [InlineData(false, true, "Phoenix DLSS5 bypassed - window in background")]
    public void EnhancementStatusRemainsVisibleWithoutOpeningSettings(bool enabled, bool background, string expected)
    {
        var status = new ScreenStudioStatus(1, "running", "", 1, 320, 240, 320, 240, 60, 1, 0);
        Assert.Equal(expected, ScreenStudioView.StatusText(status, enabled, background));
    }
    [Fact]
    public void PresetCommandsActuallySaveLoadDeleteAndReset() => OnSta(() =>
    {
        using var view = new ScreenStudioView(() => null, () => "", () => UiPalette.Dark, preview: true);
        view.LoadSettings(new() { Tone = 1.8f, Iterations = 3 }); Field<TextBox>(view, "presetName").Text = "My preset";
        Call(view, "SavePreset"); var prefs = Field<ScreenStudioPreferences>(view, "preferences"); Assert.Equal(3, prefs.Presets["My preset"].Iterations);
        view.LoadSettings(new()); Call(view, "LoadPreset"); Assert.Equal(3, view.ReadSettings().Iterations);
        Call(view, "DeletePreset"); Assert.Empty(prefs.Presets);
        view.LoadSettings(new()); Assert.Equal(1, view.ReadSettings().Iterations);
    });
    static IEnumerable<Control> Descendants(Control c) => c.Controls.Cast<Control>().SelectMany(child => new[] { child }.Concat(Descendants(child)));
    static T Field<T>(object value, string name) => (T)value.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(value)!;
    static void SetField(object value, string name, object replacement) => value.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(value, replacement);
    static void Call(object value, string name) => value.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(value, null);
    static void OnSta(Action action) { Exception? error = null; var thread = new Thread(() => { try { action(); } catch (Exception ex) { error = ex; } }); thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join(); if (error is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw(); }
}
