using System.IO.Compression;
using System.ComponentModel;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;
using Asset = Dlss5CompatApp.PhoenixReleaseAsset;

namespace Dlss5CompatApp;

sealed record PhoenixReleasePayload(string Source, string Version, long Size, string Sha256, bool IsDevelopment, string? NoticesPath = null,
    string? ReleaseAssetDigest = null, long? ReleaseAssetSize = null, string? ReleaseAssetsFingerprint = null, Dictionary<string, string>? NoticeSha256 = null)
{
    public string? Companion32Path { get; init; }
    public string? Companion32Sha256 { get; init; }
    public long? Companion32Size { get; init; }
    public string? RuntimePath { get; init; }
    public Dictionary<string, string>? RuntimeSha256 { get; init; }
}
sealed record PhoenixDownloadProgress(string Message, long BytesDownloaded, long? TotalBytes);

sealed class PhoenixReleaseUnavailableException(string message) : InvalidOperationException(message);

static partial class PhoenixReleaseDownloader
{
    public const string Repository = "RazvanManolache/dlss5-phoenix-addon";
    public const string AddonFileName = "phoenix.addon64";
    public const string SourceManifestFileName = "phoenix-release.json";
    public const string LatestReleaseUrl = "https://api.github.com/repos/" + Repository + "/releases/latest";
    const long MaxAssetBytes = 1024L * 1024 * 1024;
    const long MaxAddonBytes = 512L * 1024 * 1024;
    const long MaxModelBytes = 2L * 1024 * 1024 * 1024;
    const int MaxChecksumBytes = 1024 * 1024;

    public static async Task<PhoenixReleasePayload> DownloadLatestAsync(HttpClient http, string payloadRoot, CancellationToken cancellationToken, Action<PhoenixDownloadProgress>? progress = null, PhoenixReleaseChannel channel = PhoenixReleaseChannel.LatestPublished, bool replaceLocalDevelopment = false)
    {
        var release = await PhoenixReleaseCatalog.GetLatestAsync(http, channel, progress, cancellationToken);
        var tag = release.Tag;
        var assets = release.Assets;
        var cached = await PhoenixReleaseCache.ReadAsync(payloadRoot, cancellationToken);
        if (cached is { IsDevelopment: true } && !replaceLocalDevelopment)
        {
            if (!await PhoenixReleaseCache.VerifyAddonAsync(payloadRoot, cached, cancellationToken))
                throw new InvalidDataException("The selected local Phoenix DLSS5 development addon changed or is invalid. Reimport it, or explicitly choose Update to replace it.");
            progress?.Invoke(new PhoenixDownloadProgress($"Phoenix DLSS5 release {tag} checked; preserving explicitly imported local development addon. Choose Update to replace it.", 0, null));
            return cached;
        }

        var direct = assets.Where(a => a.Name.Equals(AddonFileName, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (direct.Length > 1)
            throw new InvalidDataException("Phoenix DLSS5 release contains ambiguous direct addon assets.");
        var candidates = direct.Length == 1 ? direct : assets.Where(a => a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) && !ExcludedArchive(a.Name)).ToArray();
        var fingerprint = PhoenixReleaseCache.AssetsFingerprint(assets);
        if (cached is { IsDevelopment: false } && cached.ReleaseAssetsFingerprint == fingerprint)
        {
            var selected = candidates.SingleOrDefault(a => a.Url == cached.Source && tag + ":" + a.Name == cached.Version);
            if (selected is not null && ArchiveRank(selected.Name) >= candidates.Max(asset => ArchiveRank(asset.Name)) &&
                string.Equals(cached.ReleaseAssetDigest, selected.Digest, StringComparison.OrdinalIgnoreCase) &&
                (!RuntimeAssets(assets).Any() || cached.RuntimePath is not null) &&
                (selected.Size <= 0 || cached.ReleaseAssetSize <= 0 || selected.Size == cached.ReleaseAssetSize) &&
                await PhoenixReleaseCache.VerifyAddonAsync(payloadRoot, cached, cancellationToken,
                    selected.Name.Equals(AddonFileName, StringComparison.OrdinalIgnoreCase) ? selected.Digest : null) &&
                await PhoenixReleaseCache.VerifyNoticesAsync(payloadRoot, cached, cancellationToken))
            {
                progress?.Invoke(new PhoenixDownloadProgress("Phoenix DLSS5 release addon and license notices verified; reusing current payload without downloading.", 0, null));
                return cached;
            }
        }
        Directory.CreateDirectory(payloadRoot);
        if (direct.Length == 1)
            return await InstallAssetAsync(http, direct[0], assets, tag, payloadRoot, cancellationToken, progress);

        // Only uploaded release assets are candidates; GitHub source archives are not binary payloads.
        var groups = assets.Where(a => a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) && !ExcludedArchive(a.Name))
            .GroupBy(a => ArchiveRank(a.Name)).OrderByDescending(g => g.Key);
        foreach (var group in groups)
        {
            var staged = new List<(Asset Asset, string Path)>();
            try
            {
                foreach (var asset in group.OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var path = await StageAssetAsync(http, asset, assets, tag, payloadRoot, cancellationToken, progress);
                    if (path is not null) staged.Add((asset, path));
                }
                if (staged.Count > 1)
                    throw new InvalidDataException("Phoenix DLSS5 release contains multiple equally suitable ZIPs with phoenix.addon64; publish a unique x64 package or direct addon asset.");
                if (staged.Count == 1)
                    return await PublishAsync(staged[0].Path, payloadRoot, staged[0].Asset.Url, tag + ":" + staged[0].Asset.Name, false, cancellationToken,
                        releaseAsset: staged[0].Asset, assetsFingerprint: fingerprint);
            }
            finally
            {
                foreach (var item in staged) DeleteTemporary(item.Path);
            }
        }
        throw new PhoenixReleaseUnavailableException(
            $"Phoenix DLSS5 release {tag} has no usable {AddonFileName} asset or ZIP containing it. " +
            "Source/debug/other-platform packages are excluded. Select a local Phoenix DLSS5 addon explicitly for development.");
    }

