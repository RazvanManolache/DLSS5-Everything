using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace Dlss5CompatApp;

sealed record OptionalModelFile(string Name, string Sha256, long MaxBytes);
sealed record OptionalModelPackage(string Id, string Label, string Model, string Subdirectory,
    IReadOnlyList<OptionalModelFile> Files);
sealed record InstalledModelPackage(string id, string label, string model, string manifest, string manifest_sha256);

static class OptionalModelLibrary
{
    public const string IndexName = "model-library.json";
    public const string Revision = "c305bfc0db1bb94bcc8e25b074904b5158d7cbd8";
    public static string Root(string payloadRoot) => Path.GetFullPath(Path.Combine(payloadRoot, "Models"));
    public static IReadOnlyList<OptionalModelPackage> Catalog { get; } = new[]
    {
        new OptionalModelPackage("canonical-original", "Original native export", "original", "weight_ht", new[]
        {
            new OptionalModelFile("model.safetensors", "4cc7a1ec1d9a3ea1dc3583a877dbebd9d434826cc2cf4f9903e9a7ec7f3ded07", 147815794),
            new OptionalModelFile("model_config.json", "cee861ebc156b25e994ebe5d967ac93257573c4928c21d64674053c51637f721", 1024 * 1024),
            new OptionalModelFile("source_manifest.json", "ff08a2176ff9101742458caf922c9a2a0be257f2a552457b2574790b681e40c3", 128 * 1024)
        }),
        new OptionalModelPackage("canonical-lightweight", "Lightweight native export", "small-fp8", "weight_cc", new[]
        {
            new OptionalModelFile("model.safetensors", "a309c5431ca6e10ba58f63ceaf805e6859298bbf7c88b1c5fecd8edd122a4fec", 113932050),
            new OptionalModelFile("model_config.json", "18c1a141edd7adb1f3e81b60057502ad4e18031e223b43f70ba9cc6bebf47b07", 1024 * 1024),
            new OptionalModelFile("source_manifest.json", "795e0effaa4bc3cc4afdf6136537113a0635d568ae94aed69af62dec188099ce", 128 * 1024)
        })
    };

    public static IReadOnlyList<InstalledModelPackage> Installed(string root, bool verifyWeights = false)
    {
        var path = Path.Combine(root, IndexName);
        if (!File.Exists(path)) return [];
        try
        {
            if (new FileInfo(path).Length > 65536) return [];
            using var index = JsonDocument.Parse(File.ReadAllText(path));
            if (index.RootElement.GetProperty("schema").GetString() != "phoenix-model-library/1" ||
                index.RootElement.GetProperty("models").GetArrayLength() > 32) return [];
            var result = new List<InstalledModelPackage>();
            foreach (var item in index.RootElement.GetProperty("models").EnumerateArray())
            {
                try
                {
                    var package = JsonSerializer.Deserialize<InstalledModelPackage>(item)!;
                    if (package is null || result.Any(p => p.id == package.id) || !Catalog.Any(p => p.Id == package.id && p.Model == package.model)) continue;
                    var manifest = Contained(root, package.manifest);
                    if (!File.Exists(manifest) || new FileInfo(manifest).Length > 65536 || HashFile(manifest) != package.manifest_sha256) continue;
                    using var description = JsonDocument.Parse(File.ReadAllText(manifest));
                    var model = description.RootElement;
                    if (model.GetProperty("schema").GetString() != "dlss5-phoenix-model/1" || model.GetProperty("model").GetString() != package.model ||
                        model.GetProperty("architecture").GetString() != (package.model == "original" ? "phoenix-network37/1" : "phoenix-cc-small-fp8/1")) continue;
                    var resource = model.GetProperty("resource");
                    if (resource.GetProperty("bytes").GetInt64() is <= 0 or > 512L * 1024 * 1024) continue;
                    var weights = Contained(Path.GetDirectoryName(manifest)!, resource.GetProperty("path").GetString()!);
                    if (!File.Exists(weights) || new FileInfo(weights).Length != resource.GetProperty("bytes").GetInt64()) continue;
                    if (verifyWeights && HashFile(weights) != resource.GetProperty("sha256").GetString()) continue;
                    result.Add(package);
                }
                catch (Exception ex) when (InvalidMetadata(ex)) { }
            }
            return result;
        }
        catch (Exception ex) when (InvalidMetadata(ex)) { return []; }
    }

    static bool InvalidMetadata(Exception error) => error is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or
        InvalidOperationException or ArgumentException or KeyNotFoundException or OverflowException;

