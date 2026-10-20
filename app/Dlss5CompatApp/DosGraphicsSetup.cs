using System.Security.Cryptography;
using System.Text.Json;

namespace Dlss5CompatApp;

/// <summary>Provision Enhanced's pinned, signed graphics files only for a requested DOS launch.</summary>
static class DosGraphicsSetup
{
    internal sealed record VendorFile(string Name, string Sha256);

    internal static VendorFile[] ExpectedFiles(string package)
    {
        var path = Path.Combine(package,"runtime.lock.json");
        if (!File.Exists(path)) return [];
        LocalPaths.RequireNoLinks(path);
        using var json = JsonDocument.Parse(File.ReadAllText(path));
        return json.RootElement.GetProperty("nvidia").GetProperty("files").EnumerateArray().Select(entry =>
        {
            var name = entry.GetProperty("name").GetString() ?? "";
            var hash = entry.GetProperty("sha256").GetString() ?? "";
            if (string.IsNullOrWhiteSpace(name) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                name != Path.GetFileName(name) || !name.EndsWith(".dll",StringComparison.OrdinalIgnoreCase) ||
                hash.Length != 64 || !hash.All(Uri.IsHexDigit))
                throw new InvalidDataException("Unsafe DOS graphics dependency declaration.");
            return new VendorFile(name,hash);
        }).ToArray();
    }

    static bool Matches(string path,string hash)
    {
        if (!File.Exists(path)) return false;
        LocalPaths.RequireNoLinks(path);
        using var file = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(file)).Equals(hash,StringComparison.OrdinalIgnoreCase);
    }

    internal static IEnumerable<string> Missing(string package)
    {
        var files = ExpectedFiles(package);
        if (files.Length == 0)
        {
            // Older local builds have no lock file; preserve their explicit readiness checks.
            foreach (var name in new[] { "sl.interposer.dll","nvngx_dlss.dll" })
                if (!File.Exists(Path.Combine(package,"runtime","nvidia",name))) yield return name;
            yield break;
        }
        foreach (var file in files)
            if (!Matches(Path.Combine(package,"runtime","nvidia",file.Name),file.Sha256)) yield return file.Name;
    }

    internal static async Task EnsureAsync(PayloadInfo payload,Action<string>? progress,CancellationToken token,
        Func<string,string,CancellationToken,Task>? execute = null)
    {
        if (payload.DosBoxEnhancedExe is null || !payload.HasCoreDlss) return;
        var package = DosGameLauncher.PackageRoot(payload.DosBoxEnhancedExe);
        if (!Missing(package).Any()) return;
        var manifest = Path.Combine(package,"runtime.lock.json");
        if (!File.Exists(manifest))
            throw new InvalidOperationException("Update DOSBox Enhanced to obtain its pinned graphics dependency manifest.");
        var expected = ExpectedFiles(package);
        if (expected.Length == 0) throw new InvalidDataException("DOS graphics dependency list is empty.");
        var model = payload.NvidiaDlls.First(path => Path.GetFileName(path).Equals("nvngx_dlssnr.dll",StringComparison.OrdinalIgnoreCase));
        var work = Path.Combine(payload.Root,".dos-graphics-"+Guid.NewGuid().ToString("N"));
        LocalPaths.RequireNoLinks(work);
        Directory.CreateDirectory(work);
        try
        {
            File.Copy(manifest,Path.Combine(work,"runtime.lock.json"));
            Directory.CreateDirectory(Path.Combine(work,"runtime"));
            foreach (var name in new[] { "dosbox_nvngx.dll","phoenix_native.dll" })
                File.Copy(Path.Combine(package,"runtime",name),Path.Combine(work,"runtime",name));
            progress?.Invoke("Preparing signed NVIDIA graphics dependencies for DOSBox Enhanced...");
            await (execute ?? RunAsync)(work,model,token);
            token.ThrowIfCancellationRequested();
            var vendor = Path.Combine(work,"runtime","nvidia");
            foreach (var file in expected)
                if (!Matches(Path.Combine(vendor,file.Name),file.Sha256))
                    throw new InvalidDataException("DOS graphics setup produced an unverified dependency: "+file.Name);
            var names = expected.Select(file => file.Name).Concat(new[] {
                "license.txt","3rd-party-licenses.md","nvngx_dlss.license.txt","reflex.license.txt" }).ToArray();
            foreach (var name in names)
                if (!File.Exists(Path.Combine(vendor,name))) throw new InvalidDataException("DOS graphics notice is missing: "+name);
            foreach (var name in names)
            {
                token.ThrowIfCancellationRequested();
                var destination = Path.Combine(package,"runtime","nvidia",name);
                LocalPaths.RequireNoLinks(destination);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                var temporary = destination+"."+Guid.NewGuid().ToString("N")+".tmp";
                try { File.Copy(Path.Combine(vendor,name),temporary); File.Move(temporary,destination,true); }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
            }
            progress?.Invoke("DOS graphics dependencies verified and ready. The model remains externally supplied.");
        }
        finally
        {
            // The manifest, SDK archive and partial downloads belong to this invocation.
            var pending = new Stack<string>(); pending.Push(work);
            while (pending.TryPop(out var directory))
            {
                LocalPaths.RequireNoLinks(directory);
                foreach (var path in Directory.EnumerateFileSystemEntries(directory))
                { LocalPaths.RequireNoLinks(path); if (Directory.Exists(path)) pending.Push(path); }
            }
            Directory.Delete(work,true);
        }
    }

    static Task RunAsync(string work,string model,CancellationToken token) =>
        DosGraphicsDownload.ProvisionAsync(work,model,token);
}
