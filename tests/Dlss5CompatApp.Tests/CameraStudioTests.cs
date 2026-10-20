using System.Reflection;
using System.Text.Json;

namespace Dlss5CompatApp.Tests;

public class CameraStudioTests
{
    [Fact]
    public void ReportedModesHandleSixtyOnlyCamerasWithoutInventingThirtyFpsModes()
    {
        var modes = CameraDevices.ParseModes("""{"schema":1,"modes":[{"width":1920,"height":1080,"fps_numerator":60,"fps_denominator":1},{"width":1280,"height":720,"fps_numerator":60,"fps_denominator":1},{"width":960,"height":540,"fps_numerator":60,"fps_denominator":1},{"width":1920,"height":1080,"fps_numerator":60,"fps_denominator":1}]}""");
        Assert.Equal(3, modes.Count); Assert.All(modes, mode => Assert.Equal(60, mode.Fps));
        Assert.Contains(modes, mode => mode.Width == 960 && mode.Height == 540);
        Assert.DoesNotContain(modes, mode => mode.Fps == 30 || mode.Width == 640);
        var source = new ScreenCaptureSource("camera", 0, 0, "Camera") { DeviceId = "camera", CaptureWidth = 0, CaptureHeight = 0, CaptureFps = 0 };
        CameraDevices.Validate(source, new());
        Assert.Throws<InvalidDataException>(() => CameraDevices.Validate(source with { CaptureHeight = 480 }, new()));
    }

    [Theory]
    [InlineData("This camera does not offer the selected resolution/frame rate.", "camera-mode-unsupported")]
    [InlineData("Open camera. Check Settings", "camera-open-failed")]
    [InlineData("No camera frames for 10 seconds", "camera-frames-timeout")]
    [InlineData("Cannot open C:\\Private\\camera", "capture-error")]
    public void FailureDiagnosticsKeepABoundedUsefulReasonWithoutPrivatePaths(string error, string expected)
        => Assert.Equal(expected, ScreenStudioSession.DiagnosticFailureCode(error));
    [Fact]
    public void DeviceIdentityIsStableAndOurVirtualOutputCannotBecomeItsOwnInput()
    {
        var devices = CameraDevices.Parse("""{"schema":1,"devices":[{"name":"USB camera","id":"usb-one"},{"name":"USB camera","id":"usb-two"},{"name":"OBS Virtual Camera","id":"virtual"},{"name":"Duplicate","id":"usb-one"}]}""");
        Assert.Equal(new[] { "usb-one", "usb-two" }, devices.Select(item => item.DeviceId));
        Assert.False(ScreenStudioView.SameTarget(devices[0], devices[1]));
        var wire = devices[1].Wire(false, 42);
        Assert.Equal("usb-two", wire["device_id"]); Assert.Equal("camera", wire["kind"]); Assert.Equal(42, wire["parent_pid"]);
        Assert.False(ScreenStudioView.SameTarget(devices[0], devices[0] with { CaptureFps = 60 }));
    }

    [Fact]
    public void CameraWorkerFailuresDoNotMasqueradeAsAnEmptyDeviceList()
    {
        Assert.Throws<IOException>(() => CameraDevices.Parse("""{"schema":1,"error":"Permission denied"}""", 1));
        Assert.Throws<InvalidDataException>(() => CameraDevices.Parse("""{"schema":2,"devices":[]}"""));
    }

    [Fact]
    public void CameraSettingsValidateAtTheSessionBoundary()
    {
        var source = new ScreenCaptureSource("camera", 0, 0, "Camera") { DeviceId = "usb-camera" };
        CameraDevices.Validate(source, new());
        Assert.Throws<InvalidDataException>(() => CameraDevices.Validate(source with { DeviceId = "" }, new()));
        Assert.Throws<InvalidDataException>(() => CameraDevices.Validate(source with { CaptureWidth = 641 }, new()));
        Assert.Throws<InvalidDataException>(() => CameraDevices.Validate(source, new() { Hdr = true }));
        Assert.Throws<InvalidOperationException>(() => CameraDevices.Validate(source, new() { VirtualCamera = true }, false));
        CameraDevices.Validate(source, new() { VirtualCamera = true }, true);
        Assert.True((bool)new ScreenStudioSettings { VirtualCamera = true }.Worker(1)["virtual_camera"]);
        Assert.Throws<InvalidDataException>(() => new ScreenStudioSettings { Hdr = true, VirtualCamera = true }.Validate());
    }

