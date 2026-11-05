namespace Dlss5CompatApp;

/// <summary>
/// An ordinary input import can load ReShade before a translator or an overlay
/// caches the system DXGI module. This changes the loading point, not the
/// graphics API, translator, renderer or inference transport.
/// </summary>
static class InputBootstrap
{
    public const string ProxyName = "dinput8.dll";
    public const string LegacyProxyName = "dinput.dll";

    public static bool IsInputProxy(string? name) => name is not null &&
        (name.Equals(ProxyName, StringComparison.OrdinalIgnoreCase) || name.Equals(LegacyProxyName, StringComparison.OrdinalIgnoreCase));

    public static string[] RequiredExports(string name) => name.Equals(ProxyName, StringComparison.OrdinalIgnoreCase)
        ? ["DirectInput8Create"] : ["DirectInputCreateA", "DirectInputCreateW", "DirectInputCreateEx"];

    public static string? AutomaticProxy(GameCandidate game) =>
        new[] { ProxyName, LegacyProxyName }.FirstOrDefault(name => CanUse(game, name));

    static bool HasStartupImport(GameCandidate game, string proxy)
    {
        var imports = PeReader.GetImports(game.ExePath, includeDelayed: false);
        if (imports.Contains(proxy, StringComparer.OrdinalIgnoreCase)) return true;
        // An engine DLL's ordinary imports also load before entry into the
        // application. Keep this one level deep, architecture matched and
        // local; never infer startup from delayed imports or proxy capability
        // strings, and never discover ourselves through an installed wrapper.
        var directory = Path.GetDirectoryName(game.ExePath)!;
        foreach (var name in imports.Take(80))
        {
            if (Path.GetFileName(name) != name || IsInputProxy(name) || GameScanner.IsGraphicsProxyOrAppDll(name)) continue;
            var module = Path.Combine(directory, name);
            if (!File.Exists(module)) continue;
            LocalPaths.RequireNoLinks(module);
            if (PeReader.GetArchitecture(module) == game.Arch &&
                PeReader.GetImports(module, includeDelayed: false).Contains(proxy, StringComparer.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    public static bool CanUse(GameCandidate game, string proxy = ProxyName)
    {
        if (!IsInputProxy(proxy) || game.Arch is not (CpuArch.X86 or CpuArch.X64) || !File.Exists(game.ExePath)) return false;
        try
        {
            LocalPaths.RequireNoLinks(game.ExePath);
            if (!HasStartupImport(game, proxy)) return false;
            var target = Path.Combine(Path.GetDirectoryName(game.ExePath)!, proxy);
            LocalPaths.RequireNoLinks(target);
            // Preserve input mods and system/game libraries. Only a compatible
            // ReShade copy is eligible for the installer's ordinary backup path.
            return !File.Exists(target) || PeReader.GetArchitecture(target) == game.Arch &&
                PackagedPhoenixPlan.IsReShade(target) && PayloadScanner.HasExports(target, RequiredExports(proxy));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return false;
        }
    }
}
