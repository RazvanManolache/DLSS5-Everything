using System.Net;
using System.Text.Json;

namespace Dlss5CompatApp;

internal static class StreamerPairing
{
    internal static byte[] Serialize(IPEndPoint endpoint, string fingerprint, string token, bool pyrowave)
    {
        if (!LanFrameProtocol.IsLanAddress(endpoint.Address) || endpoint.Port is < 1024 or > 65535 ||
            fingerprint.Length != 64 || token.Length != 64 || !fingerprint.All(Uri.IsHexDigit) || !token.All(Uri.IsHexDigit))
            throw new InvalidDataException("Start the streamer before exporting pairing.");
        return JsonSerializer.SerializeToUtf8Bytes(new {
            host = endpoint.Address.ToString(), port = endpoint.Port, fingerprint, token,
            codec = pyrowave ? "pyrowave" : "raw", bitrate = 100, fps = 15, maxEdge = 4096,
            captureMode = "follow", touchThrough = true, opacity = 1
        });
    }

    internal static void WriteNew(string path, byte[] json)
    {
        path = LocalPaths.Normalize(path); LocalPaths.RequireNoLinks(path);
        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        file.Write(json); file.Flush(true);
    }
}
