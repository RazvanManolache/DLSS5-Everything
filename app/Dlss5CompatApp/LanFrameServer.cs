using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

namespace Dlss5CompatApp;

internal interface ILanFrameProcessor : IDisposable
{
    Task ApplyAsync(LanFrameSettings settings, CancellationToken token);
    Task<byte[]> EvaluateAsync(ReadOnlyMemory<byte> rgba, bool reset, CancellationToken token);
}
internal sealed record LanFrameStatus(string State, ulong Frames = 0, long Revision = 0,
    int Width = 0, int Height = 0, double ProcessingMs = 0, string? Codec = null);

internal sealed class LanFrameServer : IDisposable
{
    readonly Func<int, int, LanFrameSettings, CancellationToken, Task<ILanFrameProcessor>> createProcessor;
    readonly CancellationTokenSource cancellation = new();
    TcpListener? listener;
    Task? loop;
    X509Certificate2? certificate;
    byte[] secret = [];
    LanFrameStatus status = new("Stopped");
    public LanFrameStatus Status => Volatile.Read(ref status);
    public string PairingToken { get; private set; } = "";
    public string Fingerprint { get; private set; } = "";
    public IPEndPoint? Endpoint => listener?.LocalEndpoint as IPEndPoint;
    public LanFrameServer(Func<int, int, LanFrameSettings, CancellationToken, Task<ILanFrameProcessor>> createProcessor) => this.createProcessor = createProcessor;

