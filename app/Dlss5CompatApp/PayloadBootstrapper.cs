using System.IO.Compression;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Dlss5CompatApp;

static partial class PayloadBootstrapper
{
    const string ManifestFileName = "dlss5-payload-manifest.json";

    static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static async Task EnsureCurrentAsync(string payloadRoot, IProgress<BootstrapProgress> progress, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(payloadRoot);
        var cacheRoot = Path.Combine(payloadRoot, ".download-cache");
        Directory.CreateDirectory(cacheRoot);

        var manifestPath = Path.Combine(payloadRoot, ManifestFileName);
        var manifest = await LoadManifestAsync(manifestPath, cancellationToken);

        using var http = CreateHttpClient();

        await DownloadReShadeSetupAsync(http, payloadRoot, manifest, progress, cancellationToken);
        await DownloadRenoDxAsync(http, payloadRoot, cacheRoot, manifest, progress, cancellationToken);
        await DownloadD3D8To9Async(http, payloadRoot, manifest, progress, cancellationToken);
        await DownloadDgVoodooAsync(http, payloadRoot, cacheRoot, manifest, progress, cancellationToken);
        await WritePayloadReadmeAsync(payloadRoot, cancellationToken);

        await SaveManifestAsync(manifestPath, manifest, cancellationToken);
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
        await DownloadFileIfChangedAsync(http, url, target, "reshade-addon-setup", version, manifest, progress, 4, 18, "ReShade full add-on setup", cancellationToken);
        RemoveOldFiles(payloadRoot, "ReShade_Setup_*_Addon.exe", target);
    }

    static async Task DownloadRenoDxAsync(HttpClient http, string payloadRoot, string cacheRoot, PayloadManifest manifest, IProgress<BootstrapProgress> progress, CancellationToken cancellationToken)
    {
        progress.Report(new BootstrapProgress("Checking RenoDX DLSS5...", 20));
        var addonTarget = Path.Combine(payloadRoot, "renodx-dlss5.addon64");
        if (HasKnownGoodRenoDxAddon(payloadRoot))
        {
            progress.Report(new BootstrapProgress("RenoDX DLSS5 add-on is already the verified build.", 32));
        }
        else
        {
            await DownloadRenoDxFromDlss5SwapperAsync(http, payloadRoot, cacheRoot, addonTarget, manifest, progress, cancellationToken);
        }

        if (!HasKnownGoodRenoDxAddon(payloadRoot))
            progress.Report(new BootstrapProgress("Verified RenoDX DLSS5 add-on was not found; manual source still required.", 32));

        await DownloadKnownGoodNvidiaAsync(http, payloadRoot, cacheRoot, manifest, progress, cancellationToken);
    }

