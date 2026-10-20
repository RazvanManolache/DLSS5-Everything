using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Dlss5CompatApp;

internal interface ILanFrameCodec : IDisposable
{
    Task<byte[]> DecodeAsync(byte[] data, CancellationToken token);
    Task<byte[]> EncodeAsync(byte[] rgba, CancellationToken token);
}
internal sealed class RawFrameCodec : ILanFrameCodec
{
    public Task<byte[]> DecodeAsync(byte[] data, CancellationToken token) { token.ThrowIfCancellationRequested(); return Task.FromResult(data); }
    public Task<byte[]> EncodeAsync(byte[] rgba, CancellationToken token) { token.ThrowIfCancellationRequested(); return Task.FromResult(rgba); }
    public void Dispose() { }
}
internal sealed class PyrowaveFrameCodec : ILanFrameCodec
{
    Process? worker;
    ProcessJob? job;
    Task? drain;
    int pixelBytes;
    public static bool Available => File.Exists(Path.Combine(AppContext.BaseDirectory, "Runtime", "codec", "dlss5-pyrowave.exe")) &&
        File.Exists(Path.Combine(AppContext.BaseDirectory, "Runtime", "codec", "phoenix-pyrowave.dll"));
    public static async Task<ILanFrameCodec> CreateAsync(string name, int width, int height, int bitrate, int fps, CancellationToken token)
    {
        if (name == "raw") return new RawFrameCodec();
        if (name != "pyrowave" || !Available) throw new NotSupportedException("Pyrowave runtime is unavailable.");
        if (bitrate is < 20 or > 500 || fps is < 1 or > 120) throw new InvalidDataException("Invalid Pyrowave bitrate or frame rate.");
        var codec = new PyrowaveFrameCodec { pixelBytes = LanFrameProtocol.PixelBytes(width, height) };
        try
        {
            var root = Path.Combine(AppContext.BaseDirectory, "Runtime", "codec"); LocalPaths.RequireNoLinks(root);
            var budget = Math.Clamp((long)bitrate * 1000000 / 8 / fps, 4096, 8 * 1024 * 1024);
            codec.job = new ProcessJob();
            codec.worker = Process.Start(new ProcessStartInfo(Path.Combine(root, "dlss5-pyrowave.exe"))
            { WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
                ArgumentList = { width.ToString(System.Globalization.CultureInfo.InvariantCulture), height.ToString(System.Globalization.CultureInfo.InvariantCulture), budget.ToString(System.Globalization.CultureInfo.InvariantCulture) } })
                ?? throw new IOException("Cannot start Pyrowave worker.");
            codec.job.Assign(codec.worker);
            codec.drain = DrainAsync(codec.worker.StandardError);
            using var cancel = token.Register(codec.Abort);
            var ready = new byte[4]; await codec.worker.StandardOutput.BaseStream.ReadExactlyAsync(ready, token).ConfigureAwait(false);
            if (BinaryPrimitives.ReadUInt32LittleEndian(ready) != 1) throw new IOException("Pyrowave encoder/decoder initialization failed.");
            return codec;
        }
        catch { codec.Dispose(); throw; }
    }
    static async Task DrainAsync(StreamReader stream)
    {
        try { var discard = new char[1024]; while (await stream.ReadAsync(discard).ConfigureAwait(false) > 0) { } }
        catch (Exception error) when (error is IOException or ObjectDisposedException) { }
    }
    public Task<byte[]> DecodeAsync(byte[] data, CancellationToken token) => ExchangeAsync(2, data, token);
    public Task<byte[]> EncodeAsync(byte[] rgba, CancellationToken token) => ExchangeAsync(1, rgba, token);
    async Task<byte[]> ExchangeAsync(byte type, byte[] data, CancellationToken token)
    {
        if (worker is null) throw new InvalidOperationException("Pyrowave worker is stopped.");
        using var cancel = token.Register(Abort);
        var request = new byte[5]; request[0] = type; BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(1), (uint)data.Length);
        await worker.StandardInput.BaseStream.WriteAsync(request, token).ConfigureAwait(false);
        await worker.StandardInput.BaseStream.WriteAsync(data, token).ConfigureAwait(false);
        await worker.StandardInput.BaseStream.FlushAsync(token).ConfigureAwait(false);
        var header = new byte[4]; await worker.StandardOutput.BaseStream.ReadExactlyAsync(header, token).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadUInt32LittleEndian(header);
        if (type == 2 ? length != pixelBytes : length is < 8 or > LanFrameProtocol.MaxEncodedBytes)
            throw new IOException("Invalid Pyrowave worker output.");
        var output = new byte[length]; await worker.StandardOutput.BaseStream.ReadExactlyAsync(output, token).ConfigureAwait(false); return output;
    }
    void Abort()
    {
        try { if (worker is { HasExited: false }) worker.Kill(true); }
        catch (InvalidOperationException) { } catch (System.ComponentModel.Win32Exception) { }
    }
    public void Dispose()
    {
        Abort(); job?.Dispose();
        try { worker?.WaitForExit(3000); } catch (InvalidOperationException) { }
        worker?.Dispose(); worker = null;
    }
}

internal sealed class ProcessJob : IDisposable
{
    readonly SafeFileHandle handle;
    public ProcessJob()
    {
        handle = CreateJobObject(IntPtr.Zero, null);
        var limits = new Limits { Basic = new BasicLimits { Flags = 0x2000 } };
        if (handle.IsInvalid || !SetInformationJobObject(handle, 9, ref limits, Marshal.SizeOf<Limits>()))
        { handle.Dispose(); throw new IOException("Cannot create process lifetime guard."); }
    }
    public void Assign(Process process)
    { if (!AssignProcessToJobObject(handle, process.Handle)) throw new IOException("Cannot guard native worker."); }
    public void Dispose() => handle.Dispose();
    [StructLayout(LayoutKind.Sequential)] struct BasicLimits
    { public long ProcessTime, JobTime; public uint Flags; public UIntPtr MinWorkingSet, MaxWorkingSet; public uint ActiveProcesses; public UIntPtr Affinity; public uint Priority, Scheduling; }
    [StructLayout(LayoutKind.Sequential)] struct IoCounters { public ulong ReadOps, WriteOps, OtherOps, ReadBytes, WriteBytes, OtherBytes; }
    [StructLayout(LayoutKind.Sequential)] struct Limits
    { public BasicLimits Basic; public IoCounters Io; public UIntPtr ProcessMemory, JobMemory, PeakProcess, PeakJob; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern SafeFileHandle CreateJobObject(IntPtr attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] static extern bool SetInformationJobObject(SafeFileHandle job, int info, ref Limits limits, int size);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] static extern bool AssignProcessToJobObject(SafeFileHandle job, IntPtr process);
}
