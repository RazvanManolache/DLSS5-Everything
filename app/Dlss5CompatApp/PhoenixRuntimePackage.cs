using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Dlss5CompatApp;

sealed record PhoenixRuntimeFile(string RelativePath, string SourcePath, string Sha256);

/// <summary>Optional runtime companions, bound to the imported addon pair. No code is loaded
/// during discovery or installation. Package bytes are verified before game mutation.</summary>
static class PhoenixRuntimePackage
{
    const int MaxFiles = 2048;
    const long MaxFileBytes = 128L * 1024 * 1024;
    const long MaxTotalBytes = 1024L * 1024 * 1024;
    static readonly string[] Sections = ["win-x86", "win-x64", "wow64"];

    static string Hash(string path)
    {
        using var input = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(input));
    }

    static void CheckName(string relative)
    {
        var parts = relative.Replace('\\', '/').Split('/');
        if (parts.Length < 2 || !Sections.Contains(parts[0], StringComparer.Ordinal))
            throw new InvalidDataException("Unknown Phoenix runtime directory.");
        var name = Path.GetFileName(relative);
        var extension = Path.GetExtension(name).ToLowerInvariant();
        if (name.StartsWith("nvngx", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("weights.bin", StringComparison.OrdinalIgnoreCase) ||
            new[] { "ntdll.dll", "nvcuda.dll", "nvcuda64.dll", "kernel32.dll" }.Contains(name, StringComparer.OrdinalIgnoreCase) ||
            !new[] { "", ".dll", ".exe", ".json", ".cubin", ".spv", ".txt", ".md" }.Contains(extension))
            throw new InvalidDataException("Excluded model, system DLL or diagnostic file in Phoenix runtime: " + relative);
    }

    static Dictionary<string, string> HashTree(string root, CancellationToken token)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Stack<string>();
        pending.Push(root);
        long total = 0;
        var entries = 0;
        while (pending.TryPop(out var folder))
        {
            LocalPaths.RequireNoLinks(folder);
            foreach (var path in Directory.EnumerateFileSystemEntries(folder))
            {
                token.ThrowIfCancellationRequested();
                if (++entries > MaxFiles * 4) throw new InvalidDataException("Too many Phoenix runtime entries.");
                LocalPaths.RequireNoLinks(path);
                if (Directory.Exists(path)) { pending.Push(path); continue; }
                var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
                _ = BoundedInstallPaths.Resolve(root, relative);
                CheckName(relative);
                var bytes = new FileInfo(path).Length;
                if (bytes > MaxFileBytes || (total += bytes) > MaxTotalBytes || result.Count >= MaxFiles ||
                    !result.TryAdd(relative, Hash(path)))
                    throw new InvalidDataException("Phoenix runtime package exceeds its limits or contains duplicate paths.");
            }
        }
        return result;
    }

    static string? RuntimeRelative(string relative)
    {
        var parts = relative.Replace('\\', '/').Split('/');
        if (parts.Length < 2 || !Sections.Contains(parts[0], StringComparer.Ordinal))
            throw new InvalidDataException("Unknown Phoenix runtime directory.");
        // Published SDK packages place binaries in bin; addon candidates use a
        // flat runtime root. Development headers/libraries are not game dependencies.
        if (parts[0] != "wow64" && new[] { "include", "lib", "examples" }.Contains(parts[1])) return null;
        if (parts[1] == "bin") relative = parts[0] + "/" + string.Join('/', parts.Skip(2));
        CheckName(relative);
        return relative;
    }

    public static void StageLocal(string addonDirectory, string destination, CancellationToken token)
    {
        var source = Path.Combine(addonDirectory, "PhoenixRuntime");
        if (!Directory.Exists(source)) return;
        var pending = new Stack<string>();
        pending.Push(source);
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        var entries = 0;
        while (pending.TryPop(out var folder))
        {
            LocalPaths.RequireNoLinks(folder);
            foreach (var path in Directory.EnumerateFileSystemEntries(folder))
            {
                token.ThrowIfCancellationRequested();
                if (++entries > MaxFiles * 4) throw new InvalidDataException("Too many local runtime entries.");
                LocalPaths.RequireNoLinks(path);
                if (Directory.Exists(path)) { pending.Push(path); continue; }
                var original = Path.GetRelativePath(source, path).Replace('\\', '/');
                _ = BoundedInstallPaths.Resolve(source, original);
                var relative = RuntimeRelative(original);
                if (relative is null) continue;
                if (!files.Add(relative) || files.Count > MaxFiles || new FileInfo(path).Length > MaxFileBytes ||
                    (total += new FileInfo(path).Length) > MaxTotalBytes)
                    throw new InvalidDataException("Local runtime exceeds limits or has conflicting layouts.");
                var target = BoundedInstallPaths.Resolve(destination, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(path, target);
            }
        }
        if (files.Count > 0) ValidateComponents(destination, HashTree(destination, token));
    }

    public static async Task<int> ExtractAsync(string archive, string destination, CancellationToken token)
    {
        using var zip = ZipFile.OpenRead(archive);
        if (zip.Entries.Count > MaxFiles * 4) throw new InvalidDataException("Too many runtime archive entries.");
        var roots = new HashSet<string>(StringComparer.Ordinal);
        var sections = new HashSet<string>(StringComparer.Ordinal);
        // Inventory and validate before writing, including entries we will ignore.
        foreach (var entry in zip.Entries)
        {
            token.ThrowIfCancellationRequested();
            var name = entry.FullName.Replace('\\', '/');
            _ = BoundedInstallPaths.Resolve(destination, name.TrimEnd('/'));
            if (((entry.ExternalAttributes >> 16) & 0xf000) == 0xa000 ||
                (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Runtime archive contains a link.");
            var parts = name.Split('/');
            var marker = Array.IndexOf(parts, "PhoenixRuntime");
            if (marker < 0) continue;
            roots.Add(string.Join('/', parts.Take(marker)) + (marker == 0 ? "" : "/"));
            if (parts.Length > marker + 1 && Sections.Contains(parts[marker + 1]))
                sections.Add(parts[marker + 1]);
        }
        if (roots.Count > 1) throw new InvalidDataException("Runtime archive has ambiguous package roots.");
        var packageRoot = roots.SingleOrDefault();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (var entry in zip.Entries.Where(entry => !entry.FullName.EndsWith('/') && !entry.FullName.EndsWith('\\')))
        {
            token.ThrowIfCancellationRequested();
            var name = entry.FullName.Replace('\\', '/');
            var parts = name.Split('/');
            var marker = Array.IndexOf(parts, "PhoenixRuntime");
            IEnumerable<string> targets = [];
            if (marker >= 0)
            {
                var relative = RuntimeRelative(string.Join('/', parts.Skip(marker + 1)));
                if (relative is not null) targets = [relative];
            }
            else if (packageRoot is not null && name.StartsWith(packageRoot, StringComparison.Ordinal))
            {
                var relative = name[packageRoot.Length..];
                var rootNotice = !relative.Contains('/') &&
                    (Path.GetFileNameWithoutExtension(relative).Equals("LICENSE", StringComparison.OrdinalIgnoreCase) ||
                     Path.GetFileNameWithoutExtension(relative).Equals("NOTICE", StringComparison.OrdinalIgnoreCase) ||
                     Path.GetFileNameWithoutExtension(relative).Equals("THIRD_PARTY", StringComparison.OrdinalIgnoreCase));
                if (rootNotice || relative.StartsWith("licenses/", StringComparison.OrdinalIgnoreCase))
                    targets = sections.Select(section => section + "/" + relative);
            }
            foreach (var relative in targets)
            {
                CheckName(relative);
                if (!names.Add(relative) || names.Count > MaxFiles || entry.Length > MaxFileBytes ||
                    (total += entry.Length) > MaxTotalBytes)
                    throw new InvalidDataException("Runtime archive exceeds limits or contains duplicate paths.");
                var target = BoundedInstallPaths.Resolve(destination, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                var temporary = target + ".extracting";
                try
                {
                    await using (var input = entry.Open())
                    await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write))
                    {
                        var buffer = new byte[65536];
                        long written = 0;
                        int count;
                        while ((count = await input.ReadAsync(buffer, token)) != 0)
                        {
                            written += count;
                            if (written > entry.Length) throw new InvalidDataException("Runtime archive expanded beyond its declared size.");
                            await output.WriteAsync(buffer.AsMemory(0, count), token);
                        }
                        if (written != entry.Length) throw new InvalidDataException("Truncated runtime archive entry.");
                    }
                    if (File.Exists(target))
                    {
                        if (Hash(target) != Hash(temporary)) throw new InvalidDataException("Runtime archives disagree: " + relative);
                    }
                    else File.Move(temporary, target);
                }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
            }
        }
        return names.Count;
    }

    static string Identity(IReadOnlyDictionary<string, string> files) => Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes(string.Join('\n', files.OrderBy(x => x.Key, StringComparer.Ordinal)
            .Select(x => x.Key + ":" + x.Value.ToUpperInvariant()))))).ToLowerInvariant();

    static bool MatchingPath(string relative, IReadOnlyDictionary<string, string> files)
    {
        var parts = relative.Split('/');
        var identity = Identity(files);
        return parts.Length == 3 && parts[0] == "phoenix-runtimes" && parts[2] == "PhoenixRuntime" &&
            (parts[1] == identity || parts[1].StartsWith(identity + "-", StringComparison.Ordinal) &&
                parts[1].Length == identity.Length + 33 && parts[1][(identity.Length + 1)..].All(Uri.IsHexDigit));
    }

    public static PhoenixReleasePayload Publish(string staged, string root, PhoenixReleasePayload release, CancellationToken token)
    {
        if (!Directory.Exists(staged)) return release;
        var files = HashTree(staged, token);
        if (files.Count == 0) return release;
        ValidateComponents(staged, files);
        var relative = "phoenix-runtimes/" + Identity(files) + "/PhoenixRuntime";
        var destination = BoundedInstallPaths.Resolve(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        if (Directory.Exists(destination))
        {
            var previous = HashTree(destination, token);
            if (Identity(previous) != Identity(files))
            {
                // Repair publishes a new directory; an old game may still have DLLs mapped.
                relative = "phoenix-runtimes/" + Identity(files) + "-" + Guid.NewGuid().ToString("N") + "/PhoenixRuntime";
                destination = BoundedInstallPaths.Resolve(root, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                Directory.Move(staged, destination);
            }
        }
        else Directory.Move(staged, destination);
        return release with { RuntimePath = relative, RuntimeSha256 = files };
    }

    public static IReadOnlyList<PhoenixRuntimeFile> For(PayloadInfo payload, CpuArch arch, bool verify = true)
    {
        // UI discovery only reads bounded metadata; do not synchronously wait on
        // an async continuation that may require the WinForms context itself.
        var provenance = BoundedInstallPaths.Resolve(payload.Root, PhoenixReleaseDownloader.SourceManifestFileName);
        var release = File.Exists(provenance)
            ? JsonSerializer.Deserialize<PhoenixReleasePayload>(LocalData.ReadText(provenance, 1024 * 1024)) : null;
        if (release?.RuntimePath is null) return [];
        var files = release.RuntimeSha256 ?? throw new InvalidDataException("Phoenix runtime fingerprints are missing; update components.");
        if (files.Count is < 1 or > MaxFiles || files.Values.Any(x => x is not { Length: 64 } || !x.All(Uri.IsHexDigit)) ||
            !MatchingPath(release.RuntimePath, files))
            throw new InvalidDataException("Invalid Phoenix runtime provenance.");
        var root = BoundedInstallPaths.Resolve(payload.Root, release.RuntimePath);
        if (verify)
        {
            if (release.Sha256 is not { Length: 64 } || !release.Sha256.All(Uri.IsHexDigit) ||
                payload.PhoenixAddon is null || Hash(payload.PhoenixAddon) != release.Sha256.ToUpperInvariant() ||
                arch == CpuArch.X86 && (payload.PhoenixAddon32 is null || release.Companion32Sha256 is null ||
                    Hash(payload.PhoenixAddon32) != release.Companion32Sha256.ToUpperInvariant()))
                throw new InvalidDataException("Phoenix runtime and selected addon pair do not match; reimport or update them together.");
            if (Identity(HashTree(root, CancellationToken.None)) != Identity(files))
                throw new InvalidDataException("Phoenix runtime is incomplete or modified; update components before installing.");
            ValidateComponents(root, files);
        }
        var prefix = arch == CpuArch.X86 ? "win-x86/" : "win-x64/";
        return files.Where(x => x.Key.StartsWith(prefix, StringComparison.Ordinal) ||
                arch == CpuArch.X86 && x.Key.StartsWith("wow64/", StringComparison.Ordinal))
            .Select(x => new PhoenixRuntimeFile(x.Key, BoundedInstallPaths.Resolve(root, x.Key), x.Value)).ToArray();
    }

    public static bool VerifyCached(string root, PhoenixReleasePayload release)
    {
        if (release.RuntimePath is null) return release.RuntimeSha256 is null;
        try
        {
            var files = release.RuntimeSha256!;
            if (files is null || files.Count is < 1 or > MaxFiles || files.Values.Any(x => x is not { Length: 64 } || !x.All(Uri.IsHexDigit)) ||
                !MatchingPath(release.RuntimePath, files)) return false;
            var directory = BoundedInstallPaths.Resolve(root, release.RuntimePath);
            if (Identity(HashTree(directory, CancellationToken.None)) != Identity(files)) return false;
            ValidateComponents(directory, files);
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or JsonException or KeyNotFoundException or InvalidOperationException)
        { return false; }
    }

    public static IEnumerable<PreflightFinding> CheckRequested(GameCandidate game, PayloadInfo payload, PhoenixSettings settings)
    {
        var mode = settings.CompatibilityExecutionMode;
        if (!PhoenixConfiguration.ExecutionModes.Contains(mode))
        { yield return new("runtime-mode", PreflightSeverity.Error, "Unknown Phoenix execution mechanism."); yield break; }
        var plan = PackagedPhoenixPlan.For(game, payload);
        if (mode != "auto" && plan is null)
        { yield return new("runtime-mode", PreflightSeverity.Error, "This execution mechanism requires the packaged Phoenix graphics bridge."); yield break; }
        if (plan is null) yield break;
        var files = For(payload, game.Arch);
        string? error = null;
        if (mode == "in_process" && (game.Arch != CpuArch.X64 || game.Api != GraphicsApi.DirectX12 || !plan.SupportsInProcessBackbuffer ||
            settings.Runner is not ("native" or "nvidia")))
            error = "Same-device internal execution requires a full x64 DX12 addon and the native renderer. Use automatic or a supported alternate mechanism.";
        if (mode == "portable")
        {
            var prefix = game.Arch == CpuArch.X86 ? "win-x86/" : "win-x64/";
            if (!files.Any(x => x.RelativePath == prefix + "vulkan/phoenix_vulkan.dll"))
                error = "Internal portable execution needs the matching PhoenixRuntime/" + prefix + "Vulkan provider and shaders. Update or import the complete runtime package.";
            else if (PhoenixConfiguration.RequiresNativeNr(settings))
                error = "The portable renderer cannot run NVIDIA DLSS5 comparison. Select the helper or a supported native mechanism.";
        }
        if (mode is "wow64_library" or "wow64_driver")
        {
            if (game.Arch != CpuArch.X86 || settings.Runner is not ("native" or "nvidia"))
                error = "The native x86 mechanisms require an x86 game and Native RTX execution.";
            else error = NativeProfileError(files);
            if (error is null && mode == "wow64_driver")
            {
                if (settings.ModelId != "original" || PhoenixConfiguration.RequiresNativeNr(settings))
                    error = "The x86 graph/native-driver mechanism requires Phoenix DLSS5 Original; use the x64-library mechanism for other models or NVIDIA DLSS5.";
                using var stream = File.OpenRead(game.ExePath);
                using var pe = new System.Reflection.PortableExecutable.PEReader(stream);
                if ((pe.PEHeaders.CoffHeader.Characteristics & System.Reflection.PortableExecutable.Characteristics.LargeAddressAware) == 0)
                    error = "The native x86 graph host requires an already large-address-aware executable. The installer never patches game executables; use the helper or x64-library mechanism.";
            }
        }
        if (error is not null) yield return new("runtime-unavailable", PreflightSeverity.Error, error);
        else if (mode == "auto" && game.Arch == CpuArch.X86 && NativeProfileError(files) is { } warning)
            yield return new("runtime-optional", PreflightSeverity.Information, warning);
    }

    public static string? NativeProfileError(IReadOnlyList<PhoenixRuntimeFile> files)
    {
        var bootstrap = files.FirstOrDefault(x => x.RelativePath == "wow64/bootstrap.json");
        if (bootstrap is null) return "Native x86 runtime is not installed; the external helper remains available.";
        try
        {
            using var json = JsonDocument.Parse(LocalData.ReadText(bootstrap.SourcePath, 65536));
            var profile = json.RootElement;
            var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            var system = Path.Combine(windows, Environment.Is64BitProcess ? "System32" : "Sysnative");
            if (!Hash(Path.Combine(system, "ntdll.dll")).Equals(profile.GetProperty("ntdll_sha256").GetString(), StringComparison.OrdinalIgnoreCase))
                return "Native x86 runtime requires a different Windows image; use the helper or a matching runtime package.";
            var driver = profile.GetProperty("driver_directory").GetString()!;
            if (Path.GetFileName(driver) != driver) throw new InvalidDataException("Unsafe driver profile path.");
            var cuda = BoundedInstallPaths.Resolve(Path.Combine(system, "DriverStore", "FileRepository"), driver + "/nvcuda64.dll");
            if (!Hash(cuda).Equals(profile.GetProperty("cuda_sha256").GetString(), StringComparison.OrdinalIgnoreCase))
                return "Native x86 runtime requires a different NVIDIA driver; use the helper or a matching runtime package.";
            return null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or JsonException or KeyNotFoundException or InvalidOperationException)
        { return "Native x86 runtime profile cannot be verified: " + error.Message; }
    }

    static void ValidateComponents(string root, IReadOnlyDictionary<string, string> files)
    {
        void Require(string relative, CpuArch? arch = null)
        {
            if (!files.ContainsKey(relative)) throw new InvalidDataException("Phoenix runtime is missing: " + relative);
            if (arch is not null && !PayloadScanner.IsPeDll(BoundedInstallPaths.Resolve(root, relative), arch.Value))
                throw new InvalidDataException("Phoenix runtime has the wrong DLL architecture: " + relative);
        }
        foreach (var section in Sections.Where(s => files.Keys.Any(x => x.StartsWith(s + "/", StringComparison.Ordinal))))
        {
            if (section != "wow64")
            {
                var arch = section == "win-x86" ? CpuArch.X86 : CpuArch.X64;
                Require(section + "/phoenix_runtime_cpp.dll", arch);
                Require(section + "/phoenix_native_cpp.json");
                using var config = JsonDocument.Parse(LocalData.ReadText(BoundedInstallPaths.Resolve(root, section + "/phoenix_native_cpp.json"), 65536));
                if (config.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Invalid portable runtime configuration.");
                Require(section + "/phoenix_cpu_cpp.dll", arch);
                if (files.ContainsKey(section + "/vulkan/phoenix_vulkan.dll"))
                {
                    Require(section + "/phoenix_vulkan_cpp.dll", arch);
                    Require(section + "/vulkan/phoenix_vulkan.dll", arch);
                    if (!files.Keys.Any(x => x.StartsWith(section + "/vulkan/", StringComparison.Ordinal) && x.EndsWith(".spv")))
                        throw new InvalidDataException("Vulkan runtime shaders are missing.");
                }
                foreach (var dll in files.Keys.Where(x => x.StartsWith(section + "/", StringComparison.Ordinal) && x.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)))
                    Require(dll, arch);
                continue;
            }
            Require("wow64/bootstrap.json");
            foreach (var dll in new[] { "phoenix_bootstrap64.dll", "phoenix_driver64.dll", "phoenix-nvngx.dll", "phoenix_native.dll", "wow64ext.dll" })
                Require("wow64/" + dll, dll == "wow64ext.dll" ? CpuArch.X86 : CpuArch.X64);
            using var bootstrap = JsonDocument.Parse(LocalData.ReadText(BoundedInstallPaths.Resolve(root, "wow64/bootstrap.json"), 65536));
            if (bootstrap.RootElement.GetProperty("schema").GetString() != "phoenix-wow64-bootstrap/2")
                throw new InvalidDataException("Unsupported native x86 bootstrap; import matching addon/runtime builds.");
            foreach (var dll in new[] { "phoenix_bootstrap64.dll", "phoenix_driver64.dll", "phoenix-nvngx.dll", "phoenix_native.dll", "wow64ext.dll" })
                if (!bootstrap.RootElement.GetProperty("files").TryGetProperty(dll, out _))
                    throw new InvalidDataException("Native x86 bootstrap omits a required DLL fingerprint.");
            foreach (var item in bootstrap.RootElement.GetProperty("files").EnumerateObject())
            {
                Require("wow64/" + item.Name);
                if (!files["wow64/" + item.Name].Equals(item.Value.GetString(), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Native x86 bootstrap DLL fingerprint mismatch.");
            }
            Require("wow64/native/native_manifest.json");
            using var manifest = JsonDocument.Parse(LocalData.ReadText(BoundedInstallPaths.Resolve(root, "wow64/native/native_manifest.json")));
            var graph = manifest.RootElement;
            if (graph.GetProperty("schema").GetString() != "phoenix-native-generated-build/1" || !graph.GetProperty("passed").GetBoolean() ||
                graph.GetProperty("model_weights_included").GetBoolean() || graph.GetProperty("kernels").GetArrayLength() != 41)
                throw new InvalidDataException("Incomplete model-free native x86 kernel bundle.");
            Require("wow64/native/native_graph.json");
            if (!files["wow64/native/native_graph.json"].Equals(graph.GetProperty("native_graph_sha256").GetString(), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Native graph fingerprint mismatch.");
            foreach (var kernel in graph.GetProperty("kernels").EnumerateArray())
            {
                var binary = "wow64/native/" + kernel.GetProperty("binary").GetString();
                Require(binary);
                if (!files[binary].Equals(kernel.GetProperty("binary_sha256").GetString(), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Native kernel fingerprint mismatch.");
            }
        }
    }
}
