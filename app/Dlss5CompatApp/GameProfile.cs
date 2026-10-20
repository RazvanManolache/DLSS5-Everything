using System.Text.Json.Serialization;

namespace Dlss5CompatApp;

sealed record GameProfile : IJsonOnDeserialized, IJsonOnSerializing
{
    public GraphicsApi? ApiOverride { get; set; }
    public GameRoutePreference? PreferredRoute { get; set; }
    string? proxyName;
    public string? ProxyName { get => proxyName; set => proxyName = value?.ToLowerInvariant(); }
    // null follows scanner suggestions; an empty string explicitly clears them.
    public string? LaunchArguments { get; set; }
    public int FrameIterations { get; set; } = 1;
    public bool ForceVrEyeSplit { get; set; }
    public string? ArtworkPath { get; set; }

    public void ValidateOverrides()
    {
        if (GameCandidate.OverrideValueValidationError(PreferredRoute, ProxyName) is { } error)
            throw new InvalidDataException(error);
    }

    void IJsonOnDeserialized.OnDeserialized() => ValidateOverrides();
    void IJsonOnSerializing.OnSerializing() => ValidateOverrides();

    public GameCandidate Apply(GameCandidate detected)
    {
        var effective = ApiOverride is { } api ? detected.WithApi(api) : detected;
        effective = effective with
        {
            PreferredRoute = PreferredRoute ?? detected.VerifiedInputRoute,
            ProxyName = ProxyName ?? (effective.Api == detected.Api ? detected.VerifiedProxyName : null)
        };
        return LaunchArguments is { } args ? effective with { SuggestedArguments = args } : effective;
    }
}

enum LibraryFilter { All, Installed, Favourites }
sealed record LibraryGame(GameCandidate DetectedGame, string Store = "Manual", bool Installed = false);
sealed record LibraryEntry(GameCandidate DetectedGame, GameCandidate EffectiveGame, string Store, bool Installed,
    bool Favourite, bool Hidden);
sealed record LibraryGroup(string Name, IReadOnlyList<LibraryEntry> Games);
sealed record VulkanWrapper(string Path, string Kind, CpuArch Architecture);
