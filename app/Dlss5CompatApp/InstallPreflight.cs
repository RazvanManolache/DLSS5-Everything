using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Dlss5CompatApp;

public enum PreflightSeverity { Information, Warning, Error }
public sealed record PreflightFinding(string Code, PreflightSeverity Severity, string Message, string? Path = null);
public sealed record PreflightProcess(int Id, string Name, string? ExecutablePath);
public sealed record PreflightLimits(int MaxEntries = 2000, int MaxDepth = 2, int MaxProcesses = 4096);
public sealed record InstallPreflightResult(IReadOnlyList<PreflightFinding> Findings)
{
    public bool CanInstall => Findings.All(f => f.Severity != PreflightSeverity.Error);
    public bool HasWarnings => Findings.Any(f => f.Severity == PreflightSeverity.Warning);
    public bool RequiresAntiCheatAcknowledgement => Findings.Any(f => f.Code == "anti-cheat");
    public string DisplayText => string.Join(Environment.NewLine, Findings.Select(f => $"[{f.Severity}] {f.Message}"));
    public void ThrowIfBlocked()
    {
        if (!CanInstall) throw new InvalidOperationException(DisplayText);
    }
}

/// <summary>Read-only checks. Call again immediately before any installation or restore writes.</summary>
public static class InstallPreflight
{
    static readonly HashSet<string> Proxies = new(StringComparer.OrdinalIgnoreCase)
    {
        "dxgi.dll", "d3d8.dll", "d3d9.dll", "d3d10.dll", "d3d10core.dll", "d3d11.dll", "d3d12.dll",
        "ddraw.dll", "d3dimm.dll", "opengl32.dll", "openvr_api.dll", "winmm.dll", "version.dll", "dinput8.dll",
        "glide.dll", "glide2x.dll", "glide3x.dll"
    };
    static readonly Regex AntiCheat = new(@"easyanticheat|battleye|eaanticheat|(?:^|[-_])(?:eac|be)[-_]?launcher|^vgk\.sys$|^vgc\.exe$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <param name="filesToWrite">Exact root-relative destinations, including removed files, for the chosen route.
    /// When omitted, all common proxy destinations are treated conservatively as planned writes.</param>
    /// <param name="managedFiles">Root-relative paths already owned by a validated installation manifest.</param>
    public static InstallPreflightResult Check(string gameRoot, string exePath,
        IEnumerable<string>? filesToWrite = null, IEnumerable<string>? managedFiles = null,
        Func<IReadOnlyList<PreflightProcess>>? processProvider = null,
        PreflightLimits? limits = null, CancellationToken cancellationToken = default,
        IReadOnlyCollection<string>? activeInjectionDirectories = null)
    {
        limits ??= new();
        if (limits.MaxEntries is < 1 or > 100000 || limits.MaxDepth is < 0 or > 8 || limits.MaxProcesses is < 1 or > 16384)
            throw new ArgumentOutOfRangeException(nameof(limits));
        var findings = new List<PreflightFinding>();
        void Add(string code, PreflightSeverity severity, string message, string? path = null) =>
            findings.Add(new(code, severity, message, path));
        string root, exe, dir;
        HashSet<string> owned, planned;
        try
        {
            root = LocalPaths.Normalize(gameRoot);
            exe = LocalPaths.Normalize(exePath);
            if (LocalPaths.Same(root, Path.GetPathRoot(root)!) || !Directory.Exists(root) || !File.Exists(exe) || !LocalPaths.IsInside(exe, root) || LocalPaths.Same(exe, root))
                throw new ArgumentException("Select an existing executable inside the game folder.");
            LocalPaths.RequireNoLinks(exe);
            dir = Path.GetDirectoryName(exe)!;
            owned = Resolve(managedFiles ?? []);
            planned = Resolve(filesToWrite ?? Proxies.Select(n => Path.GetRelativePath(root, Path.Combine(dir, n))));

            HashSet<string> Resolve(IEnumerable<string> paths)
            {
                var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var count = 0;
                foreach (var relative in paths)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (++count > 4096) throw new ArgumentException("Too many installation paths.");
                    result.Add(BoundedInstallPaths.Resolve(root, relative));
                }
                return result;
            }
        }
        catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException)
        {
            Add("unsafe-target", PreflightSeverity.Error, e.Message);
            return new(findings.AsReadOnly());
        }

