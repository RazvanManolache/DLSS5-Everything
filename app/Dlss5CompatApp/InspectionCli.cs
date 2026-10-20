using System.Text.Json;
using System.Text.Json.Serialization;

namespace Dlss5CompatApp;

/// <summary>Produces a read-only inventory with the same detector used by installation.</summary>
static class InspectionCli
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length is not (4 or 6) || args[2] != "--output" || args.Length == 6 && args[4] != "--payload") return 2;
        var target = Path.GetFullPath(args[1]);
        var warnings = new List<string>();
        var games = File.Exists(target)
            ? GameScanner.ScanSingleExe(target) is { } game ? new[] { game } : []
            : await GameScanner.ScanAsync(target, cancellationToken: CancellationToken.None, warning: warnings.Add);
        var options = new JsonSerializerOptions { WriteIndented = true };
        options.Converters.Add(new JsonStringEnumConverter());
        var payload = args.Length == 6 ? PayloadScanner.Scan(Path.GetFullPath(args[5])) : null;
        await File.WriteAllTextAsync(Path.GetFullPath(args[3]), JsonSerializer.Serialize(new
        {
            Schema = "dlss5-install-inventory/1", CreatedUtc = DateTimeOffset.UtcNow,
            Target = target, Games = games, Warnings = warnings,
            Plans = games.Select(game => new
            {
                game.ExePath, Plan = PackagedPhoenixPlan.For(game, payload),
                EngineBootstrap = EngineBootstrap.Find(game.ExePath),
                Missing = payload?.MissingFor(game),
                Preflight = InstallerEngine.CheckPreflight(game, payload)
            }).ToArray()
        }, options));
        return 0;
    }
}
