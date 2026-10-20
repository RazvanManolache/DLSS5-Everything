namespace Dlss5CompatApp;

/// <summary>Separate evidence from a recommendation: an unverified guess needs a user choice.</summary>
static class InstallationChoice
{
    public static bool NeedsSelection(GameCandidate detected, GameCandidate selected, bool explicitRenderer) =>
        selected.Route == InstallRoute.Unsupported || !explicitRenderer && !detected.HasVerifiedRenderer &&
        (detected.RuntimeSelectionRequired || detected.Api is GraphicsApi.Unknown or GraphicsApi.Dxgi ||
         RendererApis(detected).Count > 1);

    static IReadOnlyList<GraphicsApi> RendererApis(GameCandidate game)
    {
        var apis = game.AllApis.Append(game.Api).Where(api => api != GraphicsApi.Unknown).Distinct().ToArray();
        return apis.Any(api => api is GraphicsApi.DirectX10 or GraphicsApi.DirectX10_1 or GraphicsApi.DirectX11 or GraphicsApi.DirectX12)
            ? apis.Where(api => api != GraphicsApi.Dxgi).ToArray() : apis;
    }

    public static IReadOnlyList<GameCandidate> Options(GameCandidate game)
    {
        var apis = RendererApis(game);
        // Dynamic/unknown renderers need an explicit manual choice. These are possibilities,
        // never advertised as detected hardware or runtime-qualified routes.
        if (apis.Count == 0 || apis.All(api => api == GraphicsApi.Dxgi))
            apis = Enum.GetValues<GraphicsApi>().Where(api => api is not (GraphicsApi.Unknown or GraphicsApi.Dxgi or GraphicsApi.OpenVr or GraphicsApi.OpenXr)).ToArray();
        return apis.Select(api => api == game.Api && game.Route != InstallRoute.Unsupported ? game :
                (game with { PreferredRoute = null, ProxyName = null }).WithApi(api))
            .Where(candidate => candidate.Route != InstallRoute.Unsupported).ToArray();
    }

    static string BaseRouteKey(GameCandidate game) => $"{game.Api}/{game.Route}/" +
        (game.Api is GraphicsApi.DirectX10 or GraphicsApi.DirectX10_1 or GraphicsApi.DirectX11 or GraphicsApi.DirectX12 or GraphicsApi.Dxgi
            ? game.ProxyName ?? "dxgi.dll" : "").ToLowerInvariant();

    internal static string RouteKey(GameCandidate game) => BaseRouteKey(game) +
        (SoftwarePresentation.RequiresHook(game) ? "/software" : "");

    // A rejection log describes the next recommendation, not the hook that was tested.
    // Keep these identities distinct so Verify can offer the software-capable variant once.
    internal static string InstalledRouteKey(GameCandidate game) => BaseRouteKey(game) +
        (SoftwarePresentation.RequiresHook(game) && SoftwarePresentation.Compatible(
            Path.Combine(Path.GetDirectoryName(game.ExePath)!, game.ProxyName ?? "dxgi.dll"), game.Arch)
                ? "/software" : "");

    public static IReadOnlyList<GameCandidate> RepairOptions(GameCandidate game, IReadOnlySet<string> attempted) =>
        (SoftwarePresentation.RequiresHook(game) ? new[] { game } : Array.Empty<GameCandidate>())
            .Concat(SmokeTestRunner.RetryChoices(game)).Concat(Options(game))
            .Where(choice => choice.Route != InstallRoute.Unsupported && !attempted.Contains(RouteKey(choice)))
            .DistinctBy(RouteKey).Take(12).ToArray();
}
