using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Dlss5CompatApp;

internal sealed record ScreenMediaOptions(string Input, string Output, string Ffmpeg = "", bool Nvenc = false, int Quality = 20, bool Audio = true)
{
    public bool Video => !new[] { ".png", ".jpg", ".jpeg", ".bmp" }.Contains(Path.GetExtension(Input).ToLowerInvariant());
    public void Validate()
    {
        LocalPaths.RequireNoLinks(LocalPaths.Normalize(Input)); LocalPaths.RequireNoLinks(LocalPaths.Normalize(Output));
        if (!File.Exists(Input)) throw new FileNotFoundException("Choose an existing input file.");
        if (File.Exists(Output) || LocalPaths.Same(Input, Output)) throw new IOException("Choose a new output filename; existing files are never overwritten.");
        if (Quality is < 0 or > 51) throw new InvalidDataException("Encoder quality must be 0..51.");
        if (!Video && Path.GetExtension(Output).ToLowerInvariant() != ".png" || Video && Path.GetExtension(Output).ToLowerInvariant() != ".mp4")
            throw new InvalidDataException("Image output must be PNG; video output must be MP4.");
        if (Video)
        {
            LocalPaths.RequireNoLinks(LocalPaths.Normalize(Ffmpeg));
            if (!File.Exists(Ffmpeg) || !File.Exists(Path.Combine(Path.GetDirectoryName(Ffmpeg)!, "ffprobe.exe")))
                throw new FileNotFoundException("Choose ffmpeg.exe with ffprobe.exe beside it.");
        }
    }
}

