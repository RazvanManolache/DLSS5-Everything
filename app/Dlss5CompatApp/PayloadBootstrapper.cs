using System.IO.Compression;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Dlss5CompatApp;

static partial class PayloadBootstrapper
{
    const string ManifestFileName = "dlss5-payload-manifest.json";

    static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static async Task EnsureCurrentAsync(string payloadRoot, IProgress<BootstrapProgress> progress, CancellationToken cancellationToken, string? localPhoenixAddonPath = null, PhoenixReleaseChannel channel = PhoenixReleaseChannel.LatestPublished, bool replaceLocalDevelopment = false)
    {
        using var http = CreateHttpClient();
        await EnsureCurrentWithClientAsync(http, payloadRoot, progress, cancellationToken, localPhoenixAddonPath, channel, replaceLocalDevelopment);
    }

    internal static async Task EnsureCurrentWithClientAsync(HttpClient http, string payloadRoot, IProgress<BootstrapProgress> progress, CancellationToken cancellationToken, string? localPhoenixAddonPath = null, PhoenixReleaseChannel channel = PhoenixReleaseChannel.LatestPublished, bool replaceLocalDevelopment = false)
    {
        Directory.CreateDirectory(payloadRoot);
        var cacheRoot = Path.Combine(payloadRoot, ".download-cache");
        Directory.CreateDirectory(cacheRoot);

        var manifestPath = Path.Combine(payloadRoot, ManifestFileName);
        var manifest = await LoadManifestAsync(manifestPath, cancellationToken);

        await DownloadReShadeSetupAsync(http, payloadRoot, manifest, progress, cancellationToken);
        PhoenixReleaseUnavailableException? missingPhoenix = null;
        try
        {
            await DownloadPhoenixAsync(http, payloadRoot, manifest, progress, cancellationToken, localPhoenixAddonPath, channel, replaceLocalDevelopment);
        }
        catch (PhoenixReleaseUnavailableException ex)
        {
            // Continue checking independent components, but do not mark Phoenix or the payload current.
            missingPhoenix = ex;
        }
        await DownloadD3D8To9Async(http, payloadRoot, manifest, progress, cancellationToken);
        await DownloadDgVoodooAsync(http, payloadRoot, cacheRoot, manifest, progress, cancellationToken);
        await DownloadDosBoxEnhancedAsync(http, payloadRoot, cacheRoot, manifest, progress, cancellationToken);
        await WritePayloadReadmeAsync(payloadRoot, cancellationToken);

        await SaveManifestAsync(manifestPath, manifest, cancellationToken);
        if (missingPhoenix is not null)
        {
            progress.Report(new BootstrapProgress("Other prerequisites checked; payload incomplete. " + missingPhoenix.Message, 100));
            throw missingPhoenix;
        }
        progress.Report(new BootstrapProgress("Payload check complete.", 100));
    }

    static async Task DownloadReShadeSetupAsync(HttpClient http, string payloadRoot, PayloadManifest manifest, IProgress<BootstrapProgress> progress, CancellationToken cancellationToken)
    {
        progress.Report(new BootstrapProgress("Checking ReShade...", 2));
        var html = await http.GetStringAsync("https://reshade.me/", cancellationToken);
        var version = ReShadeAddonVersion().Match(html).Groups[1].Value;
        if (string.IsNullOrWhiteSpace(version))
            throw new InvalidOperationException("Could not determine latest ReShade add-on version from reshade.me.");

        var fileName = $"ReShade_Setup_{version}_Addon.exe";
        var url = $"https://reshade.me/downloads/{fileName}";
        var target = Path.Combine(payloadRoot, fileName);
        var changed = await DownloadFileIfChangedAsync(http, url, target, "reshade-addon-setup", version, manifest, progress, 4, 16, "ReShade full add-on setup", cancellationToken);
        RemoveOldFiles(payloadRoot, "ReShade_Setup_*_Addon.exe", target);
        if (changed || ShouldExtractReShadeDlls(payloadRoot))
        {
            var sevenZip = await EnsureSevenZipAsync(http, cacheRoot: Path.Combine(payloadRoot, ".download-cache"), manifest, progress, cancellationToken);
            await ExtractReShadeDllsAsync(sevenZip, target, payloadRoot, cancellationToken);
            progress.Report(new BootstrapProgress("Extracted ReShade x86/x64 add-on DLLs.", 18));
        }
    }

