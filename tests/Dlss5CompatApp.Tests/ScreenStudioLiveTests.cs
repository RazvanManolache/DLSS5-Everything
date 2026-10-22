using System.Diagnostics;

namespace Dlss5CompatApp.Tests;

public sealed class ScreenLiveFactAttribute : FactAttribute
{
    public ScreenLiveFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DLSS5_SCREEN_LIVE_PAYLOAD")))
            Skip = "Opt-in GPU capture test; needs separately supplied Phoenix DLSS5/ReShade/model payload.";
    }
}
public sealed class ScreenNeuralFactAttribute : FactAttribute
{
    public ScreenNeuralFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DLSS5_SCREEN_LIVE_PAYLOAD")) ||
            Environment.GetEnvironmentVariable("DLSS5_SCREEN_LIVE_NEURAL") != "1" ||
            Environment.GetEnvironmentVariable("DLSS5_SCREEN_LIVE_MONITOR") != "1")
            Skip = "Separate opt-in monitor/feedback test; run only on an idle test desktop with no personal content.";
    }
}

public class ScreenStudioLiveTests
{
    readonly Xunit.Abstractions.ITestOutputHelper testOutput;
    public ScreenStudioLiveTests(Xunit.Abstractions.ITestOutputHelper output) => testOutput = output;

    [ScreenLiveFact]
    public void CameraSlidersAutomaticallyApplyToPhoenixAndStopReleasesTheSharedSession() => OnSta(async () =>
    {
        // Reuse a generated window as the transport fixture. This qualifies the camera UI's
        // automatic live-control path, without opening a physical camera or a phone source.
        var payloadRoot = Environment.GetEnvironmentVariable("DLSS5_SCREEN_LIVE_PAYLOAD")!;
        using var workspace = new TestWorkspace();
        using var source = new PatternWindow { ClientSize = new(320, 240), Location = new(50, 50), ShowInTaskbar = false };
        source.Show(); source.Activate(); Application.DoEvents();
        using var session = new ScreenStudioSession(workspace.FilePath("session"));
        var settings = new ScreenStudioSettings { Cursor = false, Fps = 30, Tone = 0, Structure = 0, Skin = 0,
            ScreenshotDirectory = workspace.FilePath("exports") };
        using var view = new ScreenStudioView(() => null, () => "", () => UiPalette.Dark, preview: true, persistPreferences: false, cameraOnly: true);
        using var form = new Form { Size = new(1060, 740), Location = new(-20000, -20000), StartPosition = FormStartPosition.Manual, ShowInTaskbar = false };
        form.Controls.Add(view); form.Show();
        var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
        T Field<T>(string name) => (T)typeof(ScreenStudioView).GetField(name, flags)!.GetValue(view)!;
        void Set(string name, object value) => typeof(ScreenStudioView).GetField(name, flags)!.SetValue(view, value);
        try
        {
            await session.StartAsync(new("window", (ulong)source.Handle, Environment.ProcessId, "Generated control fixture"),
                settings, PayloadScanner.Scan(payloadRoot), Path.Combine(payloadRoot, "nvngx_dlssnr.dll"));
            Wait(() => session.ReadStatus()?.Frames >= 1, session, TimeSpan.FromSeconds(90));
            view.LoadSettings(settings); Set("active", settings); Set("session", session);
            Field<TrackBar>("tone").Value = 180;
            Wait(() => session.WorkerRevision > 1 && session.ReadStatus()?.AppliedRevision == session.WorkerRevision &&
                Field<Label>("pending").Text == "Applied and saved",
                session, TimeSpan.FromSeconds(30));
            var reply = await new PhoenixControlClient(session.HostDirectory).SendAsync(PhoenixOperation.Status);
            Assert.True(reply.Ok, reply.Error);
            Assert.Equal(1.8f, reply.Snapshot.GetProperty("settings").GetProperty("Tone").GetSingle());
            Assert.Equal("Applied and saved", Field<Label>("pending").Text);
            Field<Button>("stop").PerformClick();
            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (Field<ScreenStudioSession?>("session") is not null && DateTime.UtcNow < deadline) { Application.DoEvents(); Thread.Sleep(15); }
            Assert.Null(Field<ScreenStudioSession?>("session")); Assert.False(session.Running);
            testOutput.WriteLine("Camera slider 0 -> 1.8 reached the real Phoenix bridge automatically; Stop released the shared worker. Camera hardware was not opened.");
        }
        finally { await session.StopAsync(); source.Close(); form.Close(); }
    });

