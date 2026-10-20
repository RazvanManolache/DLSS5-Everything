using System.Buffers.Binary;
using System.Text;

namespace Dlss5CompatApp.Tests;

sealed class TestWorkspace : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "dlss5-tests-" + Guid.NewGuid().ToString("N"));
    public TestWorkspace() => Directory.CreateDirectory(Root);
    public string FilePath(string name) => Path.Combine(Root, name);
    public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, true); }

    public string WritePe(string filename, CpuArch arch, params string[] imports)
    {
        var path = FilePath(filename);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, PeBytes(arch, imports));
        return path;
    }

    public static void SetPhoenixExports(string path, params string[] exports)
    {
        var bytes = File.ReadAllBytes(path);
        var directory = 0x98 + (BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(0x98)) == 0x20b ? 112 : 96);
        U32(directory, 0x1800); U32(directory + 4, 40);
        U32(0xc00 + 24, (uint)exports.Length); U32(0xc00 + 32, 0x1840);
        for (var i = 0; i < exports.Length; i++)
        {
            U32(0xc40 + i * 4, (uint)(0x1900 + i * 64));
            Encoding.ASCII.GetBytes(exports[i] + "\0").CopyTo(bytes, 0xd00 + i * 64);
        }
        File.WriteAllBytes(path, bytes);
        void U32(int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset), value);
    }

    public static void SetPhoenixFeederExports(string path) => SetPhoenixExports(path,
        "CnrPHOENIXSummary", "CnrPHOENIXRegister", "CnrPHOENIXFeederVersion", "CnrPHOENIXFeederEvaluate");

    public static void SetEmbeddedPhoenixRuntime(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var manifest = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new
        {
            schema = "dlss5-phoenix-embedded-runtime/1", model_data_included = false, nvidia_dll_included = false,
            files = new[] {
                new { path = "runtime/phoenix_runtime_cpp.dll", resource = 1100, bytes = 8, packed_bytes = 8, algorithm = "raw", sha256 = new string('A', 64) },
                new { path = "runtime/phoenix_native_cpp.json", resource = 1101, bytes = 2, packed_bytes = 2, algorithm = "raw", sha256 = new string('B', 64) }
            }
        });
        U32(0x118, 0x2000); U32(0x11c, 0x1000);
        U16(0x1400 + 14, 1); U32(0x1410, 10); U32(0x1414, 0x80000020);
        U16(0x1420 + 14, 3);
        U32(0x1430, 1001); U32(0x1434, 0x80000060);
        U32(0x1438, 1100); U32(0x143c, 0x80000080);
        U32(0x1440, 1101); U32(0x1444, 0x800000a0);
        U16(0x1460 + 14, 1); U32(0x1470, 1033); U32(0x1474, 0xc0);
        U16(0x1480 + 14, 1); U32(0x1490, 1033); U32(0x1494, 0xd0);
        U16(0x14a0 + 14, 1); U32(0x14b0, 1033); U32(0x14b4, 0xe0);
        U32(0x14c0, 0x2100); U32(0x14c4, (uint)manifest.Length);
        U32(0x14d0, 0x2600); U32(0x14d4, 8);
        U32(0x14e0, 0x2610); U32(0x14e4, 2);
        manifest.CopyTo(bytes, 0x1500);
        Encoding.ASCII.GetBytes("MZ-TEST!").CopyTo(bytes, 0x1a00);
        Encoding.ASCII.GetBytes("{}").CopyTo(bytes, 0x1a10);
        File.WriteAllBytes(path, bytes);
        void U16(int offset, ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset), value);
        void U32(int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset), value);
    }
    public static byte[] PeBytes(CpuArch arch, string[] imports, string markers = "")
    {
        var bytes = new byte[160 * 1024];
        U16(0, 0x5a4d); U32(0x3c, 0x80); U32(0x80, 0x4550);
        U16(0x84, arch == CpuArch.X64 ? (ushort)0x8664 : (ushort)0x14c);
        var optionalBytes = arch == CpuArch.X64 ? 240 : 224;
        U16(0x86, 1); U16(0x94, (ushort)optionalBytes); U16(0x98, arch == CpuArch.X64 ? (ushort)0x20b : (ushort)0x10b);
        var dir = 0x98 + (arch == CpuArch.X64 ? 112 : 96);
        U32(dir + 8, 0x1000); U32(dir + 12, (uint)((imports.Length + 1) * 20));
        var section = 0x98 + optionalBytes;
        U32(section + 8, 0x4000); U32(section + 12, 0x1000); U32(section + 16, 0x4000); U32(section + 20, 0x400);
        for (var i = 0; i < imports.Length; i++)
        {
            U32(0x400 + i * 20 + 12, (uint)(0x1200 + i * 256));
            Encoding.ASCII.GetBytes(imports[i]).CopyTo(bytes, 0x600 + i * 256);
        }
        Encoding.ASCII.GetBytes(markers).CopyTo(bytes, 0x10000);
        return bytes;
        void U16(int offset, ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset), value);
        void U32(int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset), value);
    }
}
