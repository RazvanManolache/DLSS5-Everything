using System.Drawing;

namespace Dlss5CompatApp;

static partial class SmokeTestRunner
{
    internal static bool IsOwnedPrivateHelper(string path, int ownerPid, DateTimeOffset helperStarted, DateTimeOffset launchedAt)
    {
        var folder = Path.GetDirectoryName(Path.GetFullPath(path))!;
        var sessions = Path.GetDirectoryName(folder);
        return ownerPid > 0 && helperStarted >= launchedAt.AddSeconds(-2) &&
            Path.GetFileName(folder).StartsWith(ownerPid + "-", StringComparison.Ordinal) &&
            string.Equals(Path.GetFileName(sessions), "sessions", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(Path.GetFileName(Path.GetDirectoryName(sessions)), "DLSS5Phoenix", StringComparison.OrdinalIgnoreCase);
    }

    sealed record LogCursor(long Length, byte[] Prefix);

    static Dictionary<string, LogCursor> CaptureLogCursors(GameCandidate game)
    {
        var result = new Dictionary<string, LogCursor>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in LogPaths(game))
            try
            {
                LocalPaths.RequireNoLinks(path);
                using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                var prefix = new byte[Math.Min(128L, input.Length)];
                input.ReadExactly(prefix);
                result[path] = new(input.Length, prefix);
            }
            catch (IOException) { }
        return result;
    }

    static string FreshLogTail(string path, LogCursor? previous)
    {
        try
        {
            LocalPaths.RequireNoLinks(path);
            using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            long start = 0;
            if (previous is not null && input.Length >= previous.Length)
            {
                var prefix = new byte[Math.Min(128L, input.Length)];
                input.ReadExactly(prefix);
                if (prefix.AsSpan().SequenceEqual(previous.Prefix)) start = previous.Length;
            }
            input.Position = Math.Max(start, input.Length - 128 * 1024);
            using var reader = new StreamReader(input);
            return reader.ReadToEnd();
        }
        catch (IOException) { return ""; }
    }

    static async Task CaptureBridgeScreenshotAsync(PhoenixControlClient client, int pid,
        SmokeTestConfig config, SmokeTestResult result)
    {
        var reply = await client.SendAsync(PhoenixOperation.Screenshot, cancellationToken: config.CancellationToken);
        var until = DateTime.UtcNow.AddSeconds(12);
        var directory = Path.GetDirectoryName(client.BridgeDirectory)!;
        var postfix = "Phoenix-bridge-" + reply.Id;
        var expected = Path.Combine(directory, postfix + ".png");
        var nativeScreenshot = reply.ScreenshotPostfix == postfix;
        var complete = false;
        while (DateTime.UtcNow < until)
        {
            await Task.Delay(250, config.CancellationToken);
            if (nativeScreenshot)
            {
                // ReShade's native screenshot API predates the compatibility
                // writer acknowledgement. Accept only the requested PNG with a
                // completed IEND record, then decode it below; partial writes retry.
                expected = Directory.EnumerateFiles(directory, "*" + postfix + ".png").Take(2)
                    .FirstOrDefault(path => Path.GetFileNameWithoutExtension(path).EndsWith(postfix, StringComparison.Ordinal)) ?? expected;
                if (CompletePng(expected)) { complete = true; break; }
                continue;
            }
            var status = await client.SendAsync(PhoenixOperation.Status, timeout: TimeSpan.FromSeconds(10), cancellationToken: config.CancellationToken);
            if (status.Pid != pid || status.Session != reply.Session) return;
            if (status.Snapshot.TryGetProperty("screenshot", out var screenshot) &&
                screenshot.TryGetProperty("pending", out var pending) && pending.ValueKind == System.Text.Json.JsonValueKind.False &&
                screenshot.TryGetProperty("path", out var path) && path.ValueKind == System.Text.Json.JsonValueKind.String &&
                LocalPaths.Same(path.GetString()!, expected))
            { complete = true; break; }
        }
        // The PNG writer creates its destination before encoding finishes. Its
        // completion acknowledgement, rather than File.Exists, retires that write.
        if (!complete || !File.Exists(expected)) return;
        LocalPaths.RequireNoLinks(expected);
        if (new FileInfo(expected).Length > 128 * 1024 * 1024) throw new IOException("Game screenshot exceeds report limit.");
        var folder = Path.GetFullPath(config.ReportPath) + ".assets";
        Directory.CreateDirectory(folder);
        var target = Path.Combine(folder, pid + "-" + reply.Id + ".png");
        File.Copy(expected, target);
        result.PresentedScreenshot = target;
        using var image = new Bitmap(target);
        var first = image.GetPixel(0, 0);
        for (var row = 0; row < 16; row++)
            for (var column = 0; column < 16; column++)
            {
                var pixel = image.GetPixel(column * (image.Width - 1) / 15, row * (image.Height - 1) / 15);
                if (Math.Abs(pixel.R - first.R) + Math.Abs(pixel.G - first.G) + Math.Abs(pixel.B - first.B) > 6)
                    result.ScreenshotHasVariation = true;
            }
    }

    internal static bool CompletePng(string path)
    {
        try
        {
            LocalPaths.RequireNoLinks(path);
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (file.Length is < 20 or > 128 * 1024 * 1024) return false;
            Span<byte> signature = stackalloc byte[8]; file.ReadExactly(signature);
            if (!signature.SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })) return false;
            file.Position = file.Length - 12;
            Span<byte> trailer = stackalloc byte[12]; file.ReadExactly(trailer);
            return trailer.SequenceEqual(new byte[] { 0, 0, 0, 0, 73, 69, 78, 68, 174, 66, 96, 130 });
        }
        catch (IOException) { return false; }
    }
}
