using System.Diagnostics;

namespace Dlss5CompatApp;

static class GameLauncher
{
    public static ProcessStartInfo Create(GameCandidate game, string arguments)
    {
        if (arguments.Length > 32767 || arguments.IndexOfAny(['\0', '\r', '\n']) >= 0)
            throw new ArgumentException("Launch arguments contain unsupported characters or are too long.");
        var bootstrap = EngineBootstrap.Find(game.ExePath);
        var explicitContent = bootstrap is not null && LocalPaths.Same(bootstrap.Bootstrap, game.ExePath) &&
            System.Text.RegularExpressions.Regex.IsMatch(arguments, @"(?:^|\s)""?-game""?(?=\s|$)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (bootstrap is not null && !explicitContent) arguments = bootstrap.Arguments + (arguments.Length > 0 ? " " + arguments : "");
        if (arguments.Length > 32767) throw new ArgumentException("Combined engine launch arguments are too long.");
        var start = new ProcessStartInfo
        {
            FileName = game.ExePath,
            WorkingDirectory = bootstrap?.WorkingDirectory ?? Path.GetDirectoryName(game.ExePath)!,
            Arguments = arguments,
            UseShellExecute = false
        };
        foreach (var pair in InstallerEngine.VulkanEnvironmentFor(game)) start.Environment[pair.Key] = pair.Value;
        if (InstallerEngine.FindSteamAppId(game.Root) is { Length: > 0 } id)
        {
            start.Environment["SteamAppId"] = id;
            start.Environment["SteamGameId"] = id;
        }
        if (game.Route == InstallRoute.X64OpenXrLayer)
        {
            start.Environment["XR_API_LAYER_PATH"] = Path.Combine(start.WorkingDirectory, "_DLSS5_OpenXR", "openxr");
            start.Environment["XR_ENABLE_API_LAYERS"] = "XR_APILAYER_DLSS5_everything";
            start.Environment["DLSS5_OPENXR_ENABLED"] = "1";
            start.Environment["DLSS5_OPENXR_WARMUP_RELEASES"] = "60";
            start.Environment["DLSS5_OPENXR_PROCESS_EVERY"] = "1";
            start.Environment["DLSS5_OPENXR_MAX_BLOCK_MS"] = "1500";
            start.Environment["DLSS5_OPENXR_PROCESS_TIMEOUT_MS"] = "5000";
        }
        if (game.Route == InstallRoute.X64OpenVrFeeder)
        {
            foreach (var variable in new[] { "DLSS5_DISABLE_VK_LAYER_reshade_1", "DISABLE_VK_LAYER_reshade_1", "DISABLE_VK_LAYER_reshade_2" })
                start.Environment[variable] = "1";
            start.Environment["VK_LOADER_LAYERS_DISABLE"] = "VK_LAYER_reshade";
        }
        return start;
    }
}