    public static async Task<PhoenixReleasePayload> ImportLocalAsync(string selectedAddonPath, string payloadRoot, CancellationToken cancellationToken, Action<PhoenixDownloadProgress>? progress = null)
        => await ImportLocalFileAsync(selectedAddonPath, payloadRoot, AddonFileName, "local-development", MaxAddonBytes, cancellationToken, progress);

    public static async Task<PhoenixReleasePayload> ImportLocalModelAsync(string selectedModelPath, string payloadRoot, CancellationToken cancellationToken, Action<PhoenixDownloadProgress>? progress = null)
    {
        if (string.IsNullOrWhiteSpace(selectedModelPath) || !Path.GetExtension(selectedModelPath).Equals(".dll", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Explicitly select an x64 NVIDIA DLSS5 model DLL.", nameof(selectedModelPath));
        var name = Path.GetFileName(selectedModelPath);
        if (!name.Equals("nvngx_dlss.dll", StringComparison.OrdinalIgnoreCase) &&
            !name.Equals("nvngx_dlssg.dll", StringComparison.OrdinalIgnoreCase) &&
            !name.Equals("nvngx_dlssnr.dll", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Select nvngx_dlssnr.dll (model), nvngx_dlss.dll (SR), or nvngx_dlssg.dll (FG).", nameof(selectedModelPath));
        return await ImportLocalFileAsync(selectedModelPath, payloadRoot, name.ToLowerInvariant(), "local-model", MaxModelBytes, cancellationToken, progress);
    }

    static async Task<PhoenixReleasePayload> ImportLocalFileAsync(string selectedPath, string payloadRoot, string targetName, string version, long limit, CancellationToken cancellationToken, Action<PhoenixDownloadProgress>? progress)
    {
        if (string.IsNullOrWhiteSpace(selectedPath))
            throw new ArgumentException("An explicit local Phoenix DLSS5 component path is required.", nameof(selectedPath));
        cancellationToken.ThrowIfCancellationRequested();
        var source = Path.GetFullPath(selectedPath);
        Directory.CreateDirectory(payloadRoot);
        if (targetName == AddonFileName && LocalPaths.Same(source, Path.Combine(payloadRoot, AddonFileName)))
        {
            var previous = await PhoenixReleaseCache.ReadAsync(payloadRoot, cancellationToken);
            if (previous is not null && await PhoenixReleaseCache.VerifyAddonAsync(payloadRoot, previous, cancellationToken))
                return previous; // Preserve indexed companions when reselecting the current binary.
        }
        var stage = TemporaryPath(payloadRoot);
        try
        {
            await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, true))
            await using (var output = new FileStream(stage, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, true))
                await CopyBoundedAsync(input, output, limit, cancellationToken, progress, "Importing " + targetName, input.Length);
            if (targetName == AddonFileName)
            {
                var companion = Path.Combine(Path.GetDirectoryName(source)!, "Phoenix.addon32");
                if (File.Exists(companion))
                {
                    LocalPaths.RequireNoLinks(companion);
                    await using var input = File.OpenRead(companion);
                    await using var output = new FileStream(stage + ".addon32", FileMode.CreateNew, FileAccess.Write);
                    await CopyBoundedAsync(input, output, MaxAddonBytes, cancellationToken);
                }
                PhoenixRuntimePackage.StageLocal(Path.GetDirectoryName(source)!, stage + ".runtimes", cancellationToken);
            }
            return await PublishAsync(stage, payloadRoot, new Uri(source).AbsoluteUri, version, true, cancellationToken, targetName, limit);
        }
        finally { DeleteTemporary(stage); }
    }

    static async Task<PhoenixReleasePayload> InstallAssetAsync(HttpClient http, Asset asset, Asset[] assets, string tag, string payloadRoot, CancellationToken cancellationToken, Action<PhoenixDownloadProgress>? progress)
    {
        var stage = await StageAssetAsync(http, asset, assets, tag, payloadRoot, cancellationToken, progress)
            ?? throw new InvalidDataException("Phoenix DLSS5 direct asset did not contain an addon.");
        try { return await PublishAsync(stage, payloadRoot, asset.Url, tag + ":" + asset.Name, false, cancellationToken,
            releaseAsset: asset, assetsFingerprint: PhoenixReleaseCache.AssetsFingerprint(assets)); }
        finally { DeleteTemporary(stage); }
    }

    static async Task<string?> StageAssetAsync(HttpClient http, Asset asset, Asset[] assets, string tag, string payloadRoot, CancellationToken cancellationToken, Action<PhoenixDownloadProgress>? progress)
    {
        var download = TemporaryPath(payloadRoot);
        var addon = TemporaryPath(payloadRoot);
        var keepAddon = false;
        try
        {
            await DownloadAsync(http, asset, download, MaxAssetBytes, cancellationToken, progress);
            await VerifyChecksumsAsync(http, asset, assets, download, cancellationToken);
            if (asset.Name.Equals(AddonFileName, StringComparison.OrdinalIgnoreCase))
                File.Move(download, addon);
            else if (!await ExtractAddonAsync(download, addon, cancellationToken))
                return null;
            if (!asset.Name.Equals(AddonFileName, StringComparison.OrdinalIgnoreCase))
                await ExtractAddonAsync(download, addon + ".addon32", cancellationToken, "phoenix.addon32");
            ValidateX64Pe(addon);
            ValidateFeederAddon(addon, "Phoenix DLSS5 release " + tag);
            await EnsureNoticesAsync(http, tag, addon + ".notices", cancellationToken);
            await StageRuntimesAsync(http, asset, assets, tag, download, addon + ".runtimes", cancellationToken, progress);
            cancellationToken.ThrowIfCancellationRequested();
            keepAddon = true;
            return addon;
        }
        finally
        {
            DeleteTemporary(download);
            if (!keepAddon) DeleteTemporary(addon);
        }
    }

    static async Task DownloadAsync(HttpClient http, Asset asset, string destination, long limit, CancellationToken cancellationToken, Action<PhoenixDownloadProgress>? progress = null)
    {
        if (!Uri.TryCreate(asset.Url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort ||
            !string.IsNullOrEmpty(uri.UserInfo) || uri.Query.Length != 0 || uri.Fragment.Length != 0 ||
            (asset.IsRawNotice
                ? uri.Host != "raw.githubusercontent.com" || !uri.AbsolutePath.StartsWith("/" + Repository + "/refs/tags/", StringComparison.Ordinal)
                : uri.Host != "github.com" || !uri.AbsolutePath.StartsWith("/" + Repository + "/releases/download/", StringComparison.Ordinal)))
            throw new InvalidDataException("Phoenix DLSS5 asset has an invalid HTTPS download URL: " + asset.Name);
        if (asset.Size == 0 || asset.Size < -1 || asset.Size > limit)
            throw new InvalidDataException("Phoenix DLSS5 asset has an invalid or excessive size: " + asset.Name);
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Accept.ParseAdd("application/octet-stream");
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        var responseSize = response.Content.Headers.ContentLength;
        if (responseSize is { } size && (size > limit || (asset.Size > 0 && size != asset.Size)))
            throw new InvalidDataException("Phoenix DLSS5 asset Content-Length differs from release metadata: " + asset.Name);
        if (asset.IsRawNotice && response.RequestMessage?.RequestUri is { } finalUri && finalUri.Host != "raw.githubusercontent.com")
            throw new InvalidDataException("Phoenix DLSS5 license notice was redirected away from the verified raw GitHub host.");
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, true);
        await CopyBoundedAsync(input, output, asset.Size > 0 ? asset.Size : limit, cancellationToken, progress, "Downloading " + asset.Name, asset.Size > 0 ? asset.Size : responseSize);
        if (output.Length == 0 || (asset.Size > 0 && output.Length != asset.Size) || (responseSize is { } length && output.Length != length))
            throw new InvalidDataException("Phoenix DLSS5 asset download is truncated: " + asset.Name);
    }

    static async Task CopyBoundedAsync(Stream input, Stream output, long limit, CancellationToken cancellationToken, Action<PhoenixDownloadProgress>? progress = null, string label = "Phoenix DLSS5 payload", long? total = null)
    {
        var buffer = new byte[128 * 1024];
        long done = 0;
        var lastReport = System.Diagnostics.Stopwatch.StartNew();
        progress?.Invoke(new PhoenixDownloadProgress(label + "...", 0, total));
        int read;
        while ((read = await input.ReadAsync(buffer, cancellationToken)) != 0)
        {
            done += read;
            if (done > limit) throw new InvalidDataException("Phoenix DLSS5 payload exceeds its declared or allowed size.");
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            if (lastReport.ElapsedMilliseconds >= 100)
            {
                progress?.Invoke(new PhoenixDownloadProgress($"{label}: {done:N0} / {total:N0} bytes", done, total));
                lastReport.Restart();
            }
        }
        progress?.Invoke(new PhoenixDownloadProgress($"{label}: {done:N0} bytes transferred", done, total));
    }

    static async Task<bool> ExtractAddonAsync(string archivePath, string destination, CancellationToken cancellationToken, string addonName = AddonFileName)
    {
        using var zip = ZipFile.OpenRead(archivePath);
        ZipArchiveEntry? selected = null;
        foreach (var entry in zip.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = entry.FullName.Replace('\\', '/');
            if (name.StartsWith('/') || name.Contains(':') || name.Contains('\0') ||
                name.Split('/').Any(p => p == ".." || p.EndsWith('.') || p.EndsWith(' ')) ||
                ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000 ||
                (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Unsafe Phoenix DLSS5 archive entry: " + entry.FullName);
            if (!name.Split('/').Last().Equals(addonName, StringComparison.OrdinalIgnoreCase)) continue;
            if (selected is not null)
                throw new InvalidDataException("Phoenix DLSS5 archive contains multiple " + addonName + " files.");
            selected = entry;
        }
        if (selected is null) return false;
        if (selected.Length <= 0 || selected.Length > MaxAddonBytes)
            throw new InvalidDataException("Phoenix DLSS5 addon in ZIP has an invalid or excessive size.");
        var names = zip.Entries.Where(e => e.Name.Length > 0).Select(e => e.FullName.Replace('\\', '/')).ToArray();
        var firstSlash = names[0].IndexOf('/');
        var root = firstSlash >= 0 && names.All(n => n.StartsWith(names[0][..(firstSlash + 1)], StringComparison.Ordinal))
            ? names[0][..(firstSlash + 1)] : "";
        long noticeBytes = 0;
        foreach (var entry in zip.Entries)
        {
            var name = entry.FullName.Replace('\\', '/');
            if (root.Length > 0)
            {
                if (!name.StartsWith(root, StringComparison.Ordinal)) continue;
                name = name[root.Length..];
            }
            if (addonName != AddonFileName || !IsNoticeFile(name)) continue;
            if (entry.Length > 4 * 1024 * 1024 || (noticeBytes += entry.Length) > 16 * 1024 * 1024)
                throw new InvalidDataException("Phoenix DLSS5 bundled license documents exceed their size limit.");
            var notice = Path.Combine(destination + ".notices", name.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(notice)!);
            await using var noticeInput = entry.Open();
            await using var noticeOutput = new FileStream(notice, FileMode.CreateNew, FileAccess.Write, FileShare.None, 32768, true);
            await CopyBoundedAsync(noticeInput, noticeOutput, entry.Length, cancellationToken);
            if (noticeOutput.Length != entry.Length) throw new InvalidDataException("Phoenix DLSS5 notice extraction is truncated.");
        }
        await using var input = selected.Open();
        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, true);
        await CopyBoundedAsync(input, output, selected.Length, cancellationToken);
        if (output.Length != selected.Length) throw new InvalidDataException("Phoenix DLSS5 addon extraction is truncated.");
        return true;
    }

    static bool IsNoticeFile(string name)
    {
        if (name.EndsWith('/')) return false;
        if (!name.Contains('/')) return Regex.IsMatch(name, @"^(LICENSE|NOTICE|THIRD_PARTY)(\.(txt|md))?$", RegexOptions.IgnoreCase);
        return (name.StartsWith("licenses/", StringComparison.OrdinalIgnoreCase) || name.StartsWith("third_party/", StringComparison.OrdinalIgnoreCase)) &&
            (new[] { ".txt", ".md", ".json", ".pdf" }.Contains(Path.GetExtension(name).ToLowerInvariant()) ||
             Path.GetFileName(name).Equals("LICENSE", StringComparison.OrdinalIgnoreCase) || Path.GetFileName(name).Equals("NOTICE", StringComparison.OrdinalIgnoreCase));
    }

    static async Task EnsureNoticesAsync(HttpClient http, string tag, string directory, CancellationToken token)
    {
        PhoenixReleaseCatalog.ValidateTag(tag);
        Directory.CreateDirectory(directory);
        foreach (var name in new[] { "LICENSE", "NOTICE", "THIRD_PARTY.md" })
        {
            var stem = Path.GetFileNameWithoutExtension(name);
            if (Directory.GetFiles(directory).Any(p => Path.GetFileNameWithoutExtension(p).Equals(stem, StringComparison.OrdinalIgnoreCase) && new FileInfo(p).Length > 0)) continue;
            var url = "https://raw.githubusercontent.com/" + Repository + "/refs/tags/" + Uri.EscapeDataString(tag) + "/" + name;
            await DownloadAsync(http, new Asset(name, url, -1, null, IsRawNotice: true), Path.Combine(directory, name), 4 * 1024 * 1024, token);
        }
    }

    // Validate structure and architecture, without loading or executing the selected DLL.
    public static void ValidateX64Pe(string path, long limit = MaxAddonBytes)
    {
        using var stream = File.OpenRead(path);
        using var reader = new BinaryReader(stream);
        if (stream.Length < 64 || stream.Length > limit || reader.ReadUInt16() != 0x5A4D)
            throw new InvalidDataException("Phoenix DLSS5 component is not a valid x64 PE DLL (missing DOS header or invalid size).");
        stream.Position = 0x3C;
        var offset = reader.ReadInt32();
        if (offset < 64 || offset > stream.Length - 24)
            throw new InvalidDataException("Phoenix DLSS5 addon has an invalid PE header offset.");
        stream.Position = offset;
        if (reader.ReadUInt32() != 0x00004550 || reader.ReadUInt16() != 0x8664)
            throw new InvalidDataException("Phoenix DLSS5 addon must be an AMD64/x64 PE DLL.");
        var sections = reader.ReadUInt16();
        stream.Position = offset + 20;
        var optionalSize = reader.ReadUInt16();
        var characteristics = reader.ReadUInt16();
        if (sections == 0 || optionalSize < 112 || offset + 24L + optionalSize + sections * 40L > stream.Length ||
            (characteristics & 0x2002) != 0x2002 || reader.ReadUInt16() != 0x20B)
            throw new InvalidDataException("Phoenix DLSS5 addon has invalid PE32+ DLL headers.");
        stream.Position = offset + 24L + optionalSize;
        for (var i = 0; i < sections; i++)
        {
            stream.Position += 16;
            var rawSize = reader.ReadUInt32();
            var rawOffset = reader.ReadUInt32();
            if (rawSize > 0 && (rawOffset == 0 || (long)rawOffset + rawSize > stream.Length))
                throw new InvalidDataException("Phoenix DLSS5 addon contains a truncated PE section.");
            stream.Position += 16;
        }
    }

    internal static void ValidateFeederAddon(string path, string label)
    {
        if (!PayloadScanner.HasPhoenixFeederApi(path))
            throw new PhoenixReleaseUnavailableException(label + " is incompatible with this app: " +
                "CnrPHOENIXFeederVersion / CnrPHOENIXFeederEvaluate are missing. " +
                "A direct-feeder-compatible Phoenix DLSS5 build is required; the existing component has not been replaced. " +
                "No SR/DLAA fallback is used.");
        if (!PayloadScanner.HasEmbeddedPhoenixRuntime(path))
            throw new PhoenixReleaseUnavailableException(label + " lacks the embedded C++ runtime required by the feeder. " +
                "Use a complete direct-feeder build; the existing component has not been replaced.");
    }

    static async Task VerifyChecksumsAsync(HttpClient http, Asset asset, Asset[] assets, string path, CancellationToken cancellationToken)
    {
        var checks = new List<(string Algorithm, string Hash)>();
        if (!string.IsNullOrWhiteSpace(asset.Digest))
        {
            var parts = asset.Digest.Split(':', 2);
            if (parts.Length != 2) throw new InvalidDataException("Malformed Phoenix DLSS5 release asset digest.");
            AddChecksum(checks, parts[0], parts[1]);
        }
        foreach (var checksum in assets.Where(a => IsChecksumAsset(a.Name, asset.Name)))
        {
            var temp = TemporaryPath(Path.GetDirectoryName(path)!);
            try
            {
                await DownloadAsync(http, checksum, temp, MaxChecksumBytes, cancellationToken);
                var text = await File.ReadAllTextAsync(temp, Encoding.UTF8, cancellationToken);
                var specific = checksum.Name.StartsWith(asset.Name + ".", StringComparison.OrdinalIgnoreCase);
                var found = false;
                foreach (var line in text.Split('\n'))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var trimmed = line.Trim();
                    if (trimmed.Length == 0 || trimmed.StartsWith('#')) continue;
                    var bsd = Regex.Match(trimmed, @"^SHA(256|512)\s*\((.+)\)\s*=\s*([0-9a-fA-F]+)$", RegexOptions.IgnoreCase);
                    var gnu = Regex.Match(trimmed, @"^([0-9a-fA-F]{64}|[0-9a-fA-F]{128})\s+\*?(.+)$");
                    string? hash = null;
                    if (bsd.Success && ChecksumNameMatches(bsd.Groups[2].Value, asset.Name)) hash = bsd.Groups[3].Value;
                    else if (gnu.Success && ChecksumNameMatches(gnu.Groups[2].Value, asset.Name)) hash = gnu.Groups[1].Value;
                    else if (specific && Regex.IsMatch(trimmed, @"^(?:[0-9a-fA-F]{64}|[0-9a-fA-F]{128})$")) hash = trimmed;
                    if (hash is null) continue;
                    var algorithm = bsd.Success ? "sha" + bsd.Groups[1].Value : hash.Length == 128 ? "sha512" : "sha256";
                    var declaredAlgorithm = Regex.Match(checksum.Name, @"sha(256|512)", RegexOptions.IgnoreCase);
                    if (declaredAlgorithm.Success && !algorithm.Equals("sha" + declaredAlgorithm.Groups[1].Value, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("Phoenix DLSS5 checksum algorithm does not match its published filename: " + checksum.Name);
                    AddChecksum(checks, algorithm, hash);
                    found = true;
                }
                if (!found) throw new InvalidDataException("Published Phoenix DLSS5 checksum file has no usable checksum for " + asset.Name + ": " + checksum.Name);
            }
            finally { DeleteTemporary(temp); }
        }
        foreach (var check in checks.Distinct())
        {
            await using var input = File.OpenRead(path);
            var hash = check.Algorithm == "sha256"
                ? await SHA256.HashDataAsync(input, cancellationToken)
                : await SHA512.HashDataAsync(input, cancellationToken);
            if (!Convert.ToHexString(hash).Equals(check.Hash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Phoenix DLSS5 " + check.Algorithm + " checksum mismatch: " + asset.Name);
        }
    }

    static void AddChecksum(List<(string Algorithm, string Hash)> checks, string algorithm, string hash)
    {
        algorithm = algorithm.Replace("-", "").ToLowerInvariant();
        var length = algorithm == "sha256" ? 64 : algorithm == "sha512" ? 128 : 0;
        if (length == 0 || hash.Length != length || !hash.All(Uri.IsHexDigit))
            throw new InvalidDataException("Unsupported or malformed Phoenix DLSS5 checksum: " + algorithm);
        checks.Add((algorithm, hash));
    }

    static bool ChecksumNameMatches(string name, string assetName) =>
        name.Trim().Trim('"').Replace('\\', '/').TrimStart('.', '/').Equals(assetName, StringComparison.OrdinalIgnoreCase);

    static bool IsChecksumAsset(string name, string assetName)
    {
        var lower = name.ToLowerInvariant();
        if (lower.StartsWith(assetName.ToLowerInvariant() + "."))
            return Regex.IsMatch(lower[(assetName.Length + 1)..], @"^sha(?:256|512)(?:sum)?(?:\.txt)?$");
        return Regex.IsMatch(lower, @"^(?:sha(?:256|512)(?:sums?|checksums)?(?:\.txt)?|checksums?(?:\.(?:txt|sha256|sha512))?)$");
    }

    internal static bool ExcludedArchive(string name) => Regex.IsMatch(name,
        @"(?:^|[._\- ])(?:source|src|symbols|pdb|debug|dbg|sdk|linux|macos|osx|darwin|arm64|aarch64|x86(?![_-]64)|win32)(?:[._\- ]|$)", RegexOptions.IgnoreCase);

    internal static int ArchiveRank(string name) =>
        // Experimental compatibility bundles are explicit alternatives to the
        // standard Windows package, not a second equally suitable default.
        (Regex.IsMatch(name, @"(?:^|[._\- ])(?:experimental|compatibility)(?:[._\- ]|$)", RegexOptions.IgnoreCase) ? -16 : 0) +
        (name.Contains("phoenix", StringComparison.OrdinalIgnoreCase) ? 4 : 0) +
        (Regex.IsMatch(name, @"(?:^|[._\- ])(?:x64|amd64|win64|x86[_-]64)(?:[._\- ]|$)", RegexOptions.IgnoreCase) ? 2 : 0) +
        (name.Contains("win", StringComparison.OrdinalIgnoreCase) ? 1 : 0);

    static async Task<PhoenixReleasePayload> PublishAsync(string stage, string payloadRoot, string source, string version, bool development, CancellationToken cancellationToken, string targetName = AddonFileName, long limit = MaxAddonBytes,
        Asset? releaseAsset = null, string? assetsFingerprint = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateX64Pe(stage, limit);
        if (targetName == AddonFileName) ValidateFeederAddon(stage, development ? "Selected Phoenix DLSS5 build" : "Downloaded Phoenix DLSS5 build");
        string hash;
        await using (var input = File.OpenRead(stage))
            hash = Convert.ToHexString(await SHA256.HashDataAsync(input, cancellationToken));
        var size = new FileInfo(stage).Length;
        var stagedNotices = stage + ".notices";
        var noticesPath = Directory.Exists(stagedNotices) ? Path.Combine("phoenix-notices", Guid.NewGuid().ToString("N")) : null;
        var noticeHashes = noticesPath is null ? null : await PhoenixReleaseCache.HashNoticesAsync(stagedNotices, cancellationToken);
        var result = new PhoenixReleasePayload(source, version, size, hash, development, noticesPath,
            releaseAsset?.Digest, releaseAsset?.Size, assetsFingerprint, noticeHashes);
        if (targetName == AddonFileName && File.Exists(stage + ".addon32"))
            result = await PhoenixCompanion32.PublishAsync(stage + ".addon32", payloadRoot, result, cancellationToken);
        if (targetName == AddonFileName)
            result = PhoenixRuntimePackage.Publish(stage + ".runtimes", payloadRoot, result, cancellationToken);
        var provenance = TemporaryPath(payloadRoot);
        try
        {
            await File.WriteAllTextAsync(provenance, JsonSerializer.Serialize(result), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            PublishStagedPayload(stage, provenance, Path.Combine(payloadRoot, targetName),
                Path.Combine(payloadRoot, targetName == AddonFileName ? SourceManifestFileName : targetName + ".source.json"),
                noticesPath is null ? null : stagedNotices, noticesPath is null ? null : Path.Combine(payloadRoot, noticesPath), cancellationToken);
            return result;
        }
        finally { DeleteTemporary(provenance); }
    }

    // Exception rollback, not a multi-file crash-atomic rename. No cancellation between binary and provenance commits.
    internal static void PublishStagedPayload(string stage, string provenance, string target, string manifest,
        string? stagedNotices, string? noticesDestination, CancellationToken token, Action? afterBinaryPublished = null)
    {
        var root = Path.GetDirectoryName(Path.GetFullPath(target))!;
        using var mutex = new Mutex(false, "Local\\Dlss5Compat-Phoenix-" +
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(root.ToUpperInvariant()))));
        try
        {
            if (WaitHandle.WaitAny([mutex, token.WaitHandle], TimeSpan.FromSeconds(15)) != 0)
            {
                token.ThrowIfCancellationRequested();
                throw new IOException("Another Phoenix DLSS5 payload publication is in progress.");
            }
        }
        catch (AbandonedMutexException) { }
        try { token.ThrowIfCancellationRequested(); Publish(); }
        finally { mutex.ReleaseMutex(); }

        void Publish()
        {
            var updates = new List<PublicationFile>();
            var noticesMoved = false;
            var noticesParentCreated = false;
            try
            {
                // Snapshot both destinations before changing either. Backups stay on the same volume.
                updates.Add(new PublicationFile(stage, target, root));
                updates.Add(new PublicationFile(provenance, manifest, root));
                if (noticesDestination is not null)
                {
                    LocalPaths.RequireNoLinks(noticesDestination);
                    var parent = Path.GetDirectoryName(noticesDestination)!;
                    noticesParentCreated = !Directory.Exists(parent);
                    Directory.CreateDirectory(parent);
                    Directory.Move(stagedNotices!, noticesDestination);
                    noticesMoved = true;
                }
                updates[0].Publish();
                afterBinaryPublished?.Invoke();
                updates[1].Publish();
            }
            catch (Exception failure)
            {
                var failures = new List<Exception> { failure };
                foreach (var update in updates.AsEnumerable().Reverse())
                    try { update.Restore(); }
                    catch (Exception recovery) { failures.Add(recovery); }
                try
                {
                    if (noticesMoved)
                    {
                        LocalPaths.RequireNoLinks(noticesDestination!);
                        Directory.Move(noticesDestination!, stagedNotices!);
                    }
                    if (noticesParentCreated)
                    {
                        var parent = Path.GetDirectoryName(noticesDestination!)!;
                        if (!Directory.EnumerateFileSystemEntries(parent).Any()) Directory.Delete(parent);
                    }
                }
                catch (Exception recovery) { failures.Add(recovery); }
                if (failures.Count > 1)
                    throw new AggregateException("Phoenix DLSS5 publication rollback was incomplete; retained backup paths are included in the errors.", failures);
                throw;
            }
            finally { foreach (var update in updates) update.Dispose(); }
        }
    }

    sealed class PublicationFile : IDisposable
    {
        const uint ReadWriteDeleteAccess = 0xC0010000, OpenExisting = 3, WriteThroughOpenReparsePoint = 0x80200000;
        const int FileDispositionInfo = 4;
        readonly string stage, target, backup, stagedHash;
        readonly bool existed;
        bool published, retainBackup;

        public PublicationFile(string stage, string target, string root)
        {
            this.stage = stage;
            this.target = target;
            backup = TemporaryPath(root);
            LocalPaths.RequireNoLinks(target);
            using (var input = File.OpenRead(stage)) stagedHash = Convert.ToHexString(SHA256.HashData(input));
            existed = File.Exists(target);
            try { if (existed) File.Copy(target, backup); }
            catch { Dispose(); throw; }
        }

        public void Publish()
        {
            LocalPaths.RequireNoLinks(target);
            File.Move(stage, target, overwrite: existed);
            published = true;
        }

        public void Restore()
        {
            if (!published) return;
            try
            {
                LocalPaths.RequireNoLinks(target);
                using var handle = CreateFileW(target, ReadWriteDeleteAccess, 0, IntPtr.Zero, OpenExisting, WriteThroughOpenReparsePoint, IntPtr.Zero);
                if (handle.IsInvalid) throw new IOException("Cannot exclusively restore Phoenix DLSS5 destination.", new Win32Exception(Marshal.GetLastWin32Error()));
                if ((File.GetAttributes(handle) & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
                    throw new IOException("Unsafe Phoenix DLSS5 rollback destination.");
                using var output = new FileStream(handle, FileAccess.ReadWrite);
                if (Convert.ToHexString(SHA256.HashData(output)) != stagedHash)
                    throw new IOException("Phoenix DLSS5 destination changed after publication; external bytes were preserved: " + target);
                if (existed)
                {
                    using var input = File.OpenRead(backup);
                    output.Position = 0;
                    input.CopyTo(output);
                    output.SetLength(input.Length);
                    output.Flush(true);
                    File.SetAttributes(handle, File.GetAttributes(backup));
                    File.SetLastWriteTimeUtc(handle, File.GetLastWriteTimeUtc(backup));
                }
                else
                {
                    var disposition = new FileDisposition { DeleteFile = 1 };
                    if (!SetFileInformationByHandle(handle, FileDispositionInfo, ref disposition, 1))
                        throw new IOException("Cannot remove failed Phoenix DLSS5 publication.", new Win32Exception(Marshal.GetLastWin32Error()));
                }
                published = false;
            }
            catch (Exception ex)
            {
                retainBackup = true;
                throw new IOException("Could not restore Phoenix DLSS5 destination " + target + "; previous bytes retained at " + backup, ex);
            }
        }

        public void Dispose()
        {
            if (retainBackup || !File.Exists(backup)) return;
            // Copies of read-only provenance must not leave read-only temporary backups behind.
            try { File.SetAttributes(backup, FileAttributes.Normal); File.Delete(backup); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        [StructLayout(LayoutKind.Sequential)]
        struct FileDisposition { public byte DeleteFile; }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
        static extern SafeFileHandle CreateFileW(string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes,
            uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);
        [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool SetFileInformationByHandle(SafeFileHandle file, int informationClass, ref FileDisposition information, uint bufferSize);
    }

    static string TemporaryPath(string root) => Path.Combine(root, ".phoenix-" + Guid.NewGuid().ToString("N") + ".tmp");

    static void DeleteTemporary(string path)
    {
        try
        {
            File.Delete(path);
            if (Path.GetFileName(path).StartsWith(".phoenix-", StringComparison.Ordinal) && Path.GetFileName(path).EndsWith(".tmp", StringComparison.Ordinal))
            {
                File.Delete(path + ".addon32");
                var runtimes = Path.GetFullPath(path + ".runtimes");
                if (Path.GetDirectoryName(runtimes) == Path.GetDirectoryName(Path.GetFullPath(path)) && Directory.Exists(runtimes))
                {
                    LocalPaths.RequireNoLinks(runtimes);
                    Directory.Delete(runtimes, recursive: true);
                }
            }
            var notices = Path.GetFullPath(path + ".notices");
            if (Path.GetFileName(path).StartsWith(".phoenix-", StringComparison.Ordinal) && Path.GetFileName(path).EndsWith(".tmp", StringComparison.Ordinal) &&
                Path.GetDirectoryName(notices) == Path.GetDirectoryName(Path.GetFullPath(path)) && Directory.Exists(notices))
                Directory.Delete(notices, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