    static async Task DownloadPhoenixAsync(HttpClient http, string payloadRoot, PayloadManifest manifest, IProgress<BootstrapProgress> progress, CancellationToken cancellationToken, string? localPhoenixAddonPath, PhoenixReleaseChannel channel, bool replaceLocalDevelopment)
    {
        progress.Report(new BootstrapProgress(localPhoenixAddonPath is null
            ? "Checking latest Phoenix DLSS5 GitHub release..."
            : "Validating explicitly selected local Phoenix DLSS5 development addon...", 20));
        void Report(PhoenixDownloadProgress item)
        {
            var percent = item.TotalBytes is > 0
                ? 20 + (int)Math.Min(12, item.BytesDownloaded * 12 / item.TotalBytes.Value)
                : 20;
            progress.Report(new BootstrapProgress(item.Message, percent));
        }
        PhoenixReleasePayload payload;
        try
        {
            payload = localPhoenixAddonPath is null
                ? await PhoenixReleaseDownloader.DownloadLatestAsync(http, payloadRoot, cancellationToken, Report, channel, replaceLocalDevelopment)
                : await PhoenixReleaseDownloader.ImportLocalAsync(localPhoenixAddonPath, payloadRoot, cancellationToken, Report);
        }
        catch (PhoenixReleaseUnavailableException ex)
        {
            progress.Report(new BootstrapProgress(ex.Message, 32));
            throw;
        }

        manifest.Items.Remove("dlss5-swapper-portable");
        manifest.Items["phoenix-addon64"] = new PayloadManifestItem
        {
            Url = payload.Source,
            Version = payload.Version,
            Size = payload.Size,
            Sha256 = payload.Sha256,
            IsDevelopment = payload.IsDevelopment,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        progress.Report(new BootstrapProgress(payload.IsDevelopment
            ? "Using explicitly selected local Phoenix DLSS5 development addon (x64 PE validated)."
            : "Phoenix DLSS5 release addon is ready (x64 PE and release payload verified).", 32));
    }

    static async Task<string> EnsureSevenZipAsync(HttpClient http, string cacheRoot, PayloadManifest manifest, IProgress<BootstrapProgress> progress, CancellationToken cancellationToken)
    {
        var sevenZip = Path.Combine(cacheRoot, "7za.exe");
        await DownloadFileIfChangedAsync(
            http,
            "https://unpkg.com/7zip-bin@5.2.0/win/x64/7za.exe",
            sevenZip,
            "7zip-bin-7za",
            "7zip-bin@5.2.0/win/x64/7za.exe",
            manifest,
            progress,
            28,
            30,
            "7za extractor",
            cancellationToken,
            1_231_360);
        return sevenZip;
    }

    static async Task DownloadD3D8To9Async(HttpClient http, string payloadRoot, PayloadManifest manifest, IProgress<BootstrapProgress> progress, CancellationToken cancellationToken)
    {
        progress.Report(new BootstrapProgress("Checking d3d8to9...", 73));
        GitHubRelease release;
        try
        {
            release = await GetLatestGitHubReleaseAsync(http, "crosire/d3d8to9", cancellationToken);
        }
        catch (HttpRequestException ex) when (IsRateLimited(ex) && File.Exists(Path.Combine(payloadRoot, "d3d8to9", "d3d8.dll")))
        {
            progress.Report(new BootstrapProgress("GitHub rate-limited d3d8to9 update check; using local d3d8to9 wrapper.", 75));
            return;
        }
        var asset = release.Assets.FirstOrDefault(a => a.Name.Equals("d3d8.dll", StringComparison.OrdinalIgnoreCase));
        if (asset is null)
        {
            progress.Report(new BootstrapProgress("d3d8to9 release DLL was not found; manual download required.", 75));
            return;
        }

        var target = Path.Combine(payloadRoot, "d3d8to9", "d3d8.dll");
        await DownloadFileIfChangedAsync(http, asset.Url, target, "d3d8to9", release.TagName + ":" + asset.Name, manifest, progress, 73, 75, "d3d8to9 D3D8 wrapper", cancellationToken, asset.Size);
    }

    static async Task DownloadDgVoodooAsync(HttpClient http, string payloadRoot, string cacheRoot, PayloadManifest manifest, IProgress<BootstrapProgress> progress, CancellationToken cancellationToken)
    {
        progress.Report(new BootstrapProgress("Checking dgVoodoo2...", 76));
        GitHubRelease release;
        try
        {
            release = await GetLatestGitHubReleaseAsync(http, "dege-diosg/dgVoodoo2", cancellationToken);
        }
        catch (HttpRequestException ex) when (IsRateLimited(ex) && !ShouldExtractDgVoodoo(payloadRoot))
        {
            progress.Report(new BootstrapProgress("GitHub rate-limited dgVoodoo2 update check; using local extracted dgVoodoo2 files.", 94));
            return;
        }
        var asset = release.Assets
            .Where(a => a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            .Where(a => a.Name.StartsWith("dgVoodoo2_", StringComparison.OrdinalIgnoreCase))
            .Where(a => !a.Name.Contains("_dbg", StringComparison.OrdinalIgnoreCase))
            .Where(a => !a.Name.Contains("_dev", StringComparison.OrdinalIgnoreCase))
            .FirstOrDefault();

        if (asset is null)
        {
            progress.Report(new BootstrapProgress("dgVoodoo2 release archive was not found; manual download required.", 95));
            return;
        }

        var version = release.TagName + ":" + asset.Name;
        // The regular archive does not contain x64 DirectDraw/D3DImm. Upstream
        // publishes those ABI-compatible builds in its separate developer asset.
        await DownloadDgVoodoo64Async(http, payloadRoot, manifest, release, progress, cancellationToken);
        if (!ShouldExtractDgVoodoo(payloadRoot) && ManifestItemMatches(manifest, "dgvoodoo2", asset.Url, version, asset.Size))
        {
            progress.Report(new BootstrapProgress("dgVoodoo2 extracted files are current.", 94));
            return;
        }

        await using var archive = await DownloadFileToMemoryAsync(http, asset.Url, "dgvoodoo2", version, manifest, progress, 78, 92, "dgVoodoo2 archive", cancellationToken, asset.Size);
        using var zip = new ZipArchive(archive, ZipArchiveMode.Read);
        ExtractDgVoodooFiles(zip, payloadRoot);
        progress.Report(new BootstrapProgress("Extracted dgVoodoo2 x86 DirectX and Glide files.", 94));
    }

    static async Task DownloadDgVoodoo64Async(HttpClient http, string payloadRoot, PayloadManifest manifest,
        GitHubRelease release, IProgress<BootstrapProgress> progress, CancellationToken cancellationToken)
    {
        var asset = release.Assets.FirstOrDefault(item => item.Name.StartsWith("dgVoodoo2_", StringComparison.OrdinalIgnoreCase)
            && item.Name.EndsWith("_dev64.zip", StringComparison.OrdinalIgnoreCase));
        if (asset is null) return; // Missing x64 translators remain an explicit readiness error.
        var version = release.TagName + ":" + asset.Name;
        var files = new[] { "DDraw.dll", "D3DImm.dll" };
        var folder = Path.Combine(payloadRoot, "MS", "x64");
        if (files.All(name => PayloadScanner.IsPeDll(Path.Combine(folder, name), CpuArch.X64)) &&
            ManifestItemMatches(manifest, "dgvoodoo2-directdraw-x64", asset.Url, version, asset.Size)) return;
        await using var archive = await DownloadFileToMemoryAsync(http, asset.Url, "dgvoodoo2-directdraw-x64",
            version, manifest, progress, 76, 80, "dgVoodoo2 x64 DirectDraw translators", cancellationToken, asset.Size);
        using var zip = new ZipArchive(archive, ZipArchiveMode.Read);
        ExtractDgVoodoo64Files(zip, payloadRoot);
        progress.Report(new BootstrapProgress("Extracted upstream x64 DirectDraw/D3DImm translators.", 80));
    }

    internal static void ExtractDgVoodoo64Files(ZipArchive zip, string payloadRoot)
    {
        foreach (var name in new[] { "DDraw.dll", "D3DImm.dll" })
        {
            // Select the normal ABI, never the alternative Spec Release or ARM builds.
            var entry = zip.Entries.SingleOrDefault(item => item.FullName.Replace('\\', '/')
                .Equals("Release/x64/" + name, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidDataException("dgVoodoo2 x64 archive is missing Release/x64/" + name);
            var target = Path.Combine(payloadRoot, "MS", "x64", name);
            ExtractEntry(entry, target);
            if (!PayloadScanner.IsPeDll(target, CpuArch.X64))
                throw new InvalidDataException("dgVoodoo2 translator has an invalid x64 PE: " + name);
        }
    }

    static async Task DownloadDosBoxEnhancedAsync(HttpClient http, string payloadRoot, string cacheRoot, PayloadManifest manifest, IProgress<BootstrapProgress> progress, CancellationToken cancellationToken)
    {
        progress.Report(new BootstrapProgress("Checking DOSBox Enhanced...", 98));
        var targetRoot = Path.Combine(payloadRoot, "dosbox-enhanced");
        var executable = Path.Combine(targetRoot, "app", "DosboxEnhanced.exe");
        if (File.Exists(executable) && PeReader.GetArchitecture(executable) == CpuArch.X64 &&
            File.Exists(Path.Combine(targetRoot, "source-build.json")))
        {
            progress.Report(new BootstrapProgress("Keeping the explicitly built DOSBox Enhanced submodule package.", 99));
            return;
        }
        GitHubRelease release;
        try { release = await GetLatestGitHubReleaseAsync(http, "RazvanManolache/dosbox-enhanced", cancellationToken); }
        catch (HttpRequestException error) when (error.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Forbidden)
        {
            progress.Report(new BootstrapProgress("DOSBox Enhanced release unavailable; build external/dosbox-enhanced for DOS support. " + error.Message, 99));
            return;
        }
        var asset = release.Assets.FirstOrDefault(asset => asset.Name.StartsWith("DOSBox-Enhanced-", StringComparison.OrdinalIgnoreCase) &&
            asset.Name.EndsWith("-win-x64.zip", StringComparison.OrdinalIgnoreCase));
        if (asset is null) { progress.Report(new BootstrapProgress("DOSBox Enhanced Windows x64 portable asset was not found.", 99)); return; }
        if (asset.Sha256 is null) throw new InvalidDataException("DOSBox Enhanced release asset has no SHA-256 digest.");
        var archive = Path.Combine(cacheRoot, "dosbox-enhanced-win-x64.zip");
        const string cacheKey = "dosbox-enhanced-windows-x64";
        if (File.Exists(archive))
        {
            using var cached = File.OpenRead(archive);
            if (!Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(cached)).Equals(asset.Sha256, StringComparison.OrdinalIgnoreCase))
                manifest.Items.Remove(cacheKey); // Redownload damaged caches instead of repeatedly trusting matching metadata.
        }
        var changed = await DownloadFileIfChangedAsync(http, asset.Url, archive, cacheKey,
            release.TagName + ":" + asset.Name + ":" + asset.Sha256, manifest, progress, 98, 99,
            "DOSBox Enhanced portable package", cancellationToken, asset.Size);
        using (var input = File.OpenRead(archive))
            if (!Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(input)).Equals(asset.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("DOSBox Enhanced portable archive differs from its published SHA-256 digest.");
        if (changed || !File.Exists(executable)) ExtractZipFlattenSingleRoot(archive, targetRoot, cancellationToken);
        if (!File.Exists(executable) || PeReader.GetArchitecture(executable) != CpuArch.X64)
            throw new InvalidDataException("DOSBox Enhanced package is missing its Windows x64 application.");
        progress.Report(new BootstrapProgress("DOSBox Enhanced portable package ready; configure its separately supplied graphics/model runtimes before DOS inference.", 99));
    }

    static async Task<bool> DownloadFileIfChangedAsync(
        HttpClient http,
        string url,
        string target,
        string key,
        string version,
        PayloadManifest manifest,
        IProgress<BootstrapProgress> progress,
        int startPercent,
        int endPercent,
        string label,
        CancellationToken cancellationToken,
        long? expectedSize = null)
    {
        var item = manifest.Items.GetValueOrDefault(key);
        if (File.Exists(target) &&
            item is not null &&
            item.Url.Equals(url, StringComparison.OrdinalIgnoreCase) &&
            item.Version.Equals(version, StringComparison.OrdinalIgnoreCase) &&
            (expectedSize is null || new FileInfo(target).Length == expectedSize.Value))
        {
            progress.Report(new BootstrapProgress(label + " is current.", endPercent));
            return false;
        }

        progress.Report(new BootstrapProgress("Downloading " + label + "...", startPercent));
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var temp = Path.Combine(
            Path.GetDirectoryName(target)!,
            Path.GetFileName(target) + "." + Guid.NewGuid().ToString("N") + ".download");

        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        var total = response.Content.Headers.ContentLength ?? expectedSize;
        var lastPercent = -1;

        await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken))
        await using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
        {
            var buffer = new byte[128 * 1024];
            long done = 0;
            while (true)
            {
                var read = await input.ReadAsync(buffer, cancellationToken);
                if (read == 0) break;
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                done += read;
                if (total is > 0)
                {
                    var span = Math.Max(1, endPercent - startPercent);
                    var percent = startPercent + (int)Math.Min(span, done * span / total.Value);
                    if (percent != lastPercent)
                    {
                        lastPercent = percent;
                        progress.Report(new BootstrapProgress("Downloading " + label + "...", percent));
                    }
                }
            }
        }

        await ReplaceDownloadedFileAsync(temp, target, cancellationToken);
        manifest.Items[key] = new PayloadManifestItem
        {
            Url = url,
            Version = version,
            Size = new FileInfo(target).Length,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        progress.Report(new BootstrapProgress(label + " updated.", endPercent));
        return true;
    }

    static async Task ReplaceDownloadedFileAsync(string temp, string target, CancellationToken cancellationToken)
    {
        Exception? lastError = null;
        for (var attempt = 0; attempt < 30; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (File.Exists(target))
                {
                    File.SetAttributes(target, FileAttributes.Normal);
                    File.Delete(target);
                }

                File.Move(temp, target, overwrite: false);
                return;
            }
            catch (UnauthorizedAccessException ex)
            {
                lastError = ex;
            }
            catch (IOException ex)
            {
                lastError = ex;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250 + attempt * 250), cancellationToken);
        }

        throw new IOException("Could not replace downloaded payload file: " + target, lastError);
    }

    static async Task<MemoryStream> DownloadFileToMemoryAsync(
        HttpClient http,
        string url,
        string key,
        string version,
        PayloadManifest manifest,
        IProgress<BootstrapProgress> progress,
        int startPercent,
        int endPercent,
        string label,
        CancellationToken cancellationToken,
        long? expectedSize = null)
    {
        progress.Report(new BootstrapProgress("Downloading " + label + "...", startPercent));
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        var total = response.Content.Headers.ContentLength ?? expectedSize;
        var lastPercent = -1;
        var memory = total is > 0 and < int.MaxValue
            ? new MemoryStream((int)total.Value)
            : new MemoryStream();

        await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken))
        {
            var buffer = new byte[128 * 1024];
            long done = 0;
            while (true)
            {
                var read = await input.ReadAsync(buffer, cancellationToken);
                if (read == 0) break;
                await memory.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                done += read;
                if (total is > 0)
                {
                    var span = Math.Max(1, endPercent - startPercent);
                    var percent = startPercent + (int)Math.Min(span, done * span / total.Value);
                    if (percent != lastPercent)
                    {
                        lastPercent = percent;
                        progress.Report(new BootstrapProgress("Downloading " + label + "...", percent));
                    }
                }
            }
        }

