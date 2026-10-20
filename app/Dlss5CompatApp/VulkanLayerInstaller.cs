using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace Dlss5CompatApp;

public sealed record VulkanLayerInstallation(string GamePath, string ManifestPath, string LibraryPath, string RestoreStatePath, bool OwnsLayer, bool ReferenceAdded = true)
{
    public IReadOnlyDictionary<string, string> LaunchEnvironment { get; init; } = new Dictionary<string, string>();
    public string LaunchManifestJson { get; init; } = "";
}

/// <summary>Explicit, per-user architecture-matched Vulkan layer setup. Separate from the game-folder recovery journal.</summary>
public sealed class VulkanLayerInstaller
{
    public const string RegistrySubKey = @"Software\Khronos\Vulkan\ImplicitLayers";
    public const string ManifestName = "ReShade64_Dlss5Compat.json";
    public const string LibraryName = "ReShade64_Dlss5Compat.dll";
    public const string StateName = "Dlss5CompatVulkan-state.json";
    const int MaxDllBytes = 64 * 1024 * 1024;
    const int MaxTextBytes = 256 * 1024;
    static readonly UTF8Encoding Utf8 = new(false, true);
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true, MaxDepth = 12, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    readonly IVulkanLayerFiles files;
    readonly IVulkanLayerRegistry registry;
    readonly string localRoot, commonRoot, statePath;
    readonly bool x86;
    string Architecture => x86 ? "32" : "64";
    string OwnedManifestName => x86 ? "ReShade32_Dlss5Compat.json" : ManifestName;
    string OwnedLibraryName => x86 ? "ReShade32_Dlss5Compat.dll" : LibraryName;

