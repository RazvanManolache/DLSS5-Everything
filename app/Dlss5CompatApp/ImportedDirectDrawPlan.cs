using System.Diagnostics;

namespace Dlss5CompatApp;

// A patched import can bind a renamed DDrawCompat DLL instead of DDraw.dll.
// Only replace identified translators, or a translator already owned by us.
static class ImportedDirectDrawPlan
{
    internal static bool IsAlias(string name) => name.Length > 4 &&
        !name.Contains('/') && !name.Contains('\\') && !name.Contains(':') &&
        name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) &&
        !name.Equals("ddraw.dll", StringComparison.OrdinalIgnoreCase);

    internal static bool IsKnownTranslator(string? product, bool previouslyOwned) =>
        previouslyOwned || string.Equals(product, "DDrawCompat", StringComparison.OrdinalIgnoreCase);

    public static IReadOnlyList<string> Find(string executable, CpuArch arch, Func<string, bool>? previouslyOwned = null)
    {
        var directory = Path.GetDirectoryName(executable)!;
        var result = new List<string>();
        foreach (var name in PeReader.GetImports(executable).Where(IsAlias).Take(80))
        {
            var path = Path.Combine(directory, name);
            if (!PayloadScanner.IsPeDll(path, arch) ||
                !PayloadScanner.HasExports(path, "DirectDrawCreate", "DirectDrawCreateEx", "DirectDrawEnumerateA")) continue;
            string? product;
            try { product = FileVersionInfo.GetVersionInfo(path).ProductName; }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { continue; }
            if (IsKnownTranslator(product, previouslyOwned?.Invoke(path) == true)) result.Add(path);
        }
        return result;
    }
}
