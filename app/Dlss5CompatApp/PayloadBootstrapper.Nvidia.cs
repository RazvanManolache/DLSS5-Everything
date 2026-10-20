using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using SharpCompress.Archives.Rar;

namespace Dlss5CompatApp;

static partial class PayloadBootstrapper
{
    internal sealed record NvidiaSource(string Id, string Repository, string Asset, string Release = "latest",
        string? ArchiveSha256 = null, long? ArchiveBytes = null, NvidiaFile? RequiredNr = null, string? RarMember = null);
    internal sealed record NvidiaFile(string Name, string Sha256, long Bytes, string Version);
    internal sealed record NvidiaReceipt(string Schema, string Repository, string Release,
        string Asset, string ArchiveSha256, NvidiaFile[] Files);
    internal static readonly NvidiaSource[] NvidiaSources = [
        // Pin the inspected compatibility build. New upstream releases require a fresh audit.
        new("nodix-compat", "NODIX-TECH/DLSS-5-MANAGER", "DLSS.5.MANAGER.rar", "1.3.7",
            "ac6ddca163ea5753d951af2e2d373197feaf1bdd4a027d308a38d5df491ee910", 707717184,
            new("nvngx_dlssnr.dll", "cdfa6c1ec784867182164c7836f324f087b5c9f374d0c9fe46f54999feabf3ed", 276162560, "310.9.16.0"),
            "mod files/dlss 5/nvngx_dlssnr.dll")
    ];
    const long NvidiaArchiveLimit = 1024L * 1024 * 1024;
    const long NvidiaFileLimit = 512L * 1024 * 1024;

    internal static async Task EnsureNvidiaPackagesAsync(HttpClient http, string payloadRoot,
        IProgress<BootstrapProgress> progress, CancellationToken token)
    {
        var states = new List<object>();
        var errors = new List<Exception>();
        foreach (var source in NvidiaSources)
        {
            try
            {
                var receipt = await EnsureNvidiaSourceAsync(http, payloadRoot, source, progress, token);
                states.Add(new { source = source.Id, source.Repository, status = "available", release = receipt.Release,
                    nr_version = receipt.Files.Single(file => file.Name.Equals("nvngx_dlssnr.dll", StringComparison.OrdinalIgnoreCase)).Version });
            }
            catch (HttpRequestException error) when (error.StatusCode == HttpStatusCode.NotFound)
            {
                var message = source.Repository + " unavailable (HTTP 404); existing packages and local imports kept.";
                progress.Report(new(message, 33));
                states.Add(new { source = source.Id, source.Repository, status = "unavailable", message });
            }
            catch (Exception error) when (error is IOException or HttpRequestException or InvalidDataException or JsonException or InvalidOperationException)
            {
                progress.Report(new(source.Repository + ": " + error.Message, 33));
                states.Add(new { source = source.Id, source.Repository, status = "error", message = error.Message });
                errors.Add(error);
            }
        }
        token.ThrowIfCancellationRequested();
        LocalData.WriteAtomic(Path.Combine(payloadRoot, "nvidia-downloads.json"), JsonSerializer.Serialize(new {
            schema = "dlss5-nvidia-downloads/1", checked_utc = DateTimeOffset.UtcNow, sources = states
        }, JsonOptions));
        if (errors.Count > 0) throw new AggregateException("NVIDIA package update incomplete; existing packages kept.", errors);
    }

