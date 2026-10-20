using Dlss5CompatApp;
using System.Text.Json;
using System.Runtime.InteropServices;
using System.Buffers.Binary;
using System.Text;
using System.Net;

if (args.Length != 3) throw new ArgumentException("canonical-repository-directory test-payload-directory reference-DLL");
var source = Path.GetFullPath(args[0]); var payload = Path.GetFullPath(args[1]);
using var http = new HttpClient(new LocalFixtureHandler(source));
await OptionalModelLibrary.EnsureSelectedAsync(payload, OptionalModelLibrary.Catalog.Select(p => p.Id), new Progress<BootstrapProgress>(), CancellationToken.None, http);
var library = OptionalModelLibrary.Root(payload);
var installed = OptionalModelLibrary.Installed(library, verifyWeights: true);
if (installed.Count != 2) throw new Exception("Both native packages must be published and authenticated.");
var proofs = new List<object>();
foreach (var package in installed)
{
    var module = Native.LoadLibraryExW(Path.GetFullPath(args[2]), IntPtr.Zero, 0x60);
    if (module == IntPtr.Zero) throw new Exception("Data-only DLL mapping failed.");
    Dictionary<string, byte[]> reference;
    try
    {
        var entry = Native.FindResourceW(module, package.model == "original" ? "WEIGHTS_HT" : "WEIGHTS_CC_SMALL_FP8", (IntPtr)10);
        if (entry == IntPtr.Zero) throw new Exception("Reference model resource missing.");
        var bytes = new byte[Native.SizeofResource(module, entry)];
        Marshal.Copy(Native.LockResource(Native.LoadResource(module, entry)), bytes, 0, bytes.Length);
        reference = Records(bytes);
    }
    finally { Native.FreeLibrary(module); }
    var manifestPath = OptionalModelLibrary.Contained(library, package.manifest);
    using var description = JsonDocument.Parse(File.ReadAllText(manifestPath));
    var resource = description.RootElement.GetProperty("resource");
    var actual = Records(File.ReadAllBytes(OptionalModelLibrary.Contained(Path.GetDirectoryName(manifestPath)!, resource.GetProperty("path").GetString()!)));
    if (!reference.Keys.Order().SequenceEqual(actual.Keys.Order())) throw new Exception("Record catalogue differs.");
    foreach (var (name, expected) in reference)
        if (!expected.AsSpan().SequenceEqual(actual[name])) throw new Exception("Native payload differs: " + name);
    proofs.Add(new { model = package.model, records = actual.Count, payload_bytes = actual.Sum(p => (long)p.Value.Length), all_payload_bytes_equal = true });
    var settings = new PhoenixSettings { ModelId = package.model, ModelLibraryDirectory = library };
    var config = PhoenixConfiguration.Create(Path.Combine(payload, "hypothetical-game"), Path.GetDirectoryName(manifestPath)!, settings);
    if ((string)config["model_library_directory"]! != library) throw new Exception("Shared library path missing.");
}
var result = new { passed = true, models = proofs, gpu_used = false, games_launched = false, python_used = false,
    transport = "Real pinned payload bytes through an in-process HTTP fixture handler; production downloader, hashes, converter, atomic publication and installer configuration executed.", library };
var text = JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
File.WriteAllText(Path.Combine(payload, "model-library-validation.json"), text); Console.WriteLine(text);

static Dictionary<string, byte[]> Records(byte[] bytes)
{
    if (BinaryPrimitives.ReadUInt64LittleEndian(bytes) != (ulong)bytes.Length) throw new Exception("Resource envelope extent.");
    var records = new Dictionary<string, byte[]>(); var offset = 8;
    while (offset < bytes.Length)
    {
        var nameBytes = checked((int)BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(offset))); offset += 8;
        var name = Encoding.UTF8.GetString(bytes, offset, nameBytes); offset += nameBytes;
        var body = checked((int)BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(offset))); offset += 8;
        var payloadBytes = checked((int)BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(offset + 8)));
        records.Add(name, bytes.AsSpan(offset + 20, payloadBytes).ToArray()); offset += body;
    }
    return records;
}

sealed class LocalFixtureHandler(string source) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var prefix = "/bdsqlsz/DLSSNR3.19.16.0/resolve/" + OptionalModelLibrary.Revision + "/";
        if (request.RequestUri is null || request.RequestUri.Host != "huggingface.co" || !request.RequestUri.AbsolutePath.StartsWith(prefix))
            throw new Exception("Unexpected download provenance.");
        var path = OptionalModelLibrary.Contained(source, request.RequestUri.AbsolutePath[prefix.Length..]);
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(File.OpenRead(path)) });
    }
}

static class Native
{
    [DllImport("kernel32", CharSet = CharSet.Unicode)] internal static extern IntPtr LoadLibraryExW(string path, IntPtr file, uint flags);
    [DllImport("kernel32", CharSet = CharSet.Unicode)] internal static extern IntPtr FindResourceW(IntPtr module, string name, IntPtr type);
    [DllImport("kernel32")] internal static extern uint SizeofResource(IntPtr module, IntPtr resource);
    [DllImport("kernel32")] internal static extern IntPtr LoadResource(IntPtr module, IntPtr resource);
    [DllImport("kernel32")] internal static extern IntPtr LockResource(IntPtr resource);
    [DllImport("kernel32")] internal static extern bool FreeLibrary(IntPtr module);
}
