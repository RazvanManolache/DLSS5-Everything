using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using Microsoft.Win32;

namespace Dlss5CompatApp;

sealed record StoreInstall(string Store, string Id, string Name, string Root);
sealed record LibraryDiscoveryResult(IReadOnlyList<LibraryGame> Games, IReadOnlyList<string> Warnings,
    IReadOnlyList<StoreInstall> Stores);
sealed class LibraryDiscoveryOptions
{
    // Supplying options makes discovery deterministic: no implicit machine probes.
    public List<string> SteamRoots { get; set; } = [];
    public List<string> EpicManifestDirectories { get; set; } = [];
    public List<StoreInstall> GogGames { get; set; } = [];
    public List<StoreInstall> UbisoftGames { get; set; } = [];
    public List<string> XboxPackageRoots { get; set; } = [];
    public List<string> XboxDriveRoots { get; set; } = [];
    public int MaxManifests { get; set; } = 2048;
    public int MaxScanRoots { get; set; } = 128;
    public int MaxGames { get; set; } = 4096;
    public int MaxManifestBytes { get; set; } = 1024 * 1024;
    public GameScanLimits ScanLimits { get; set; } = new();
    internal void Validate()
    {
        if (MaxManifests is < 1 or > 10000 || MaxScanRoots is < 1 or > 1024 || MaxGames is < 1 or > 10000
            || MaxManifestBytes is < 64 or > 4 * 1024 * 1024 || ScanLimits is null)
            throw new ArgumentOutOfRangeException(nameof(LibraryDiscoveryOptions));
        ScanLimits.Validate();
    }
}

sealed record GameScanLimits(int MaxDepth = 10, int MaxDirectories = 4096, int MaxEntries = 100000,
    long MaxExecutableBytes = 1024L * 1024 * 1024)
{
    internal void Validate()
    {
        if (MaxDepth is < 0 or > 16 || MaxDirectories is < 1 or > 10000 || MaxEntries is < 1 or > 200000
            || MaxExecutableBytes is < 1 or > 1024L * 1024 * 1024) throw new ArgumentOutOfRangeException(nameof(GameScanLimits));
    }
}

