using System.Buffers.Binary;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Dlss5CompatApp;

internal sealed record LanFrameSettings
{
    public bool Enabled { get; init; } = true;
    public int Iterations { get; init; } = 1;
    public float Tone { get; init; } = 1;
    public float Structure { get; init; } = 1;
    public float Skin { get; init; } = -1;
    public bool AutoMask { get; init; }
    public void Validate() => Screen().Validate();
    public ScreenStudioSettings Screen() => new()
    { Enabled = Enabled, Iterations = Iterations, Tone = Tone, Structure = Structure, Skin = Skin, AutoMask = AutoMask };
}

internal sealed record LanFramePatch(bool? Enabled = null, int? Iterations = null, float? Tone = null,
    float? Structure = null, float? Skin = null, bool? AutoMask = null)
{
    public LanFrameSettings Apply(LanFrameSettings current)
    {
        var next = current with { Enabled = Enabled ?? current.Enabled, Iterations = Iterations ?? current.Iterations,
            Tone = Tone ?? current.Tone, Structure = Structure ?? current.Structure,
            Skin = Skin ?? current.Skin, AutoMask = AutoMask ?? current.AutoMask };
        next.Validate(); return next;
    }
}

internal sealed record LanFrameCommand(string Op, string? Token = null, int Version = 1,
    int Width = 0, int Height = 0, LanFramePatch? Settings = null,
    string? Codec = null, int? BitrateMbps = null, int? Fps = null);

internal static class LanFrameProtocol
{
    public const int Version = 1, MaxControlBytes = 4096, MaxPixelBytes = 64 * 1024 * 1024;
    public const int CodecVersion = 2, MaxEncodedBytes = 16 * 1024 * 1024;
    public const byte Control = 1, InputFrame = 2, OutputFrame = 3;
    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 8
    };
    public static int PixelBytes(int width, int height)
    {
        if (width is < 1 or > 16384 || height is < 1 or > 16384 || (long)width * height * 4 > MaxPixelBytes)
            throw new InvalidDataException("Frame dimensions exceed the LAN memory budget.");
        return checked(width * height * 4);
    }
    public static bool IsLanAddress(IPAddress address)
    {
        if (address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) return false;
        var b = address.GetAddressBytes();
        return IPAddress.IsLoopback(address) || b[0] == 10 || b[0] == 172 && b[1] is >= 16 and <= 31 ||
            b[0] == 192 && b[1] == 168 || b[0] == 169 && b[1] == 254;
    }
    // Header: type:u8, payload length:u32 LE. Validate lengths before allocating a payload.
    public static async Task<(byte Type, int Length)> ReadHeaderAsync(Stream stream, int pixelBytes, CancellationToken token, bool compressed = false)
    {
        var header = new byte[5]; await stream.ReadExactlyAsync(header, token).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(1));
        if (header[0] == Control && length is > 0 and <= MaxControlBytes ||
            header[0] == InputFrame && pixelBytes > 0 && (compressed ? length is >= 20 and <= MaxEncodedBytes + 12 : length == (uint)(pixelBytes + 12)))
            return (header[0], checked((int)length));
        throw new InvalidDataException("Invalid packet type or size.");
    }
    public static async Task WriteAsync(Stream stream, byte type, ReadOnlyMemory<byte> payload, CancellationToken token)
    {
        var header = new byte[5]; header[0] = type;
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(1), checked((uint)payload.Length));
        await stream.WriteAsync(header, token).ConfigureAwait(false);
        await stream.WriteAsync(payload, token).ConfigureAwait(false);
    }
    public static Task ReplyAsync(Stream stream, object value, CancellationToken token) =>
        WriteAsync(stream, Control, JsonSerializer.SerializeToUtf8Bytes(value, Json), token);
}
