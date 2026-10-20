namespace Dlss5CompatApp;

/// <summary>Use software hooks only after observed runtime rejection or explicit per-game opt-in.</summary>
static class SoftwarePresentation
{
    public static bool RequiresHook(GameCandidate game)
    {
        if (game.Api is not (GraphicsApi.DirectX10 or GraphicsApi.DirectX10_1 or GraphicsApi.DirectX11 or GraphicsApi.Dxgi)) return false;
        var directory = Path.GetDirectoryName(game.ExePath)!;
        try
        {
            if (IniEditor.GetValue(Path.Combine(directory, "ReShade.ini"), "APP", "IncludeSoftwareDevices") == "1") return true;
            var path = Path.Combine(directory, "ReShade.log");
            if (!File.Exists(path)) return false;
            LocalPaths.RequireNoLinks(path);
            using var reader = new StreamReader(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete));
            var buffer = new char[128 * 1024];
            var text = new string(buffer, 0, reader.ReadBlock(buffer, 0, buffer.Length));
            return text.Contains("into '" + game.ExePath + "'", StringComparison.OrdinalIgnoreCase) &&
                !text.Contains("Recreated runtime environment", StringComparison.OrdinalIgnoreCase) &&
                (text.Contains("Skipping device because the driver type is 'D3D_DRIVER_TYPE_WARP'", StringComparison.Ordinal) ||
                 text.Contains("Skipping device because it uses the Microsoft Basic Render Driver", StringComparison.Ordinal));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException) { return false; }
    }

    public static bool Compatible(string? path, CpuArch architecture) =>
        PayloadScanner.IsPeDll(path, architecture) &&
        PayloadScanner.HasExports(path!, "ReShadeRegisterAddon", "CnrDLSS5SoftwareDevices");
}
