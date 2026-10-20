using Dlss5CompatApp;
using System.Text.Json;

if (args.Length != 1) throw new ArgumentException("test-payload");
var root = Path.GetFullPath(args[0]);
using var handler = new CountRequests();
using var http = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(20) };
http.DefaultRequestHeaders.UserAgent.ParseAdd("Dlss5CompatApp-NvidiaProbe/1.0");
var progress = new PrintProgress();
await PayloadBootstrapper.EnsureNvidiaPackagesAsync(http, root, progress, default);
var downloaded = handler.Assets;
await PayloadBootstrapper.EnsureNvidiaPackagesAsync(http, root, progress, default);
if (handler.Assets != downloaded) throw new InvalidOperationException("Cache verification redownloaded a package.");
var source = PayloadBootstrapper.NvidiaSources.Single();
var packageRoot = Path.Combine(root, "Nvidia", source.Id);
var packages = Directory.GetDirectories(packageRoot).Select(folder => new {
    folder, receipt = JsonSerializer.Deserialize<PayloadBootstrapper.NvidiaReceipt>(File.ReadAllText(Path.Combine(folder, "package.json")))
}).ToArray();
var selected = PayloadScanner.Scan(root).NvidiaDlls;
if (!selected.Any(file => Path.GetDirectoryName(file)!.StartsWith(packageRoot, StringComparison.OrdinalIgnoreCase)))
    throw new InvalidOperationException("Compatibility package not discovered by scanner.");
LocalData.WriteAtomic(Path.Combine(root, "validation.json"), JsonSerializer.Serialize(new {
    schema = "nvidia-runtime-download-validation/1", passed = true, checked_utc = DateTimeOffset.UtcNow,
    gpu_used = false, games_launched = false, package_asset_downloads = downloaded,
    authenticated_cache_reused = true, packages,
    inference_or_older_gpu_compatibility_verified = false
}, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine("Passed; receipt: " + Path.Combine(root, "validation.json"));

sealed class PrintProgress : IProgress<BootstrapProgress>
{
    public void Report(BootstrapProgress item) => Console.WriteLine(item.Message);
}
sealed class CountRequests : DelegatingHandler
{
    public int Assets { get; private set; }
    public CountRequests() : base(new HttpClientHandler()) { }
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        if (request.RequestUri!.AbsolutePath.Contains("/releases/download/")) Assets++;
        return base.SendAsync(request, token);
    }
}