        memory.Position = 0;
        manifest.Items[key] = new PayloadManifestItem
        {
            Url = url,
            Version = version,
            Size = memory.Length,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        progress.Report(new BootstrapProgress(label + " downloaded.", endPercent));
        return memory;
    }

    static void ExtractDgVoodooFiles(string archive, string payloadRoot)
    {
        using var zip = ZipFile.OpenRead(archive);
        ExtractDgVoodooFiles(zip, payloadRoot);
    }

    static void ExtractDgVoodooFiles(ZipArchive zip, string payloadRoot)
    {
        foreach (var entry in zip.Entries)
        {
            var path = entry.FullName.Replace('/', '\\');
            if (path.EndsWith(@"MS\x86\D3D8.dll", StringComparison.OrdinalIgnoreCase))
                ExtractEntry(entry, Path.Combine(payloadRoot, "MS", "x86", "D3D8.dll"));
            else if (path.EndsWith(@"MS\x86\D3D9.dll", StringComparison.OrdinalIgnoreCase))
                ExtractEntry(entry, Path.Combine(payloadRoot, "MS", "x86", "D3D9.dll"));
            else if (path.EndsWith(@"MS\x86\D3DImm.dll", StringComparison.OrdinalIgnoreCase))
                ExtractEntry(entry, Path.Combine(payloadRoot, "MS", "x86", "D3DImm.dll"));
            else if (path.EndsWith(@"MS\x86\DDraw.dll", StringComparison.OrdinalIgnoreCase))
                ExtractEntry(entry, Path.Combine(payloadRoot, "MS", "x86", "DDraw.dll"));
            else if (path.EndsWith(@"3Dfx\x86\Glide.dll", StringComparison.OrdinalIgnoreCase))
                ExtractEntry(entry, Path.Combine(payloadRoot, "3Dfx", "x86", "Glide.dll"));
            else if (path.EndsWith(@"3Dfx\x86\Glide2x.dll", StringComparison.OrdinalIgnoreCase))
                ExtractEntry(entry, Path.Combine(payloadRoot, "3Dfx", "x86", "Glide2x.dll"));
            else if (path.EndsWith(@"3Dfx\x86\Glide3x.dll", StringComparison.OrdinalIgnoreCase))
                ExtractEntry(entry, Path.Combine(payloadRoot, "3Dfx", "x86", "Glide3x.dll"));
            else if (path.EndsWith(@"3Dfx\x86\Napalm\Glide3x.dll", StringComparison.OrdinalIgnoreCase))
                ExtractEntry(entry, Path.Combine(payloadRoot, "3Dfx", "x86", "Napalm", "Glide3x.dll"));
            else if (entry.Name.Equals("dgVoodooCpl.exe", StringComparison.OrdinalIgnoreCase))
                ExtractEntry(entry, Path.Combine(payloadRoot, "dgVoodooCpl.exe"));
        }
    }