    [Theory]
    [InlineData(1060, 740, false)]
    [InlineData(1400, 900, true)]
    public void CameraTabReusesControlsAndShowsStartStopOutputsWithoutOpeningACamera(int width, int height, bool light)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var form = new MainForm(preview: true, previewLight: light, previewPage: "Camera")
                    { Size = new(width, height), Location = new(-20000, -20000), ShowInTaskbar = false };
                form.Show(); Application.DoEvents();
                var view = Field<ScreenStudioView>(form, "_cameraStudio");
                Assert.True(view.Visible); Assert.Equal("Start camera", Field<Button>(view, "start").Text);
                Assert.Equal("Stop camera", Field<Button>(view, "stop").Text);
                Assert.False(Field<Button>(view, "stop").Enabled);
                Assert.All(Field<ComboBox>(view, "source").Items.Cast<ScreenCaptureSource>(), item => Assert.Equal("camera", item.Kind));
                Assert.DoesNotContain(All(view).OfType<RadioButton>(), item => item.Visible);
                Assert.Contains(All(view).OfType<CheckBox>(), item => item.Visible && item.Text == "Virtual webcam");
                Assert.Contains(All(view).OfType<CheckBox>(), item => item.Visible && item.Text == "Spout / OBS");
                Assert.False(view.ReadSettings().VirtualCamera); Assert.False(view.ReadSettings().Spout);
                Assert.Equal(Keys.Home, view.ReadSettings().StopKey);
                Assert.Equal(30, view.ReadSettings().Fps);
                var modePicker = Field<ComboBox>(view, "cameraMode");
                Assert.Equal(new ScreenStudioView.CameraMode(0, 0, 0), modePicker.SelectedItem);
                Assert.Single(modePicker.Items);
                typeof(ScreenStudioView).GetField("preferences", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(view,
                    new ScreenStudioPreferences { Settings = view.ReadSettings(), Camera = new("preview-camera", 640, 480, 30) });
                typeof(ScreenStudioView).GetMethod("RefreshCameraModeChoices", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(view, null);
                Assert.Equal(new ScreenStudioView.CameraMode(0, 0, 0), modePicker.SelectedItem);
                var capture = Environment.GetEnvironmentVariable("DLSS5_CAMERA_UI_CAPTURE");
                if (capture is not null)
                {
                    Directory.CreateDirectory(capture);
                    using var image = new Bitmap(width, height); form.DrawToBitmap(image, new Rectangle(0, 0, width, height));
                    image.Save(Path.Combine(capture, $"camera-{width}-{(light ? "light" : "dark")}.png"));
                }
                form.Close();
            }
            catch (Exception failure) { error = failure; }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30))); if (error is not null) throw error;
    }

    [Fact]
    public void CameraPreferencesDoNotOverwriteScreenStudioPreferences()
    {
        using var workspace = new TestWorkspace();
        var screen = workspace.FilePath("screen-studio.json"); var camera = workspace.FilePath("camera-studio.json");
        new ScreenStudioPreferences { Settings = new() { Tone = .5f } }.Save(screen);
        new ScreenStudioPreferences { Settings = new() { Tone = 1.7f, VirtualCamera = true }, Camera = new("usb-camera", 1920, 1080, 30) }.Save(camera);
        Assert.Equal(.5f, ScreenStudioPreferences.Load(screen).Settings.Tone);
        Assert.True(ScreenStudioPreferences.Load(camera).Settings.VirtualCamera);
        Assert.Equal(new("usb-camera", 1920, 1080, 30), ScreenStudioPreferences.Load(camera).Camera);
    }

    static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;
    static IEnumerable<Control> All(Control parent) => parent.Controls.Cast<Control>().SelectMany(child => new[] { child }.Concat(All(child)));
}

