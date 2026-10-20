using System.Buffers.Binary;
using System.Net;
using System.Text.Json;

namespace Dlss5CompatApp.Tests;

// Auth, socket and GPU scenarios must be validated separately. These are CPU contracts.
public class LanFrameTests
{
    [Theory]
    [InlineData("wrong-token")]
    [InlineData("unknown-field")]
    [InlineData("invalid-size")]
    public async Task RejectedClientDoesNotKillTheListener(string failure)
    {
        var reserve = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        reserve.Start(); var port = ((IPEndPoint)reserve.LocalEndpoint).Port; reserve.Stop();
        using var server = new LanFrameServer((_, _, _, _) => throw new InvalidOperationException("No authenticated session expected"));
        server.Start(IPAddress.Loopback, port);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        for (var attempt = 0; attempt < 2; attempt++) {
            using var socket = new System.Net.Sockets.TcpClient();
            await socket.ConnectAsync(IPAddress.Loopback, port, timeout.Token);
            using var tls = new System.Net.Security.SslStream(socket.GetStream(), false, (_, cert, _, _) =>
                cert is not null && Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(cert.GetRawCertData())) == server.Fingerprint);
            await tls.AuthenticateAsClientAsync(new System.Net.Security.SslClientAuthenticationOptions { TargetHost = "localhost" }, timeout.Token);
            Assert.True(tls.IsAuthenticated);
            if (attempt == 1) continue;
            if (failure == "invalid-size") await tls.WriteAsync(new byte[] { 2, 1, 0, 0, 0 }, timeout.Token);
            else {
                var json = failure == "wrong-token" ? "{\"op\":\"open\",\"version\":2,\"token\":\"" + new string('0',64) + "\"}" : "{\"op\":\"open\",\"unknown\":true}";
                var body = System.Text.Encoding.UTF8.GetBytes(json); var header = new byte[5]; header[0] = 1;
                BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(1),body.Length);
                await tls.WriteAsync(header,timeout.Token); await tls.WriteAsync(body,timeout.Token);
            }
            var reply = await LanFrameProtocol.ReadHeaderAsync(tls,0,timeout.Token);
            var bytes = new byte[reply.Length]; await tls.ReadExactlyAsync(bytes,timeout.Token);
            using var parsed = JsonDocument.Parse(bytes); Assert.False(parsed.RootElement.GetProperty("ok").GetBoolean());
        }
    }
    [Fact]
    public async Task LoopbackTlsHandshakeWorksWithWindowsSchannel()
    {
        // Reserve an ephemeral port only to discover an available non-privileged number.
        var reserve = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        reserve.Start(); var port = ((IPEndPoint)reserve.LocalEndpoint).Port; reserve.Stop();
        using var server = new LanFrameServer((_, _, _, _) => throw new InvalidOperationException("No authenticated session expected"));
        server.Start(IPAddress.Loopback, port);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var socket = new System.Net.Sockets.TcpClient();
        await socket.ConnectAsync(IPAddress.Loopback, port, timeout.Token);
        using var tls = new System.Net.Security.SslStream(socket.GetStream(), false, (_, cert, _, _) =>
            cert is not null && Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(cert.GetRawCertData())) == server.Fingerprint);
        await tls.AuthenticateAsClientAsync(new System.Net.Security.SslClientAuthenticationOptions {
            TargetHost = "localhost", EnabledSslProtocols = System.Security.Authentication.SslProtocols.Tls12 | System.Security.Authentication.SslProtocols.Tls13
        }, timeout.Token);
        Assert.True(tls.IsAuthenticated); Assert.True(tls.IsEncrypted);
    }
    [Fact]
    public void DefaultsKeepOnePassNativeResolutionAndDirectPhoenix()
    {
        var settings = new LanFrameSettings(); settings.Validate();
        Assert.True(settings.Enabled); Assert.Equal(1, settings.Iterations);
        Assert.Equal(1, settings.Screen().Scale);
        Assert.Equal(0, settings.Screen().Phoenix().ResolutionMode);
        Assert.Equal(0, settings.Screen().Phoenix().Upscaler);
        Assert.Equal("3", PhoenixConfiguration.Controls(settings.Screen().Phoenix())["Backend"]);
    }
    [Theory]
    [InlineData(0, 1)] [InlineData(1, 0)] [InlineData(-1, 100)]
    [InlineData(16385, 1)] [InlineData(1, 16385)] [InlineData(7680, 4320)]
    [InlineData(int.MaxValue, int.MaxValue)]
    public void DimensionsRejectOverflowAndMemoryExcess(int width, int height) =>
        Assert.Throws<InvalidDataException>(() => LanFrameProtocol.PixelBytes(width, height));
    [Theory]
    [InlineData(1, 1, 4)] [InlineData(3840, 2160, 33177600)] [InlineData(16384, 1024, 67108864)]
    public void CustomDimensionsFitBudget(int w, int h, int expected) => Assert.Equal(expected, LanFrameProtocol.PixelBytes(w, h));
    [Theory]
    [InlineData("127.0.0.1", true)] [InlineData("10.1.2.3", true)]
    [InlineData("172.16.0.1", true)] [InlineData("172.31.255.1", true)]
    [InlineData("192.168.0.1", true)] [InlineData("169.254.2.1", true)]
    [InlineData("172.15.0.1", false)] [InlineData("172.32.0.1", false)]
    [InlineData("8.8.8.8", false)] [InlineData("0.0.0.0", false)]
    [InlineData("::1", false)] [InlineData("::ffff:192.168.1.1", false)]
    public void OnlyLocalIpv4AddressesAreAllowed(string value, bool expected) =>
        Assert.Equal(expected, LanFrameProtocol.IsLanAddress(IPAddress.Parse(value)));
    [Fact]
    public void PatchesPreserveUnspecifiedSettings()
    {
        var original = new LanFrameSettings { Skin = .8f, AutoMask = true, Structure = .4f };
        var next = new LanFramePatch(Tone: 1.6f, Enabled: false).Apply(original);
        Assert.Equal(1.6f, next.Tone); Assert.False(next.Enabled);
        Assert.Equal(.8f, next.Skin); Assert.True(next.AutoMask); Assert.Equal(.4f, next.Structure);
        Assert.Equal(1f, original.Tone); Assert.True(original.Enabled);
    }
    [Fact]
    public void PatchesRejectInvalidRangesAndNonfiniteNumbers()
    {
        Assert.Throws<InvalidDataException>(() => new LanFramePatch(Iterations: 0).Apply(new()));
        Assert.Throws<InvalidDataException>(() => new LanFramePatch(Iterations: 11).Apply(new()));
        Assert.Throws<InvalidDataException>(() => new LanFramePatch(Tone: float.NaN).Apply(new()));
        Assert.Throws<InvalidDataException>(() => new LanFramePatch(Structure: float.PositiveInfinity).Apply(new()));
        Assert.Throws<InvalidDataException>(() => new LanFramePatch(Skin: -2).Apply(new()));
    }
    [Fact]
    public void JsonUsesDocumentedSnakeCaseAndRejectsUnknownControls()
    {
        var command = JsonSerializer.Deserialize<LanFrameCommand>("""{"op":"update","settings":{"auto_mask":true,"iterations":2}}""", LanFrameProtocol.Json)!;
        Assert.Equal("update", command.Op); Assert.True(command.Settings!.AutoMask); Assert.Equal(2, command.Settings.Iterations);
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<LanFrameCommand>("""{"op":"open","path":"anything"}""", LanFrameProtocol.Json));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<LanFrameCommand>("""{"op":"update","settings":{"model":"anything"}}""", LanFrameProtocol.Json));
    }
    [Theory]
    [InlineData(1, 0, 0)] [InlineData(1, 4097, 0)] [InlineData(2, 16, 0)]
    [InlineData(2, 15, 4)] [InlineData(2, 17, 4)] [InlineData(3, 16, 4)]
    [InlineData(255, int.MaxValue, 4)]
    public async Task PacketHeaderRejectsInvalidSizesBeforeReadingBodies(int type, int length, int pixelBytes)
    {
        var bytes = new byte[5]; bytes[0] = (byte)type;
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(1), (uint)length);
        using var stream = new MemoryStream(bytes);
        await Assert.ThrowsAsync<InvalidDataException>(() => LanFrameProtocol.ReadHeaderAsync(stream, pixelBytes, default));
    }
    [Theory]
    [InlineData(1, 4096, 0)] [InlineData(2, 16, 4)]
    public async Task PacketHeadersUseLittleEndianAndExpectedTypes(int type, int length, int pixelBytes)
    {
        var bytes = new byte[5]; bytes[0] = (byte)type;
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(1), (uint)length);
        using var stream = new MemoryStream(bytes);
        var header = await LanFrameProtocol.ReadHeaderAsync(stream, pixelBytes, default);
        Assert.Equal((byte)type, header.Type); Assert.Equal(length, header.Length);
    }
    [Fact]
    public async Task TruncatedHeadersFailClosed()
    {
        using var stream = new MemoryStream(new byte[4]);
        await Assert.ThrowsAsync<EndOfStreamException>(() => LanFrameProtocol.ReadHeaderAsync(stream, 4, default));
    }
    [Theory]
    [InlineData(12, false)] [InlineData(13, false)] [InlineData(20, true)] [InlineData(16 * 1024 * 1024 + 12, true)]
    [InlineData(16 * 1024 * 1024 + 13, false)]
    public async Task CompressedPacketsUseIndependentBound(int length, bool valid)
    {
        var header = new byte[5]; header[0] = LanFrameProtocol.InputFrame;
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(1), (uint)length);
        using var stream = new MemoryStream(header);
        if (valid) Assert.Equal(length, (await LanFrameProtocol.ReadHeaderAsync(stream, 4, default, compressed: true)).Length);
        else await Assert.ThrowsAsync<InvalidDataException>(() => LanFrameProtocol.ReadHeaderAsync(stream, 4, default, compressed: true));
    }
    [Fact]
    public void V2NegotiatesCodecButV1StillDefaultsToRaw()
    {
        var old = JsonSerializer.Deserialize<LanFrameCommand>("""{"op":"open","width":10,"height":10}""", LanFrameProtocol.Json)!;
        Assert.Equal(1, old.Version); Assert.Null(old.Codec);
        var modern = JsonSerializer.Deserialize<LanFrameCommand>("""{"op":"open","version":2,"codec":"pyrowave","bitrate_mbps":100,"fps":15}""", LanFrameProtocol.Json)!;
        Assert.Equal(2, modern.Version); Assert.Equal("pyrowave", modern.Codec); Assert.Equal(100, modern.BitrateMbps); Assert.Equal(15, modern.Fps);
    }
    [Fact]
    public async Task ControlRepliesUseBoundedJsonHeader()
    {
        using var stream = new MemoryStream();
        await LanFrameProtocol.ReplyAsync(stream, new { ok = true, op = "update", revision = 2 }, default);
        stream.Position = 0; var header = await LanFrameProtocol.ReadHeaderAsync(stream, 0, default);
        using var json = JsonDocument.Parse(stream.ToArray().AsMemory(5));
        Assert.Equal(stream.Length - 5, header.Length); Assert.True(json.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal(2, json.RootElement.GetProperty("revision").GetInt32());
    }
}