    public VulkanLayerInstaller(bool x86 = false) : this(new VulkanLayerFiles(), new VulkanLayerRegistry(x86),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ReShade"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ReShade"), x86) { }

    internal VulkanLayerInstaller(IVulkanLayerFiles files, IVulkanLayerRegistry registry, string localRoot, string commonRoot, bool x86 = false)
    {
        this.x86 = x86;
        this.files = files;
        this.registry = registry;
        this.localRoot = Normalize(localRoot);
        this.commonRoot = Normalize(commonRoot);
        if (Same(this.localRoot, this.commonRoot)) throw new ArgumentException("Layer roots must be distinct.");
        statePath = Path.Combine(this.localRoot, x86 ? "Dlss5CompatVulkan32-state.json" : StateName);
    }

    /// <summary>Call only after explicit consent to shared per-user registration. Game headers are bounded to 64 KiB.</summary>
    public Task<VulkanLayerInstallation> InstallAsync(string gamePath, string reShade64Source, CancellationToken cancellationToken = default,
        Action<VulkanLayerInstallation>? beforeCommit = null)
        => Task.Run(() => Install(gamePath, reShade64Source, cancellationToken, beforeCommit), cancellationToken);

    /// <summary>Detach one tracked game; remove only owned layer files/registration after its last reference. No game-folder restore.</summary>
    public Task<bool> RestoreAsync(string gamePath, CancellationToken cancellationToken = default)
        => Task.Run(() => Restore(gamePath, cancellationToken), cancellationToken);

    VulkanLayerInstallation Install(string gamePath, string sourcePath, CancellationToken token, Action<VulkanLayerInstallation>? beforeCommit)
    {
        token.ThrowIfCancellationRequested();
        if (!files.Is64BitOperatingSystem) throw new PlatformNotSupportedException("The Vulkan feeder requires 64-bit Windows.");
        var game = Normalize(gamePath);
        var source = Normalize(sourcePath);
        if (!Path.GetExtension(game).Equals(".exe", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Select the actual x64 game executable.");
        ValidatePath(game, false);
        ValidatePath(source, false);
        var executable = files.ReadPeHeaders(game, 64 * 1024);
        ValidatePe(executable.Headers, dll: false, executable.FileLength);
        var library = files.ReadBounded(source, MaxDllBytes);
        ValidatePe(library, dll: true);
        ValidatePath(statePath, false);
        using var lease = files.Lock(localRoot);
        var state = ReadState();
        var enabled = registry.EnabledReShade64Manifests().Select(Normalize)
            .Where(path => !Same(path, Path.Combine(localRoot, OwnedManifestName)) || Exists(path)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var launchEnvironment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (enabled.Length > 1) enabled = SelectLayerForLaunch(enabled, launchEnvironment);
        if (state is null)
        {
            state = new LayerState();
            if (enabled.Length == 1)
            {
                if (Same(enabled[0], Path.Combine(localRoot, "ReShade" + Architecture + ".json"))) state.UseCommonRoot = false;
                else if (Same(enabled[0], Path.Combine(commonRoot, "ReShade" + Architecture + ".json")))
                {
                    _ = ReadRecognizedLayer(enabled[0]);
                    _ = UpdateApps(ReadOptional(Path.Combine(commonRoot, "ReShadeApps.ini"), MaxTextBytes), game,
                        add: true, out var alreadyAllowed);
                    // A system installation may be read-only to this user. Reuse
                    // it only when it already allows the game; new registrations
                    // belong to our per-user layer, never its protected allow-list.
                    state.UseCommonRoot = alreadyAllowed;
                    state.OwnsLayer = !alreadyAllowed;
                }
                else if (Same(enabled[0], Path.Combine(localRoot, OwnedManifestName)))
                {
                    // Older installer releases registered this manifest against
                    // the official local DLL. Adopt it without claiming its bytes.
                    _ = ReadRecognizedLayer(enabled[0]);
                    state.LegacyLocalManifest = true;
                }
                else throw new InvalidOperationException("An existing ReShade layer is outside the supported official layout; it will not be replaced or disabled.");
            }
            else
            {
                state.OwnsLayer = true;
            }
            if (state.OwnsLayer)
            {
                foreach (var path in new[] { LayerManifest(state), LayerLibrary(state) })
                    if (Exists(path)) throw new IOException("Untracked Vulkan layer files will not be overwritten: " + path);
                state.PreviousRegistration = registry.ReadCurrentUser64(LayerManifest(state));
                state.LibrarySha256 = Hash(library);
            }
        }
        else
        {
            if (state.Pending) throw new InvalidOperationException("An interrupted Vulkan layer operation needs Restore before another installation.");
            if (enabled.Length != 1 || !Same(enabled[0], LayerManifest(state)))
                throw new IOException("Vulkan layer registration changed outside this installer; no files were changed.");
            if (state.OwnsLayer && !Hash(library).Equals(state.LibrarySha256, StringComparison.OrdinalIgnoreCase))
                throw new IOException("A different ReShade build cannot replace a shared layer while games still reference it. Restore its references first.");
        }
        var manifest = LayerManifest(state);
        var dll = LayerLibrary(state);
        var apps = AppsPath(state);
        foreach (var path in new[] { manifest, dll, apps, statePath }) ValidatePath(path, false);
        if (!state.OwnsLayer || Exists(manifest) || Exists(dll)) VerifyLayer(state, allowMissing: false);
        var beforeApps = ReadOptional(apps, MaxTextBytes);
        var afterApps = UpdateApps(beforeApps, game, add: true, out var alreadyListed);
        if (state.Games.Count == 0) state.AppsFileCreated = beforeApps is null;
        var existing = state.Games.SingleOrDefault(g => Same(g.Path, game));
        if (existing is not null && !alreadyListed) throw new IOException("The game allow-list changed outside this installer. Restore before reinstalling.");
        if (existing is null)
        {
            if (state.Games.Count >= 512) throw new InvalidDataException("Too many Vulkan layer references.");
            state.Games.Add(new GameReference { Path = game, AddedToApps = !alreadyListed });
        }
        token.ThrowIfCancellationRequested();
        var launchManifest = System.Text.Json.Nodes.JsonNode.Parse(state.OwnsLayer ? ManifestBytes() : ReadOptional(manifest, MaxTextBytes)!)!;
        launchManifest["layer"]!["library_path"] = dll;
        var installation = new VulkanLayerInstallation(game, manifest, dll, statePath, state.OwnsLayer, ReferenceAdded: existing is null)
        {
            LaunchEnvironment = launchEnvironment,
            LaunchManifestJson = launchManifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true })
        };
        beforeCommit?.Invoke(installation);
        Transact(state, state.OwnsLayer ? new[] { dll, manifest, apps, statePath } : new[] { apps, statePath }, apps, beforeApps, changes =>
        {
            state.Pending = true;
            SaveState(state, changes);
            if (state.OwnsLayer)
            {
                if (!Exists(dll)) changes.Write(dll, library);
                if (!Exists(manifest)) changes.Write(manifest, ManifestBytes());
            }
            if (!alreadyListed) changes.Write(apps, afterApps);
            token.ThrowIfCancellationRequested();
            if (state.OwnsLayer) registry.WriteCurrentUser64(manifest, 0);
            state.Pending = false;
            SaveState(state, changes);
        });
        return installation;
    }

    string[] SelectLayerForLaunch(string[] enabled, Dictionary<string, string> environment)
    {
        // Retire duplicate ReShade layers only for this launched game. Registry
        // entries and other games' allow-lists stay intact. Unknown layers fail closed.
        if (enabled.Any(path => !RecognizedLayerPath(path)))
            throw new InvalidOperationException("An existing ReShade layer is outside the recognized layouts; it will not be disabled.");
        var primary = new[] { Path.Combine(localRoot, "ReShade" + Architecture + ".json"),
            Path.Combine(localRoot, OwnedManifestName), Path.Combine(commonRoot, "ReShade" + Architecture + ".json") }
            .FirstOrDefault(path => enabled.Any(item => Same(path, item)));
        if (primary is null) throw new InvalidOperationException("Multiple ReShade Vulkan layers are enabled without a recognized official layer.");
        var selected = ReadRecognizedLayer(primary);
        foreach (var path in enabled.Where(path => !Same(path, primary)))
        {
            var other = ReadRecognizedLayer(path);
            if (other.Count == 0)
                throw new InvalidOperationException("Duplicate ReShade layers cannot be isolated with their declared per-process disable controls.");
            // Shared disable keys must remain unset or they disable the selected
            // layer too. The game-local manifest search priority resolves these
            // identical layer names before the loader checks disable controls.
            foreach (var pair in other.Where(pair => !selected.ContainsKey(pair.Key))) environment[pair.Key] = pair.Value;
        }
        return [primary];
    }

    Dictionary<string, string> ReadRecognizedLayer(string path)
    {
        var root = Path.GetDirectoryName(path)!;
        if (!RecognizedLayerPath(path))
            throw new InvalidOperationException("An existing ReShade layer is outside the recognized layouts; it will not be disabled.");
        var bytes = ReadOptional(path, MaxTextBytes) ?? throw new IOException("Registered Vulkan layer manifest is missing.");
        using var document = JsonDocument.Parse(bytes);
        RejectDuplicateProperties(document.RootElement);
        var layer = document.RootElement.GetProperty("layer");
        var library = Normalize(Path.GetFullPath(Path.Combine(root, layer.GetProperty("library_path").GetString() ?? "")));
        if (layer.GetProperty("name").GetString() != "VK_LAYER_reshade" || layer.GetProperty("type").GetString() != "GLOBAL" ||
            !new[] { Path.Combine(root, "ReShade" + Architecture + ".dll"), Path.Combine(root, OwnedLibraryName) }.Any(item => Same(item, library)))
            throw new InvalidDataException("Duplicate Vulkan manifest is not a recognized ReShade layer.");
        ValidatePe(ReadOptional(library, MaxDllBytes) ?? throw new IOException("ReShade layer DLL is missing."), dll: true);
        var controls = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (layer.TryGetProperty("disable_environment", out var disable))
            foreach (var pair in disable.EnumerateObject())
            {
                var value = pair.Value.GetString() ?? "";
                if (!System.Text.RegularExpressions.Regex.IsMatch(pair.Name, @"\A[A-Za-z_][A-Za-z0-9_]{0,127}\z") ||
                    !System.Text.RegularExpressions.Regex.IsMatch(value, @"\A[A-Za-z0-9_]{1,128}\z"))
                    throw new InvalidDataException("Unsafe Vulkan layer environment control.");
                controls.Add(pair.Name, value);
            }
        return controls;
    }

    bool RecognizedLayerPath(string path) =>
        (Same(Path.GetDirectoryName(path)!, localRoot) || Same(Path.GetDirectoryName(path)!, commonRoot)) &&
        new[] { "ReShade" + Architecture + ".json", OwnedManifestName }
            .Contains(Path.GetFileName(path), StringComparer.OrdinalIgnoreCase);

    bool Restore(string gamePath, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var game = Normalize(gamePath);
        ValidatePath(statePath, false);
        using var lease = files.Lock(localRoot);
        var state = ReadState();
        if (state is null) return false;
        var reference = state.Games.SingleOrDefault(g => Same(g.Path, game));
        if (reference is null) return false;
        var apps = AppsPath(state);
        var manifest = LayerManifest(state);
        var dll = LayerLibrary(state);
        foreach (var path in new[] { apps, manifest, dll, statePath }) ValidatePath(path, false);
        VerifyLayer(state, allowMissing: state.Pending);
        var beforeApps = ReadOptional(apps, MaxTextBytes);
        var afterApps = reference.AddedToApps ? UpdateApps(beforeApps, game, add: false, out _) : beforeApps;
        var last = state.Games.Count == 1;
        if (state.OwnsLayer)
        {
            var current = registry.ReadCurrentUser64(manifest);
            if (current != 0 && !(state.Pending && current == state.PreviousRegistration))
                throw new IOException("The owned Vulkan registry value changed; restore refuses to overwrite it.");
        }
        token.ThrowIfCancellationRequested();
        Transact(state, state.OwnsLayer ? new[] { dll, manifest, apps, statePath } : new[] { apps, statePath }, apps, beforeApps, changes =>
        {
            state.Pending = true;
            SaveState(state, changes);
            if (last && state.OwnsLayer) registry.WriteCurrentUser64(manifest, state.PreviousRegistration);
            if (last && state.AppsFileCreated && afterApps is not null && afterApps.AsSpan().SequenceEqual(Utf8.GetBytes("Apps=\r\n"))) changes.Write(apps, null);
            else if (reference.AddedToApps) changes.Write(apps, afterApps);
            if (last)
            {
                if (state.OwnsLayer) { changes.Write(manifest, null); changes.Write(dll, null); }
                changes.Write(statePath, null);
            }
            else
            {
                state.Games.Remove(reference);
                state.Pending = false;
                SaveState(state, changes);
            }
        });
        return true;
    }

    void Transact(LayerState state, string[] paths, string appsPath, byte[]? expectedApps, Action<FileChanges> apply)
    {
        var snapshots = paths.ToDictionary(p => p, p => ReadOptional(p, Path.GetExtension(p).Equals(".dll", StringComparison.OrdinalIgnoreCase) ? MaxDllBytes : MaxTextBytes));
        if (!SameBytes(snapshots[appsPath], expectedApps)) throw new IOException("ReShadeApps.ini changed outside this installer; retry after the other installer finishes.");
        var changes = new FileChanges(this, snapshots);
        var manifest = LayerManifest(state);
        var registration = state.OwnsLayer ? registry.ReadCurrentUser64(manifest) : null;
        try { apply(changes); }
        catch (Exception failure)
        {
            var errors = new List<Exception> { failure };
            var canRestoreFiles = true;
            if (state.OwnsLayer && registration != 0)
            {
                try
                {
                    if (registry.ReadCurrentUser64(manifest) == 0) registry.WriteCurrentUser64(manifest, 1);
                }
                catch (Exception ex) { errors.Add(ex); canRestoreFiles = false; }
            }
            // Restore files before re-enabling the previous registration. Never delete a registry key or directory.
            if (canRestoreFiles)
                foreach (var path in snapshots.Keys.Where(path => !Same(path, statePath)).Reverse())
                    try { changes.Rollback(path); }
                    catch (Exception ex) { errors.Add(ex); }
            if (state.OwnsLayer && canRestoreFiles)
                try { registry.WriteCurrentUser64(manifest, registration); }
                catch (Exception ex) { errors.Add(ex); }
            try
            {
                if (errors.Count == 1)
                {
                    changes.Rollback(statePath);
                }
                else { state.Pending = true; SaveState(state, changes); }
            }
            catch (Exception ex) { errors.Add(ex); }
            if (errors.Count > 1) throw new AggregateException("Vulkan layer rollback was incomplete; check the retained restore state before retrying.", errors);
            throw;
        }
    }

    LayerState? ReadState()
    {
        var bytes = ReadOptional(statePath, MaxTextBytes);
        if (bytes is null) return null;
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 12 });
        RejectDuplicateProperties(document.RootElement);
        var state = JsonSerializer.Deserialize<LayerState>(bytes, Json) ?? throw new InvalidDataException("Invalid Vulkan restore state.");
        if (state.Version != 1 || state.Games is null || state.Games.Count is < 1 or > 512 || (state.OwnsLayer && state.UseCommonRoot) ||
            (state.LegacyLocalManifest && (state.OwnsLayer || state.UseCommonRoot)) ||
            (state.OwnsLayer && (state.LibrarySha256 is not { Length: 64 } || !state.LibrarySha256.All(Uri.IsHexDigit))))
            throw new InvalidDataException("Invalid Vulkan restore state identity or limits.");
        var games = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var reference in state.Games)
            if (reference is null || !games.Add(Normalize(reference.Path)) || !Path.GetExtension(reference.Path).Equals(".exe", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Invalid or duplicate Vulkan game reference.");
        return state;
    }

    void SaveState(LayerState state, FileChanges changes)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(state, Json);
        if (bytes.Length > MaxTextBytes) throw new InvalidDataException("Vulkan restore state exceeds its limit.");
        ValidatePath(statePath, false);
        changes.Write(statePath, bytes);
    }

    void VerifyLayer(LayerState state, bool allowMissing)
    {
        var manifest = ReadOptional(LayerManifest(state), MaxTextBytes);
        var library = ReadOptional(LayerLibrary(state), MaxDllBytes);
        if ((!allowMissing && (manifest is null || library is null)) || (library is not null && state.OwnsLayer && Hash(library) != state.LibrarySha256))
            throw new IOException("The Vulkan layer is missing or changed; restore will not remove unrecognized files.");
        if (library is not null) ValidatePe(library, dll: true);
        if (manifest is null) return;
        if (state.OwnsLayer)
        {
            if (!manifest.AsSpan().SequenceEqual(ManifestBytes())) throw new IOException("The owned Vulkan manifest changed.");
            return;
        }
        using var json = JsonDocument.Parse(manifest);
        RejectDuplicateProperties(json.RootElement);
        var layer = json.RootElement.GetProperty("layer");
        if (layer.GetProperty("name").GetString() != "VK_LAYER_reshade" || layer.GetProperty("type").GetString() != "GLOBAL" ||
            json.RootElement.GetProperty("file_format_version").GetString() != "1.0.0") throw new InvalidDataException("The existing manifest is not an official ReShade Vulkan layer.");
        var libraryPath = layer.GetProperty("library_path").GetString() ?? "";
        var resolved = Path.GetFullPath(Path.Combine(LayerRoot(state), libraryPath));
        if (!Same(resolved, LayerLibrary(state))) throw new InvalidDataException("Existing ReShade manifest references an unexpected DLL path.");
    }

    byte[]? ReadOptional(string path, int limit) { ValidatePath(path, false); return Exists(path) ? files.ReadBounded(path, limit) : null; }
    bool Exists(string path) => files.Attributes(path) is not null;
    string LayerRoot(LayerState state) => state.UseCommonRoot ? commonRoot : localRoot;
    string LayerManifest(LayerState state) => Path.Combine(LayerRoot(state), state.OwnsLayer || state.LegacyLocalManifest ? OwnedManifestName : "ReShade" + Architecture + ".json");
    string LayerLibrary(LayerState state) => Path.Combine(LayerRoot(state), state.OwnsLayer ? OwnedLibraryName : "ReShade" + Architecture + ".dll");
    string AppsPath(LayerState state) => Path.Combine(LayerRoot(state), "ReShadeApps.ini");

    static bool SameBytes(byte[]? left, byte[]? right) => left is null ? right is null : right is not null && left.AsSpan().SequenceEqual(right);

    sealed class FileChanges(VulkanLayerInstaller owner, Dictionary<string, byte[]?> snapshots)
    {
        readonly Dictionary<string, byte[]?> written = new(StringComparer.OrdinalIgnoreCase);

        public void Write(string path, byte[]? data)
        {
            var expected = written.TryGetValue(path, out var previous) ? previous : snapshots[path];
            owner.ValidatePath(path, false);
            if (!SameBytes(owner.ReadOptional(path, Limit(path)), expected)) throw Conflict(path);
            if (SameBytes(expected, data)) return;
            // Record attempted versions too: a filesystem call can fail after publishing its bytes.
            written[path] = data;
            owner.files.ReplaceIfUnchanged(path, expected, data);
        }

        public void Rollback(string path)
        {
            if (!written.TryGetValue(path, out var ours)) return;
            owner.ValidatePath(path, false);
            var current = owner.ReadOptional(path, Limit(path));
            var original = snapshots[path];
            if (SameBytes(current, original)) { written[path] = original; return; }
            if (!SameBytes(current, ours)) throw Conflict(path);
            owner.files.ReplaceIfUnchanged(path, ours, original);
            written[path] = original;
        }

        static int Limit(string path) => Path.GetExtension(path).Equals(".dll", StringComparison.OrdinalIgnoreCase) ? MaxDllBytes : MaxTextBytes;
        static IOException Conflict(string path) => new("A Vulkan layer file changed outside this transaction; external edits were preserved: " + path);
    }

    void ValidatePath(string path, bool directory)
    {
        files.RequireLocalDrive(path);
        for (string? current = Normalize(path); current is not null; current = Path.GetDirectoryName(current))
        {
            var attributes = files.Attributes(current);
            if (attributes is not { } value) continue;
            if ((value & FileAttributes.ReparsePoint) != 0 || ((value & FileAttributes.Directory) != 0) != (current != path || directory))
                throw new IOException("Unsafe Vulkan layer path: " + current);
        }
    }

    static string Normalize(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || path.StartsWith(@"\\") || path.StartsWith("//") ||
            path.IndexOfAny(['\0', '\r', '\n', ',']) >= 0 || path.Skip(2).Contains(':') ||
            path[Path.GetPathRoot(path)!.Length..].Split(['/', '\\']).Any(p => p is "." or ".." || p.EndsWith('.') || p.EndsWith(' ')))
            throw new ArgumentException("An absolute local path without traversal, ADS, or CSV delimiters is required.", nameof(path));
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }

    static bool Same(string left, string right) => left.Equals(right, StringComparison.OrdinalIgnoreCase);
    static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    void ValidatePe(byte[] bytes, bool dll, long? fileLength = null)
    {
        if (bytes.Length < 64 || BinaryPrimitives.ReadUInt16LittleEndian(bytes) != 0x5A4D) throw new InvalidDataException("A valid x64 PE file is required.");
        var offset = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(0x3c));
        if (offset < 64 || offset > bytes.Length - 26 || BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset)) != 0x4550 ||
            BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset + 4)) != (x86 ? 0x14c : 0x8664) ||
            BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset + 24)) != (x86 ? 0x10b : 0x20b))
            throw new InvalidDataException("The game executable and ReShade DLL must match the selected x86/x64 architecture.");
        var characteristics = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset + 22));
        var optionalSize = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset + 20));
        var sections = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset + 6));
        if ((characteristics & 2) == 0 || ((characteristics & 0x2000) != 0) != dll || optionalSize < 112 || sections == 0 ||
            offset + 24L + optionalSize + sections * 40L > bytes.Length) throw new InvalidDataException("Invalid x64 executable/DLL headers.");
        for (var section = offset + 24 + optionalSize; section < offset + 24 + optionalSize + sections * 40; section += 40)
        {
            var size = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(section + 16));
            var start = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(section + 20));
            if (size > 0 && (start == 0 || start + (long)size > (fileLength ?? bytes.Length))) throw new InvalidDataException("Truncated x64 PE section.");
        }
    }

    static byte[]? UpdateApps(byte[]? original, string game, bool add, out bool alreadyListed)
    {
        var text = original is null ? "" : Utf8.GetString(original);
        var bom = text.StartsWith('\uFEFF');
        if (bom) text = text[1..];
        var offset = 0;
        var valueStart = -1;
        var valueLength = 0;
        foreach (var line in text.Split('\n'))
        {
            var content = line.TrimEnd('\r');
            if (content.TrimStart().StartsWith('[')) break;
            var equals = content.IndexOf('=');
            if (equals >= 0 && content[..equals].Trim().Equals("Apps", StringComparison.OrdinalIgnoreCase))
            {
                if (valueStart >= 0) throw new InvalidDataException("Duplicate global Apps entries in ReShadeApps.ini.");
                valueStart = offset + equals + 1;
                valueLength = content.Length - equals - 1;
            }
            offset += line.Length + 1;
        }
        var values = valueStart < 0 ? Array.Empty<string>() : text.Substring(valueStart, valueLength).Split(',');
        var matches = values.Count(v => Same(v.Trim(), game));
        alreadyListed = matches > 0;
        if (matches > 1) throw new InvalidDataException("Duplicate game entries in ReShadeApps.ini.");
        if (add == alreadyListed) return original;
        var updated = add ? string.Join(',', values.Where(v => v.Length > 0).Append(game)) : string.Join(',', values.Where(v => !Same(v.Trim(), game)));
        text = valueStart < 0 ? "Apps=" + updated + "\r\n" + text : text.Remove(valueStart, valueLength).Insert(valueStart, updated);
        var bytes = Utf8.GetBytes((bom ? "\uFEFF" : "") + text);
        if (bytes.Length > MaxTextBytes) throw new InvalidDataException("ReShadeApps.ini exceeds its size limit.");
        return bytes;
    }

    byte[] ManifestBytes() => JsonSerializer.SerializeToUtf8Bytes(new
    {
        file_format_version = "1.0.0",
        layer = new
        {
            name = "VK_LAYER_reshade", type = "GLOBAL", library_path = ".\\" + OwnedLibraryName,
            api_version = "1.3.268", implementation_version = "1", description = "crosire's ReShade post-processing injector for " + Architecture + "-bit",
            device_extensions = new[] { new { name = "VK_EXT_tooling_info", spec_version = "1", entrypoints = new[] { "vkGetPhysicalDeviceToolPropertiesEXT" } } },
            disable_environment = new Dictionary<string, string> { ["DISABLE_VK_LAYER_reshade_1"] = "1" }
        }
    }, Json);

    static void RejectDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new InvalidDataException("Duplicate Vulkan state/manifest property.");
                RejectDuplicateProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array) foreach (var item in element.EnumerateArray()) RejectDuplicateProperties(item);
    }

    sealed class LayerState
    {
        public int Version { get; set; } = 1;
        public bool OwnsLayer { get; set; }
        public bool UseCommonRoot { get; set; }
        public bool LegacyLocalManifest { get; set; }
        public string? LibrarySha256 { get; set; }
        public int? PreviousRegistration { get; set; }
        public bool Pending { get; set; }
        public bool AppsFileCreated { get; set; }
        public List<GameReference> Games { get; set; } = [];
    }
    sealed class GameReference { public string Path { get; set; } = ""; public bool AddedToApps { get; set; } }
}

