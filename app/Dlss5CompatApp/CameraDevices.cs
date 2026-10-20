using System.Diagnostics;
using System.Text.Json;
using Microsoft.Win32;

namespace Dlss5CompatApp;

internal static class CameraDevices
{
    // The installed OBS DirectShow filter is external. We publish to its documented queue;
    // we neither register a driver nor change its name/configuration behind the user's back.
    internal const string ObsCameraClass = @"CLSID\{A3FCE0F5-3493-419F-958A-ABA1250EC20B}\InprocServer32";
    internal static bool VirtualCameraInstalled()
    {
        using var root = RegistryKey.OpenBaseKey(RegistryHive.ClassesRoot, RegistryView.Registry64);
        using var key = root.OpenSubKey(ObsCameraClass);
        return key?.GetValue(null) is string path && File.Exists(path.Trim('"'));
    }

    internal static async Task<IReadOnlyList<ScreenCaptureSource>> EnumerateAsync(string? workerPath = null, CancellationToken token = default)
    {
        var reply = await QueryAsync(["--list-cameras"], workerPath, token).ConfigureAwait(false);
        return Parse(reply.Text, reply.ExitCode);
    }

    internal static async Task<IReadOnlyList<ScreenStudioView.CameraMode>> ModesAsync(string deviceId, CancellationToken token)
    {
        var reply = await QueryAsync(["--camera-modes", deviceId], null, token).ConfigureAwait(false);
        return ParseModes(reply.Text, reply.ExitCode);
    }

    static async Task<(string Text, int ExitCode)> QueryAsync(string[] arguments, string? workerPath, CancellationToken token)
    {
        workerPath ??= Path.Combine(AppContext.BaseDirectory, "Runtime", "screen", "dlss5-screen.exe");
        if (!File.Exists(workerPath)) throw new FileNotFoundException("The camera capture worker is missing. Rebuild or reinstall the application.", workerPath);
        using var process = new Process { StartInfo = new(workerPath) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true } };
        foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        process.Start();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(10));
        var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var error = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            var text = await output.ConfigureAwait(false); _ = await error.ConfigureAwait(false);
            return (text, process.ExitCode);
        }
        catch
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            throw;
        }
    }

    internal static IReadOnlyList<ScreenStudioView.CameraMode> ParseModes(string json, int exitCode = 0)
    {
        if (json.Length > 256 * 1024) throw new InvalidDataException("Camera mode list is too large.");
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.GetProperty("schema").GetInt32() != 1) throw new InvalidDataException("Unsupported camera worker version.");
        if (exitCode != 0 || document.RootElement.TryGetProperty("error", out _))
            throw new IOException(document.RootElement.TryGetProperty("error", out var error) ? error.GetString() : "Cannot read camera modes.");
        return document.RootElement.GetProperty("modes").EnumerateArray().Select(item =>
        {
            var numerator = item.GetProperty("fps_numerator").GetDouble();
            var denominator = item.GetProperty("fps_denominator").GetDouble();
            var rate = denominator > 0 ? numerator / denominator : 0;
            return new ScreenStudioView.CameraMode(item.GetProperty("width").GetInt32(), item.GetProperty("height").GetInt32(),
                double.IsFinite(rate) && rate > 0 && rate <= 120 ? (int)Math.Round(rate) : 0);
        }).Where(item => item.Width is >= 2 and <= 3840 && item.Height is >= 2 and <= 2160 &&
            item.Width % 2 == 0 && item.Height % 2 == 0 && item.Fps is >= 1 and <= 120).Distinct().ToArray();
    }

    internal static IReadOnlyList<ScreenCaptureSource> Parse(string json, int exitCode = 0)
    {
        if (json.Length > 256 * 1024) throw new InvalidDataException("Camera list is too large.");
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.GetProperty("schema").GetInt32() != 1) throw new InvalidDataException("Unsupported camera worker version.");
        if (exitCode != 0 || doc.RootElement.TryGetProperty("error", out _))
            throw new IOException(doc.RootElement.TryGetProperty("error", out var failure) ? failure.GetString() : "Camera enumeration failed.");
        return doc.RootElement.GetProperty("devices").EnumerateArray().Select(item => new ScreenCaptureSource("camera", 0, 0,
            item.GetProperty("name").GetString() ?? "Camera") { DeviceId = item.GetProperty("id").GetString() ?? "" })
            // Feeding our own output into the input would create a camera feedback loop.
            .Where(item => !item.Name.Equals("OBS Virtual Camera", StringComparison.OrdinalIgnoreCase) &&
                !item.DeviceId.Contains("a3fce0f5-3493-419f-958a-aba1250ec20b", StringComparison.OrdinalIgnoreCase))
            .DistinctBy(item => item.DeviceId, StringComparer.OrdinalIgnoreCase).Take(64).ToArray();
    }

    internal static void Validate(ScreenCaptureSource source, ScreenStudioSettings settings, bool? virtualInstalled = null)
    {
        var automatic = source.CaptureWidth == 0 && source.CaptureHeight == 0 && source.CaptureFps == 0;
        if (source.Kind != "camera" || string.IsNullOrWhiteSpace(source.DeviceId) || source.DeviceId.Length > 8192 || source.DeviceId.Contains('\0') ||
            !automatic && (source.CaptureWidth is < 2 or > 3840 || source.CaptureHeight is < 2 or > 2160 ||
            source.CaptureWidth % 2 != 0 || source.CaptureHeight % 2 != 0 || source.CaptureFps is < 1 or > 120))
            throw new InvalidDataException("Choose a valid camera resolution and frame rate.");
        if (settings.Hdr) throw new InvalidDataException("Camera capture uses SDR; HDR transport is unavailable.");
        if (settings.VirtualCamera && !(virtualInstalled ?? VirtualCameraInstalled()))
            throw new InvalidOperationException("Install OBS Studio's Virtual Camera component first, then choose OBS Virtual Camera in Discord/Zoom. OBS itself can remain closed.");
    }
}
