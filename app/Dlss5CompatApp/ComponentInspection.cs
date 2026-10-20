using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

namespace Dlss5CompatApp;

internal enum ComponentInspectionState { Current, Outdated, Different, Missing, PayloadMissing, Unreadable, Limited }
internal enum ComponentInspectionFilter { All, Available, Outdated, Addons }

internal sealed record ComponentInspectionRow(string Name, string InstalledPath, string? PayloadPath,
    string? InstalledVersion, string? PayloadVersion, string? InstalledSha256, string? PayloadSha256,
    ComponentInspectionState State, bool IsAddon, bool InstalledAvailable, string? Detail = null)
{
    // "Outdated" filters include unequal copies whose version ordering is unknown or newer.
    public bool NeedsUpdate => State is ComponentInspectionState.Outdated or ComponentInspectionState.Different;
    public bool MatchesFilter(ComponentInspectionFilter filter) => filter switch
    {
        ComponentInspectionFilter.All => true,
        ComponentInspectionFilter.Available => InstalledAvailable,
        ComponentInspectionFilter.Outdated => NeedsUpdate,
        ComponentInspectionFilter.Addons => IsAddon,
        _ => false
    };
}

// Counts describe inspected files, not route readiness. Addons counts installed add-on files.
internal sealed record ComponentInspectionSummary(int Available, int Outdated, int Addons, int Missing, bool Limited)
{
    public bool MatchesFilter(ComponentInspectionFilter filter) => filter switch
    {
        ComponentInspectionFilter.All => true,
        ComponentInspectionFilter.Available => Available > 0,
        ComponentInspectionFilter.Outdated => Outdated > 0,
        ComponentInspectionFilter.Addons => Addons > 0,
        _ => false
    };
}

internal sealed record ComponentInspectionReport(IReadOnlyList<ComponentInspectionRow> Rows,
    ComponentInspectionSummary Summary)
{
    public const string EvidenceScope = "Local file versions and SHA-256 only; current means identical to the selected payload. Runtime and neural activation are not verified.";
    public bool MatchesFilter(ComponentInspectionFilter filter) => Summary.MatchesFilter(filter);
}

internal sealed record ComponentInspectionLimits(int MaxRows = 64, long MaxFileBytes = 512L * 1024 * 1024,
    long MaxTotalBytes = 2L * 1024 * 1024 * 1024, TimeSpan? Timeout = null)
{
    public TimeSpan EffectiveTimeout => Timeout ?? TimeSpan.FromSeconds(15);
    public void Validate()
    {
        if (MaxRows is < 1 or > 256 || MaxFileBytes is < 1 or > 2L * 1024 * 1024 * 1024 ||
            MaxTotalBytes is < 1 or > 8L * 1024 * 1024 * 1024 || EffectiveTimeout <= TimeSpan.Zero ||
            EffectiveTimeout > TimeSpan.FromMinutes(1)) throw new ArgumentOutOfRangeException(nameof(ComponentInspectionLimits));
    }
}

internal static class ComponentInspectionService
{
    public static Task<ComponentInspectionReport> InspectAsync(GameCandidate game, PayloadInfo? payload,
        CancellationToken cancellationToken = default) => InspectAsync(game, payload, new ComponentInspectionLimits(), cancellationToken);

    public static Task<ComponentInspectionReport> InspectAsync(GameCandidate game, PayloadInfo? payload,
        ComponentInspectionLimits limits, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(game);
        ArgumentNullException.ThrowIfNull(limits);
        limits.Validate();
        // Path and metadata operations also run off the UI thread. No DLL is loaded or executed.
        return Task.Run(() => InspectCoreAsync(game, payload, limits, cancellationToken), cancellationToken);
    }