    static bool ManifestItemMatches(PayloadManifest manifest, string key, string url, string version, long? expectedSize = null)
    {
        var item = manifest.Items.GetValueOrDefault(key);
        return item is not null &&
               item.Url.Equals(url, StringComparison.OrdinalIgnoreCase) &&
               item.Version.Equals(version, StringComparison.OrdinalIgnoreCase) &&
               (expectedSize is null || item.Size == expectedSize.Value);
    }

    static bool IsRateLimited(HttpRequestException ex) =>
        ex.StatusCode == HttpStatusCode.Forbidden;

    internal static void ExtractZipFlattenSingleRoot(string archive, string targetRoot, CancellationToken cancellationToken = default)
    {
        const long maxArchiveBytes = 1024L * 1024 * 1024;
        const long maxFileBytes = 512L * 1024 * 1024;
        const long maxTotalBytes = 1024L * 1024 * 1024;
        cancellationToken.ThrowIfCancellationRequested();
        if (new FileInfo(archive).Length > maxArchiveBytes)
            throw new InvalidDataException("DOSBox ZIP exceeds the archive size limit.");
        using var zip = ZipFile.OpenRead(archive);
        if (zip.Entries.Count > 10000)
            throw new InvalidDataException("DOSBox ZIP exceeds the entry count limit.");
        var entries = zip.Entries.Select(entry => (Entry: entry, Name: ValidateDosBoxEntryName(entry))).ToArray();
        var rootPrefix = CommonArchiveRoot(entries.Where(e => !e.Name.EndsWith('/')).Select(e => e.Name)).Replace('\\', '/');
        var root = Path.GetFullPath(targetRoot);
        var rootWithSeparator = Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar;
        var paths = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        var originalPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var plan = new List<(ZipArchiveEntry Entry, string Target, bool IsDirectory)>();
        long totalBytes = 0;
        ValidateDosBoxDestination(root, isDirectory: true);
        // Validate every entry and destination before creating directories or replacing any file.
        foreach (var (entry, originalName) in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = originalName;
            if (!originalPaths.Add(name.TrimEnd('/')))
                throw new InvalidDataException("DOSBox ZIP contains a duplicate entry: " + originalName);
            var isDirectory = name.EndsWith('/');
            if (!string.IsNullOrEmpty(rootPrefix) && name.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
                name = name[rootPrefix.Length..];
            if (name.Length == 0) continue;
            var target = Path.GetFullPath(Path.Combine(root, name.TrimEnd('/').Replace('/', Path.DirectorySeparatorChar)));
            if (!target.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase) || !paths.TryAdd(target, isDirectory))
                throw new InvalidDataException("DOSBox ZIP contains an unsafe or duplicate destination: " + originalName);
            if (entry.Length > maxFileBytes || (totalBytes += entry.Length) > maxTotalBytes || (isDirectory && entry.Length != 0))
                throw new InvalidDataException("DOSBox ZIP exceeds extraction size limits.");
            ValidateDosBoxDestination(target, isDirectory);
            plan.Add((entry, target, isDirectory));
        }
        foreach (var item in plan)
        {
            for (var parent = Path.GetDirectoryName(item.Target); parent is not null && !parent.Equals(root, StringComparison.OrdinalIgnoreCase);
                 parent = Path.GetDirectoryName(parent))
                if (paths.TryGetValue(parent, out var directory) && !directory)
                    throw new InvalidDataException("DOSBox ZIP uses a file as a parent directory.");
        }
        var buffer = new byte[128 * 1024];
        foreach (var (entry, target, isDirectory) in plan)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ValidateDosBoxDestination(target, isDirectory);
            if (isDirectory) { Directory.CreateDirectory(target); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            var temporary = Path.Combine(Path.GetDirectoryName(target)!, ".dosbox-" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                using (var input = entry.Open())
                using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    long written = 0;
                    int read;
                    while ((read = input.Read(buffer)) != 0)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if ((written += read) > entry.Length)
                            throw new InvalidDataException("DOSBox ZIP entry exceeds its declared size.");
                        output.Write(buffer, 0, read);
                    }
                    if (written != entry.Length) throw new InvalidDataException("DOSBox ZIP entry is truncated.");
                }
                cancellationToken.ThrowIfCancellationRequested();
                ValidateDosBoxDestination(target, isDirectory: false);
                File.Move(temporary, target, overwrite: true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }

    static string ValidateDosBoxEntryName(ZipArchiveEntry entry)
    {
        var name = entry.FullName.Replace('\\', '/');
        var parts = name.TrimEnd('/').Split('/');
        var unixType = (entry.ExternalAttributes >> 16) & 0xF000;
        if (name.Length == 0 || name.StartsWith('/') || name.EndsWith("//") || name.Contains(':') || name.Contains('\0') ||
            (unixType != 0 && unixType != 0x8000 && unixType != 0x4000) ||
            (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0 ||
            ((entry.ExternalAttributes & (int)FileAttributes.Directory) != 0 && !name.EndsWith('/')) ||
            (unixType == 0x4000 && !name.EndsWith('/')) ||
            parts.Any(p => p.Length == 0 || p is "." or ".." || p.EndsWith('.') || p.EndsWith(' ') ||
                p.Any(c => c < 32 || "<>\"|?*".Contains(c)) ||
                Regex.IsMatch(p.Split('.')[0], @"^(CON|PRN|AUX|NUL|COM[1-9\u00b9\u00b2\u00b3]|LPT[1-9\u00b9\u00b2\u00b3])$", RegexOptions.IgnoreCase)))
            throw new InvalidDataException("Unsafe DOSBox ZIP entry: " + entry.FullName);
        return name;
    }

    static void ValidateDosBoxDestination(string path, bool isDirectory)
    {
        for (string? current = path; current is not null; current = Path.GetDirectoryName(current))
        {
            FileAttributes attributes;
            try { attributes = File.GetAttributes(current); }
            catch (FileNotFoundException) { continue; }
            catch (DirectoryNotFoundException) { continue; }
            if ((attributes & FileAttributes.ReparsePoint) != 0 ||
                ((attributes & FileAttributes.Directory) != 0) != (current != path || isDirectory))
                throw new InvalidDataException("Unsafe DOSBox extraction destination: " + current);
        }
    }

    static string CommonArchiveRoot(IEnumerable<string> names)
    {
        string? root = null;
        foreach (var name in names)
        {
            var normalized = name.Replace('/', '\\');
            var slash = normalized.IndexOf('\\');
            if (slash <= 0)
                return "";
            var candidate = normalized[..(slash + 1)];
            if (root is null)
                root = candidate;
            else if (!root.Equals(candidate, StringComparison.OrdinalIgnoreCase))
                return "";
        }

        return root ?? "";
    }

    static bool ShouldExtractDgVoodoo(string payloadRoot)
    {
        return new[]
        {
            Path.Combine(payloadRoot, "MS", "x86", "D3D8.dll"),
            Path.Combine(payloadRoot, "MS", "x86", "D3D9.dll"),
            Path.Combine(payloadRoot, "MS", "x86", "D3DImm.dll"),
            Path.Combine(payloadRoot, "MS", "x86", "DDraw.dll"),
            Path.Combine(payloadRoot, "3Dfx", "x86", "Glide.dll"),
            Path.Combine(payloadRoot, "3Dfx", "x86", "Glide2x.dll"),
            Path.Combine(payloadRoot, "3Dfx", "x86", "Glide3x.dll"),
            Path.Combine(payloadRoot, "3Dfx", "x86", "Napalm", "Glide3x.dll"),
            Path.Combine(payloadRoot, "dgVoodooCpl.exe")
        }.Any(path => !File.Exists(path));
    }

    static bool ShouldExtractReShadeDlls(string payloadRoot)
    {
        return !File.Exists(Path.Combine(payloadRoot, "ReShade32.dll")) ||
               !File.Exists(Path.Combine(payloadRoot, "ReShade64.dll"));
    }

    static async Task ExtractReShadeDllsAsync(string sevenZip, string setup, string payloadRoot, CancellationToken cancellationToken)
    {
        await RunProcessAsync(
            sevenZip,
            new[]
            {
                "x",
                setup,
                "-o" + payloadRoot,
                "ReShade32.dll",
                "ReShade64.dll",
                "-y"
            },
            cancellationToken);

        if (ShouldExtractReShadeDlls(payloadRoot))
            throw new InvalidOperationException("ReShade setup did not contain ReShade32.dll and ReShade64.dll.");
    }

    static void ExtractEntry(ZipArchiveEntry entry, string target)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        entry.ExtractToFile(target, overwrite: true);
    }

    static async Task RunProcessAsync(string fileName, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);

        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start " + Path.GetFileName(fileName) + ".");
        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);