static class LibraryDiscovery
{
    public static async Task<LibraryDiscoveryResult> DiscoverAsync(LibraryState state,
        LibraryDiscoveryOptions? options = null, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        cancellationToken.ThrowIfCancellationRequested();
        // Registry and metadata work stays off the UI thread as well as PE inspection.
        return await Task.Run(async () =>
        {
            var warnings = new List<string>();
            options ??= SystemOptions(warnings, cancellationToken);
            options.Validate();
            var stores = DiscoverStores(options, warnings.Add, cancellationToken);
            var games = new Dictionary<string, LibraryGame>(StringComparer.OrdinalIgnoreCase);
            var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var scanCount = 0;
            foreach (var install in stores.Concat(state.ScanRoots.Select(r => new StoreInstall("Manual", "", "", r))))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (games.Count >= options.MaxGames) { warnings.Add("Game limit reached."); break; }
                try
                {
                    var root = LocalPaths.Normalize(install.Root);
                    LocalPaths.RequireNoLinks(root);
                    if (!roots.Add(root)) continue;
                    if (++scanCount > options.MaxScanRoots) { warnings.Add("Scan root limit reached."); break; }
                    if (!Directory.Exists(root)) { warnings.Add("Scan root is unavailable: " + root); continue; }
                    progress?.Report("Scanning " + (install.Name.Length > 0 ? install.Name : root));
                    var found = await GameScanner.ScanAsync(root, progress, cancellationToken, options.ScanLimits, warnings.Add);
                    foreach (var candidate in found)
                    {
                        if (games.Count >= options.MaxGames) break;
                        var named = install.Store == "Manual" ? candidate : candidate with { Root = root, DisplayName = install.Name };
                        games.TryAdd(LocalPaths.Normalize(named.ExePath), new(named, install.Store, RuntimeDiagnostics.HasInstallManifest(named)));
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
                { warnings.Add($"Could not scan {install.Root}: {ex.Message}"); }
            }
            foreach (var exe in state.ManualPaths.Take(options.MaxGames))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (games.Count >= options.MaxGames) { warnings.Add("Game limit reached."); break; }
                try
                {
                    var path = LocalPaths.Normalize(exe);
                    LocalPaths.RequireNoLinks(path);
                    if (games.ContainsKey(path)) continue;
                    var game = GameScanner.ScanSingleExeBounded(path, options.ScanLimits.MaxExecutableBytes);
                    if (game is null) { warnings.Add("Manual executable is unavailable or not recognized: " + path); continue; }
                    games.Add(path, new(game, "Manual", RuntimeDiagnostics.HasInstallManifest(game)));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
                { warnings.Add($"Could not inspect {exe}: {ex.Message}"); }
            }
            return new LibraryDiscoveryResult(games.Values.OrderBy(g => g.DetectedGame.DisplayName, StringComparer.OrdinalIgnoreCase).ToArray(), warnings, stores);
        }, cancellationToken);
    }

    public static IReadOnlyList<StoreInstall> DiscoverStores(LibraryDiscoveryOptions options,
        Action<string>? warning = null, CancellationToken cancellationToken = default)
    {
        options.Validate();
        var found = new Dictionary<string, StoreInstall>(StringComparer.OrdinalIgnoreCase);
        var remaining = options.MaxManifests;
        var limited = false;
        void Warn(string message) => warning?.Invoke(message);
        bool Budget()
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (remaining-- > 0) return true;
            if (!limited) { Warn("Store metadata limit reached."); limited = true; }
            return false;
        }
        void Add(StoreInstall game)
        {
            try
            {
                var root = LocalPaths.Normalize(game.Root);
                LocalPaths.RequireNoLinks(root);
                if (!Directory.Exists(root)) return; // Stale launcher records outlive an uninstall.
                if (found.Count >= options.MaxGames) { Warn("Store game limit reached."); return; }
                if (!string.IsNullOrWhiteSpace(game.Name)) found.TryAdd(root, game with { Root = root });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            { Warn($"Skipped {game.Store} location: {ex.Message}"); }
        }
        void Attempt(Action action, string label)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try { action(); }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or JsonException or XmlException or InvalidOperationException)
            { Warn($"{label}: {ex.Message}"); }
        }
        foreach (var steam in options.SteamRoots.Take(options.MaxScanRoots)) Attempt(() =>
        {
            var baseRoot = LocalPaths.Normalize(steam);
            LocalPaths.RequireNoLinks(baseRoot);
            var libraries = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { baseRoot };
            var folders = Path.Combine(baseRoot, "steamapps", "libraryfolders.vdf");
            if (File.Exists(folders) && Budget())
            {
                var vdf = VdfNode.Parse(LocalData.ReadText(folders, options.MaxManifestBytes));
                foreach (var pair in vdf.Descendants().Where(p => p.Key.Equals("path", StringComparison.OrdinalIgnoreCase)).Take(options.MaxScanRoots))
                    if (pair.Value.Value is { } path) libraries.Add(LocalPaths.Normalize(path));
                // Legacy libraryfolders.vdf used numeric keys directly as paths.
                foreach (var pair in vdf.Descendants().Where(p => int.TryParse(p.Key, out _) && p.Value.Value is not null).Take(options.MaxScanRoots))
                    if (Path.IsPathFullyQualified(pair.Value.Value!)) libraries.Add(LocalPaths.Normalize(pair.Value.Value!));
            }
            foreach (var library in libraries.Take(options.MaxScanRoots))
            {
                foreach (var file in Entries(Path.Combine(library, "steamapps"), options.MaxManifests, Warn)
                    .OfType<FileInfo>().Where(f => f.Name.StartsWith("appmanifest_", StringComparison.OrdinalIgnoreCase) && f.Extension.Equals(".acf", StringComparison.OrdinalIgnoreCase)))
                {
                    if (!Budget()) break;
                    Attempt(() =>
                    {
                        var doc = VdfNode.Parse(LocalData.ReadText(file.FullName, options.MaxManifestBytes));
                        var state = doc.Children.FirstOrDefault(p => p.Key.Equals("AppState", StringComparison.OrdinalIgnoreCase)).Value;
                        if (state is null) throw new InvalidDataException("Missing Steam AppState.");
                        var dir = state.Get("installdir"); var id = state.Get("appid");
                        if (!RuntimeDiagnostics.SafeRelative(dir) || id is null || !id.All(char.IsAsciiDigit))
                            throw new InvalidDataException("Invalid Steam installation path or app id.");
                        var common = Path.Combine(library, "steamapps", "common");
                        var root = Path.GetFullPath(Path.Combine(common, dir!));
                        if (!LocalPaths.IsInside(root, common)) throw new InvalidDataException("Steam path escapes its library.");
                        Add(new("Steam", id, state.Get("name") ?? dir!, root));
                    }, "Steam manifest " + file.Name);
                }
            }
        }, "Steam library");
        foreach (var dir in options.EpicManifestDirectories.Take(options.MaxScanRoots))
            foreach (var file in Entries(dir, options.MaxManifests, Warn).OfType<FileInfo>().Where(f => f.Extension.Equals(".item", StringComparison.OrdinalIgnoreCase)))
            {
                if (!Budget()) break;
                Attempt(() =>
                {
                    using var json = JsonDocument.Parse(LocalData.ReadText(file.FullName, options.MaxManifestBytes));
                    var root = json.RootElement;
                    if (root.TryGetProperty("bIsIncompleteInstall", out var incomplete) && incomplete.ValueKind == JsonValueKind.True) return;
                    var location = JsonString(root, "InstallLocation");
                    if (location is not null) Add(new("Epic", JsonString(root, "AppName") ?? "", JsonString(root, "DisplayName") ?? Path.GetFileName(location), location));
                }, "Epic manifest " + file.Name);
            }
        foreach (var entry in options.GogGames) { if (!Budget()) break; Add(entry with { Store = "GOG" }); }
        foreach (var entry in options.UbisoftGames) { if (!Budget()) break; Add(entry with { Store = "Ubisoft" }); }