    internal static async Task<NvidiaReceipt> EnsureNvidiaSourceAsync(HttpClient http, string payloadRoot,
        NvidiaSource source, IProgress<BootstrapProgress> progress, CancellationToken token,
        Func<string, string, CancellationToken, Task>? archiveExtractor = null)
    {
        LocalPaths.RequireNoLinks(payloadRoot);
        progress.Report(new("Checking NVIDIA runtime package from " + source.Repository + "...", 33));
        var release = await GetGitHubReleaseAsync(http, source.Repository, source.Release, token);
        if (source.Release != "latest" && release.TagName != source.Release)
            throw new InvalidDataException("NVIDIA package release differs from the pinned release.");
        var candidates = release.Assets.Where(asset => asset.Name.Equals(source.Asset, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (candidates.Length != 1) throw new InvalidDataException("Expected one NVIDIA runtime package in " + source.Repository + ".");
        var asset = candidates[0];
        if (asset.Sha256 is null || asset.Size <= 0 || asset.Size > NvidiaArchiveLimit)
            throw new InvalidDataException("NVIDIA package needs a published SHA-256 and bounded asset size.");
        if (source.ArchiveSha256 is not null && !source.ArchiveSha256.Equals(asset.Sha256, StringComparison.OrdinalIgnoreCase) ||
            source.ArchiveBytes is not null && source.ArchiveBytes != asset.Size)
            throw new InvalidDataException("NVIDIA package differs from the inspected archive identity.");
        if (!Uri.TryCreate(asset.Url, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.Host != "github.com" ||
            !uri.AbsolutePath.StartsWith("/" + source.Repository + "/releases/download/", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Unexpected NVIDIA package URL.");
        var tag = Regex.Replace(release.TagName, "[^a-zA-Z0-9._-]", "_");
        if (tag.Length is 0 or > 100 || tag is "." or "..") throw new InvalidDataException("Invalid NVIDIA release tag.");
        var sourceRoot = Path.Combine(payloadRoot, "Nvidia", source.Id);
        LocalPaths.RequireNoLinks(sourceRoot);
        Directory.CreateDirectory(sourceRoot);
        var packageName = tag + "-" + asset.Sha256[..12];
        // Repairs publish a new immutable folder instead of overwriting DLLs a game may have mapped.
        foreach (var candidate in Directory.EnumerateDirectories(sourceRoot, packageName + "*"))
        {
            token.ThrowIfCancellationRequested();
            var receipt = await ValidNvidiaPackageAsync(candidate, source, release.TagName, asset.Name, asset.Sha256, token);
            if (receipt is not null) { ReportNvidiaVersions(receipt, progress, "Verified cached"); return receipt; }
        }
        var work = Path.Combine(payloadRoot, ".download-cache", "nvidia-" + Guid.NewGuid().ToString("N"));
        LocalPaths.RequireNoLinks(work);
        Directory.CreateDirectory(work);
        try
        {
            var archive = Path.Combine(work, source.RarMember is not null ? "package.rar" : "package.zip");
            using (var response = await http.GetAsync(asset.Url, HttpCompletionOption.ResponseHeadersRead, token))
            {
                response.EnsureSuccessStatusCode();
                if (response.Content.Headers.ContentLength is long length && length != asset.Size)
                    throw new InvalidDataException("NVIDIA package size differs from release metadata.");
                await using var input = await response.Content.ReadAsStreamAsync(token);
                await using var output = new FileStream(archive, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                progress.Report(new("Downloading NVIDIA runtime package from " + source.Repository + "...", 34));
                await CopyNvidiaBoundedAsync(input, output, asset.Size, token);
                if (output.Length != asset.Size) throw new InvalidDataException("NVIDIA package download was truncated.");
            }
            await using (var input = File.OpenRead(archive))
                if (!Convert.ToHexString(await SHA256.HashDataAsync(input, token)).Equals(asset.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("NVIDIA package failed SHA-256 verification.");
            var extracted = Path.Combine(work, "runtime"); Directory.CreateDirectory(extracted);
            if (archiveExtractor is not null) await archiveExtractor(archive, extracted, token);
            else if (source.RarMember is not null) await ExtractNvidiaRarAsync(archive, extracted, source.RarMember, token);
            else await ExtractNvidiaZipAsync(archive, extracted, token);
            var files = new List<NvidiaFile>();
            foreach (var path in Directory.EnumerateFiles(extracted))
            {
                token.ThrowIfCancellationRequested(); LocalPaths.RequireNoLinks(path);
                var name = Path.GetFileName(path);
                if (!NvidiaRuntimeFile(name)) { File.Delete(path); continue; }
                var bytes = new FileInfo(path).Length;
                if (bytes <= 0 || bytes > NvidiaFileLimit) throw new InvalidDataException("NVIDIA runtime file exceeds its size limit.");
                var dll = name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);
                if (dll && !PayloadScanner.IsX64Dll(path)) throw new InvalidDataException("Invalid x64 NVIDIA runtime DLL: " + name);
                await using var input = File.OpenRead(path);
                files.Add(new(name, Convert.ToHexString(await SHA256.HashDataAsync(input, token)).ToLowerInvariant(), bytes,
                    dll ? NvidiaVersion(path) : ""));
            }
            if (!files.Any(file => file.Name.Equals("nvngx_dlssnr.dll", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("Package contains no x64 nvngx_dlssnr.dll.");
            if (!RequiredNvidiaIdentity(source, files))
                throw new InvalidDataException("NVIDIA NR DLL differs from the inspected runtime identity.");
            var result = new NvidiaReceipt("dlss5-nvidia-package/1", source.Repository, release.TagName, asset.Name,
                asset.Sha256.ToLowerInvariant(), files.OrderBy(file => file.Name, StringComparer.OrdinalIgnoreCase).ToArray());
            LocalData.WriteAtomic(Path.Combine(extracted, "package.json"), JsonSerializer.Serialize(result, JsonOptions));
            token.ThrowIfCancellationRequested();
            var target = Path.Combine(sourceRoot, packageName);
            if (Directory.Exists(target)) target += "-" + Guid.NewGuid().ToString("N");
            LocalPaths.RequireNoLinks(target);
            Directory.Move(extracted, target);
            ReportNvidiaVersions(result, progress, "Downloaded and verified");
            return result;
        }
        finally
        {
            LocalPaths.RequireNoLinks(work);
            if (Directory.Exists(work)) Directory.Delete(work, true);
        }
    }

    static bool NvidiaRuntimeFile(string name) => Regex.IsMatch(name,
        @"^(nvngx_[a-z_]+|sl\.[a-z_]+)\.dll$|^(?:[a-z0-9_ .-]+[._ -])?(?:license|notice|third[._ -]?party)[a-z0-9_ .-]*(?:\.txt|\.md)?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    internal static string NvidiaVersion(string path)
    {
        var version = FileVersionInfo.GetVersionInfo(path);
        return version.FileVersion is null ? "unknown" :
            $"{version.FileMajorPart}.{version.FileMinorPart}.{version.FileBuildPart}.{version.FilePrivatePart}";
    }

    static void ReportNvidiaVersions(NvidiaReceipt receipt, IProgress<BootstrapProgress> progress, string action) =>
        progress.Report(new(action + " " + receipt.Repository + " " + receipt.Release + ": " +
            string.Join(", ", receipt.Files.Where(file => file.Name.StartsWith("nvngx_", StringComparison.OrdinalIgnoreCase) && file.Name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                .Select(file => file.Name + " " + file.Version)), 35));

    static bool RequiredNvidiaIdentity(NvidiaSource source, IEnumerable<NvidiaFile> files) => source.RequiredNr is null ||
        files.Any(file => file.Name.Equals(source.RequiredNr.Name, StringComparison.OrdinalIgnoreCase) &&
            file.Bytes == source.RequiredNr.Bytes && file.Version == source.RequiredNr.Version &&
            file.Sha256.Equals(source.RequiredNr.Sha256, StringComparison.OrdinalIgnoreCase));

    static async Task<NvidiaReceipt?> ValidNvidiaPackageAsync(string folder, NvidiaSource source, string release,
        string asset, string digest, CancellationToken token)
    {
        try
        {
            LocalPaths.RequireNoLinks(folder);
            var receipt = JsonSerializer.Deserialize<NvidiaReceipt>(LocalData.ReadText(Path.Combine(folder, "package.json"), 65536));
            if (receipt is null || receipt.Schema != "dlss5-nvidia-package/1" || receipt.Repository != source.Repository ||
                receipt.Release != release || receipt.Asset != asset || receipt.ArchiveSha256 != digest.ToLowerInvariant() ||
                receipt.Files is null || receipt.Files.Length is 0 or > 64 ||
                !receipt.Files.Any(file => file.Name.Equals("nvngx_dlssnr.dll", StringComparison.OrdinalIgnoreCase)) ||
                !RequiredNvidiaIdentity(source, receipt.Files)) return null;
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in receipt.Files)
            {
                token.ThrowIfCancellationRequested();
                if (file.Name != Path.GetFileName(file.Name) || !NvidiaRuntimeFile(file.Name) || !names.Add(file.Name) ||
                    file.Bytes <= 0 || file.Bytes > NvidiaFileLimit) return null;
                var path = Path.Combine(folder, file.Name); LocalPaths.RequireNoLinks(path);
                if (!File.Exists(path) || new FileInfo(path).Length != file.Bytes ||
                    file.Name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) && !PayloadScanner.IsX64Dll(path)) return null;
                await using var input = File.OpenRead(path);
                if (!Convert.ToHexString(await SHA256.HashDataAsync(input, token)).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase)) return null;
            }
            return receipt;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or JsonException or ArgumentException) { return null; }
    }

    internal static async Task ExtractNvidiaRarAsync(string archive, string destination, string member, CancellationToken token)
    {
        // Read only the pinned member; do not extract or execute the manager or its other payloads.
        using var rar = RarArchive.OpenArchive(archive);
        var matches = rar.Entries.Where(entry => string.Equals(entry.Key?.Replace('\\', '/'), member, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length != 1) throw new InvalidDataException("Missing or ambiguous NVIDIA RAR member.");
        var entry = matches[0];
        if (entry.IsDirectory || entry.IsEncrypted || entry.Size <= 0 || entry.Size > NvidiaFileLimit)
            throw new InvalidDataException("Invalid NVIDIA RAR member.");
        token.ThrowIfCancellationRequested();
        await using var input = await entry.OpenEntryStreamAsync(token);
        await using var output = new FileStream(Path.Combine(destination, "nvngx_dlssnr.dll"), FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await CopyNvidiaBoundedAsync(input, output, entry.Size, token);
        if (output.Length != entry.Size) throw new InvalidDataException("Truncated NVIDIA RAR member.");
    }

    static async Task ExtractNvidiaZipAsync(string archive, string destination, CancellationToken token)
    {
        using var zip = ZipFile.OpenRead(archive);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (var entry in zip.Entries)
        {
            token.ThrowIfCancellationRequested();
            var normalized = entry.FullName.Replace('\\', '/');
            var name = normalized.Split('/').Last();
            if (!NvidiaRuntimeFile(name)) continue;
            if (normalized.StartsWith('/') || normalized.Contains(':') || normalized.Split('/').Any(part => part is "." or "..") ||
                ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000 || !names.Add(name) ||
                entry.Length <= 0 || entry.Length > NvidiaFileLimit || (total += entry.Length) > NvidiaArchiveLimit)
                throw new InvalidDataException("Invalid or ambiguous NVIDIA archive entry: " + entry.FullName);
            await using var input = entry.Open();
            await using var output = new FileStream(Path.Combine(destination, name), FileMode.CreateNew, FileAccess.Write, FileShare.None);
            await CopyNvidiaBoundedAsync(input, output, entry.Length, token);
            if (output.Length != entry.Length) throw new InvalidDataException("Truncated NVIDIA runtime file.");
        }
    }

    static async Task CopyNvidiaBoundedAsync(Stream input, Stream output, long limit, CancellationToken token)
    {
        var buffer = new byte[128 * 1024]; long total = 0; int count;
        while ((count = await input.ReadAsync(buffer, token)) != 0)
        {
            if ((total += count) > limit) throw new InvalidDataException("NVIDIA download/extraction exceeds its size limit.");
            await output.WriteAsync(buffer.AsMemory(0, count), token);
        }
    }
}
