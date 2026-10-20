using System.Diagnostics;

namespace Dlss5CompatApp;

internal sealed partial class ScreenStudioView
{
    internal sealed record CameraMode(int Width, int Height, int Fps)
    {
        public override string ToString() => Width == 0 ? "Automatic (device native mode)" : $"{Width} × {Height} · {Fps} FPS";
    }
    readonly ComboBox cameraMode = Choice("Camera capture mode (restart required)");
    readonly CheckBox virtualCamera = Toggle("Virtual webcam (OBS Virtual Camera)", false);
    readonly System.Windows.Forms.Timer cameraApplyTimer = new() { Interval = 350 };
    bool enumeratingCameras, cameraListLoaded;
    readonly Dictionary<string, IReadOnlyList<CameraMode>> reportedCameraModes = new(StringComparer.OrdinalIgnoreCase);
    string? cameraModeDeviceId;
    Button? checkCameraModes;

    static ScreenStudioSettings CameraDefaults() => new()
    {
        Fps = 30, Cursor = false, SpoutName = "DLSS5 Camera", StopKey = Keys.Home,
        ToggleKey = Keys.F5, CompareKey = Keys.F6, ScreenshotKey = Keys.F7
    };

    void BuildCameraScope(FlowLayoutPanel scope)
    {
        scope.WrapContents = true; scope.AutoSize = true;
        scope.Controls.Add(new Label { Text = "Capture mode", AutoSize = true, Margin = new(0, 8, 12, 0) });
        cameraMode.Dock = DockStyle.None; cameraMode.Width = 265;
        cameraMode.Items.Add(new CameraMode(0, 0, 0));
        cameraMode.SelectedIndex = 0; scope.Controls.Add(cameraMode);
        cameraMode.SelectedIndexChanged += (_, _) => UpdateActions();
        checkCameraModes = Command("Check modes", "\uE72C", CheckCameraModesAsync);
        tips.SetToolTip(checkCameraModes, "Briefly open the selected camera to read its actual resolution/frame-rate modes. No images are saved. Stop capture first.");
        scope.Controls.Add(checkCameraModes);
    }

    void InitializeCamera()
    {
        if (!cameraOnly) return;
        RefreshCameraModeChoices();
        virtualCamera.Enabled = preview || CameraDevices.VirtualCameraInstalled();
        stop.AccessibleName = "Stop camera and release device";
        tips.SetToolTip(start, "Open only the selected camera, start Phoenix and show its enhanced preview. No audio is captured.");
        tips.SetToolTip(stop, "Stop preview, Spout and virtual webcam output, and release the camera.");
        tips.SetToolTip(virtualCamera, "Requires the separately installed OBS Virtual Camera component. OBS can stay closed. Select OBS Virtual Camera in your calling app; stop other virtual-camera senders first.");
        SetNotice("Local preview opens in its own window. Enable Spout for OBS or Virtual webcam for calls. Camera opens only on Start or Check modes.");
        VisibleChanged += (_, _) => { if (Visible && !cameraListLoaded) RefreshCameraSources(); };
        cameraApplyTimer.Tick += async (_, _) =>
        {
            if (resourcesDisposed || session?.Running != true) { cameraApplyTimer.Stop(); return; }
            if (busy || starting || stopping) return;
            cameraApplyTimer.Stop();
            try { await ApplyAsync(); }
            catch (OperationCanceledException) { }
            catch (Exception error) { ShowError(error); }
        };
    }

    void RefreshCameraModeChoices()
    {
        if (!cameraOnly || source.SelectedItem is not ScreenCaptureSource device) return;
        var previous = cameraModeDeviceId == device.DeviceId ? cameraMode.SelectedItem as CameraMode : null;
        cameraModeDeviceId = device.DeviceId;
        var choices = reportedCameraModes.GetValueOrDefault(device.DeviceId) ?? [];
        CameraMode? saved = preferences.Camera is { DeviceReported: true } preference && preference.DeviceId == device.DeviceId
            ? new(preference.Width, preference.Height, preference.Fps) : null;
        // Older versions offered unverified, fixed modes. Default those selections to native
        // rather than repeating an unsupported choice. Current reported selections are retained.
        if (saved is { Width: > 0 } && !reportedCameraModes.ContainsKey(device.DeviceId)) choices = [saved];
        cameraMode.Items.Clear(); cameraMode.Items.Add(new CameraMode(0, 0, 0));
        cameraMode.Items.AddRange(choices.Cast<object>().ToArray());
        cameraMode.SelectedItem = cameraMode.Items.Cast<CameraMode>().FirstOrDefault(item => item == previous)
            ?? cameraMode.Items.Cast<CameraMode>().FirstOrDefault(item => item == saved) ?? cameraMode.Items[0];
    }