    public void Start(IPAddress address, int port)
    {
        if (loop is not null || cancellation.IsCancellationRequested) throw new InvalidOperationException("Create a new LAN server to restart.");
        if (!LanFrameProtocol.IsLanAddress(address) || port is < 1024 or > 65535)
            throw new InvalidDataException("Choose a local IPv4 address and port 1024..65535.");
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=DLSS5 Everything LAN", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.1") }, true));
        // Schannel rejects CreateSelfSigned's ephemeral key. Import into a temporary
        // user key container, without PersistKeySet; disposing the certificate deletes it.
        using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1));
        var pfx = generated.Export(X509ContentType.Pfx);
        try { certificate = X509CertificateLoader.LoadPkcs12(pfx, null, X509KeyStorageFlags.UserKeySet); }
        finally { CryptographicOperations.ZeroMemory(pfx); }
        Fingerprint = Convert.ToHexString(SHA256.HashData(certificate.RawData));
        secret = RandomNumberGenerator.GetBytes(32); PairingToken = Convert.ToHexString(secret);
        listener = new TcpListener(address, port); listener.Start(1);
        Volatile.Write(ref status, new("Waiting for device"));
        loop = Task.Run(RunAsync);
    }
    async Task RunAsync()
    {
        var token = cancellation.Token;
        try
        {
            while (!token.IsCancellationRequested)
            {
                using var client = await listener!.AcceptTcpClientAsync(token).ConfigureAwait(false);
                if (client.Client.RemoteEndPoint is not IPEndPoint remote || !LanFrameProtocol.IsLanAddress(remote.Address)) continue;
                client.NoDelay = true;
                using var stream = new SslStream(client.GetStream(), false);
                try
                {
                    using (var handshake = Deadline(token, 10))
                        await stream.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                        { ServerCertificate = certificate, EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                            ClientCertificateRequired = false, CertificateRevocationCheckMode = X509RevocationMode.NoCheck }, handshake.Token).ConfigureAwait(false);
                    await ServeAsync(stream, token).ConfigureAwait(false);
                }
                catch (Exception error) when (error is IOException or InvalidDataException or AuthenticationException or OperationCanceledException or
                    JsonException or ArgumentException or InvalidOperationException or UnauthorizedAccessException or
                    NotSupportedException or System.ComponentModel.Win32Exception)
                {
                    if (!token.IsCancellationRequested)
                    {
                        Volatile.Write(ref status, new("Device disconnected or session failed"));
                        // Never send local paths, model details, exception text or secrets to a client.
                        try { using var deadline = Deadline(token, 2); await LanFrameProtocol.ReplyAsync(stream,
                            new { ok = false, error = "session_failed" }, deadline.Token).ConfigureAwait(false); }
                        catch (Exception failure) when (failure is IOException or OperationCanceledException or InvalidOperationException) { }
                        await Task.Delay(250, token).ConfigureAwait(false);
                    }
                }
            }
        }
        catch (Exception error) when (error is OperationCanceledException or ObjectDisposedException or SocketException)
        { if (!token.IsCancellationRequested) Volatile.Write(ref status, new("Listener stopped unexpectedly")); }
    }
    async Task ServeAsync(SslStream stream, CancellationToken token)
    {
        LanFrameCommand open;
        using (var auth = Deadline(token, 10))
        {
            var header = await LanFrameProtocol.ReadHeaderAsync(stream, 0, auth.Token).ConfigureAwait(false);
            open = await ReadCommandAsync(stream, header.Length, auth.Token).ConfigureAwait(false);
        }
        if (open.Op != "open" || open.Version is not (LanFrameProtocol.Version or LanFrameProtocol.CodecVersion) || !Authenticate(open.Token))
            throw new InvalidDataException("Pairing failed.");
        var codecName = open.Codec ?? "raw";
        if ((open.BitrateMbps ?? 100) is < 20 or > 500 || (open.Fps ?? 30) is < 1 or > 120)
            throw new InvalidDataException("Invalid transport bitrate or frame rate.");
        if (codecName is not ("raw" or "pyrowave") || open.Version == 1 && codecName != "raw")
            throw new InvalidDataException("Unsupported codec contract.");
        if (codecName == "pyrowave" && !PyrowaveFrameCodec.Available)
        {
            using var deadline = Deadline(token, 2);
            await LanFrameProtocol.ReplyAsync(stream, new { ok = false, error = "codec_unavailable" }, deadline.Token).ConfigureAwait(false);
            return;
        }
        var pixelBytes = LanFrameProtocol.PixelBytes(open.Width, open.Height);
        var settings = (open.Settings ?? new()).Apply(new());
        Volatile.Write(ref status, new("Starting Phoenix DLSS5", Width: open.Width, Height: open.Height));
        using var startup = Deadline(token, 120);
        using var codec = await PyrowaveFrameCodec.CreateAsync(codecName, open.Width, open.Height, open.BitrateMbps ?? 100, open.Fps ?? 30, startup.Token).ConfigureAwait(false);
        using var processor = await createProcessor(open.Width, open.Height, settings, startup.Token).ConfigureAwait(false);
        long revision = 1; ulong lastSequence = 0, frames = 0; bool resetPending = true;
        using (var deadline = Deadline(token, 10))
            await LanFrameProtocol.ReplyAsync(stream, new { ok = true, op = "open", version = open.Version, session = Guid.NewGuid().ToString("N"),
                width = open.Width, height = open.Height, format = "rgba8_srgb", codec = codecName, chroma = "444", revision, settings,
                max_in_flight = 1, max_pixel_bytes = LanFrameProtocol.MaxPixelBytes, max_encoded_bytes = LanFrameProtocol.MaxEncodedBytes }, deadline.Token).ConfigureAwait(false);
        Volatile.Write(ref status, new("Connected", Revision: revision, Width: open.Width, Height: open.Height, Codec: codecName));
        while (!token.IsCancellationRequested)
        {
            (byte Type, int Length) header;
            using (var idle = Deadline(token, 30))
                header = await LanFrameProtocol.ReadHeaderAsync(stream, pixelBytes, idle.Token, codecName == "pyrowave").ConfigureAwait(false);
            using var operation = Deadline(token, 120); var opToken = operation.Token;
            if (header.Type == LanFrameProtocol.Control)
            {
                var command = await ReadCommandAsync(stream, header.Length, opToken).ConfigureAwait(false);
                if (command.Token is not null || command.Width != 0 || command.Height != 0 || command.Version != open.Version ||
                    command.Codec is not null || command.BitrateMbps is not null || command.Fps is not null)
                    throw new InvalidDataException("Session geometry and identity are immutable.");
                switch (command.Op)
                {
                    case "update":
                        var next = (command.Settings ?? throw new InvalidDataException("Missing settings.")).Apply(settings);
                        await processor.ApplyAsync(next, opToken).ConfigureAwait(false);
                        settings = next; ++revision; resetPending = true;
                        Volatile.Write(ref status, Status with { Revision = revision });
                        await LanFrameProtocol.ReplyAsync(stream, new { ok = true, op = "update", revision, settings }, opToken).ConfigureAwait(false);
                        break;
                    case "ping" when command.Settings is null:
                        await LanFrameProtocol.ReplyAsync(stream, new { ok = true, op = "ping", revision }, opToken).ConfigureAwait(false); break;
                    case "close" when command.Settings is null:
                        await LanFrameProtocol.ReplyAsync(stream, new { ok = true, op = "close" }, opToken).ConfigureAwait(false);
                        Volatile.Write(ref status, new("Waiting for device")); return;
                    default: throw new InvalidDataException("Unsupported session command.");
                }
                continue;
            }
            var metadata = new byte[12]; await stream.ReadExactlyAsync(metadata, opToken).ConfigureAwait(false);
            var sequence = BinaryPrimitives.ReadUInt64LittleEndian(metadata);
            var flags = BinaryPrimitives.ReadUInt32LittleEndian(metadata.AsSpan(8));
            if (sequence <= lastSequence || (flags & ~1u) != 0) throw new InvalidDataException("Invalid frame sequence or flags.");
            var payload = new byte[header.Length - 12]; await stream.ReadExactlyAsync(payload, opToken).ConfigureAwait(false);
            var timer = Stopwatch.StartNew();
            var pixels = await codec.DecodeAsync(payload, opToken).ConfigureAwait(false);
            if (pixels.Length != pixelBytes) throw new IOException("Invalid decoded frame size.");
            var output = await processor.EvaluateAsync(pixels, resetPending || flags == 1, opToken).ConfigureAwait(false);
            if (output.Length != pixelBytes) throw new IOException("Wrong processed frame size.");
            var encoded = await codec.EncodeAsync(output, opToken).ConfigureAwait(false);
            timer.Stop();
            var reply = new byte[29]; reply[0] = LanFrameProtocol.OutputFrame;
            BinaryPrimitives.WriteUInt32LittleEndian(reply.AsSpan(1), (uint)(24 + encoded.Length));
            BinaryPrimitives.WriteUInt64LittleEndian(reply.AsSpan(5), sequence);
            BinaryPrimitives.WriteInt64LittleEndian(reply.AsSpan(13), revision);
            BinaryPrimitives.WriteUInt64LittleEndian(reply.AsSpan(21), (ulong)(timer.Elapsed.TotalMilliseconds * 1000));
            await stream.WriteAsync(reply, opToken).ConfigureAwait(false);
            await stream.WriteAsync(encoded, opToken).ConfigureAwait(false);
            lastSequence = sequence; resetPending = false;
            Volatile.Write(ref status, new("Processing", ++frames, revision, open.Width, open.Height, timer.Elapsed.TotalMilliseconds, codecName));
        }
    }
    bool Authenticate(string? value)
    {
        if (value?.Length != 64) return false;
        try { return CryptographicOperations.FixedTimeEquals(Convert.FromHexString(value), secret); }
        catch (FormatException) { return false; }
    }
    static async Task<LanFrameCommand> ReadCommandAsync(Stream stream, int length, CancellationToken token)
    {
        var body = new byte[length]; await stream.ReadExactlyAsync(body, token).ConfigureAwait(false);
        return JsonSerializer.Deserialize<LanFrameCommand>(body, LanFrameProtocol.Json) ?? throw new InvalidDataException("Empty command.");
    }
    static CancellationTokenSource Deadline(CancellationToken token, int seconds)
    { var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(seconds)); return deadline; }
    public void Stop() { cancellation.Cancel(); listener?.Stop(); }
    public async Task StopAsync()
    {
        Stop(); if (loop is not null) await loop.ConfigureAwait(false);
        Volatile.Write(ref status, new("Stopped"));
    }
    public void Dispose()
    {
        Stop();
        // All network and backend continuations are independent of the WinForms thread.
        loop?.GetAwaiter().GetResult(); certificate?.Dispose();
        CryptographicOperations.ZeroMemory(secret); PairingToken = ""; Fingerprint = "";
    }
}