        if (process.ExitCode != 0)
        {
            var output = await outputTask;
            var error = await errorTask;
            var details = string.Join(Environment.NewLine, new[] { output, error }.Where(s => !string.IsNullOrWhiteSpace(s)));
            throw new InvalidOperationException(Path.GetFileName(fileName) + " failed with exit code " + process.ExitCode + "." + (string.IsNullOrWhiteSpace(details) ? "" : Environment.NewLine + details));
        }
    }

    static bool HasAny(string root, params string[] patterns)
    {
        return patterns.Any(pattern => Directory.EnumerateFiles(root, pattern, SearchOption.TopDirectoryOnly).Any());
    }

    static void RemoveOldFiles(string root, string pattern, string keep)
    {
        foreach (var file in Directory.EnumerateFiles(root, pattern, SearchOption.TopDirectoryOnly))
        {
            if (file.Equals(keep, StringComparison.OrdinalIgnoreCase)) continue;
            try { File.Delete(file); } catch { }
        }
    }

    static async Task<PayloadManifest> LoadManifestAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            if (!File.Exists(path)) return new PayloadManifest();
            var json = await File.ReadAllTextAsync(path, cancellationToken);
            return JsonSerializer.Deserialize<PayloadManifest>(json) ?? new PayloadManifest();
        }
        catch
        {
            return new PayloadManifest();
        }
    }

    static async Task SaveManifestAsync(string path, PayloadManifest manifest, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(manifest, JsonOptions);
        await File.WriteAllTextAsync(path, json, cancellationToken);
    }

    static async Task WritePayloadReadmeAsync(string payloadRoot, CancellationToken cancellationToken)
    {
        var text = """
        # DLSS5 Compatibility Installer Payload

        This folder is populated by the app on startup.

        Automatically downloaded files may include:

        - ReShade_Setup_*_Addon.exe from reshade.me
        - ReShade32.dll and ReShade64.dll extracted from the ReShade add-on setup
        - phoenix.addon64 from the latest RazvanManolache/dlss5-phoenix-addon GitHub release (direct asset or ZIP)
        - d3d8to9/d3d8.dll from crosire/d3d8to9 for x86 DirectX 8 games
        - MS/x86/DDraw.dll, D3DImm.dll, D3D8.dll, D3D9.dll, 3Dfx/x86/Glide*.dll, and dgVoodooCpl.exe from dgVoodoo2
        - dosbox-enhanced/app/DosboxEnhanced.exe and its runtime folder from DOSBox Enhanced for native RTX DOS launch
        - .download-cache/7za.exe from 7zip-bin on unpkg, used to extract the ReShade setup

        Phoenix DLSS5 release lookup failures, including HTTP 404 when no release is published, are reported as errors.
        Development callers can explicitly pass localPhoenixAddonPath to EnsureCurrentAsync to select a local x64 Phoenix DLSS5 addon.
        That selection is recorded as development provenance; existing payload files are never a silent release fallback.
        NVIDIA DLSS5 model DLLs are supplied separately by explicit local selection; no model DLL source is downloaded automatically.
        LatestPublished includes previews; StableOnly explicitly excludes them. Releases are ordered by publication time.
        Release LICENSE, NOTICE, THIRD_PARTY, and bundled license documents are retained under phoenix-notices;
        phoenix-release.json identifies the exact source and notices folder. API rate limits use verified public GitHub metadata.
        Unchanged release payloads are reused only after source/version, release identity, addon SHA256/x64 PE,
        and all preserved notice hashes verify. Local development imports remain selected until an explicit Update
        passes replaceLocalDevelopment: true; startup checks do not replace them.
        """;
        await File.WriteAllTextAsync(Path.Combine(payloadRoot, "README.md"), text.ReplaceLineEndings(Environment.NewLine), cancellationToken);
    }

    static HttpClient CreateHttpClient()
    {
        var http = new HttpClient();
        http.Timeout = TimeSpan.FromMinutes(20);
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Dlss5CompatApp", "1.0"));
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return http;
    }

    static async Task<GitHubRelease> GetLatestGitHubReleaseAsync(HttpClient http, string repository, CancellationToken cancellationToken)
    {
        return await GetGitHubReleaseAsync(http, repository, "latest", cancellationToken);
    }

    static async Task<GitHubRelease> GetGitHubReleaseAsync(HttpClient http, string repository, string tagOrLatest, CancellationToken cancellationToken)
    {
        var suffix = tagOrLatest.Equals("latest", StringComparison.OrdinalIgnoreCase)
            ? "latest"
            : "tags/" + Uri.EscapeDataString(tagOrLatest);
        using var response = await http.GetAsync($"https://api.github.com/repos/{repository}/releases/{suffix}", cancellationToken);
        if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
        {
            var published = await GitHubReleaseFallback.ReadAsync(http, repository, tagOrLatest, cancellationToken);
            return new GitHubRelease(published.Tag, published.Assets.Select(asset =>
                new GitHubAsset(asset.Name, asset.Url, asset.Size, asset.Digest![7..])).ToList());
        }
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var root = json.RootElement;
        var tag = root.GetProperty("tag_name").GetString() ?? "latest";
        var assets = new List<GitHubAsset>();

        foreach (var asset in root.GetProperty("assets").EnumerateArray())
        {
            var name = asset.GetProperty("name").GetString() ?? "";
            var url = asset.GetProperty("browser_download_url").GetString() ?? "";
            var size = asset.TryGetProperty("size", out var sizeValue) ? sizeValue.GetInt64() : 0;
            if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(url))
                {
                var digest = asset.TryGetProperty("digest", out var value) ? value.GetString() : null;
                var sha256 = digest is { Length: 71 } && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) &&
                    digest[7..].All(Uri.IsHexDigit) ? digest[7..] : null;
                assets.Add(new GitHubAsset(name, url, size, sha256));
            }
        }

        return new GitHubRelease(tag, assets);
    }

    [GeneratedRegex("Download\\s+ReShade\\s+([0-9]+(?:\\.[0-9]+)+)\\s+with\\s+full\\s+add-on\\s+support", RegexOptions.IgnoreCase)]
    private static partial Regex ReShadeAddonVersion();

    sealed class PayloadManifest
    {
        public Dictionary<string, PayloadManifestItem> Items { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    sealed class PayloadManifestItem
    {
        public string Url { get; set; } = "";
        public string Version { get; set; } = "";
        public long Size { get; set; }
        public string? Sha256 { get; set; }
        public bool IsDevelopment { get; set; }
        public DateTimeOffset UpdatedAt { get; set; }
    }

    sealed record GitHubRelease(string TagName, IReadOnlyList<GitHubAsset> Assets);
    sealed record GitHubAsset(string Name, string Url, long Size, string? Sha256);
}

sealed record BootstrapProgress(string Message, int Percent);