internal static class ScreenMediaConverter
{
    internal const int MaximumFrames = 2000;
    internal const long MaximumTemporaryBytes = 4L * 1024 * 1024 * 1024;
    internal static string FindFfmpeg() => (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
        .Select(p => Path.Combine(p.Trim('"'), "ffmpeg.exe")).FirstOrDefault(File.Exists) ?? "";

    internal static ScreenStudioStatus? ObserveWorker(Func<ScreenStudioStatus?> read, Func<bool> running)
    {
        var status = read();
        if (status?.State != "completed" && !running())
        {
            // The worker publishes terminal status before exiting; the first read can precede that publication.
            status = read();
            if (status?.State != "completed") throw new IOException(status?.Error is { Length: > 0 } error ? error : "Conversion worker stopped before completion.");
        }
        if (status?.State == "failed") throw new IOException(status.Error);
        return status;
    }

    internal static (int Width, int Height, string Rate) ReadVideoMetadata(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var video = doc.RootElement.GetProperty("streams").EnumerateArray().FirstOrDefault(s => s.GetProperty("codec_type").GetString() == "video");
        if (video.ValueKind == JsonValueKind.Undefined) throw new InvalidDataException("No video stream found.");
        if (video.TryGetProperty("color_transfer", out var transfer) && transfer.GetString() is "smpte2084" or "arib-std-b67")
            throw new InvalidDataException("HDR video conversion is not supported. Provide an SDR source.");
        var w = video.GetProperty("width").GetInt32(); var h = video.GetProperty("height").GetInt32();
        var rate = video.GetProperty("avg_frame_rate").GetString() ?? "";
        var parts = rate.Split('/');
        if (w is < 1 or > 16384 || h is < 1 or > 16384 || parts.Length != 2 ||
            !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var numerator) ||
            !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var denominator) ||
            denominator <= 0 || numerator <= 0 || numerator / (double)denominator > 240)
            throw new InvalidDataException("Unsupported video dimensions or frame rate.");
        return (w, h, rate);
    }

    internal static async Task<string> RunToolAsync(string exe, IEnumerable<string> arguments, CancellationToken token,
        string? budgetDirectory = null)
    {
        token.ThrowIfCancellationRequested();
        var info = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        using var process = Process.Start(info) ?? throw new IOException("Could not start media tool.");
        var stdout = new StringBuilder(); var stderr = new StringBuilder();
        static async Task Drain(StreamReader reader, StringBuilder target)
        {
            var buffer = new char[4096]; int count;
            while ((count = await reader.ReadAsync(buffer)) > 0) if (target.Length < 128 * 1024) target.Append(buffer, 0, Math.Min(count, 128 * 1024 - target.Length));
        }
        var output = Drain(process.StandardOutput, stdout); var error = Drain(process.StandardError, stderr);
        try
        {
            while (!process.HasExited)
            {
                token.ThrowIfCancellationRequested();
                if (budgetDirectory is not null && Directory.EnumerateFiles(budgetDirectory).Sum(f => new FileInfo(f).Length) > MaximumTemporaryBytes)
                    throw new IOException("Decoded frames exceeded the 4 GiB temporary-storage limit.");
                await Task.Delay(150, token).ConfigureAwait(false);
            }
            await process.WaitForExitAsync(token).ConfigureAwait(false); await Task.WhenAll(output, error).ConfigureAwait(false);
            if (process.ExitCode != 0) throw new IOException("Media tool failed: " + stderr.ToString()[^Math.Min(stderr.Length, 4000)..]);
            return stdout.ToString();
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync().ConfigureAwait(false); await Task.WhenAll(output, error).ConfigureAwait(false);
        }
    }

    public static async Task ConvertAsync(ScreenMediaOptions media, ScreenStudioSettings settings, PayloadInfo payload, string model,
        IProgress<(string Stage, int Done, int Total)>? progress = null, CancellationToken token = default)
    {
        media.Validate(); settings.Validate();
        // All decode intermediates stay in our disposable GUID session, never beside the source.
        using var session = new ScreenStudioSession(keepSessionFiles: false);
        var decoded = Path.Combine(session.DirectoryPath, "decoded");
        LocalPaths.RequireNoLinks(decoded); Directory.CreateDirectory(decoded);
        string rate = "30/1";
        string? partial = null;
        try
        {
            IReadOnlyList<string> inputs;
            if (media.Video)
            {
                progress?.Report(("Inspecting video", 0, 0));
                var metadata = await RunToolAsync(Path.Combine(Path.GetDirectoryName(media.Ffmpeg)!, "ffprobe.exe"),
                    ["-v", "error", "-show_streams", "-of", "json", media.Input], token).ConfigureAwait(false);
                var (width, height, fps) = ReadVideoMetadata(metadata); rate = fps;
                if (width * settings.Scale > 2560 || height * settings.Scale > 1080)
                    throw new InvalidDataException("Lower processing scale to fit a supported Phoenix DLSS5 canvas (maximum 2560 x 1080).");
                progress?.Report(("Decoding video", 0, 0));
                await RunToolAsync(media.Ffmpeg, ["-nostdin", "-v", "error", "-n", "-i", media.Input, "-map", "0:v:0", "-an",
                    "-vf", "fps=" + rate, "-frames:v", (MaximumFrames + 1).ToString(CultureInfo.InvariantCulture), Path.Combine(decoded, "%09d.png")], token, decoded).ConfigureAwait(false);
                inputs = Directory.GetFiles(decoded, "*.png").Order(StringComparer.Ordinal).ToArray();
                if (inputs.Count > MaximumFrames) throw new InvalidDataException($"Conversion is limited to {MaximumFrames} frames per job. Use a shorter video.");
            }
            else inputs = [media.Input];
            if (inputs.Count == 0) throw new InvalidDataException("Input contains no decodable frames.");
            session.ConversionInputs = inputs;
            await session.StartAsync(new("files", 0, 0, "Media conversion"), settings with { Hdr = false, Spout = false, Fps = 0, Comparison = 0 }, payload, model, cancellationToken: token).ConfigureAwait(false);
            var clock = Stopwatch.StartNew(); var last = 0;
            while (true)
            {
                token.ThrowIfCancellationRequested();
                var status = ObserveWorker(session.ReadStatus, () => session.Running);
                if (status is not null) progress?.Report(("Processing Phoenix DLSS5 frames", status.Converted, inputs.Count));
                if (status?.State == "completed") break;
                if (status?.Converted > last) { last = status.Converted; clock.Restart(); }
                if (clock.Elapsed > TimeSpan.FromMinutes(3)) throw new TimeoutException("No conversion progress for three minutes.");
                if (Directory.EnumerateFiles(session.DirectoryPath, "*.png", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length) > MaximumTemporaryBytes)
                    throw new IOException("Conversion exceeded the 4 GiB temporary-storage limit.");
                await Task.Delay(200, token).ConfigureAwait(false);
            }
            var converted = Path.Combine(session.DirectoryPath, "converted");
            if (Directory.GetFiles(converted, "*.png").Length != inputs.Count) throw new IOException("Conversion output is incomplete.");
            var destination = LocalPaths.Normalize(media.Output); LocalPaths.RequireNoLinks(destination);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            partial = destination + "." + Guid.NewGuid().ToString("N") + ".partial";
            if (media.Video)
            {
                progress?.Report(("Encoding MP4", 0, 0));
                var args = new List<string> { "-nostdin", "-v", "error", "-n", "-framerate", rate, "-i", Path.Combine(converted, "%09d.png"), "-i", media.Input, "-map", "0:v:0" };
                if (media.Audio) args.AddRange(["-map", "1:a:0?", "-c:a", "aac"]); else args.Add("-an");
                args.AddRange(["-vf", "pad=ceil(iw/2)*2:ceil(ih/2)*2", "-c:v", media.Nvenc ? "h264_nvenc" : "libx264", media.Nvenc ? "-cq" : "-crf", media.Quality.ToString(CultureInfo.InvariantCulture),
                    "-pix_fmt", "yuv420p", "-frames:v", inputs.Count.ToString(CultureInfo.InvariantCulture), "-movflags", "+faststart", "-f", "mp4", partial]);
                await RunToolAsync(media.Ffmpeg, args, token).ConfigureAwait(false);
            }
            else File.Copy(Path.Combine(converted, "000000001.png"), partial, false);
            token.ThrowIfCancellationRequested(); File.Move(partial, destination, false); partial = null;
            progress?.Report(("Completed", inputs.Count, inputs.Count));
        }
        finally
        {
            await session.StopAsync().ConfigureAwait(false);
            if (partial is not null && File.Exists(partial)) { LocalPaths.RequireNoLinks(partial); File.Delete(partial); }
        }
    }
}