    [ScreenLiveFact]
    public void RunningCaptureCanSelectAndSwitchToAnotherOwnedWindow() => OnSta(async () =>
    {
        var payloadRoot=Environment.GetEnvironmentVariable("DLSS5_SCREEN_LIVE_PAYLOAD")!;
        using var workspace=new TestWorkspace();
        using var first=new PatternWindow { ClientSize=new(320,240), Location=new(50,50), ShowInTaskbar=false, Text="Synthetic first source" };
        using var second=new PatternWindow { ClientSize=new(352,256), Location=new(400,50), ShowInTaskbar=false, Text="Synthetic second source" };
        using var view=new ScreenStudioView(()=>PayloadScanner.Scan(payloadRoot),()=>Path.Combine(payloadRoot,"nvngx_dlssnr.dll"),()=>UiPalette.Dark,persistPreferences:false);
        using var form=new Form { Size=new(900,600), Location=new(-20000,-20000), StartPosition=FormStartPosition.Manual, ShowInTaskbar=false };
        form.Controls.Add(view); form.Show(); first.Show(); second.Show(); first.Activate();
        T Field<T>(string name)=>(T)typeof(ScreenStudioView).GetField(name,System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.GetValue(view)!;
        view.LoadSettings(new() { Enabled=false,Cursor=false,ScreenshotDirectory=workspace.FilePath("export") });
        Field<RadioButton>("specificWindow").Checked=true;
        var picker=Field<ComboBox>("source"); picker.Items.Clear();
        var a=new ScreenCaptureSource("window",(ulong)first.Handle,Environment.ProcessId,"Synthetic first");
        var b=new ScreenCaptureSource("window",(ulong)second.Handle,Environment.ProcessId,"Synthetic second");
        picker.Items.AddRange([a,b]); picker.SelectedItem=a;
        Field<Button>("start").PerformClick();
        var deadline=DateTime.UtcNow.AddSeconds(90);
        while(Field<ScreenStudioSession?>("session")?.ReadStatus()?.Frames is not >=1) {
            Application.DoEvents(); Thread.Sleep(15); if(DateTime.UtcNow>deadline) throw new TimeoutException(Field<Label>("state").Text);
        }
        var previous=Field<ScreenStudioSession>("session");
        var previousWidth=previous.ReadStatus()!.Width;
        try
        {
            Assert.True(picker.Enabled); Assert.True(Field<RadioButton>("specificWindow").Enabled); Assert.True(Field<RadioButton>("wholeDisplay").Enabled);
            picker.SelectedItem=b; Assert.Equal("Switch target",Field<Button>("start").Text); Assert.True(Field<Button>("start").Enabled);
            Field<Button>("start").PerformClick(); second.Activate();
            deadline=DateTime.UtcNow.AddSeconds(90);
            while(Field<ScreenStudioSession?>("session") is not { } current || ReferenceEquals(previous,current) || current.ReadStatus()?.Frames is not >=1) {
                Application.DoEvents(); Thread.Sleep(15); if(DateTime.UtcNow>deadline) throw new TimeoutException(Field<Label>("state").Text + "; worker: " + Field<ScreenStudioSession?>("session")?.ReadStatus());
            }
            Assert.False(previous.Running); Assert.True(ScreenStudioView.SameTarget(b,Field<ScreenCaptureSource>("activeSource")));
            var currentSession=Field<ScreenStudioSession>("session");
            Assert.True(currentSession.ReadStatus()!.Width>previousWidth,"Capture did not adopt the second window's dimensions.");
        }
        finally { if(Field<ScreenStudioSession?>("session") is { } current) await current.StopAsync(); first.Close(); second.Close(); form.Close(); }
    });

    [ScreenLiveFact]
    public void StrengthControlsChangeReturnedPixels() => OnSta(async () =>
    {
        var payloadRoot = Environment.GetEnvironmentVariable("DLSS5_SCREEN_LIVE_PAYLOAD")!;
        using var workspace = new TestWorkspace();
        using var source = new PatternWindow { ClientSize = new(640,384), Text = "Synthetic Phoenix DLSS5 strength validation", Location = new(50,50), ShowInTaskbar = false };
        source.Show(); source.Activate(); Application.DoEvents();
        using var session = new ScreenStudioSession(workspace.FilePath("session"), keepSessionFiles:true);
        var settings = new ScreenStudioSettings { Cursor=false, Tone=0, Structure=0, Skin=0, ScreenshotDirectory=workspace.FilePath("export") };
        try
        {
            await session.StartAsync(new("window",(ulong)source.Handle,Environment.ProcessId,"Synthetic"), settings, PayloadScanner.Scan(payloadRoot),Path.Combine(payloadRoot,"nvngx_dlssnr.dll"));
            Wait(()=>session.ReadStatus()?.Frames>=1,session,TimeSpan.FromSeconds(90));
            testOutput.WriteLine($"First-frame presentation: {session.ReadStatus()}");
            Assert.Equal(session.ReadStatus()!.TargetForeground, session.ReadStatus()!.OverlayVisible);
            Assert.True(session.ReadStatus()!.PresentedFrames>0, "Processing completed but no non-occluded frame was presented.");
            await session.RequestScreenshotAsync(); Wait(()=>session.ReadStatus()?.Screenshot>=1,session,TimeSpan.FromSeconds(60));
            settings=settings with { Tone=2,Structure=2,Skin=2 };
            await session.ApplyAsync(settings);
            await session.RequestScreenshotAsync(); Wait(()=>session.ReadStatus()?.Screenshot>=2,session,TimeSpan.FromSeconds(90));
            using var low=new Bitmap(Path.Combine(session.DirectoryPath,"screenshots","1-enhanced.png"));
            using var high=new Bitmap(Path.Combine(session.DirectoryPath,"screenshots","2-enhanced.png"));
            double delta=0; int count=0;
            for(var y=0;y<low.Height;y+=3) for(var x=0;x<low.Width;x+=3) { var a=low.GetPixel(x,y); var b=high.GetPixel(x,y); delta+=Math.Abs(a.R-b.R)+Math.Abs(a.G-b.G)+Math.Abs(a.B-b.B); count+=3; }
            testOutput.WriteLine($"Low/max strengths mean absolute RGB difference: {delta/count:0.0000}/255");
            // Completion polling no longer writes a diagnostic report each
            // frame. Verify the live settings over the authenticated bridge.
            var reply=await new PhoenixControlClient(session.HostDirectory).SendAsync(PhoenixOperation.Status);
            Assert.True(reply.Ok,reply.Error);
            var controls=reply.Snapshot.GetProperty("settings");
            Assert.True(controls.GetProperty("OverrideStrengths").GetBoolean());
            Assert.Equal(2,controls.GetProperty("Tone").GetSingle()); Assert.Equal(2,controls.GetProperty("Structure").GetSingle());
            Assert.True(delta/count>.05,"Strength acknowledgement did not produce a changed returned image.");
        }
        finally { await session.StopAsync(); source.Close(); }
    });

    [ScreenLiveFact]
    public async Task CustomResolutionsReachPhoenixWithoutPresetPadding()
    {
        var payloadRoot = Environment.GetEnvironmentVariable("DLSS5_SCREEN_LIVE_PAYLOAD")!;
        var payload = PayloadScanner.Scan(payloadRoot);
        var model = Path.Combine(payloadRoot, "nvngx_dlssnr.dll");
        using var workspace = new TestWorkspace();
        foreach (var size in new[] { new System.Drawing.Size(257, 255), new System.Drawing.Size(2560, 1440) })
        {
            var input = workspace.FilePath($"input-{size.Width}x{size.Height}.png");
            using (var image = new Bitmap(size.Width, size.Height))
            using (var graphics = Graphics.FromImage(image))
            {
                graphics.Clear(Color.FromArgb(50, 80, 120));
                for (var y = 0; y < size.Height; y += 32) for (var x = 0; x < size.Width; x += 32)
                {
                    using var brush = new SolidBrush(Color.FromArgb(60 + x % 170, 50 + y % 180, 30 + (x + y) % 200));
                    graphics.FillEllipse(brush, x, y, 40, 24);
                }
                image.Save(input);
            }
            var original = File.ReadAllBytes(input);
            var bypass = workspace.FilePath($"bypass-{size.Width}.png");
            var enhanced = workspace.FilePath($"enhanced-{size.Width}.png");
            await ScreenMediaConverter.ConvertAsync(new(input, bypass), new() { Enabled = false }, payload, model);
            await ScreenMediaConverter.ConvertAsync(new(input, enhanced), new() { Enabled = true }, payload, model);
            Assert.Equal(original, File.ReadAllBytes(input));
            using var source = new Bitmap(input); using var raw = new Bitmap(bypass); using var output = new Bitmap(enhanced);
            Assert.Equal(size, raw.Size); Assert.Equal(size, output.Size);
            double delta = 0; int channels = 0;
            for (var y = 0; y < size.Height; y += 11) for (var x = 0; x < size.Width; x += 13)
            {
                var a = source.GetPixel(x, y); var b = output.GetPixel(x, y);
                Assert.Equal(a, raw.GetPixel(x, y));
                delta += Math.Abs(a.R - b.R) + Math.Abs(a.G - b.G) + Math.Abs(a.B - b.B); channels += 3;
            }
            Assert.True(delta / channels > .05, $"No measurable neural result at {size.Width}x{size.Height}.");
        }
    }

    [ScreenLiveFact]
    public async Task OfflineImageAndVideoConversionUsesGpuAndKeepsInputs()
    {
        var payloadRoot = Environment.GetEnvironmentVariable("DLSS5_SCREEN_LIVE_PAYLOAD")!;
        using var workspace = new TestWorkspace();
        var input = workspace.FilePath("synthetic.png"); var output = workspace.FilePath("converted.png");
        using (var pattern = new Bitmap(320, 240))
        {
            for (var y = 0; y < 240; ++y) for (var x = 0; x < 320; ++x) pattern.SetPixel(x, y, PatternWindow.Pixel(x, y));
            pattern.Save(input);
        }
        var original = File.ReadAllBytes(input);
        var payload = PayloadScanner.Scan(payloadRoot); var model = Path.Combine(payloadRoot, "nvngx_dlssnr.dll");
        await ScreenMediaConverter.ConvertAsync(new(input, output), new() { Enabled = false }, payload, model);
        Assert.Equal(original, File.ReadAllBytes(input));
        using (var expected = new Bitmap(input)) using (var actual = new Bitmap(output))
        {
            Assert.Equal(expected.Size, actual.Size);
            for (var y = 0; y < actual.Height; y += 7) for (var x = 0; x < actual.Width; x += 7) Assert.Equal(expected.GetPixel(x, y), actual.GetPixel(x, y));
        }
        var neural = workspace.FilePath("neural.png");
        await ScreenMediaConverter.ConvertAsync(new(input, neural), new() { Enabled = true }, payload, model);
        using (var expected = new Bitmap(input)) using (var actual = new Bitmap(neural))
        {
            double sum=0; int count=0;
            for (var y=0;y<240;y+=4) for(var x=0;x<320;x+=4) { var a=expected.GetPixel(x,y); var b=actual.GetPixel(x,y); sum+=Math.Abs(a.R-b.R)+Math.Abs(a.G-b.G)+Math.Abs(a.B-b.B); count+=3; }
            Assert.True(sum/count>.05, "Offline Phoenix DLSS5 output has no measurable change.");
        }
        var ffmpeg = Environment.GetEnvironmentVariable("DLSS5_SCREEN_LIVE_FFMPEG");
        if (!string.IsNullOrEmpty(ffmpeg))
        {
            var video = workspace.FilePath("input.mp4"); var converted = workspace.FilePath("output.mp4");
            await ScreenMediaConverter.RunToolAsync(ffmpeg, ["-nostdin","-v","error","-n","-loop","1","-framerate","5","-i",input,"-frames:v","3","-c:v","libx264","-pix_fmt","yuv420p",video], CancellationToken.None);
            await ScreenMediaConverter.ConvertAsync(new(video, converted, ffmpeg), new() { Enabled=false }, payload, model);
            var metadata = await ScreenMediaConverter.RunToolAsync(Path.Combine(Path.GetDirectoryName(ffmpeg)!,"ffprobe.exe"), ["-v","error","-show_streams","-of","json",converted], CancellationToken.None);
            using var doc=System.Text.Json.JsonDocument.Parse(metadata); var stream=doc.RootElement.GetProperty("streams")[0];
            Assert.Equal("h264",stream.GetProperty("codec_name").GetString()); Assert.Equal("3",stream.GetProperty("nb_frames").GetString());
            Assert.Equal((320,240,"5/1"),ScreenMediaConverter.ReadVideoMetadata(metadata));
        }
    }
    [ScreenLiveFact]
    public void ControlledWindowPresentsFloatHdrAndDisablesSharingWhilePaused() => OnSta(async () =>
    {
        var payloadRoot=Environment.GetEnvironmentVariable("DLSS5_SCREEN_LIVE_PAYLOAD")!;
        using var workspace=new TestWorkspace();
        using var source=new PatternWindow { ClientSize=new(320,240), Text="Synthetic HDR/sharing validation", Location=new(50,50), ShowInTaskbar=false };
        source.Show(); source.Activate(); Application.DoEvents();
        using var session=new ScreenStudioSession(workspace.FilePath("sessions"),keepSessionFiles:true);
        var settings=new ScreenStudioSettings { Enabled=false, Cursor=false, Hdr=true, Spout=true, SpoutName="DLSS5 Synthetic HDR", ScreenshotDirectory=workspace.FilePath("screenshots") };
        try
        {
            await session.StartAsync(new("window",(ulong)source.Handle,Environment.ProcessId,"Synthetic"),settings,PayloadScanner.Scan(payloadRoot),Path.Combine(payloadRoot,"nvngx_dlssnr.dll"));
            Wait(()=>session.ReadStatus()?.Frames>=1,session,TimeSpan.FromSeconds(60));
            Assert.True(session.ReadStatus()!.Hdr); Assert.True(session.ReadStatus()!.Spout);
            await session.RequestScreenshotAsync(); Wait(()=>session.ReadStatus()?.Screenshot>=1,session,TimeSpan.FromSeconds(20));
            var images=session.ExportScreenshots(session.ReadStatus()!);
            using var input=new Bitmap(images.Single(p=>p.EndsWith("-normal.png"))); using var view=new Bitmap(images.Single(p=>p.EndsWith("-view.png")));
            Assert.Equal(input.Size,view.Size);
            double difference=0; int count=0;
            for(var y=40;y<input.Height-30;y+=7) for(var x=20;x<input.Width-20;x+=7) { var a=input.GetPixel(x,y); var b=view.GetPixel(x,y); difference+=Math.Abs(a.R-b.R)+Math.Abs(a.G-b.G)+Math.Abs(a.B-b.B); count+=3; }
            Assert.True(difference/count<=1,"HDR bypass screenshot differs from input.");
            // Sharing must shut down even while no source frames can arrive.
            source.Hide(); Application.DoEvents();
            Wait(()=>session.ReadStatus()?.State=="paused",session,TimeSpan.FromSeconds(10));
            await session.ApplyAsync(settings with { Spout=false });
            Wait(()=>session.ReadStatus() is { State:"paused", Spout:false },session,TimeSpan.FromSeconds(10));
        }
        finally { await session.StopAsync(); source.Close(); }
    });
    [ScreenNeuralFact]
    public void SyntheticMonitorDoesNotCaptureItsEnhancedOverlay() => OnSta(async () =>
    {
        var payloadRoot = Environment.GetEnvironmentVariable("DLSS5_SCREEN_LIVE_PAYLOAD")!;
        var validationRoot = Path.Combine(Path.GetTempPath(), "DLSS5-ScreenValidation", Guid.NewGuid().ToString("N"));
        var display = Screen.PrimaryScreen!;
        // Cover the selected display with generated content, never a user's desktop.
        using var pattern = new PatternWindow
        {
            FormBorderStyle = FormBorderStyle.None,
            StartPosition = FormStartPosition.Manual,
            Bounds = display.Bounds,
            TopMost = true,
            ShowInTaskbar = false,
            Text = "Synthetic monitor validation"
        };
        pattern.Show(); pattern.Activate(); pattern.BringToFront(); pattern.Refresh(); Application.DoEvents();
        // Test hosts can inherit STARTUPINFO=SW_HIDE. The first ShowWindow then
        // ignores Form.Show's requested state; explicitly show the test source.
        Assert.True(SetWindowPos(pattern.Handle, new nint(-1), display.Bounds.Left, display.Bounds.Top,
            display.Bounds.Width, display.Bounds.Height, 0x0050)); // SHOWWINDOW | NOACTIVATE
        await Task.Delay(350); pattern.Refresh(); Application.DoEvents();
        // Establish the capture precondition before recording the monitor.
        // A correctly painted offscreen form is not proof that DWM displays it.
        using (var visiblePixel = new Bitmap(1, 1))
        {
            using (var graphics = Graphics.FromImage(visiblePixel))
                graphics.CopyFromScreen(display.Bounds.Left + 50, display.Bounds.Top + 80, 0, 0, new Size(1, 1));
            var pointWindow = WindowFromPoint(new Point(display.Bounds.Left + 50, display.Bounds.Top + 80));
            Assert.True(PatternWindow.Pixel(50, 80).ToArgb() == visiblePixel.GetPixel(0, 0).ToArgb(),
                $"Synthetic desktop is not visible: expected={PatternWindow.Pixel(50, 80)}, actual={visiblePixel.GetPixel(0, 0)}, " +
                $"form={pattern.Bounds}, client={pattern.ClientSize}, dpi={pattern.DeviceDpi}, screen={display.Bounds}, " +
                $"source=0x{pattern.Handle:X}, pointWindow=0x{pointWindow:X}, visible={pattern.Visible}");
        }
        var selected = ScreenCaptureSources.Enumerate().Single(item => item.Kind == "monitor" && item.Name.EndsWith("(primary)"));
        using var session = new ScreenStudioSession(validationRoot);
        var settings = new ScreenStudioSettings { Cursor = false, Scale = .25f, Comparison = 3, ScreenshotDirectory = Path.Combine(validationRoot, "export") };
        try
        {
            await session.StartAsync(selected, settings, PayloadScanner.Scan(payloadRoot), Path.Combine(payloadRoot, "nvngx_dlssnr.dll"));
            // Static desktops need only produce their initial frame. The two
            // screenshot requests below explicitly exercise later processing.
            Wait(() => session.ReadStatus()?.Frames >= 1, session, TimeSpan.FromSeconds(90));
            Assert.True(session.ReadStatus()!.CaptureExcluded, "The worker did not confirm output exclusion.");
            // Driver startup notifications can briefly occlude the generated
            // monitor source. Require two complete, matching observations after
            // they settle; never exclude a region or relax the pixel oracle.
            var deadline = Stopwatch.StartNew(); var consecutive = 0; var captureIndex = 0;
            var mismatch = "No monitor sample";
            while (consecutive < 2 && deadline.Elapsed < TimeSpan.FromSeconds(40))
            {
                await session.RequestScreenshotAsync(); ++captureIndex;
                Wait(() => session.ReadStatus()?.Screenshot >= captureIndex, session, TimeSpan.FromSeconds(60));
                using var normal = new Bitmap(Path.Combine(session.DirectoryPath, "screenshots", $"{captureIndex}-normal.png"));
                using var view = new Bitmap(Path.Combine(session.DirectoryPath, "screenshots", $"{captureIndex}-view.png"));
                Assert.Equal(display.Bounds.Size, normal.Size);
                var mismatches = 0; double outputDifference = 0; var samples = 0;
                for (var y = 80; y < normal.Height - 40; y += 83) for (var x = 50; x < normal.Width - 40; x += 97)
                {
                    var expected = PatternWindow.Pixel(x, y); var actual = normal.GetPixel(x, y);
                    if (Math.Abs(expected.R - actual.R) > 1 || Math.Abs(expected.G - actual.G) > 1 || Math.Abs(expected.B - actual.B) > 1)
                    { ++mismatches; mismatch = $"Monitor input changed at {x},{y}: expected {expected}, actual {actual}"; }
                    var shown = view.GetPixel(x,y);
                    outputDifference += Math.Abs(shown.R-actual.R)+Math.Abs(shown.G-actual.G)+Math.Abs(shown.B-actual.B); ++samples;
                }
                Assert.True(outputDifference / (samples * 3) > 5, "The output view must differ enough to expose feedback.");
                Assert.True(session.ReadStatus() is { OverlayVisible: true, PresentedFrames: > 0 }, "Output overlay is not being displayed.");
                consecutive = mismatches == 0 ? consecutive + 1 : 0;
                if (consecutive < 2) await Task.Delay(2000);
            }
            Assert.True(consecutive == 2, mismatch + "; monitor never produced two feedback-free samples.");
        }
        finally { await session.StopAsync(); pattern.Close(); }
        Assert.False(session.Running);
    });
    [ScreenLiveFact]
    public void ControlledWindowCaptureRoundTripsGpuAndResizes()
        => OnSta(Run);
    static void OnSta(Func<Task> action)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { var task = action(); while (!task.IsCompleted) { Application.DoEvents(); Thread.Sleep(10); } task.GetAwaiter().GetResult(); }
            catch (Exception ex) { error = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (error is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
    }
    static async Task Run()
    {
        var payloadRoot = Environment.GetEnvironmentVariable("DLSS5_SCREEN_LIVE_PAYLOAD")!;
        var components = PayloadScanner.Scan(payloadRoot);
        var model = Path.Combine(payloadRoot, "nvngx_dlssnr.dll");
        var validationRoot = Path.Combine(Path.GetTempPath(), "DLSS5-ScreenValidation", Guid.NewGuid().ToString("N"));
        using var source = new PatternWindow { ClientSize = new(320, 240), Text = "DLSS5 capture validation", StartPosition = FormStartPosition.Manual, Location = new(30, 30), ShowInTaskbar = false };
        source.Show(); source.Activate(); Application.DoEvents();
        using var repaint = new System.Windows.Forms.Timer { Interval = 60 };
        repaint.Tick += (_, _) => source.Invalidate(); repaint.Start();
        using var session = new ScreenStudioSession(validationRoot, keepSessionFiles: true);
        var settings = new ScreenStudioSettings { Enabled = false, Cursor = false, ScreenshotDirectory = Path.Combine(validationRoot, "export") };
        var selected = new ScreenCaptureSource("window", (ulong)source.Handle, Environment.ProcessId, "Validation");
        try
        {
            await session.StartAsync(selected, settings, components, model);
            Wait(() => session.ReadStatus()?.Frames >= 3, session, TimeSpan.FromSeconds(60));
            await session.RequestScreenshotAsync();
            Wait(() => session.ReadStatus()?.Screenshot >= 1, session, TimeSpan.FromSeconds(15));
            var status = session.ReadStatus()!; var paths = session.ExportScreenshots(status); Assert.Equal(3, paths.Count);
            using (var input = new Bitmap(paths.Single(path => path.EndsWith("-normal.png"))))
            using (var output = new Bitmap(paths.Single(path => path.EndsWith("-enhanced.png"))))
            {
                Assert.Equal(input.Size, output.Size);
                var max = 0;
                for (var y = 0; y < input.Height; y += 3) for (var x = 0; x < input.Width; x += 3)
                    {
                        var a = input.GetPixel(x, y); var b = output.GetPixel(x, y);
                        max = Math.Max(max, Math.Max(Math.Abs(a.R - b.R), Math.Max(Math.Abs(a.G - b.G), Math.Abs(a.B - b.B))));
                    }
                Assert.True(max <= 1, $"Bypass pixels differ by {max}");
            }
            settings = settings with { Scale = .5f, Iterations = 3, Comparison = 2 };
            await session.ApplyAsync(settings);
            Wait(() => session.ReadStatus() is { } current && current.AppliedRevision == session.WorkerRevision && current.ProcessingWidth == Math.Max(1, (uint)Math.Round(current.Width * .5f)), session, TimeSpan.FromSeconds(20));
            await session.RequestScreenshotAsync();
            Wait(() => session.ReadStatus()?.Screenshot >= 2, session, TimeSpan.FromSeconds(20));
            paths = session.ExportScreenshots(session.ReadStatus()!);
            using (var input = new Bitmap(paths.Single(path => path.EndsWith("-normal.png"))))
            using (var enhanced = new Bitmap(paths.Single(path => path.EndsWith("-enhanced.png"))))
            {
                Assert.Equal((int)Math.Round(input.Width * .5f), enhanced.Width);
                Assert.Equal((int)Math.Round(input.Height * .5f), enhanced.Height);
            }
            var before = session.ReadStatus()!.Frames;
            source.ClientSize = new(480, 300); source.Invalidate();
            Wait(() => session.ReadStatus() is { } current && current.Width > 400 && current.Frames > before + 2, session, TimeSpan.FromSeconds(20));
            repaint.Stop();
            settings = settings with { Scale = 1, Comparison = 2, Divider = .5f };
            await session.ApplyAsync(settings);
            Wait(() => session.ReadStatus()?.AppliedRevision == session.WorkerRevision, session, TimeSpan.FromSeconds(20));
            await session.RequestScreenshotAsync();
            Wait(() => session.ReadStatus()?.Screenshot >= 3, session, TimeSpan.FromSeconds(20));
            paths = session.ExportScreenshots(session.ReadStatus()!);
            using (var input = new Bitmap(paths.Single(path => path.EndsWith("-normal.png"))))
            using (var view = new Bitmap(paths.Single(path => path.EndsWith("-view.png"))))
            {
                Assert.Equal(input.Size, view.Size);
                var x = input.Width / 2;
                for (var y = 0; y < input.Height; y += 3)
                {
                    var a = input.GetPixel(x, y); var b = view.GetPixel(x, y);
                    Assert.True(Math.Abs(a.R - b.R) <= 1 && Math.Abs(a.G - b.G) <= 1 && Math.Abs(a.B - b.B) <= 1,
                        "Disabled enhancement must not draw a wipe separator.");
                }
            }
            // A true processed-output test, not inference inferred from a running process.
            if (Environment.GetEnvironmentVariable("DLSS5_SCREEN_LIVE_NEURAL") == "1")
            {
                repaint.Start();
                settings = settings with { Enabled = true, Scale = 1, Iterations = 1 };
                await session.ApplyAsync(settings);
                before = session.ReadStatus()!.Frames;
                Wait(() => session.ReadStatus()?.Frames >= before + 3, session, TimeSpan.FromSeconds(90));
                await session.RequestScreenshotAsync(); Wait(() => session.ReadStatus()?.Screenshot >= 4, session, TimeSpan.FromSeconds(60));
                paths = session.ExportScreenshots(session.ReadStatus()!); Assert.Equal(3, paths.Count);
                using var input = new Bitmap(paths.Single(path => path.EndsWith("-normal.png")));
                using var output = new Bitmap(paths.Single(path => path.EndsWith("-enhanced.png")));
                double sum = 0; int count = 0;
                for (var y = 0; y < input.Height; y += 2) for (var x = 0; x < input.Width; x += 2) { var a = input.GetPixel(x, y); var b = output.GetPixel(x, y); sum += Math.Abs(a.R - b.R) + Math.Abs(a.G - b.G) + Math.Abs(a.B - b.B); count += 3; }
                Assert.True(sum / count > .05, $"Neural output has no measurable change ({sum / count})");
                repaint.Stop();
                var reply = await new PhoenixControlClient(session.HostDirectory).SetAsync((settings with { Tone = 2 }).LiveControls());
                Assert.True(reply.Ok, reply.Error);
                var synchronized = await session.SynchronizeStrengthsAsync();
                Assert.Equal(2, synchronized.Tone);
                Wait(() => session.ReadStatus()?.AppliedRevision == session.WorkerRevision, session, TimeSpan.FromSeconds(60));
            }
        }
        finally { repaint.Stop(); await session.StopAsync(); source.Close(); }
        Assert.False(session.Running);
    }
    static void Wait(Func<bool> predicate, ScreenStudioSession session, TimeSpan timeout)
    {
        var clock = Stopwatch.StartNew();
        while (!predicate())
        {
            var status = session.ReadStatus();
            if (status?.State == "failed") throw new IOException(status.Error + "; diagnostics: " + session.DirectoryPath);
            if (!session.Running) throw new IOException("Screen worker exited; diagnostics: " + session.DirectoryPath);
            if (clock.Elapsed > timeout) throw new TimeoutException("Screen validation timed out; status: " + status + "; diagnostics: " + session.DirectoryPath);
            Application.DoEvents(); Thread.Sleep(15);
        }
    }
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern nint WindowFromPoint(Point point);
    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
    sealed class PatternWindow : Form
    {
        public static Color Pixel(int x, int y) => x % 24 < 20 && y % 24 < 20 ? ((x / 24 + y / 24) % 2 == 0 ? Color.FromArgb(220, 60, 50) : Color.FromArgb(60, 210, 110)) : Color.FromArgb(38, 70, 110);
        // Location must be honored: WindowsDefaultLocation can cascade a source
        // behind another window or onto the deliberately offscreen controls.
        public PatternWindow() { DoubleBuffered = true; StartPosition = FormStartPosition.Manual; }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e); e.Graphics.Clear(Color.FromArgb(38, 70, 110));
            using var a = new SolidBrush(Color.FromArgb(220, 60, 50)); using var b = new SolidBrush(Color.FromArgb(60, 210, 110));
            for (var y = 0; y < ClientSize.Height; y += 24) for (var x = 0; x < ClientSize.Width; x += 24) e.Graphics.FillRectangle(((x + y) / 24) % 2 == 0 ? a : b, x, y, 20, 20);
            e.Graphics.DrawString("GPU capture validation", Font, Brushes.White, 10, 10);
        }
    }
}