        cancellationToken.ThrowIfCancellationRequested();
        findings.AddRange(RendererDiagnostics.Inspect(exe));
        try
        {
            var processes = (processProvider ?? ReadProcesses)();
            if (processes.Count > limits.MaxProcesses)
                Add("process-limit", PreflightSeverity.Warning, "Process check reached its limit; lock checks still apply.");
            foreach (var p in processes.Take(limits.MaxProcesses))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (p.Id == Environment.ProcessId) continue;
                var matches = false;
                if (!string.IsNullOrWhiteSpace(p.ExecutablePath))
                {
                    try { matches = LocalPaths.IsInside(p.ExecutablePath, root); }
                    catch (ArgumentException) { }
                }
                else matches = Path.GetFileNameWithoutExtension(p.Name).Equals(Path.GetFileNameWithoutExtension(exe), StringComparison.OrdinalIgnoreCase)
                    || Path.GetFileNameWithoutExtension(p.Name).Equals("dlss5-feed-host64", StringComparison.OrdinalIgnoreCase);
                if (matches) Add("game-running", PreflightSeverity.Error, $"Close the game or helper before continuing: {p.Name} (PID {p.Id}).", p.ExecutablePath);
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Add("process-check-unavailable", PreflightSeverity.Warning, "Process check unavailable; executable and destination lock checks still apply. " + e.Message);
        }

