using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Dlss5CompatApp;

/// <summary>Remembers a renderer proven by a live Phoenix session. Profiles belong
/// to an executable's content and architecture, never to its title or folder name.</summary>
static class VerifiedRendererProfiles
{
    sealed record Profile(int Version, string Executable, string Sha256, CpuArch Architecture,
        GraphicsApi Api, bool Backbuffer, DateTimeOffset VerifiedAt, string? Proxy = null,
        GraphicsApi? InputApi = null);

    static string DefaultDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DLSS5Everything", "renderer-profiles");

    static string ProfilePath(string executable, string directory) => Path.Combine(directory,
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(executable).ToUpperInvariant()))) + ".json");

    static string ContentHash(string executable)
    {
        LocalPaths.RequireNoLinks(executable);
        using var stream = File.OpenRead(executable);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    public static GameCandidate Apply(GameCandidate game, string? directory = null)
    {
        try
        {
            var path = ProfilePath(game.ExePath, directory ?? DefaultDirectory);
            if (!File.Exists(path)) return game;
            LocalPaths.RequireNoLinks(path);
            if (new FileInfo(path).Length > 16384) return game;
            var profile = JsonSerializer.Deserialize<Profile>(File.ReadAllText(path));
            if (profile is null || profile.Version is not (1 or 2 or 3) || profile.Architecture != game.Arch ||
                !Path.GetFullPath(game.ExePath).Equals(profile.Executable, StringComparison.OrdinalIgnoreCase) ||
                !IsBridgeApi(profile.Api) || GameCandidate.OverrideValueValidationError(null, profile.Proxy) is not null ||
                profile.InputApi is { } input && !IsTranslatorInput(input) ||
                profile.Sha256 != ContentHash(game.ExePath)) return game;
            // Older versions incorrectly parsed "Direct3D 10" as DX11.
            if (profile.Version < 3 && game.Api is (GraphicsApi.DirectX10 or GraphicsApi.DirectX10_1) &&
                profile.Api == GraphicsApi.DirectX11) return game;
            // A translator's D3D11 output does not replace its DirectDraw/D3D8 input.
            // Fresh installations must still deploy the original API translator.
            var installApi = profile.InputApi is not null && game.TranslatorKinds.Count > 0
                ? game.Api == GraphicsApi.Dxgi ? profile.Api : game.Api
                : profile.InputApi ?? (IsTranslatorInput(game.Api) && game.Api != GraphicsApi.DirectX9 ? game.Api : profile.Api);
            var verified = game.WithApi(installApi) with
            {
                PossibleApis = game.AllApis.Append(profile.Api).Distinct().ToArray(),
                Detection = game.Detection + ", verified live renderer",
                HasVerifiedRenderer = true,
                PreferredRoute = profile.Backbuffer ? GameRoutePreference.Feeder : null,
                ProxyName = installApi is GraphicsApi.DirectX10 or GraphicsApi.DirectX10_1 or GraphicsApi.DirectX11 or GraphicsApi.DirectX12 or GraphicsApi.Dxgi ? profile.Proxy : null,
                VerifiedInputRoute = profile.Backbuffer ? GameRoutePreference.Feeder : null,
                VerifiedProxyName = installApi is GraphicsApi.DirectX10 or GraphicsApi.DirectX10_1 or GraphicsApi.DirectX11 or GraphicsApi.DirectX12 or GraphicsApi.Dxgi ? profile.Proxy : null
            };
            return verified.OverrideValidationError is null ? verified : game;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        { return game; }
    }

    public static bool Record(GameCandidate game, PhoenixControlResponse reply, int launchedPid, string? directory = null)
    {
        if (!PhoenixBridgeEvidence.Evaluate(reply, launchedPid).OutputCompleted ||
            !reply.Snapshot.TryGetProperty("api", out var apiName) || apiName.ValueKind != JsonValueKind.String ||
            ParseApi(apiName.GetString()) is not { } api) return false;
        directory ??= DefaultDirectory;
        Directory.CreateDirectory(directory);
        var path = ProfilePath(game.ExePath, directory);
        LocalPaths.RequireNoLinks(path);
        var translated = game.Route is InstallRoute.X86DirectXLegacyViaDgVoodoo or InstallRoute.X86Dx8ViaD3D8To9
            or InstallRoute.X86Dx8ViaDgVoodoo or InstallRoute.X86Dx9ViaDgVoodoo or InstallRoute.X86Glide211ViaDgVoodoo
            or InstallRoute.X86Glide245ViaDgVoodoo or InstallRoute.X86Glide31ViaDgVoodoo or InstallRoute.X86Glide31NapalmViaDgVoodoo;
        // ReShade reports DX10 and DX10.1 as one family; keep a more precise
        // concrete input import rather than inventing a different API version.
        if (api == GraphicsApi.Dxgi && game.Api is GraphicsApi.DirectX10 or GraphicsApi.DirectX10_1) api = game.Api;
        var inputApi = translated ? game.Api : game.TranslatorKinds.Count > 0
            ? game.DetectedInputApis.FirstOrDefault(IsTranslatorInput, GraphicsApi.Unknown) : GraphicsApi.Unknown;
        var profile = new Profile(3, Path.GetFullPath(game.ExePath), ContentHash(game.ExePath), game.Arch,
            api, true, DateTimeOffset.UtcNow, game.ProxyName, inputApi != GraphicsApi.Unknown ? inputApi : null);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(profile));
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        return true;
    }

    internal static GraphicsApi? ParseApi(string? value) => value switch
    {
        "Direct3D 9" => GraphicsApi.DirectX9,
        "Direct3D 10" => GraphicsApi.DirectX10,
        "Direct3D 10.1" => GraphicsApi.DirectX10_1,
        "Direct3D 10 / 10.1" => GraphicsApi.Dxgi,
        "Direct3D 11" => GraphicsApi.DirectX11,
        "Direct3D 12" => GraphicsApi.DirectX12,
        "OpenGL" => GraphicsApi.OpenGl,
        "Vulkan" => GraphicsApi.Vulkan,
        _ => null
    };

    static bool IsBridgeApi(GraphicsApi api) => api is GraphicsApi.DirectX9 or GraphicsApi.DirectX10 or GraphicsApi.DirectX10_1
        or GraphicsApi.DirectX11 or GraphicsApi.DirectX12 or GraphicsApi.Dxgi or GraphicsApi.OpenGl or GraphicsApi.Vulkan;

    static bool IsTranslatorInput(GraphicsApi api) => api is GraphicsApi.DirectX7OrOlder or GraphicsApi.DirectX8
        or GraphicsApi.DirectX8DgVoodoo or GraphicsApi.DirectX9 or GraphicsApi.Glide211 or GraphicsApi.Glide245
        or GraphicsApi.Glide31 or GraphicsApi.Glide31Napalm;
}
