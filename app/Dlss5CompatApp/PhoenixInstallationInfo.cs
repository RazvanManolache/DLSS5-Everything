using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace Dlss5CompatApp;

sealed record PhoenixInstallationReport(string Summary, string Details);

static class PhoenixInstallationInfo
{
    internal static async Task<PhoenixInstallationReport> InspectAsync(GameCandidate game, PayloadInfo? payload,
        PhoenixUpdateResult? latest, CancellationToken token)
    {
        var directory = Path.GetDirectoryName(game.ExePath)!;
        var lines = new List<string> { "Phoenix installation — " + game.DisplayName,
            "Files/configuration only; this does not prove which module a running game has loaded.", "" };
        var cached = payload is null ? null : await PhoenixReleaseCache.ReadAsync(payload.Root, token);
        var installation = ReadInstallationReceipt(game);
        lines.Add("Component library: " + (payload?.Root ?? "Not selected"));
        lines.Add("Cached Phoenix release: " + (cached?.Version ?? "Unknown"));
        lines.Add("Component addon x64: " + (payload?.PhoenixAddon ?? "Not available"));
        lines.Add("Component addon x86: " + (payload?.PhoenixAddon32 ?? "Not available"));
        lines.Add("GitHub: " + (latest?.Message ?? "Not checked yet"));
        var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { directory,
            Path.Combine(directory, "host64"), Path.Combine(directory, "_DLSS5_OpenXR", "host64") };
        foreach (var folder in folders.ToArray())
            ReadConfiguredPaths(folder, lines, folders);
        var versions = new List<string>();
        foreach (var folder in folders.Take(12))
        foreach (var name in new[] { "Phoenix.addon32", "Phoenix.addon64" })
        {
            token.ThrowIfCancellationRequested();
            var installed = Path.Combine(folder, name);
            if (!File.Exists(installed)) continue;
            try
            {
                await using var input = new FileStream(installed, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (input.Length > 512L * 1024 * 1024) { lines.Add("Skipped oversized addon: " + installed); continue; }
                var hash = Convert.ToHexString(await SHA256.HashDataAsync(input, token));
                bool x86 = name.EndsWith("32", StringComparison.Ordinal);
                var source = x86 ? payload?.PhoenixAddon32 : payload?.PhoenixAddon;
                var expected = x86 ? cached?.Companion32Sha256 : cached?.Sha256;
                bool matches = expected is not null && hash.Equals(expected, StringComparison.OrdinalIgnoreCase);
                // A matching hash identifies releases even when the addon has no PE version resource.
                var installedHash = x86 ? installation?.PhoenixAddon32Sha256 : installation?.PhoenixAddon64Sha256;
                bool recorded = AppUpdateService.IsVersion(installation?.PhoenixVersion) && hash.Equals(installedHash, StringComparison.OrdinalIgnoreCase);
                var version = recorded ? installation!.PhoenixVersion! : matches && cached is not null ? cached.Version.Split(':')[0]
                    : CompanionUpdateService.EmbeddedVersion(installed) ?? "unknown (no matching release receipt or embedded version)";
                versions.Add(version);
                lines.AddRange(["", name + ": " + version, "Installed binary: " + installed,
                    "Component binary: " + (source ?? "Not available"),
                    "Relationship: " + DescribeLink(installed, source),
                    "Version evidence: " + (recorded ? "Installation receipt and SHA-256" : matches ? "Component release receipt and SHA-256" : "Embedded version, if available"),
                    "Component match: " + (matches ? "SHA-256 matches cached release" : "Different or unrecognized; reinstall to use the selected component"),
                    "SHA-256: " + hash]);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
            { lines.Add("Could not inspect " + installed + ": " + error.Message); }
        }
        var summary = versions.Count == 0 ? "No Phoenix addon found in the game or configured addon folders."
            : "Installed Phoenix: " + string.Join("; ", versions.Distinct()) + ".";
        return new(summary, string.Join(Environment.NewLine, lines));
    }

    static InstallManifest? ReadInstallationReceipt(GameCandidate game)
    {
        var path = Path.Combine(game.Root, "_DLSS5_Compat_Backup", "manifest.json");
        if (!File.Exists(path)) return null;
        try
        {
            var receipt = JsonSerializer.Deserialize<InstallManifest>(LocalData.ReadText(path, 1024 * 1024));
            return receipt is not null && LocalPaths.Same(BoundedInstallPaths.Resolve(game.Root, receipt.ExeRelativePath), game.ExePath)
                ? receipt : null;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException)
        { return null; }
    }

    static void ReadConfiguredPaths(string directory, List<string> lines, HashSet<string> addonFolders)
    {
        foreach (var name in new[] { "ReShade.ini", "ReShadeVR.ini" })
        {
            var path = Path.Combine(directory, name);
            if (!File.Exists(path)) continue;
            try
            {
                var text = LocalData.ReadText(path, 256 * 1024);
                lines.Add("Configuration: " + path);
                string section = "";
                foreach (var raw in text.Split('\n'))
                {
                    var line = raw.Trim();
                    if (line.StartsWith('[') && line.EndsWith(']')) { section = line[1..^1]; continue; }
                    if (line.StartsWith(';') || line.StartsWith('#')) continue;
                    int separator = line.IndexOf('='); if (separator < 1) continue;
                    var key = line[..separator].Trim(); var value = line[(separator + 1)..].Trim().Trim('"');
                    if (value.Length == 0) continue;
                    bool addon = section.Equals("ADDON", StringComparison.OrdinalIgnoreCase) && key.Equals("AddonPath", StringComparison.OrdinalIgnoreCase);
                    bool dependency = section.StartsWith("Phoenix", StringComparison.OrdinalIgnoreCase) &&
                        new[] { "NvidiaDllFolder", "ModelLibraryDirectory", "NativeRuntime", "PortableRuntime", "HelperPath" }.Contains(key, StringComparer.OrdinalIgnoreCase);
                    if (!addon && !dependency) continue;
                    var resolved = Path.GetFullPath(value, directory);
                    lines.Add($"  [{section}] {key} → {resolved} ({(File.Exists(resolved) || Directory.Exists(resolved) ? "exists" : "missing")})");
                    if (addon && addonFolders.Count < 12) addonFolders.Add(resolved);
                }
            }
            catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            { lines.Add("Could not read " + path + ": " + error.Message); }
        }
        var modelConfig = Path.Combine(directory, PhoenixConfiguration.FileName);
        if (!File.Exists(modelConfig)) return;
        try
        {
            using var json = JsonDocument.Parse(LocalData.ReadText(modelConfig, 256 * 1024));
            if (json.RootElement.TryGetProperty("weight_source", out var source) && source.ValueKind == JsonValueKind.Object &&
                source.TryGetProperty("path", out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } model)
                lines.Add("Model reference: " + Path.GetFullPath(model, directory));
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException)
        { lines.Add("Could not read model reference: " + error.Message); }
    }

    internal static string DescribeLink(string installed, string? source)
    {
        using var handle = File.OpenHandle(installed, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (!GetFileInformationByHandle(handle, out var installedInfo)) return "Link relationship unavailable";
        var resolved = new StringBuilder(32768);
        uint length = GetFinalPathNameByHandleW(handle, resolved, (uint)resolved.Capacity, 0);
        var finalPath = length > 0 && length < resolved.Capacity ? resolved.ToString() : installed;
        if (finalPath.StartsWith(@"\\?\UNC\", StringComparison.Ordinal)) finalPath = @"\\" + finalPath[8..];
        else if (finalPath.StartsWith(@"\\?\", StringComparison.Ordinal)) finalPath = finalPath[4..];
        if (!LocalPaths.Same(finalPath, installed)) return "Link / redirected path → " + finalPath;
        if (source is not null && File.Exists(source))
        {
            using var sourceHandle = File.OpenHandle(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (GetFileInformationByHandle(sourceHandle, out var sourceInfo) && installedInfo.Volume == sourceInfo.Volume &&
                installedInfo.IndexHigh == sourceInfo.IndexHigh && installedInfo.IndexLow == sourceInfo.IndexLow)
                return LocalPaths.Same(installed, source) ? "Uses component binary directly" : "Hard link: same file as " + source;
        }
        return installedInfo.Links > 1 ? "Hard-linked file; not linked to the selected component binary" : "Independent file; no link to the selected component binary";
    }

    [StructLayout(LayoutKind.Sequential)]
    struct FileIdentity
    {
        public uint Attributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME Created, Accessed, Written;
        public uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileIdentity info);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern uint GetFinalPathNameByHandleW(SafeFileHandle handle, StringBuilder path, uint length, uint flags);
}
