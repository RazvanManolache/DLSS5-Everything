using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Dlss5CompatApp;

/// <summary>Bind a published file inventory to its GitHub asset digest before inspecting local bytes.
/// Unknown/older release formats are unverified, never silently treated as current.</summary>
static class ReleaseFileVerifier
{
    static readonly HttpClient Client = new();

    internal static async Task<AppUpdateResult> VerifyAsync(string repository, AppUpdateResult release, string directory,
        string primaryBinary, CancellationToken token, HttpClient? client = null, string? stateDirectory = null)
    {
        AppUpdateResult Unverified(string detail) => release with { Status = AppUpdateStatus.Unverified, Error = detail };
        if (AppUpdateService.CompareVersions(release.CurrentVersion, release.LatestVersion) != 0)
            return Unverified("Local build/version differs from the published release; not verified as current.");
        var asset = release.Assets.FirstOrDefault(a => a.Name == "FILES_SHA256.json")
            ?? release.Assets.FirstOrDefault(a => a.Name == "VALIDATION.json");
        if (asset is null) return Unverified("This release has no published file inventory; local files cannot be verified.");
        if (release.Assets.Any(a => !IsDigest(a.Digest) || a.Size <= 0))
            return Unverified("Release asset identity is incomplete; not verified as current.");
        var expectedUrl = $"https://github.com/{repository}/releases/download/{Uri.EscapeDataString(release.LatestVersion!)}/{asset.Name}";
        if (asset.Url != expectedUrl || !IsDigest(asset.Digest) || asset.Size is <= 0 or > 4 * 1024 * 1024)
            return Unverified("Published file inventory lacks a trusted URL, size or SHA-256 digest.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, asset.Url);
            request.Headers.UserAgent.ParseAdd("DLSS5-Everything-update-notice");
            using var response = await (client ?? Client).SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is { } length && length != asset.Size)
                return Unverified("Published inventory size changed during the check; retry.");
            using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            var bytes = new byte[checked((int)asset.Size + 1)]; int count = 0;
            while (count < bytes.Length)
            {
                int read = await stream.ReadAsync(bytes.AsMemory(count), timeout.Token);
                if (read == 0) break; count += read;
            }
            if (count != asset.Size || !Convert.ToHexString(SHA256.HashData(bytes.AsSpan(0, count)))
                .Equals(asset.Digest![7..], StringComparison.OrdinalIgnoreCase))
                return Unverified("Published inventory checksum changed or is invalid; retry.");
            using var json = JsonDocument.Parse(bytes.AsMemory(0, count));
            var inventory = Inventory(json.RootElement);
            if (!inventory.ContainsKey(primaryBinary)) return Unverified("Published inventory does not cover this application binary.");
            long total = 0;
            foreach (var file in inventory)
            {
                timeout.Token.ThrowIfCancellationRequested();
                var path = BoundedInstallPaths.Resolve(directory, file.Key); LocalPaths.RequireNoLinks(path);
                if (!File.Exists(path)) return release with { Status = AppUpdateStatus.NeedsRepair, Error = "Published file is missing: " + file.Key };
                await using var input = File.OpenRead(path);
                if (input.Length > 512L * 1024 * 1024 || (total += input.Length) > 2L * 1024 * 1024 * 1024)
                    return Unverified("Local file verification exceeded its size limit.");
                if (!Convert.ToHexString(await SHA256.HashDataAsync(input, timeout.Token)).Equals(file.Value, StringComparison.OrdinalIgnoreCase))
                    return release with { Status = AppUpdateStatus.NeedsRepair, Error = "Published files differ (local build, replaced release or damage): " + file.Key };
            }
            // DOS native_manifest.json authenticates its kernel binaries and graph as a second level.
            if (inventory.ContainsKey("runtime/native/native_manifest.json"))
            {
                using var manifest = JsonDocument.Parse(LocalData.ReadText(Path.Combine(directory, "runtime/native/native_manifest.json"), 1024 * 1024));
                var root = manifest.RootElement;
                var kernels = root.GetProperty("kernels");
                if (kernels.GetArrayLength() is < 1 or > 512) return Unverified("Native kernel manifest exceeds its limits.");
                foreach (var kernel in kernels.EnumerateArray())
                {
                    var path = BoundedInstallPaths.Resolve(Path.Combine(directory, "runtime/native"), kernel.GetProperty("binary").GetString()!);
                    if (!await MatchesAsync(path, kernel.GetProperty("binary_sha256").GetString(), timeout.Token))
                        return release with { Status = AppUpdateStatus.NeedsRepair, Error = "Native kernel is missing or differs: " + path };
                }
                if (!await MatchesAsync(Path.Combine(directory, "runtime/native/native_graph.json"), root.GetProperty("native_graph_sha256").GetString(), timeout.Token))
                    return release with { Status = AppUpdateStatus.NeedsRepair, Error = "Native graph is missing or differs." };
            }
            // Remember release identity only after authenticating the inventory and local files.
            // A later same-tag asset replacement must not disappear behind equal version numbers.
            var fingerprint = PhoenixReleaseCache.AssetsFingerprint(release.Assets);
            var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(repository + "\n" + Path.GetFullPath(directory))));
            var receipt = Path.Combine(stateDirectory ?? Path.Combine(LocalData.DirectoryPath, "ReleaseChecks"), key + ".json");
            bool assetsChanged = false;
            if (File.Exists(receipt))
            {
                using var previous = JsonDocument.Parse(LocalData.ReadText(receipt, 65536));
                if (previous.RootElement.GetProperty("tag").GetString() == release.LatestVersion &&
                    previous.RootElement.GetProperty("fingerprint").GetString() != fingerprint)
                    assetsChanged = true;
            }
            LocalData.WriteAtomic(receipt, JsonSerializer.Serialize(new { tag = release.LatestVersion, fingerprint }));
            return release with { Status = AppUpdateStatus.UpToDate, Error = assetsChanged
                ? "Release assets were replaced under the same tag; installed files still match the authenticated published inventory."
                : "Published inventory, release identity and installed file checksums verified." };
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { return Unverified("File verification timed out; not verified as current."); }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or HttpRequestException or
            InvalidOperationException or KeyNotFoundException or ArgumentException)
        { return Unverified("File verification unavailable: " + error.Message); }
    }

    internal static Dictionary<string, string> Inventory(JsonElement root)
    {
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        void Add(string name, string? hash)
        {
            if (files.Count >= 4096 || hash is not { Length: 64 } || !hash.All(Uri.IsHexDigit) || !files.TryAdd(name, hash))
                throw new InvalidDataException("Invalid or duplicate inventory entry.");
            _ = BoundedInstallPaths.Resolve(Path.GetTempPath(), name);
        }
        if (root.ValueKind == JsonValueKind.Array)
            foreach (var file in root.EnumerateArray()) Add(file.GetProperty("path").GetString()!, file.GetProperty("sha256").GetString());
        else if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("passed", out var passed) && passed.ValueKind == JsonValueKind.True)
        {
            var schema = root.GetProperty("schema").GetString();
            var fields = schema switch
            {
                "dosbox-release-validation/1" => new[] { "runtime_sha256" },
                "phoenix-player-ui-refresh/1" => new[] { "binaries", "retained_processing_files_sha256" },
                _ => Array.Empty<string>()
            };
            foreach (var field in fields)
                foreach (var file in root.GetProperty(field).EnumerateObject()) Add(file.Name, file.Value.GetString());
        }
        return files;
    }

    static bool IsDigest(string? value) => value is { Length: 71 } && value.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) && value[7..].All(Uri.IsHexDigit);
    static async Task<bool> MatchesAsync(string path, string? digest, CancellationToken token)
    {
        LocalPaths.RequireNoLinks(path);
        if (!File.Exists(path) || digest is not { Length: 64 } || !digest.All(Uri.IsHexDigit)) return false;
        await using var file = File.OpenRead(path);
        return file.Length <= 128L * 1024 * 1024 && Convert.ToHexString(await SHA256.HashDataAsync(file, token)).Equals(digest, StringComparison.OrdinalIgnoreCase);
    }
}
