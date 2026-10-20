using Asset = Dlss5CompatApp.PhoenixReleaseAsset;

namespace Dlss5CompatApp;

static partial class PhoenixReleaseDownloader
{
    internal static IEnumerable<Asset> RuntimeAssets(Asset[] assets) => assets.Where(asset =>
        asset.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) && !ExcludedArchive(asset.Name) &&
        (asset.Name.Replace('_', '-').Contains("Portable-Runtimes", StringComparison.OrdinalIgnoreCase) ||
         asset.Name.Replace('_', '-').Contains("Experimental-WOW64", StringComparison.OrdinalIgnoreCase)));

    static async Task StageRuntimesAsync(HttpClient http, Asset addonAsset, Asset[] assets, string tag,
        string addonArchive, string destination, CancellationToken token, Action<PhoenixDownloadProgress>? progress)
    {
        if (!addonAsset.Name.Equals(AddonFileName, StringComparison.OrdinalIgnoreCase))
            await PhoenixRuntimePackage.ExtractAsync(addonArchive, destination, token);
        var runtimeAssets = RuntimeAssets(assets).ToArray();
        if (runtimeAssets.Length > 2 || runtimeAssets.GroupBy(asset =>
            asset.Name.Replace('_', '-').Contains("Portable-Runtimes", StringComparison.OrdinalIgnoreCase)).Any(group => group.Count() > 1))
            throw new InvalidDataException("Release contains ambiguous Phoenix runtime archives.");
        foreach (var asset in runtimeAssets)
        {
            token.ThrowIfCancellationRequested();
            progress?.Invoke(new PhoenixDownloadProgress("Checking optional Phoenix runtime: " + asset.Name, 0, asset.Size));
            var download = TemporaryPath(Path.GetDirectoryName(destination)!);
            try
            {
                await DownloadAsync(http, asset, download, MaxAssetBytes, token, progress);
                await VerifyChecksumsAsync(http, asset, assets, download, token);
                if (await PhoenixRuntimePackage.ExtractAsync(download, destination, token) == 0)
                    throw new InvalidDataException("Runtime archive has no PhoenixRuntime directory: " + asset.Name);
            }
            finally { DeleteTemporary(download); }
        }
    }
}
