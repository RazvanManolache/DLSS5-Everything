using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Dlss5CompatApp;

// Adapter for feed_ipc.h v7's CPU RGBA8 route. No screen capture, SR hook or per-frame files.
internal sealed class LanPhoenixBackend : ILanFrameProcessor
{
    const uint Magic = 0x35534c44, IpcVersion = 7;
    readonly string directory = Path.Combine(LocalData.DirectoryPath, "LanSessions", Guid.NewGuid().ToString("N"));
    NamedPipeClientStream? pipe;
    Process? host;
    ProcessJob? job;
    int disposed;
    int pixelBytes, width;
    ulong frame;
    public LanFrameSettings Settings { get; private set; } = new();

    public async Task StartAsync(PayloadInfo payload, string model, int w, int h, LanFrameSettings settings, CancellationToken token)
    {
        pixelBytes = LanFrameProtocol.PixelBytes(w, h); width = w; settings.Validate();
        var files = ScreenStudioSession.RequiredFiles(AppContext.BaseDirectory, payload, includeCaptureWorker: false);
        _ = PhoenixConfiguration.Create(directory, model, settings.Screen().Phoenix(), PhoenixConfiguration.FeederInputProvider);
        LocalPaths.RequireNoLinks(directory); Directory.CreateDirectory(directory);
        foreach (var (source, target) in files)
        {
            token.ThrowIfCancellationRequested(); LocalPaths.RequireNoLinks(source);
            var dest = Path.Combine(directory, Path.GetFileName(target));
            using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
            using var output = new FileStream(dest, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
            await input.CopyToAsync(output, token).ConfigureAwait(false);
        }
        PhoenixConfiguration.Write(directory, model, settings.Screen().Phoenix(), PhoenixConfiguration.FeederInputProvider);
        IniEditor.SetValue(Path.Combine(directory, "ReShade.ini"), "OVERLAY", "TutorialProgress", "4");
        IniEditor.SetValue(Path.Combine(directory, "ReShade.ini"), "GENERAL", "EffectSearchPaths", ".\\empty-effects");
        Directory.CreateDirectory(Path.Combine(directory, "empty-effects"));
        LocalData.WriteAtomic(Path.Combine(directory, "dlss5-feed.cfg"), "vr_eye_split=0\n");
        token.ThrowIfCancellationRequested();
        job = new ProcessJob();
        host = Process.Start(new ProcessStartInfo(Path.Combine(directory, "dlss5-feed-host64.exe"))
        { WorkingDirectory = directory, UseShellExecute = false, CreateNoWindow = true,
            ArgumentList = { Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture), "--hide" } })
            ?? throw new IOException("Cannot start LAN Phoenix DLSS5 host.");
        job.Assign(host);
        using var cancellation = token.Register(Abort);
        pipe = new NamedPipeClientStream(".", "dlss5-feed." + Environment.ProcessId, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(token).ConfigureAwait(false);
        if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var pid) || pid != host.Id)
            throw new IOException("Unexpected local feeder pipe owner.");
        // CPU-only transport needs no process handles. PID zero keeps the host from
        // duplicating unused shared-fence handles into the long-lived app process.
        var hello = new byte[12]; Put(hello, 0, Magic); Put(hello, 4, IpcVersion);
        await pipe.WriteAsync(hello, token).ConfigureAwait(false);
        var ack = new byte[8]; await pipe.ReadExactlyAsync(ack, token).ConfigureAwait(false);
        if (Get(ack, 0) != Magic || Get(ack, 4) != IpcVersion) throw new IOException("Feeder protocol mismatch.");
        var build = new byte[109]; build[0] = (byte)'B';
        Put(build, 1, (uint)w); Put(build, 5, (uint)h); Put(build, 9, (uint)w); Put(build, 13, (uint)h);
        Put(build, 17, 28); Put(build, 21, 28); // DXGI_FORMAT_R8G8B8A8_UNORM
        Put(build, 33, uint.MaxValue); Put(build, 45, 1); // flags_override=-1, cpu_bridge=1
        Put(build, 49, BitConverter.SingleToUInt32Bits(1)); Put(build, 53, BitConverter.SingleToUInt32Bits(1));
        Put(build, 61, (uint)w); Put(build, 65, (uint)h);
        await pipe.WriteAsync(build, token).ConfigureAwait(false);
        var ready = new byte[24]; await pipe.ReadExactlyAsync(ready, token).ConfigureAwait(false);
        if (Get(ready, 0) != 1) throw new IOException("Phoenix DLSS5 rejected the LAN session.");
        Settings = settings;
    }
    public async Task ApplyAsync(LanFrameSettings settings, CancellationToken token)
    {
        settings.Validate();
        if (settings.Tone != Settings.Tone || settings.Structure != Settings.Structure || settings.Skin != Settings.Skin || settings.AutoMask != Settings.AutoMask)
        {
            var reply = await new PhoenixControlClient(directory).SetAsync(settings.Screen().LiveControls(), token).ConfigureAwait(false);
            if (!reply.Ok) throw new IOException("Phoenix DLSS5 rejected LAN controls.");
        }
        Settings = settings;
    }
    public async Task<byte[]> EvaluateAsync(ReadOnlyMemory<byte> rgba, bool reset, CancellationToken token)
    {
        if (rgba.Length != pixelBytes || pipe is null) throw new InvalidDataException("Wrong input frame size.");
        using var cancellation = token.Register(Abort);
        var message = new byte[25]; message[0] = (byte)'F';
        BinaryPrimitives.WriteUInt64LittleEndian(message.AsSpan(1), ++frame);
        Put(message, 9, reset || frame == 1 ? 1u : 0u); Put(message, 13, Settings.Enabled ? 1u : 0u);
        Put(message, 17, (uint)Settings.Iterations);
        await pipe.WriteAsync(message, token).ConfigureAwait(false);
        await pipe.WriteAsync(rgba, token).ConfigureAwait(false);
        var ack = new byte[8]; await pipe.ReadExactlyAsync(ack, token).ConfigureAwait(false);
        if (Get(ack, 0) != 1 || Get(ack, 4) != width * 4) throw new IOException("Phoenix DLSS5 did not return a completed frame.");
        var pixels = new byte[pixelBytes]; await pipe.ReadExactlyAsync(pixels, token).ConfigureAwait(false); return pixels;
    }
    static void Put(byte[] bytes, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset), value);
    static uint Get(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset));
    void Abort()
    {
        try { pipe?.Dispose(); } catch (IOException) { }
        try { if (host is { HasExited: false }) host.Kill(true); } catch (InvalidOperationException) { } catch (System.ComponentModel.Win32Exception) { }
    }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        Abort(); job?.Dispose();
        try { host?.WaitForExit(3000); } catch (InvalidOperationException) { }
        host?.Dispose();
        try { if (Directory.Exists(directory)) { LocalPaths.RequireNoLinks(directory); Directory.Delete(directory, true); } }
        catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint pid);
}
