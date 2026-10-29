using System.Security.Cryptography;
using System.Text.Json;

namespace Dlss5CompatApp;

sealed record LegacyCleanupFile(string Path, string Sha256, string Manifest);
sealed record LegacyCleanupPlan(IReadOnlyList<LegacyCleanupFile> Retire, IReadOnlyList<PreflightFinding> Findings);

// Ownership comes from the old install journal plus the exact file hash and
// addon identity. A filename or an arbitrary entry in an "added" list is not
// permission to remove a user's mods or the game's own NVIDIA libraries.
static class LegacyInstallCleanup
{
    const string OverlayIdentity = "DLSS 5 Swapper Overlay";
    const long MaximumAddonBytes = 32L * 1024 * 1024;

    internal static LegacyCleanupPlan Inspect(GameCandidate game, CancellationToken token = default)
    {
        var retire = new Dictionary<string, LegacyCleanupFile>(StringComparer.OrdinalIgnoreCase);
        var findings = new List<PreflightFinding>();
        var folder = Path.GetFullPath(game.Root);
        // Unreal-style game roots can sit below an earlier installer's root.
        // Probe exact manifest names only; never enumerate other games.
        for (var depth = 0; depth < 4 && folder is not null; depth++, folder = Path.GetDirectoryName(folder))
        {
            token.ThrowIfCancellationRequested();
            var manifest = Path.Combine(folder, "_DLSS5_Backup", "manifest.json");
            try
            {
                LocalPaths.RequireNoLinks(manifest);
                if (!File.Exists(manifest)) continue;
                using var document = JsonDocument.Parse(LocalData.ReadText(manifest, 512 * 1024));
                var root = document.RootElement;
                RequireUniqueFields(root);
                if (root.GetProperty("version").GetInt32() != 1) throw new InvalidDataException("Unsupported previous-installer journal version.");
                var previousGame = root.GetProperty("game");
                RequireUniqueFields(previousGame);
                var executable = BoundedInstallPaths.Resolve(folder, previousGame.GetProperty("exe").GetString()!);
                if (!LocalPaths.Same(executable, game.ExePath)) continue;
                if (!LocalPaths.Same(previousGame.GetProperty("dir").GetString()!, folder))
                    throw new InvalidDataException("The previous install root does not match its journal location.");
                if (!root.TryGetProperty("labOverlay", out var overlay) || overlay.ValueKind == JsonValueKind.Null) continue;
                RequireUniqueFields(overlay);
                var added = root.GetProperty("added");
                if (added.ValueKind != JsonValueKind.Array || added.GetArrayLength() > 4096)
                    throw new InvalidDataException("Invalid previous-installer file inventory.");
                var path = BoundedInstallPaths.Resolve(folder, overlay.GetProperty("rel").GetString()!);
                if (!LocalPaths.IsInside(path, game.Root) ||
                    !LocalPaths.Same(Path.GetDirectoryName(path)!, Path.GetDirectoryName(game.ExePath)!))
                    throw new InvalidDataException("Legacy overlay is outside the selected injection directory.");
                if (!added.EnumerateArray().Any(item => LocalPaths.Same(BoundedInstallPaths.Resolve(folder, item.GetString()!), path)))
                    throw new InvalidDataException("The overlay is not recorded as added by the previous installer.");
                LocalPaths.RequireNoLinks(path);
                if (!File.Exists(path)) continue;
                var hash = overlay.GetProperty("sha256").GetString() ?? throw new InvalidDataException("Missing recorded overlay hash.");
                if (hash.Length != 64 || !hash.All(Uri.IsHexDigit)) throw new InvalidDataException("Invalid recorded overlay hash.");
                var file = new LegacyCleanupFile(path, hash, manifest);
                Verify(file);
                retire[path] = file;
            }
            catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or
                JsonException or InvalidOperationException or KeyNotFoundException)
            {
                findings.Add(new("legacy-cleanup-review", PreflightSeverity.Warning,
                    "Previous DLSS installer cleanup needs review; unverifiable files will be kept and may still load. " + error.Message, manifest));
            }
        }
        return new(retire.Values.ToArray(), findings);
    }

    static void RequireUniqueFields(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Invalid previous-installer record.");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (value.EnumerateObject().Any(property => !names.Add(property.Name)))
            throw new InvalidDataException("Ambiguous duplicate fields in previous-installer journal.");
    }

    internal static void Verify(LegacyCleanupFile file)
    {
        LocalPaths.RequireNoLinks(file.Path);
        if (!File.Exists(file.Path) || new FileInfo(file.Path).Length is < 64 or > MaximumAddonBytes)
            throw new InvalidDataException("Legacy overlay is missing or exceeds its inspection limit.");
        using (var input = File.OpenRead(file.Path))
            if (!Convert.ToHexString(SHA256.HashData(input)).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Legacy overlay has changed since its original installation: " + Path.GetFileName(file.Path));
        var architecture = PeReader.GetArchitecture(file.Path);
        if (architecture == CpuArch.Unknown || !Path.GetExtension(file.Path).Equals(
                architecture == CpuArch.X86 ? ".addon32" : ".addon64", StringComparison.OrdinalIgnoreCase) ||
            !PayloadScanner.HasExports(file.Path, "AddonInit", "AddonUninit") ||
            !PeReader.FindMarkers(file.Path, [OverlayIdentity]).Contains(OverlayIdentity))
            throw new InvalidDataException("Recorded file is not a recognized legacy Swapper overlay: " + Path.GetFileName(file.Path));
    }
}