internal interface IVulkanLayerFiles
{
    bool Is64BitOperatingSystem { get; }
    void RequireLocalDrive(string path);
    FileAttributes? Attributes(string path);
    byte[] ReadBounded(string path, int limit);
    VulkanPeHeaders ReadPeHeaders(string path, int limit);
    void ReplaceIfUnchanged(string path, byte[]? expected, byte[]? data);
    IDisposable Lock(string identity);
}

internal sealed record VulkanPeHeaders(byte[] Headers, long FileLength);

internal interface IVulkanLayerRegistry
{
    IReadOnlyList<string> EnabledReShade64Manifests();
    int? ReadCurrentUser64(string manifestPath);
    void WriteCurrentUser64(string manifestPath, int? value);
}

sealed class VulkanLayerFiles(Action<string>? afterVerification = null) : IVulkanLayerFiles
{
    public bool Is64BitOperatingSystem => Environment.Is64BitOperatingSystem;
    public void RequireLocalDrive(string path)
    {
        if (new DriveInfo(Path.GetPathRoot(path)!).DriveType == DriveType.Network) throw new IOException("Network layer paths are unsupported.");
    }
    public FileAttributes? Attributes(string path)
    {
        try { return File.GetAttributes(path); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }
    public byte[] ReadBounded(string path, int limit)
    {
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length > limit) throw new InvalidDataException("Vulkan layer file exceeds its read limit.");
        using var output = new MemoryStream();
        var buffer = new byte[128 * 1024];
        int read;
        while ((read = input.Read(buffer)) != 0)
        {
            if (output.Length + read > limit) throw new InvalidDataException("Vulkan layer file exceeds its read limit.");
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }
    public VulkanPeHeaders ReadPeHeaders(string path, int limit)
    {
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var headers = new byte[(int)Math.Min(input.Length, limit)];
        input.ReadExactly(headers);
        return new(headers, input.Length);
    }
    public void ReplaceIfUnchanged(string path, byte[]? expected, byte[]? data)
    {
        LocalPaths.RequireNoLinks(path);
        using var parents = GuardParents(path, create: expected is null && data is not null);
        var create = expected is null && data is not null;
        var access = GenericRead | (data is not null ? GenericWrite : 0) | (create || data is null ? DeleteAccess : 0);
        using var handle = CreateFileW(path, access, 0, IntPtr.Zero, create ? CreateNew : OpenExisting,
            OpenReparsePoint | (data is not null ? WriteThrough : 0), IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            if (expected is null && data is null && error is 2 or 3) return;
            throw NativeFailure("Cannot exclusively open Vulkan layer destination", path, error);
        }
        var attributes = File.GetAttributes(handle);
        if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            throw new IOException("Unsafe Vulkan layer destination: " + path);
        using var stream = new FileStream(handle, data is null ? FileAccess.Read : FileAccess.ReadWrite, 128 * 1024);
        if (!create)
        {
            if (expected is null || stream.Length != expected.Length)
                throw new IOException("Vulkan layer destination changed before exclusive access: " + path);
            var current = new byte[expected.Length];
            stream.ReadExactly(current);
            if (!current.AsSpan().SequenceEqual(expected))
                throw new IOException("Vulkan layer destination changed before exclusive access: " + path);
        }
        var writing = false;
        try
        {
            // Verification and mutation share this non-shared handle; rename/delete cannot slip between them.
            afterVerification?.Invoke(path);
            if (data is null) { DeleteByHandle(handle, path); return; }
            writing = true;
            WriteAndFlush(stream, data);
        }
        catch (Exception failure)
        {
            try
            {
                if (create) DeleteByHandle(handle, path);
                else if (writing) WriteAndFlush(stream, expected!);
            }
            catch (Exception recovery) { throw new AggregateException("Exclusive Vulkan file update and its recovery both failed: " + path, failure, recovery); }
            throw;
        }
    }

    static void WriteAndFlush(FileStream stream, byte[] data)
    {
        stream.Position = 0;
        stream.Write(data);
        stream.SetLength(data.Length);
        stream.Flush(flushToDisk: true);
    }

    static IDisposable GuardParents(string path, bool create)
    {
        var handles = new List<SafeFileHandle>();
        var directories = new Stack<string>();
        for (var directory = Path.GetDirectoryName(path); directory is not null; directory = Path.GetDirectoryName(directory))
            directories.Push(directory);
        try
        {
            foreach (var directory in directories)
            {
                var handle = CreateFileW(directory, 0, 3, IntPtr.Zero, OpenExisting, OpenReparsePoint | BackupSemantics, IntPtr.Zero);
                if (handle.IsInvalid)
                {
                    var error = Marshal.GetLastWin32Error();
                    handle.Dispose();
                    if (!create || error is not (2 or 3)) throw NativeFailure("Cannot guard Vulkan layer directory", directory, error);
                    Directory.CreateDirectory(directory);
                    handle = CreateFileW(directory, 0, 3, IntPtr.Zero, OpenExisting, OpenReparsePoint | BackupSemantics, IntPtr.Zero);
                    if (handle.IsInvalid)
                    {
                        error = Marshal.GetLastWin32Error();
                        handle.Dispose();
                        throw NativeFailure("Cannot guard Vulkan layer directory", directory, error);
                    }
                }
                handles.Add(handle);
                var attributes = File.GetAttributes(handle);
                if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != FileAttributes.Directory)
                    throw new IOException("Unsafe Vulkan layer directory: " + directory);
            }
            return new DirectoryLease(handles);
        }
        catch { foreach (var handle in handles.AsEnumerable().Reverse()) handle.Dispose(); throw; }
    }

