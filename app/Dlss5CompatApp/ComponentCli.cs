namespace Dlss5CompatApp;

internal static class ComponentCli
{
    internal sealed record ImportRequest(string PayloadRoot, string? PhoenixPath, string? ModelPath, bool Update = false);

    public static bool IsImportCommand(string[] args) => args.Length > 0 &&
        args[0].Equals("--components-import", StringComparison.OrdinalIgnoreCase);

    public static bool IsUpdateCommand(string[] args) => args.Length > 0 &&
        args[0].Equals("--components-update", StringComparison.OrdinalIgnoreCase);

    internal static ImportRequest Parse(string[] args)
    {
        if (!IsImportCommand(args) && !IsUpdateCommand(args)) throw new ArgumentException("Expected --components-import or --components-update.");
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 1; i < args.Length; i += 2)
        {
            if (args[i] is not ("--payload" or "--phoenix" or "--model") || i + 1 >= args.Length ||
                string.IsNullOrWhiteSpace(args[i + 1]) || args[i + 1].StartsWith("--", StringComparison.Ordinal) ||
                !values.TryAdd(args[i], args[i + 1]))
                throw new ArgumentException("Use --components-import --payload <folder> [--phoenix <addon>] [--model <DLL>].");
        }
        if (!values.TryGetValue("--payload", out var root) || !IsUpdateCommand(args) && !values.ContainsKey("--phoenix") && !values.ContainsKey("--model"))
            throw new ArgumentException("A payload folder and at least one explicitly selected component are required.");
        return new(LocalPaths.Normalize(Path.GetFullPath(root)), values.GetValueOrDefault("--phoenix"), values.GetValueOrDefault("--model"), IsUpdateCommand(args));
    }

    public static async Task<int> RunAsync(string[] args)
    {
        try
        {
            var request = Parse(args);
            if (request.Update)
                await PayloadBootstrapper.EnsureCurrentAsync(request.PayloadRoot,
                    new Progress<BootstrapProgress>(progress => Console.WriteLine(progress.Message)),
                    CancellationToken.None, request.PhoenixPath, replaceLocalDevelopment: true);
            else if (request.PhoenixPath is { } phoenix)
            {
                var result = await PhoenixReleaseDownloader.ImportLocalAsync(phoenix, request.PayloadRoot, CancellationToken.None);
                Console.WriteLine("Compatible local Phoenix DLSS5 imported: " + result.Sha256);
            }
            if (request.ModelPath is { } model)
            {
                var result = await PhoenixReleaseDownloader.ImportLocalModelAsync(model, request.PayloadRoot, CancellationToken.None);
                Console.WriteLine("Local model imported (runtime validation required): " + result.Sha256);
            }
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine("Component import failed: " + error.Message);
            return 1;
        }
    }
}