        foreach (var path in planned.Append(exe).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                LocalPaths.RequireNoLinks(path);
                if (Directory.Exists(path)) throw new IOException("Destination is a directory.");
                for (var parent = Path.GetDirectoryName(path); parent is not null && LocalPaths.IsInside(parent, root); parent = Path.GetDirectoryName(parent))
                    if (File.Exists(parent)) throw new IOException("Destination parent is a file: " + parent);
                if (!File.Exists(path)) continue;
                // Opening checks permissions/sharing without truncating or changing file contents.
                using var stream = new FileStream(path, FileMode.Open, path == exe ? FileAccess.Read : FileAccess.ReadWrite, FileShare.None);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Add("file-unavailable", PreflightSeverity.Error, $"File is locked, read-only, or inaccessible: {path}. {e.Message}", path);
            }
        }

        // Examine both the installation root and a deeply nested executable folder, sharing one budget.
        var queue = new Queue<(string Path, int Depth)>();
        queue.Enqueue((root, 0));
        var injectionDirectories = new HashSet<string>(planned.Select(p => Path.GetDirectoryName(p)!), StringComparer.OrdinalIgnoreCase)
        { dir };
        var activeDirectories = activeInjectionDirectories is null ? injectionDirectories :
            new HashSet<string>(activeInjectionDirectories.Select(Path.GetFullPath), StringComparer.OrdinalIgnoreCase);
        foreach (var active in activeDirectories)
        {
            if (!LocalPaths.Same(active, root) && !LocalPaths.IsInside(active, root))
                throw new ArgumentException("An active injection directory must be inside the selected game.");
            LocalPaths.RequireNoLinks(active);
        }
        foreach (var injectionDirectory in injectionDirectories)
            if (!LocalPaths.Same(root, injectionDirectory) && Directory.Exists(injectionDirectory)) queue.Enqueue((injectionDirectory, 0));
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var examined = 0;
        while (queue.Count > 0 && examined < limits.MaxEntries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = queue.Dequeue();
            if (!visited.Add(current.Path)) continue;
            try
            {
                LocalPaths.RequireNoLinks(current.Path);
                foreach (var entry in Directory.EnumerateFileSystemEntries(current.Path))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (examined++ >= limits.MaxEntries) break;
                    var name = Path.GetFileName(entry);
                    if ((File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0)
                    {
                        Add("link-skipped", PreflightSeverity.Warning, "Linked path excluded from inspection: " + entry, entry);
                        continue;
                    }
                    if (AntiCheat.IsMatch(name))
                        Add("anti-cheat", PreflightSeverity.Warning, "Anti-cheat detected. Injection can trigger enforcement or bans; review the game's policy and explicitly acknowledge before installing: " + name, entry);
                    if (injectionDirectories.Contains(current.Path) && File.Exists(entry) && !owned.Contains(entry))
                    {
                        var activeBinary = new[] { ".dll", ".asi", ".addon32", ".addon64" }
                            .Contains(Path.GetExtension(name), StringComparer.OrdinalIgnoreCase);
                        var incompatible = name.Equals("OptiScaler.ini", StringComparison.OrdinalIgnoreCase) ||
                            activeBinary && (name.Contains("renodx", StringComparison.OrdinalIgnoreCase)
                            || name.Contains("optiscaler", StringComparison.OrdinalIgnoreCase));
                        var mod = incompatible || Proxies.Contains(name) || name.EndsWith(".asi", StringComparison.OrdinalIgnoreCase)
                            || name.EndsWith(".addon64", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".addon32", StringComparison.OrdinalIgnoreCase)
                            || name.Equals("enbseries.ini", StringComparison.OrdinalIgnoreCase) || name.Equals("SpecialK.ini", StringComparison.OrdinalIgnoreCase);
                        if (mod) Add("mod-conflict", incompatible && activeDirectories.Contains(current.Path) || planned.Contains(entry) ? PreflightSeverity.Error : PreflightSeverity.Warning,
                            "Existing unmanaged graphics mod or proxy requires review; it will not be removed automatically: " + name, entry);
                    }
                    if (Directory.Exists(entry) && current.Depth < limits.MaxDepth
                        && !name.Equals("_DLSS5_Compat_Backup", StringComparison.OrdinalIgnoreCase)
                        && !name.Equals("node_modules", StringComparison.OrdinalIgnoreCase)
                        && !name.Equals(".git", StringComparison.OrdinalIgnoreCase)) queue.Enqueue((entry, current.Depth + 1));
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Add("scan-incomplete", PreflightSeverity.Warning, "Could not inspect folder: " + current.Path + ". " + e.Message, current.Path);
            }
        }
        if (examined >= limits.MaxEntries)
            Add("scan-limit", PreflightSeverity.Warning, "Preflight reached the directory entry limit; detection is incomplete.");
        var ancestor = dir;
        for (var depth = 0; ancestor is not null && depth < 5; ancestor = Path.GetDirectoryName(ancestor), depth++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(Path.Combine(ancestor, "ModOrganizer.exe")) && Directory.Exists(Path.Combine(ancestor, "Stock Game")))
                Add("managed-modpack", PreflightSeverity.Error, "Mod Organizer / Stock Game detected. Configure injection through the mod manager rather than replacing its deployed files.", ancestor);
        }
        if (findings.Count == 0) Add("checks-passed", PreflightSeverity.Information, "No running game, locked destination, known anti-cheat, or unmanaged mod conflict was found within the inspection limits.");
        return new(findings.AsReadOnly());
    }

    static IReadOnlyList<PreflightProcess> ReadProcesses()
    {
        var result = new List<PreflightProcess>();
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    var name = process.ProcessName;
                    string? path = null;
                    try { path = process.MainModule?.FileName; }
                    catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException) { }
                    result.Add(new(process.Id, name, path));
                }
                catch (InvalidOperationException) { } // Process exited during enumeration.
            }
        }
        return result;
    }
}

internal static class BoundedInstallPaths
{
    public static string Resolve(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || relative.Length > 1024 || Path.IsPathRooted(relative)
            || relative.IndexOfAny([':', '\0', '\r', '\n', '*', '?', '"', '<', '>', '|']) >= 0)
            throw new ArgumentException("Invalid relative installation path: " + relative);
        var parts = relative.Replace('\\', '/').Split('/');
        if (parts.Any(p => p.Length == 0 || p is "." or ".." || p.EndsWith(' ') || p.EndsWith('.') || p.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || Regex.IsMatch(p, @"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\.|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)))
            throw new ArgumentException("Unsafe relative installation path: " + relative);
        var full = LocalPaths.Normalize(Path.Combine(root, Path.Combine(parts)));
        if (!LocalPaths.IsInside(full, root) || LocalPaths.Same(full, root)) throw new ArgumentException("Path escapes the game folder.");
        LocalPaths.RequireNoLinks(full);
        return full;
    }
}