    static void DeleteByHandle(SafeFileHandle handle, string path)
    {
        var disposition = new FileDisposition { DeleteFile = 1 };
        if (!SetFileInformationByHandle(handle, 4, ref disposition, 1))
            throw NativeFailure("Cannot delete verified Vulkan layer file", path, Marshal.GetLastWin32Error());
    }

    static IOException NativeFailure(string operation, string path, int error)
        => new(operation + ": " + path, new Win32Exception(error));

    const uint GenericRead = 0x80000000, GenericWrite = 0x40000000, DeleteAccess = 0x10000;
    const uint CreateNew = 1, OpenExisting = 3, OpenReparsePoint = 0x200000, BackupSemantics = 0x2000000, WriteThrough = 0x80000000;
    [StructLayout(LayoutKind.Sequential)]
    struct FileDisposition { public byte DeleteFile; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    static extern SafeFileHandle CreateFileW(string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes,
        uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);
    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool SetFileInformationByHandle(SafeFileHandle file, int informationClass, ref FileDisposition information, uint bufferSize);
    sealed class DirectoryLease(List<SafeFileHandle> handles) : IDisposable
    {
        public void Dispose() { foreach (var handle in handles.AsEnumerable().Reverse()) handle.Dispose(); }
    }
    public IDisposable Lock(string identity)
    {
        var name = "Local\\Dlss5Compat-Vulkan-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity.ToUpperInvariant())));
        var mutex = new Mutex(false, name);
        try
        {
            try { if (!mutex.WaitOne(TimeSpan.FromSeconds(15))) throw new IOException("Another Vulkan layer operation is in progress."); }
            catch (AbandonedMutexException) { }
            return new MutexLease(mutex);
        }
        catch { mutex.Dispose(); throw; }
    }
    sealed class MutexLease(Mutex mutex) : IDisposable { public void Dispose() { mutex.ReleaseMutex(); mutex.Dispose(); } }
}

sealed class VulkanLayerRegistry(bool x86 = false) : IVulkanLayerRegistry
{
    string Key(RegistryHive hive) => x86 && hive == RegistryHive.CurrentUser
        ? @"Software\WOW6432Node\Khronos\Vulkan\ImplicitLayers" : VulkanLayerInstaller.RegistrySubKey;
    RegistryView View(RegistryHive hive) => x86 && hive == RegistryHive.LocalMachine ? RegistryView.Registry32 : RegistryView.Registry64;