    async Task CheckCameraModesAsync()
    {
        if (preview || resourcesDisposed || busy || starting || stopping || session?.Running == true || source.SelectedItem is not ScreenCaptureSource device) return;
        var operation = BeginOperation();
        try
        {
            var choices = await CameraDevices.ModesAsync(device.DeviceId, lifetime.Token);
            if (resourcesDisposed || operationId != operation) return;
            reportedCameraModes[device.DeviceId] = choices;
            RefreshCameraModeChoices();
            state.Tag = null; state.Text = "Camera is off · press Start camera";
            SetNotice(choices.Count > 0 ? $"{choices.Count} camera-reported modes found. Automatic uses the native default; the frame cap controls processing separately."
                : "No supported camera modes were reported. Check the camera's source app and permissions.");
        }
        finally { EndOperation(operation); }
    }

    ScreenCaptureSource? SelectedSource() => source.SelectedItem is ScreenCaptureSource selected
        ? cameraOnly && cameraMode.SelectedItem is CameraMode mode
            ? selected with { CaptureWidth = mode.Width, CaptureHeight = mode.Height, CaptureFps = mode.Fps }
            : selected
        : null;

    void RefreshCameraSources()
    {
        sourceLabel.Text = "Camera"; source.AccessibleName = "Camera device";
        if (preview)
        {
            source.Items.Clear(); source.Items.Add(new ScreenCaptureSource("camera", 0, 0, "Example USB camera") { DeviceId = "preview-camera" }); source.SelectedIndex = 0;
            return;
        }
        if (!Visible || enumeratingCameras) return;
        _ = RefreshCamerasAsync();
    }

    async Task RefreshCamerasAsync()
    {
        enumeratingCameras = true; refresh.Enabled = false;
        try
        {
            var entries = await CameraDevices.EnumerateAsync(token: lifetime.Token);
            if (resourcesDisposed) return;
            var previous = (source.SelectedItem as ScreenCaptureSource)?.DeviceId ?? preferences.Camera?.DeviceId;
            source.Items.Clear(); source.Items.AddRange(entries.Cast<object>().ToArray());
            source.SelectedItem = entries.FirstOrDefault(item => item.DeviceId == previous) ?? entries.FirstOrDefault();
            cameraListLoaded = true;
            virtualCamera.Enabled = preview || CameraDevices.VirtualCameraInstalled();
            if (session?.Running != true) state.Text = entries.Count == 0 ? "No camera found. Connect one, then Refresh." : "Camera is off · press Start camera";
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { if (!resourcesDisposed) ShowError(error); }
        finally { enumeratingCameras = false; if (!resourcesDisposed) UpdateActions(); }
    }

    void QueueCameraApply()
    {
        if (!cameraOnly || resourcesDisposed || session?.Running != true) return;
        cameraApplyTimer.Stop(); cameraApplyTimer.Start();
    }

    Control CameraOutputs()
    {
        var panel = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true };
        CheckBox Mirror(CheckBox original, string label)
        {
            var toggle = new CheckBox { Text = label, AccessibleName = label, Checked = original.Checked, AutoSize = true, Margin = new(0, 7, 16, 0) };
            toggle.CheckedChanged += (_, _) => original.Checked = toggle.Checked;
            original.CheckedChanged += (_, _) => toggle.Checked = original.Checked;
            original.EnabledChanged += (_, _) => toggle.Enabled = original.Enabled;
            toggle.Enabled = original.Enabled;
            tips.SetToolTip(toggle, original == virtualCamera
                ? "Requires OBS Studio's Virtual Camera component. Open Webcam setup for installation and permission help."
                : "Publish enhanced GPU frames to a Spout-compatible receiver such as OBS.");
            return toggle;
        }
        panel.Controls.Add(Mirror(spout, "Spout / OBS")); panel.Controls.Add(Mirror(virtualCamera, "Virtual webcam"));
        var help = new Button { Text = "Webcam setup", AccessibleName = "Virtual webcam setup and camera permissions", AutoSize = true };
        help.Click += (_, _) =>
        {
            using var dialog = new Form { Text = "Camera output setup", Size = new(660, 310), StartPosition = FormStartPosition.CenterParent };
            var text = new Label { Dock = DockStyle.Fill, Padding = new(14), Text =
                "For OBS: add a Spout2 Capture source and choose DLSS5 Camera.\n\nFor Discord/Zoom: install OBS Studio with its Virtual Camera component, enable Virtual webcam here, then choose OBS Virtual Camera in your calling app. OBS need not run. Only one sender can use that camera at a time.\n\nNo microphone/audio or recording is enabled by this feature." };
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 45 };
            var privacy = new Button { Text = "Camera permissions", AutoSize = true };
            privacy.Click += (_, _) => Process.Start(new ProcessStartInfo("ms-settings:privacy-webcam") { UseShellExecute = true });
            var obs = new Button { Text = "Get OBS Studio", AutoSize = true };
            obs.Click += (_, _) => Process.Start(new ProcessStartInfo("https://obsproject.com/download") { UseShellExecute = true });
            buttons.Controls.AddRange([privacy, obs]); dialog.Controls.Add(text); dialog.Controls.Add(buttons); UiTheme.Apply(dialog, palette()); dialog.ShowDialog(this);
        };
        help.Disposed += (_, _) => help.Image?.Dispose(); panel.Controls.Add(help); return panel;
    }
}