    static async Task DownloadRenoDxFromDlss5SwapperAsync(
        HttpClient http,
        string payloadRoot,
        string cacheRoot,
        string addonTarget,
        PayloadManifest manifest,
        IProgress<BootstrapProgress> progress,
        CancellationToken cancellationToken)
    {
        var release = await GetLatestGitHubReleaseAsync(http, "rakanki911/DLSS5-Swapper", cancellationToken);
        var portable = release.Assets
            .Where(a => a.Name.EndsWith("-portable.exe", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(a => a.Size)
            .FirstOrDefault();

        if (portable is null)
        {
            progress.Report(new BootstrapProgress("DLSS5-Swapper portable release was not found.", 32));
            return;
        }

        var archive = Path.Combine(cacheRoot, "DLSS5-Swapper-portable.exe");
        var changed = await DownloadFileIfChangedAsync(http, portable.Url, archive, "dlss5-swapper-portable", release.TagName + ":" + portable.Name, manifest, progress, 22, 28, "DLSS5-Swapper portable package", cancellationToken, portable.Size);
        if (!changed && HasKnownGoodRenoDxAddon(payloadRoot))
        {
            progress.Report(new BootstrapProgress("RenoDX DLSS5 add-on is current.", 32));
            return;
        }

        var sevenZip = await EnsureSevenZipAsync(http, cacheRoot, manifest, progress, cancellationToken);
        var extractRoot = Path.Combine(cacheRoot, "dlss5-swapper-extract");
        if (Directory.Exists(extractRoot))
            Directory.Delete(extractRoot, recursive: true);

        try
        {
            Directory.CreateDirectory(extractRoot);
            await RunProcessAsync(
                sevenZip,
                new[]
                {
                    "x",
                    archive,
                    "-o" + extractRoot,
                    @"resources\payload\renodx-dlss5.addon64",
                    "-y"
                },
                cancellationToken);

            var extracted = Path.Combine(extractRoot, "resources", "payload", "renodx-dlss5.addon64");
            if (!File.Exists(extracted))
            {
                extracted = Directory.EnumerateFiles(extractRoot, "renodx-dlss5.addon64", SearchOption.AllDirectories)
                    .FirstOrDefault() ?? "";
            }

            if (File.Exists(extracted))
            {
                File.Copy(extracted, addonTarget, overwrite: true);
                if (HasKnownGoodRenoDxAddon(payloadRoot))
                    progress.Report(new BootstrapProgress("Extracted verified RenoDX DLSS5 add-on.", 32));
                else
                    progress.Report(new BootstrapProgress("Extracted RenoDX DLSS5 add-on did not match the verified build.", 32));
            }
        }
        finally
        {
            try
            {
                if (Directory.Exists(extractRoot))
                    Directory.Delete(extractRoot, recursive: true);
            }
            catch
            {
            }
        }
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

    static async Task DownloadKnownGoodNvidiaAsync(HttpClient http, string payloadRoot, string cacheRoot, PayloadManifest manifest, IProgress<BootstrapProgress> progress, CancellationToken cancellationToken)
    {
        progress.Report(new BootstrapProgress("Checking NVIDIA DLSSNR 310.8 payload...", 34));
        var release = await GetLatestGitHubReleaseAsync(http, "zhubaohi/FF7R-DLSS5", cancellationToken);
        var nvidia = release.Assets.FirstOrDefault(a => a.Name.Equals("nvidia.zip", StringComparison.OrdinalIgnoreCase));
        if (nvidia is null)
        {
            progress.Report(new BootstrapProgress("Verified NVIDIA 310.8 archive was not found; manual download required.", 72));
            return;
        }

        var archive = Path.Combine(cacheRoot, "nvidia-3108.zip");
        var changed = await DownloadFileIfChangedAsync(http, nvidia.Url, archive, "nvidia-dlssnr-3108", release.TagName + ":" + nvidia.Name, manifest, progress, 36, 68, "verified NVIDIA 310.8 archive", cancellationToken, nvidia.Size);
        if (changed || ShouldExtractStreamline(payloadRoot) || !HasKnownGoodDlssNr(payloadRoot))
        {
            ExtractMatchingFiles(archive, payloadRoot, entryName =>
                entryName.Equals("nvngx_dlss.dll", StringComparison.OrdinalIgnoreCase) ||
                entryName.Equals("nvngx_dlss.license.txt", StringComparison.OrdinalIgnoreCase) ||
                entryName.Equals("nvngx_dlssg.dll", StringComparison.OrdinalIgnoreCase) ||
                entryName.Equals("nvngx_dlssnr.dll", StringComparison.OrdinalIgnoreCase) ||
                entryName.StartsWith("sl.", StringComparison.OrdinalIgnoreCase) && entryName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase));
            if (!HasKnownGoodDlssNr(payloadRoot))
                throw new InvalidOperationException("Downloaded NVIDIA archive did not contain the verified nvngx_dlssnr.dll build.");

            progress.Report(new BootstrapProgress("Extracted verified NVIDIA 310.8 payload files.", 72));
        }
    }

    static async Task DownloadD3D8To9Async(HttpClient http, string payloadRoot, PayloadManifest manifest, IProgress<BootstrapProgress> progress, CancellationToken cancellationToken)
    {
        progress.Report(new BootstrapProgress("Checking d3d8to9...", 73));
        var release = await GetLatestGitHubReleaseAsync(http, "crosire/d3d8to9", cancellationToken);
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
        var release = await GetLatestGitHubReleaseAsync(http, "dege-diosg/dgVoodoo2", cancellationToken);
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

        var archive = Path.Combine(cacheRoot, "dgVoodoo2.zip");
        var changed = await DownloadFileIfChangedAsync(http, asset.Url, archive, "dgvoodoo2", release.TagName + ":" + asset.Name, manifest, progress, 78, 92, "dgVoodoo2 archive", cancellationToken, asset.Size);
        if (changed || ShouldExtractDgVoodoo(payloadRoot))
        {
            ExtractDgVoodooFiles(archive, payloadRoot);
            progress.Report(new BootstrapProgress("Extracted dgVoodoo2 x86 DirectX and Glide files.", 94));
        }
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
        var temp = target + ".download";

        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        var total = response.Content.Headers.ContentLength ?? expectedSize;

        await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken))
        await using (var output = File.Create(temp))
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
                    progress.Report(new BootstrapProgress("Downloading " + label + "...", percent));
                }
            }
        }

        File.Move(temp, target, overwrite: true);
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

    static void ExtractMatchingFiles(string archive, string payloadRoot, Func<string, bool> fileNameMatches)
    {
        using var zip = ZipFile.OpenRead(archive);
        foreach (var entry in zip.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name) || !fileNameMatches(entry.Name)) continue;
            ExtractEntry(entry, Path.Combine(payloadRoot, entry.Name));
        }
    }

    static void ExtractDgVoodooFiles(string archive, string payloadRoot)
    {
        using var zip = ZipFile.OpenRead(archive);
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

    static bool ShouldExtractStreamline(string payloadRoot)
    {
        var required = new[]
        {
            "nvngx_dlss.dll",
            "nvngx_dlssnr.dll",
            "sl.common.dll",
            "sl.dlss.dll",
            "sl.dlss_nr.dll",
            "sl.interposer.dll"
        };

        if (required.Any(file => !File.Exists(Path.Combine(payloadRoot, file))))
            return true;

        var dlss = FileVersion(Path.Combine(payloadRoot, "nvngx_dlss.dll"));
        var dlssnr = FileVersion(Path.Combine(payloadRoot, "nvngx_dlssnr.dll"));
        return dlss is null || dlssnr is null || !dlss.Equals(dlssnr, StringComparison.OrdinalIgnoreCase);
    }

    static bool HasKnownGoodDlssNr(string payloadRoot) =>
        FileSha256(Path.Combine(payloadRoot, "nvngx_dlssnr.dll"))?.Equals(PayloadInfo.KnownGoodDlssNrSha256, StringComparison.OrdinalIgnoreCase) == true;

    static bool HasKnownGoodRenoDxAddon(string payloadRoot) =>
        FileSha256(Path.Combine(payloadRoot, "renodx-dlss5.addon64"))?.Equals(PayloadInfo.KnownGoodRenoDxAddonSha256, StringComparison.OrdinalIgnoreCase) == true;

    static string? FileSha256(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            using var stream = File.OpenRead(path);
            return Convert.ToHexString(SHA256.HashData(stream));
        }
        catch
        {
            return null;
        }
    }

    static string? FileVersion(string path)
    {
        try
        {
            return File.Exists(path)
                ? System.Diagnostics.FileVersionInfo.GetVersionInfo(path).FileVersion
                : null;
        }
        catch
        {
            return null;
        }
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
        - renodx-dlss5.addon64 extracted from the DLSS5-Swapper portable release
        - verified nvngx_dlss.dll, nvngx_dlssg.dll, nvngx_dlssnr.dll, and sl.*.dll files from the FF7R-DLSS5 NVIDIA archive, when present
        - d3d8to9/d3d8.dll from crosire/d3d8to9 for x86 DirectX 8 games
        - MS/x86/DDraw.dll, D3DImm.dll, D3D8.dll, D3D9.dll, 3Dfx/x86/Glide*.dll, and dgVoodooCpl.exe from dgVoodoo2
        - .download-cache/7za.exe from 7zip-bin on unpkg, used only to extract the portable package

        If the app reports that a file is missing, download it from the source listed in the main README and place it here.
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
        using var response = await http.GetAsync($"https://api.github.com/repos/{repository}/releases/latest", cancellationToken);
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
                assets.Add(new GitHubAsset(name, url, size));
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
        public DateTimeOffset UpdatedAt { get; set; }
    }

    sealed record GitHubRelease(string TagName, IReadOnlyList<GitHubAsset> Assets);
    sealed record GitHubAsset(string Name, string Url, long Size);
}

sealed record BootstrapProgress(string Message, int Percent);