public sealed class CameraDeviceFactAttribute : FactAttribute
{
    public CameraDeviceFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("DLSS5_CAMERA_LIVE_SELECTION") is null ||
            Environment.GetEnvironmentVariable("DLSS5_SCREEN_LIVE_PAYLOAD") is null)
            Skip = "Explicit selected-camera qualification; never opens a camera in ordinary test runs.";
    }
}

public class CameraDeviceLiveTests
{
    readonly Xunit.Abstractions.ITestOutputHelper output;
    public CameraDeviceLiveTests(Xunit.Abstractions.ITestOutputHelper output) => this.output = output;
    [CameraDeviceFact]
    public async Task SelectedCameraAutomaticModeProcessesPresentsAppliesAndStopsWithoutSavingImages()
    {
        var selectionPath = Environment.GetEnvironmentVariable("DLSS5_CAMERA_LIVE_SELECTION")!;
        var payloadRoot = Environment.GetEnvironmentVariable("DLSS5_SCREEN_LIVE_PAYLOAD")!;
        var selected = ScreenStudioPreferences.Load(selectionPath).Camera!;
        var devices = await CameraDevices.EnumerateAsync();
        var device = Assert.Single(devices, device => device.DeviceId == selected.DeviceId);
        Assert.DoesNotContain("motorola", device.Name, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Smart Connect", device.Name, StringComparison.OrdinalIgnoreCase);
        using var workspace = new TestWorkspace();
        using var session = new ScreenStudioSession(workspace.FilePath("session"));
        var settings = new ScreenStudioSettings { Cursor = false, Fps = 30 };
        try
        {
            await session.StartAsync(device with { CaptureWidth = 0, CaptureHeight = 0, CaptureFps = 0 }, settings,
                PayloadScanner.Scan(payloadRoot), Path.Combine(payloadRoot, "nvngx_dlssnr.dll"));
            async Task Wait(Func<ScreenStudioStatus, bool> condition)
            {
                var end = DateTime.UtcNow.AddSeconds(60);
                while (DateTime.UtcNow < end)
                {
                    if (session.ReadStatus() is { } status)
                    {
                        Assert.True(status.State != "failed", JsonSerializer.Serialize(status));
                        if (condition(status)) return;
                    }
                    Assert.True(session.Running, "Camera worker exited before processing/presentation");
                    await Task.Delay(100);
                }
                throw new TimeoutException("Selected camera did not return processed and presented frames");
            }
            await Wait(status => status.Frames >= 8 && status.PresentedFrames >= 3 && status.OverlayVisible && status.CameraFps > 0);
            var initial = session.ReadStatus()!;
            output.WriteLine(JsonSerializer.Serialize(new { initial.Width, initial.Height, initial.CameraFps, initial.Frames,
                initial.PresentedFrames, initial.OverlayVisible, CameraMode = "Automatic", ImagesSaved = false }));
            Assert.True(initial.Width > 0 && initial.Height > 0);
            await session.ApplyAsync(settings with { Tone = 1.5f });
            await Wait(status => status.Frames >= initial.Frames + 3 && status.AppliedRevision == session.WorkerRevision);
            var reply = await new PhoenixControlClient(session.HostDirectory).SendAsync(PhoenixOperation.Status);
            Assert.True(reply.Ok, reply.Error);
            Assert.Equal(1.5f, reply.Snapshot.GetProperty("settings").GetProperty("Tone").GetSingle());
            Assert.False(Directory.Exists(Path.Combine(session.DirectoryPath, "screenshots")));
        }
        finally { await session.StopAsync(); }
        Assert.False(session.Running);
    }
}