    static async Task<ComponentInspectionReport> InspectCoreAsync(GameCandidate game, PayloadInfo? payload,
        ComponentInspectionLimits limits, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var root = LocalPaths.Normalize(game.Root);
        var exe = LocalPaths.Normalize(game.ExePath);
        if (!LocalPaths.IsInside(exe, root) || LocalPaths.Same(exe, root))
            throw new ArgumentException("The game executable must be inside its root.", nameof(game));
        var directory = Path.GetDirectoryName(exe)!;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(limits.EffectiveTimeout);
        var reader = new Inspector(limits, deadline.Token);
        var rows = new List<ComponentInspectionRow>();
        bool limited = payload is not null && (payload.NvidiaDlls.Count > 64 || payload.ExtraAddons.Count > 64);
        try
        {
            foreach (var target in Targets(directory, game, payload))
            {
                deadline.Token.ThrowIfCancellationRequested();
                if (rows.Count >= limits.MaxRows) { limited = true; break; }
                var installed = await reader.ReadAsync(target.InstalledPath);
                if (target.Optional && installed.Status == FileStatus.Missing) continue;
                var selected = target.ExpectedSha256 is { } expected
                    ? new FileInspection(FileStatus.Available, Hash: expected.ToUpperInvariant())
                    : await reader.ReadAsync(target.PayloadPath);
                var state = Compare(installed, selected);
                limited |= installed.Status == FileStatus.Limited || selected.Status == FileStatus.Limited;
                var detail = installed.Error ?? selected.Error;
                if (detail is null && target.Name.StartsWith("ReShade / proxy", StringComparison.Ordinal) &&
                    installed.Status == FileStatus.Available && state != ComponentInspectionState.Current)
                    detail = "Proxy filename alone does not identify ReShade; this file does not match the selected payload.";
                rows.Add(new(target.Name, target.InstalledPath, target.PayloadPath, installed.Version, selected.Version,
                    installed.Hash, selected.Hash, state, target.IsAddon, installed.Status == FileStatus.Available,
                    detail));
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            limited = true;
        }
        cancellationToken.ThrowIfCancellationRequested();
        var snapshot = rows.AsReadOnly();
        return new(snapshot, new(rows.Count(r => r.InstalledAvailable), rows.Count(r => r.NeedsUpdate),
            rows.Count(r => r.IsAddon && r.InstalledAvailable), rows.Count(r => r.State == ComponentInspectionState.Missing), limited));
    }

    static ComponentInspectionState Compare(FileInspection installed, FileInspection selected)
    {
        if (installed.Status == FileStatus.Missing) return ComponentInspectionState.Missing;
        if (installed.Status == FileStatus.Limited || selected.Status == FileStatus.Limited) return ComponentInspectionState.Limited;
        if (installed.Status == FileStatus.Unreadable || selected.Status == FileStatus.Unreadable) return ComponentInspectionState.Unreadable;
        if (selected.Status == FileStatus.Missing) return ComponentInspectionState.PayloadMissing;
        if (installed.Hash == selected.Hash) return ComponentInspectionState.Current;
        return installed.NumericVersion is not null && selected.NumericVersion is not null &&
            installed.NumericVersion < selected.NumericVersion ? ComponentInspectionState.Outdated : ComponentInspectionState.Different;
    }

    sealed record Target(string Name, string InstalledPath, string? PayloadPath, bool IsAddon = false, bool Optional = false, string? ExpectedSha256 = null);

    static Target? SharedNativeModel(string directory)
    {
        try
        {
            var path = Path.Combine(directory, PhoenixConfiguration.FileName);
            if (!File.Exists(path)) return null;
            using var configuration = JsonDocument.Parse(LocalData.ReadText(path, 65536));
            var source = configuration.RootElement.GetProperty("weight_source");
            if (source.GetProperty("kind").GetString() != "model_folder") return null;
            var folder = source.GetProperty("path").GetString()!;
            if (!Path.IsPathFullyQualified(folder)) return null;
            using var manifest = JsonDocument.Parse(LocalData.ReadText(Path.Combine(folder, "model.json"), 65536));
            if (manifest.RootElement.GetProperty("schema").GetString() != "dlss5-phoenix-model/1") return null;
            var resource = manifest.RootElement.GetProperty("resource");
            var checksum = resource.GetProperty("sha256").GetString()!;
            if (checksum.Length != 64 || checksum.Any(c => !Uri.IsHexDigit(c))) return null;
            var weights = OptionalModelLibrary.Contained(folder, resource.GetProperty("path").GetString()!);
            return new("Phoenix native model (shared library)", weights, null, ExpectedSha256: checksum);
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or InvalidOperationException or KeyNotFoundException or ArgumentException)
        { return null; }
    }

    static IEnumerable<Target> Targets(string directory, GameCandidate game, PayloadInfo? payload)
    {
        if (payload is not null && PackagedPhoenixPlan.For(game, payload) is not null)
            foreach (var file in PhoenixRuntimePackage.For(payload, game.Arch, verify: false)
                .Where(file => Path.GetExtension(file.RelativePath).Equals(".dll", StringComparison.OrdinalIgnoreCase) ||
                    file.RelativePath.EndsWith("bootstrap.json", StringComparison.Ordinal)))
                yield return new("Phoenix runtime (" + file.RelativePath + ")",
                    Path.GetFullPath(Path.Combine(directory, "PhoenixRuntime", file.RelativePath)), file.SourcePath, ExpectedSha256: file.Sha256);
        // Only these three locations are inspected; no game or payload directory enumeration.
        var host = Path.Combine(directory, "host64");
        var xr = Path.Combine(directory, "_DLSS5_OpenXR", "host64");
        var route = game.Route;
        var direct = route == InstallRoute.X64DirectPhoenix;
        var primary = direct ? directory : route == InstallRoute.X64OpenXrLayer ? xr : host;
        var sr = FindNvidia(payload, "nvngx_dlss.dll");
        var nr = FindNvidia(payload, "nvngx_dlssnr.dll");
        var denoiser = FindNvidia(payload, "nvngx_dlssd.dll");
        foreach (var folder in new[] { primary, directory, host, xr }.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var suffix = LocalPaths.Same(folder, directory) ? "game" : LocalPaths.Same(folder, xr) ? "OpenXR host" : "host64";
            var optional = !LocalPaths.Same(folder, primary);
            yield return new("Phoenix DLSS5 (" + suffix + ")", Path.Combine(folder, "Phoenix.addon64"), payload?.PhoenixAddon, true, optional);
            if (SharedNativeModel(folder) is { } shared) yield return shared;
            else yield return new("Phoenix DLSS5 model (" + suffix + ")", Path.Combine(folder, "PhoenixModel.dll"), nr, Optional: optional);
            yield return new("NGX DLSS SR (" + suffix + ")", Path.Combine(folder, "nvngx_dlss.dll"), sr, Optional: true);
            yield return new("NGX DLSS NR comparison (" + suffix + ")", Path.Combine(folder, "nvngx_dlssnr.dll"), nr, Optional: true);
            yield return new("NGX DLSS D (" + suffix + ")", Path.Combine(folder, "nvngx_dlssd.dll"), denoiser, Optional: true);
            if (!LocalPaths.Same(folder, directory))
                yield return new("ReShade (" + suffix + ")", Path.Combine(folder, "dxgi.dll"), payload?.ReShade64Dll, Optional: optional);
        }
        var gameReShade = (payload is not null ? PackagedPhoenixPlan.For(game, payload)?.ReShadeOverride : null)
            ?? (game.Arch == CpuArch.X86 ? payload?.ReShade32Dll : payload?.ReShade64Dll);
        var expectedProxy = game.OverrideValidationError is null && game.ProxyName is not null ? game.ProxyName.ToLowerInvariant() :
            route == InstallRoute.X64OpenVrFeeder ? "ReShade64.dll" :
            route is InstallRoute.X86OpenGlFeeder or InstallRoute.X64OpenGlFeeder ? "opengl32.dll" :
            route is InstallRoute.X86Dx8ViaD3D8To9 or InstallRoute.X86Dx9NativeFeeder ? "d3d9.dll" :
            game.Detection.Contains("rendersystemdx11.dll", StringComparison.OrdinalIgnoreCase) ? "d3d11.dll" : "dxgi.dll";
        foreach (var proxy in new[] { expectedProxy, "dxgi.dll", "d3d11.dll", "d3d9.dll", "opengl32.dll", "ReShade64.dll" }
            .Distinct(StringComparer.OrdinalIgnoreCase))
            yield return new("ReShade / proxy (game / " + proxy + ")", Path.Combine(directory, proxy),
                proxy == "ReShade64.dll" ? payload?.ReShade64Dll : gameReShade,
                Optional: proxy != expectedProxy || route is InstallRoute.X64OpenXrLayer or InstallRoute.X64VulkanFeeder);
        // Custom add-ons are explicit payload paths, capped independently of directory contents.
        var extension = game.Arch == CpuArch.X86 ? ".addon32" : ".addon64";
        foreach (var path in (payload?.ExtraAddons ?? []).Take(64).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!Path.GetExtension(path).Equals(extension, StringComparison.OrdinalIgnoreCase)) continue;
            var name = Path.GetFileName(path);
            if (name.Equals("Phoenix.addon64", StringComparison.OrdinalIgnoreCase)) continue;
            yield return new(name, Path.Combine(directory, name), path, true);
        }
    }