    internal static string Contained(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || relative.IndexOfAny(['\0', ':']) >= 0 || Path.IsPathRooted(relative) || relative.Replace('\\', '/').Split('/').Any(p => p is ".." or "." or ""))
            throw new InvalidDataException("Invalid model-library relative path.");
        var path = Path.GetFullPath(Path.Combine(root, relative));
        if (!path.StartsWith(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Model library path escapes its root.");
        LocalPaths.RequireNoLinks(path);
        return path;
    }

    public static async Task EnsureSelectedAsync(string payloadRoot, IEnumerable<string> selected,
        IProgress<BootstrapProgress> progress, CancellationToken cancellationToken, HttpClient? client = null)
    {
        var ids = selected.Distinct(StringComparer.Ordinal).ToArray();
        if (ids.Any(id => !Catalog.Any(p => p.Id == id))) throw new ArgumentException("Unknown optional native model.");
        if (ids.Length == 0) return;
        var root = Root(payloadRoot); LocalPaths.RequireNoLinks(root); Directory.CreateDirectory(root);
        // Serialize the index transaction across UI and CLI instances. Existing
        // resources remain immutable while a running game may have them mapped.
        using var gate = await AcquireAsync(root, cancellationToken);
        using var ownedClient = client is null ? new HttpClient { Timeout = TimeSpan.FromMinutes(20) } : null;
        var http = client ?? ownedClient!;
        if (ownedClient is not null) http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("DLSS-Everything", "3.0"));
        var installed = Installed(root, verifyWeights: true).ToList();
        foreach (var id in ids)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var package = Catalog.Single(p => p.Id == id);
            if (installed.Any(p => p.id == id)) { progress.Report(new($"{package.Label}: installed and verified.", 100)); continue; }
            var work = Path.Combine(root, ".install-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(work);
            try
            {
                var source = Path.Combine(work, "source"); Directory.CreateDirectory(source);
                foreach (var file in package.Files)
                {
                    progress.Report(new($"Downloading {package.Label}: {file.Name}", 0));
                    var uri = $"https://huggingface.co/bdsqlsz/DLSSNR3.19.16.0/resolve/{Revision}/{package.Subdirectory}/{file.Name}";
                    using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                    response.EnsureSuccessStatusCode();
                    if (response.Content.Headers.ContentLength > file.MaxBytes) throw new InvalidDataException("Optional model download exceeds its pinned limit.");
                    await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
                    var path = Path.Combine(source, file.Name);
                    await using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, true))
                    {
                        var buffer = new byte[131072]; long total = 0; int count;
                        while ((count = await input.ReadAsync(buffer, cancellationToken)) != 0)
                        {
                            total += count; if (total > file.MaxBytes) throw new InvalidDataException("Optional model download exceeds its pinned limit.");
                            await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
                            progress.Report(new($"Downloading {package.Label}: {total / 1048576.0:F1} MiB", (int)Math.Min(95, total * 95 / file.MaxBytes)));
                        }
                    }
                    if (HashFile(path) != file.Sha256) throw new InvalidDataException("Optional model download checksum differs: " + file.Name);
                }
                progress.Report(new($"Preparing {package.Label} for native Phoenix...", 96));
                var prepared = Path.Combine(work, "prepared");
                await Task.Run(() => CanonicalModelImporter.Import(source, prepared, package.Model, cancellationToken), cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                var folder = $"models/{package.Id}-{Revision[..12]}";
                var final = Contained(root, folder);
                if (Directory.Exists(final))
                {
                    // Never overwrite a version that another process might be
                    // reading. A repair receives a new immutable directory.
                    folder += "-" + Guid.NewGuid().ToString("N"); final = Contained(root, folder);
                }
                Directory.CreateDirectory(Path.GetDirectoryName(final)!); Directory.Move(prepared, final);
                var entry = new InstalledModelPackage(package.Id, package.Label, package.Model, folder + "/model.json", HashFile(Path.Combine(final, "model.json")));
                installed.RemoveAll(p => p.id == id); installed.Add(entry);
                LocalData.WriteAtomic(Path.Combine(root, IndexName), JsonSerializer.Serialize(new { schema = "phoenix-model-library/1", models = installed }, new JsonSerializerOptions { WriteIndented = true }));
                progress.Report(new($"{package.Label}: ready in the shared model library.", 100));
            }
            finally { if (Directory.Exists(work)) Directory.Delete(work, recursive: true); }
        }
    }

    internal static string HashFile(string path)
    {
        using var file = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(file)).ToLowerInvariant();
    }

    static async Task<FileStream> AcquireAsync(string root, CancellationToken cancellationToken)
    {
        var path = Path.Combine(root, ".model-library.lock"); LocalPaths.RequireNoLinks(path);
        for (var attempt = 0; ; ++attempt)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (attempt < 100) { await Task.Delay(50, cancellationToken); }
        }
    }
}
