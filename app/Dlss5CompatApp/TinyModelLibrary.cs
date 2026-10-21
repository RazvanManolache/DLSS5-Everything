using System.IO.Compression;
using System.Text.Json;

namespace Dlss5CompatApp;

static class TinyModelLibrary
{
    internal const string SourceSha256 = "5c7ee11d86b685196af2ccb2f2a539e43f17f865448fe900dc61d416465bc79b";
    internal const string WeightsSha256 = "ac3835ecbde43bd2934cded28da99678649864fb7414ee50f660dc8a232d68e8";
    internal const int WeightBytes = 375800;
    internal const string SourceUrl = "https://raw.githubusercontent.com/Benjamin-Wegener/dlss5-distill/9e3bc2f9e755f0e281b72e439f9452be0e8b7f31/weights/model_dlss5_distill_deploy.pt";

    internal static string? Find(string payloadRoot)
    {
        try
        {
            var root = OptionalModelLibrary.Root(payloadRoot);
            var index = Path.Combine(root, "tiny-upscaler.json"); LocalPaths.RequireNoLinks(index);
            if (!File.Exists(index) || new FileInfo(index).Length > 4096) return null;
            using var document = JsonDocument.Parse(File.ReadAllText(index)); var model = document.RootElement;
            if (model.GetProperty("schema").GetString() != "phoenix-tiny-2x/1") return null;
            var file = OptionalModelLibrary.Contained(root, model.GetProperty("path").GetString()!);
            return File.Exists(file) && new FileInfo(file).Length == WeightBytes && OptionalModelLibrary.HashFile(file) == WeightsSha256 ? file : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or KeyNotFoundException or ArgumentException) { return null; }
    }

    // The pinned ZIP's storage bytes are copied directly using embedded metadata.
    // Pickle is never parsed or executed by the deployed application.
    internal static byte[] ConvertCheckpoint(byte[] source)
    {
        if (source.Length != 390409 || Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(source)).ToLowerInvariant() != SourceSha256)
            throw new InvalidDataException("Unrecognized tiny upscaler checkpoint.");
        using var stream = new MemoryStream(source, writable: false);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        if (archive.Entries.Count > 128 || archive.Entries.Select(e => e.FullName).Distinct(StringComparer.Ordinal).Count() != archive.Entries.Count)
            throw new InvalidDataException("Invalid tiny upscaler archive.");
        var meta = archive.Entries.Single(e => e.FullName.EndsWith("/data.pkl", StringComparison.Ordinal));
        var prefix = meta.FullName[..^"data.pkl".Length];
        using var layoutStream = typeof(TinyModelLibrary).Assembly.GetManifestResourceStream("Dlss5CompatApp.TinyLayout.json")
            ?? throw new InvalidDataException("Tiny coefficient map is missing.");
        using var layout = JsonDocument.Parse(layoutStream);
        var weights = new byte[WeightBytes];
        foreach (var tensor in layout.RootElement.GetProperty("tensors").EnumerateArray())
        {
            var storage = archive.GetEntry(prefix + "data/" + tensor.GetProperty("storage").GetString())
                ?? throw new InvalidDataException("Missing tiny coefficient storage.");
            var count = tensor.GetProperty("count").GetInt32();
            var storageCount = tensor.GetProperty("storage_count").GetInt32();
            if (storage.Length != storageCount * 4L || storage.Length > WeightBytes) throw new InvalidDataException("Invalid coefficient extent.");
            using var input = storage.Open();
            var raw = new byte[storage.Length]; input.ReadExactly(raw);
            Buffer.BlockCopy(raw, tensor.GetProperty("storage_offset").GetInt32() * 4,
                weights, tensor.GetProperty("offset").GetInt32() * 4, count * 4);
        }
        if (Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(weights)).ToLowerInvariant() != WeightsSha256)
            throw new InvalidDataException("Tiny native coefficient checksum differs.");
        return weights;
    }

    internal static async Task EnsureAsync(string payloadRoot, IProgress<BootstrapProgress> progress, CancellationToken token)
    {
        if (Find(payloadRoot) is not null) return;
        using var http = new HttpClient();
        using var response = await http.GetAsync(SourceUrl, HttpCompletionOption.ResponseHeadersRead, token);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is { } bytes && bytes != 390409) throw new InvalidDataException("Tiny download size differs.");
        await using var input = await response.Content.ReadAsStreamAsync(token);
        var source = new byte[390409]; await input.ReadExactlyAsync(source, token);
        if (await input.ReadAsync(new byte[1], token) != 0) throw new InvalidDataException("Tiny download exceeds its limit.");
        progress.Report(new("Preparing tiny 2x upscaler for native processing…", 96));
        var weights = ConvertCheckpoint(source); token.ThrowIfCancellationRequested();
        var root = OptionalModelLibrary.Root(payloadRoot); Directory.CreateDirectory(root); LocalPaths.RequireNoLinks(root);
        // Immutable destination; publish the small pointer only after all coefficients exist.
        var relative = "tiny-2x-" + WeightsSha256[..12] + "-" + Guid.NewGuid().ToString("N") + "/weights.bin";
        var target = OptionalModelLibrary.Contained(root, relative); Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        try
        {
            await File.WriteAllBytesAsync(target, weights, token); token.ThrowIfCancellationRequested();
            LocalData.WriteAtomic(Path.Combine(root, "tiny-upscaler.json"), JsonSerializer.Serialize(new
            { schema = "phoenix-tiny-2x/1", path = relative, source_sha256 = SourceSha256, weights_sha256 = WeightsSha256, license = "Apache-2.0", upstream = SourceUrl }));
        }
        catch { if (File.Exists(target)) File.Delete(target); Directory.Delete(Path.GetDirectoryName(target)!); throw; }
        progress.Report(new("Tiny native 2x upscaler is ready in the shared model library.", 100));
    }
}
