using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Dlss5CompatApp;

// Import the published native-precision canonical exports as data. No Python,
// tensor framework or third-party inference process is used by the importer or game.
// Every reconstructed stage must match its pinned upstream byte hash before publication.
static class CanonicalModelImporter
{
    internal sealed record Result(string Model, string ResourceSha256, long Bytes, int Records);
    sealed record Tensor(string Dtype, int[] Shape, int Offset, int Bytes);

    public static Result Import(string sourceDirectory, string destination, string model,
        CancellationToken cancellationToken = default)
    {
        if (model is not ("original" or "small-fp8")) throw new InvalidDataException("Unsupported native model.");
        using var configuration = JsonDocument.Parse(File.ReadAllText(Path.Combine(sourceDirectory, "model_config.json")));
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(sourceDirectory, "source_manifest.json")));
        var config = configuration.RootElement;
        if (config.GetProperty("schema").GetString() != "dlssnr_canonical_v1" ||
            config.GetProperty("profile").GetString() != (model == "original" ? "dlss_nr_310_8_0" : "dlss_nr_310_9_16_cc") ||
            config.GetProperty("parameter_layout").GetString() != "pytorch_out_in" ||
            config.GetProperty("weight_storage").GetProperty("precision").GetString() != "native")
            throw new InvalidDataException("Only the native-precision canonical export is supported.");
        var data = File.ReadAllBytes(Path.Combine(sourceDirectory, "model.safetensors"));
        if (data.Length < 8) throw new InvalidDataException("Truncated safetensors file.");
        var headerBytes = BinaryPrimitives.ReadUInt64LittleEndian(data);
        if (headerBytes > 4 * 1024 * 1024 || headerBytes > (ulong)data.Length - 8)
            throw new InvalidDataException("Invalid safetensors header extent.");
        using var header = JsonDocument.Parse(data.AsMemory(8, (int)headerBytes));
        var start = checked(8 + (int)headerBytes);
        var tensors = new Dictionary<string, Tensor>(StringComparer.Ordinal);
        foreach (var item in header.RootElement.EnumerateObject())
        {
            if (item.Name == "__metadata__") continue;
            var offsets = item.Value.GetProperty("data_offsets").EnumerateArray().Select(x => x.GetInt32()).ToArray();
            var shape = item.Value.GetProperty("shape").EnumerateArray().Select(x => x.GetInt32()).ToArray();
            if (offsets.Length != 2 || offsets[0] < 0 || offsets[1] < offsets[0] || offsets[1] > data.Length - start)
                throw new InvalidDataException("Safetensors payload extent.");
            var dtype = item.Value.GetProperty("dtype").GetString()!;
            var elementBytes = dtype switch { "F8_E4M3" or "U8" => 1, "F16" => 2, "F32" => 4, _ => throw new InvalidDataException("Unsupported tensor storage.") };
            if (shape.Any(x => x <= 0) || shape.Aggregate(1L, (a, x) => checked(a * x)) * elementBytes != offsets[1] - offsets[0])
                throw new InvalidDataException("Safetensors shape differs from payload.");
            tensors.Add(item.Name, new(dtype, shape, start + offsets[0], offsets[1] - offsets[0]));
        }
        var stages = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var stage in manifest.RootElement.GetProperty("stages").EnumerateArray())
        {
            var bytes = stage.GetProperty("packedByteLength").GetInt32();
            if (bytes <= 0 || bytes > 256 * 1024 * 1024) throw new InvalidDataException("Stage extent.");
            stages.Add(stage.GetProperty("id").GetString()!, new byte[bytes]);
        }
        var records = new Dictionary<string, (byte[] Stage, int Start, int Bytes)>(StringComparer.Ordinal);
        foreach (var record in manifest.RootElement.GetProperty("tensors").EnumerateArray())
        {
            var stage = stages[record.GetProperty("stage").GetString()!];
            var offset = record.GetProperty("stageOffset").GetInt32();
            var bytes = record.GetProperty("byteLength").GetInt32();
            if (offset < 0 || bytes <= 0 || offset > stage.Length - bytes) throw new InvalidDataException("Record extent.");
            records.Add(record.GetProperty("name").GetString()!, (stage, offset, bytes));
        }
        if (records.Count != (model == "original" ? 153 : 77) ||
            config.GetProperty("blocks").GetArrayLength() != (model == "original" ? 71 : 31))
            throw new InvalidDataException("Canonical export does not match the selected native architecture.");
        var recipes = config.GetProperty("tensors").EnumerateArray().ToArray();
        var matrices = recipes.Where(x => x.GetProperty("source_dtype").GetString() == "e4")
            .GroupBy(x => (Record: x.GetProperty("record").GetString()!, Offset: x.GetProperty("offset").GetInt32()))
            .ToDictionary(g => g.Key, g => (K: g.Max(x => x.GetProperty("k1").GetInt32()), N: g.Max(x => x.GetProperty("n1").GetInt32())));
        foreach (var recipe in recipes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = recipe.GetProperty("name").GetString()!;
            var tensor = tensors[name];
            var record = records[recipe.GetProperty("record").GetString()!];
            var offset = recipe.GetProperty("offset").GetInt32();
            var k0 = recipe.GetProperty("k0").GetInt32(); var k1 = recipe.GetProperty("k1").GetInt32();
            var n0 = recipe.GetProperty("n0").GetInt32(); var n1 = recipe.GetProperty("n1").GetInt32();
            var kind = recipe.GetProperty("source_dtype").GetString();
            var target = record.Stage.AsSpan(record.Start, record.Bytes);
            var source = data.AsSpan(tensor.Offset, tensor.Bytes);
            if (kind == "e4")
            {
                if (tensor.Dtype != "F8_E4M3" || source.Length != (k1 - k0) * (n1 - n0))
                    throw new InvalidDataException("FP8 matrix extent: " + name);
                var matrix = matrices[(recipe.GetProperty("record").GetString()!, offset)];
                for (var n = n0; n < n1; ++n)
                    for (var k = k0; k < k1; ++k)
                    {
                        var index = PackedFp8(n, k, matrix.K, matrix.N / 16);
                        target[checked(offset + index)] = source[(n - n0) * (k1 - k0) + k - k0];
                    }
            }
            else if (kind == "f16frag")
            {
                if (tensor.Dtype != "F16" || source.Length != (k1 - k0) * (n1 - n0) * 2)
                    throw new InvalidDataException("Half fragment extent: " + name);
                for (var n = n0; n < n1; ++n)
                    for (var k = k0; k < k1; ++k)
                    {
                        var index = name.Contains("input_adapter", StringComparison.Ordinal)
                            ? n / 16 * 256 + 32 * (n % 8) + 8 * (k % 8 / 2) + 4 * (n / 8 % 2) + k % 2 + 2 * (k / 8)
                            : k / 16 * 256 + 32 * (n % 8) + 8 * (k % 8 / 2) + 4 * (n / 8) + k % 2 + 2 * (k % 16 / 8);
                        source.Slice(((n - n0) * (k1 - k0) + k - k0) * 2, 2).CopyTo(target.Slice(checked(offset + index * 2), 2));
                    }
            }
            else if (kind == "prior")
            {
                if (tensor.Dtype != "F16" || tensor.Shape.Length != 3 || tensor.Shape[1] != 64 || tensor.Shape[2] != 64)
                    throw new InvalidDataException("Attention prior extent.");
                for (var head = 0; head < tensor.Shape[0]; ++head)
                    for (var q = 0; q < 64; ++q)
                        for (var k = 0; k < 64; ++k)
                        {
                            // Export uses natural 8x8 spatial tokens; native
                            // attention numbers the same tokens in register order.
                            var nq = NaturalToNativeToken(q); var nk = NaturalToNativeToken(k);
                            var lane = 4 * (nq % 8) + nk % 8 / 2;
                            var slot = nk % 2 | (nq % 16 / 8 << 1) | (nk % 16 / 8 << 2);
                            var index = head * 4096 + (4 * (nq / 16) + nk / 16) * 256 + lane * 8 + slot;
                            source.Slice((head * 4096 + q * 64 + k) * 2, 2).CopyTo(target.Slice(checked(offset + index * 2), 2));
                        }
            }
            else if (kind is "f16" or "f32")
            {
                if (tensor.Dtype != (kind == "f16" ? "F16" : "F32")) throw new InvalidDataException("Scalar storage differs.");
                source.CopyTo(target.Slice(offset, source.Length));
            }
            else throw new InvalidDataException("Unknown canonical tensor recipe.");
        }
        foreach (var item in config.GetProperty("opaque_vit_layer3").EnumerateArray())
        {
            var name = item.GetString()!; var block = name.Split('.')[1]; var tensor = tensors[name];
            var record = records["block" + block + ".layer3.layer"];
            if (tensor.Bytes != record.Bytes) throw new InvalidDataException("Opaque scalar extent.");
            data.AsSpan(tensor.Offset, tensor.Bytes).CopyTo(record.Stage.AsSpan(record.Start, record.Bytes));
        }
        foreach (var stage in manifest.RootElement.GetProperty("stages").EnumerateArray())
        {
            var id = stage.GetProperty("id").GetString()!;
            var actual = Hash(stages[id]);
            if (actual != stage.GetProperty("sha256").GetString())
                throw new InvalidDataException("Canonical import failed exact stage verification: " + id + " (" + actual + ").");
        }
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(destination);
        var resourceFile = Path.Combine(destination, "weights.bin");
        using (var output = new FileStream(resourceFile, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
        using (var writer = new BinaryWriter(output, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(0UL);
            foreach (var (name, record) in records.OrderBy(x => x.Key, StringComparer.Ordinal))
            {
                var nameBytes = Encoding.UTF8.GetBytes(name);
                writer.Write((ulong)nameBytes.Length); writer.Write(nameBytes);
                writer.Write((ulong)record.Bytes + 20); writer.Write((ulong)record.Bytes + 20);
                writer.Write((ulong)record.Bytes); writer.Write(1U); writer.Write(record.Stage, record.Start, record.Bytes);
            }
            writer.Flush(); var length = output.Length; output.Position = 0; writer.Write((ulong)length);
        }
        using var resourceStream = File.OpenRead(resourceFile);
        var digest = Convert.ToHexString(SHA256.HashData(resourceStream)).ToLowerInvariant();
        var result = new Result(model, digest, resourceStream.Length, records.Count);
        var description = new
        {
            schema = "dlss5-phoenix-model/1", model,
            architecture = model == "original" ? "phoenix-network37/1" : "phoenix-cc-small-fp8/1",
            resource = new { path = "weights.bin", sha256 = digest, bytes = result.Bytes },
            import = new { format = "dlssnr_canonical_v1", precision = "native", verified_stages = stages.Count, payload_byte_identical = true }
        };
        File.WriteAllText(Path.Combine(destination, "model.json"), JsonSerializer.Serialize(description, new JsonSerializerOptions { WriteIndented = true }));
        return result;
    }

    static string Hash(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
    static int NaturalToNativeToken(int value) => (value & 35) | (value & 4) << 2 | (value & 8) >> 1 | (value & 16) >> 1;
    static int PackedFp8(int n, int k, int columns, int inner) =>
        (((((((n / (16 * inner) * (columns / 32) + k / 32) * inner + n / 16 % inner) * 8 + n % 8) * 4 + k / 2 % 4) * 2 + n / 8 % 2) * 4 + k / 8 % 4) * 2 + k % 2);
}