        void Xbox(string package)
        {
            Attempt(() =>
            {
                var root = LocalPaths.Normalize(package);
                LocalPaths.RequireNoLinks(root);
                if (Path.GetFileName(root).Equals("Content", StringComparison.OrdinalIgnoreCase)) root = Path.GetDirectoryName(root)!;
                var config = Path.Combine(root, "Content", "MicrosoftGame.config");
                if (!File.Exists(config)) config = Path.Combine(root, "MicrosoftGame.config");
                if (!File.Exists(config)) return;
                var text = LocalData.ReadText(config, options.MaxManifestBytes);
                using var reader = XmlReader.Create(new StringReader(text), new XmlReaderSettings
                    { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = options.MaxManifestBytes });
                var xml = XDocument.Load(reader);
                var identity = xml.Descendants().FirstOrDefault(e => e.Name.LocalName == "Identity")?.Attribute("Name")?.Value ?? "";
                var name = xml.Descendants().Attributes().FirstOrDefault(a => a.Name.LocalName == "DefaultDisplayName")?.Value;
                if (string.IsNullOrWhiteSpace(name) || name.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase)) name = Path.GetFileName(root);
                Add(new("Xbox", identity, name, root));
            }, "Xbox package");
        }
        foreach (var package in options.XboxPackageRoots) { if (!Budget()) break; Xbox(package); }
        foreach (var drive in options.XboxDriveRoots.Take(options.MaxScanRoots)) Attempt(() =>
        {
            var root = LocalPaths.Normalize(drive);
            var shelves = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Path.Combine(root, "XboxGames") };
            var gaming = Path.Combine(root, ".GamingRoot");
            if (File.Exists(gaming) && Budget())
            {
                LocalPaths.RequireNoLinks(gaming);
                using var stream = File.OpenRead(gaming);
                if (stream.Length is < 10 or > 4096) throw new InvalidDataException("Invalid .GamingRoot length.");
                var bytes = new byte[(int)stream.Length]; stream.ReadExactly(bytes);
                if (Encoding.ASCII.GetString(bytes, 0, 4) != "RGBX") throw new InvalidDataException("Invalid .GamingRoot signature.");
                var relative = Encoding.Unicode.GetString(bytes, 8, bytes.Length - 8).Split('\0')[0];
                if (!RuntimeDiagnostics.SafeRelative(relative)) throw new InvalidDataException("Invalid .GamingRoot path.");
                shelves.Add(Path.Combine(root, relative));
            }
            foreach (var shelf in shelves)
                foreach (var child in Entries(shelf, options.MaxManifests, Warn).OfType<DirectoryInfo>())
                { if (!Budget()) break; Xbox(child.FullName); }
        }, "Xbox library");
        return found.Values.OrderBy(g => g.Store).ThenBy(g => g.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    static string? JsonString(JsonElement root, string name) => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    static IReadOnlyList<FileSystemInfo> Entries(string dir, int limit, Action<string> warning)
    {
        try
        {
            dir = LocalPaths.Normalize(dir);
            LocalPaths.RequireNoLinks(dir);
            if (!Directory.Exists(dir)) return [];
            var entries = new DirectoryInfo(dir).EnumerateFileSystemInfos().Take(limit + 1).ToArray();
            if (entries.Length > limit) warning("Directory entry limit reached: " + dir);
            return entries.Take(limit).Where(e => (e.Attributes & FileAttributes.ReparsePoint) == 0).ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        { warning($"Could not read {dir}: {ex.Message}"); return []; }
    }

    static LibraryDiscoveryOptions SystemOptions(List<string> warnings, CancellationToken ct)
    {
        var options = new LibraryDiscoveryOptions();
        if (!OperatingSystem.IsWindows()) return options;
        try
        {
            using var steam = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam", false);
            if (steam?.GetValue("SteamPath") is string path) options.SteamRoots.Add(path);
            options.EpicManifestDirectories.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Epic", "EpicGamesLauncher", "Data", "Manifests"));
            foreach (var view in new[] { RegistryView.Registry32, RegistryView.Registry64 })
            {
                using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                ReadGames(machine, @"SOFTWARE\GOG.com\Games", "GOG", "path", "gameName", options.GogGames, warnings, ct);
                ReadGames(machine, @"SOFTWARE\Ubisoft\Launcher\Installs", "Ubisoft", "InstallDir", null, options.UbisoftGames, warnings, ct);
            }
            using var gaming = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\GamingServices\PackageRepository\Root", false);
            if (gaming is not null)
            {
                foreach (var name in gaming.GetSubKeyNames().Take(2048))
                {
                    ct.ThrowIfCancellationRequested();
                    using var package = gaming.OpenSubKey(name, false);
                    if (package?.GetValue("Root") is string location)
                        options.XboxPackageRoots.Add(location.StartsWith(@"\\?\", StringComparison.Ordinal) ? location[4..] : location);
                }
            }
            options.XboxDriveRoots.AddRange(DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady).Select(d => d.RootDirectory.FullName));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        { warnings.Add("Store registry discovery: " + ex.Message); }
        return options;
    }

    static void ReadGames(RegistryKey machine, string path, string store, string pathValue, string? nameValue,
        List<StoreInstall> found, List<string> warnings, CancellationToken ct)
    {
        try
        {
            using var root = machine.OpenSubKey(path, false);
            if (root is null) return;
            foreach (var id in root.GetSubKeyNames().Take(2048))
            {
                ct.ThrowIfCancellationRequested();
                using var game = root.OpenSubKey(id, false);
                if (game?.GetValue(pathValue) is not string location) continue;
                var name = nameValue is null ? null : game.GetValue(nameValue) as string;
                found.Add(new(store, id, name ?? Path.GetFileName(location.TrimEnd('/', '\\')), location));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        { warnings.Add($"{store} registry discovery: {ex.Message}"); }
    }

    // KeyValues has quoted/unquoted tokens, escaped backslashes, comments and nested objects.
    internal sealed class VdfNode
    {
        public string? Value;
        public List<KeyValuePair<string, VdfNode>> Children = [];
        public string? Get(string name) => Children.FirstOrDefault(p => p.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Value?.Value;
        public IEnumerable<KeyValuePair<string, VdfNode>> Descendants()
        {
            foreach (var pair in Children) { yield return pair; foreach (var child in pair.Value.Descendants()) yield return child; }
        }
        public static VdfNode Parse(string text)
        {
            var index = 0; var tokens = 0;
            string? Token()
            {
                if (++tokens > 100000) throw new InvalidDataException("KeyValues token limit reached.");
                while (index < text.Length)
                {
                    if (char.IsWhiteSpace(text[index]) || text[index] == '\uFEFF') { index++; continue; }
                    if (text[index] == '/' && index + 1 < text.Length && text[index + 1] == '/')
                    { while (index < text.Length && text[index] != '\n') index++; continue; }
                    break;
                }
                if (index >= text.Length) return null;
                var c = text[index++];
                if (c is '{' or '}') return c.ToString();
                var value = new StringBuilder();
                if (c == '"')
                {
                    while (index < text.Length)
                    {
                        c = text[index++];
                        if (c == '"') return value.ToString();
                        if (c == '\\' && index < text.Length && text[index] is '\\' or '"') c = text[index++];
                        value.Append(c);
                    }
                    throw new InvalidDataException("Unterminated KeyValues string.");
                }
                value.Append(c);
                while (index < text.Length && !char.IsWhiteSpace(text[index]) && text[index] is not ('{' or '}')) value.Append(text[index++]);
                return value.ToString();
            }
            VdfNode Object(int depth)
            {
                if (depth > 16) throw new InvalidDataException("KeyValues nesting limit reached.");
                var node = new VdfNode();
                while (true)
                {
                    var key = Token();
                    if (key is null) { if (depth > 0) throw new InvalidDataException("Incomplete KeyValues object."); return node; }
                    if (key == "}") { if (depth == 0) throw new InvalidDataException("Unexpected KeyValues brace."); return node; }
                    var value = Token();
                    if (value is null or "}" || key == "{") throw new InvalidDataException("Incomplete KeyValues pair.");
                    node.Children.Add(new(key, value == "{" ? Object(depth + 1) : new VdfNode { Value = value }));
                }
            }
            return Object(0);
        }
    }
}