    static string? FindNvidia(PayloadInfo? payload, string name) => payload?.NvidiaDlls.Take(64)
        .FirstOrDefault(p => Path.GetFileName(p).Equals(name, StringComparison.OrdinalIgnoreCase));

    enum FileStatus { Available, Missing, Unreadable, Limited }
    sealed record FileInspection(FileStatus Status, string? Version = null, Version? NumericVersion = null,
        string? Hash = null, string? Error = null);

    sealed class Inspector(ComponentInspectionLimits limits, CancellationToken cancellationToken)
    {
        readonly Dictionary<string, FileInspection> cache = new(StringComparer.OrdinalIgnoreCase);
        long bytesRead;

        public async Task<FileInspection> ReadAsync(string? path)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (path is null) return new(FileStatus.Missing);
            string normalized;
            try { normalized = LocalPaths.Normalize(path); }
            catch (ArgumentException exception) { return new(FileStatus.Unreadable, Error: exception.Message); }
            if (cache.TryGetValue(normalized, out var cached)) return cached;
            FileInspection result;
            try
            {
                // Check ancestors from the drive down, before accessing a child of a junction.
                var ancestors = new Stack<string>();
                for (string? current = normalized; current is not null; current = Path.GetDirectoryName(current)) ancestors.Push(current);
                while (ancestors.TryPop(out var ancestor))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var attributes = File.GetAttributes(ancestor);
                    if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Reparse points are excluded.");
                }
                await using var stream = new FileStream(normalized, FileMode.Open, FileAccess.Read, FileShare.Read,
                    64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                if (stream.Length > limits.MaxFileBytes || stream.Length > limits.MaxTotalBytes - bytesRead)
                    result = new(FileStatus.Limited, Error: "Inspection byte limit reached.");
                else
                {
                    using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    var buffer = new byte[64 * 1024];
                    int count;
                    while ((count = await stream.ReadAsync(buffer, cancellationToken)) != 0)
                    {
                        bytesRead += count;
                        if (bytesRead > limits.MaxTotalBytes || stream.Position > limits.MaxFileBytes)
                            throw new IOException("File changed beyond the inspection byte limit.");
                        hash.AppendData(buffer, 0, count);
                    }
                    cancellationToken.ThrowIfCancellationRequested();
                    var version = FileVersionInfo.GetVersionInfo(normalized);
                    var hasVersion = !string.IsNullOrWhiteSpace(version.FileVersion);
                    result = new(FileStatus.Available, hasVersion ? version.FileVersion : null,
                        hasVersion ? new Version(version.FileMajorPart, version.FileMinorPart, version.FileBuildPart, version.FilePrivatePart) : null,
                        Convert.ToHexString(hash.GetHashAndReset()));
                }
            }
            catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
            { result = new(FileStatus.Missing); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or System.ComponentModel.Win32Exception)
            { result = new(FileStatus.Unreadable, Error: exception.Message); }
            cache[normalized] = result;
            return result;
        }
    }
}