    public IReadOnlyList<string> EnabledReShade64Manifests()
    {
        var paths = new List<string>();
        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        {
            using var root = RegistryKey.OpenBaseKey(hive, View(hive));
            using var key = root.OpenSubKey(Key(hive));
            if (key is null) continue;
            var names = key.GetValueNames();
            if (names.Length > 4096) throw new InvalidDataException("Too many Vulkan registry entries.");
            foreach (var name in names)
                if (Path.GetFileName(name).Contains("reshade", StringComparison.OrdinalIgnoreCase) &&
                    key.GetValueKind(name) == RegistryValueKind.DWord && key.GetValue(name) is int value && value == 0) paths.Add(name);
        }
        return paths;
    }
    public int? ReadCurrentUser64(string manifestPath)
    {
        using var root = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
        using var key = root.OpenSubKey(Key(RegistryHive.CurrentUser));
        var value = key?.GetValue(manifestPath);
        if (value is null) return null;
        if (key!.GetValueKind(manifestPath) != RegistryValueKind.DWord || value is not int number)
            throw new InvalidDataException("The Vulkan registry value is not REG_DWORD; it will not be overwritten.");
        return number;
    }
    public void WriteCurrentUser64(string manifestPath, int? value)
    {
        using var root = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
        if (value is { } number)
        {
            using var key = root.CreateSubKey(Key(RegistryHive.CurrentUser), writable: true);
            key.SetValue(manifestPath, number, RegistryValueKind.DWord);
        }
        else
        {
            using var key = root.OpenSubKey(Key(RegistryHive.CurrentUser), writable: true);
            key?.DeleteValue(manifestPath, throwOnMissingValue: false);
        }
    }
}
