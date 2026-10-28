using System.Globalization;
using System.Text.Json;

namespace Dlss5CompatApp;

/// <summary>Scriptable access to the same authenticated client used by the Controls window.</summary>
internal static class PhoenixControlCli
{
    internal sealed record Request(string Executable, int Pid, PhoenixOperation Operation,
        PhoenixControlSettings? Settings, string? Session, string Output, int TimeoutSeconds);

    internal static PhoenixControlSettings ParseSettings(string json)
    {
        if (System.Text.Encoding.UTF8.GetByteCount(json) > 32768)
            throw new ArgumentException("Settings exceed 32 KiB.");
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 4 });
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("Settings must be an object.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var settings = new PhoenixControlSettings();
        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (!seen.Add(property.Name)) throw new ArgumentException("Duplicate setting: " + property.Name);
            var definition = PhoenixControlContract.Settings.SingleOrDefault(x => x.WireKey == property.Name)
                ?? throw new ArgumentException("Unknown setting: " + property.Name);
            var value = property.Value;
            if (definition.ValueType == typeof(bool) && value.ValueKind is JsonValueKind.True or JsonValueKind.False)
                settings.Set(definition.Key, value.GetBoolean());
            else if (definition.ValueType == typeof(int) && value.TryGetInt32(out var integer))
                settings.Set(definition.Key, integer);
            else if (definition.ValueType == typeof(float) && value.ValueKind == JsonValueKind.Number)
                settings.Set(definition.Key, value.GetSingle());
            else if (definition.ValueType == typeof(string) && value.ValueKind == JsonValueKind.String)
                settings.Set(definition.Key, value.GetString()!);
            else throw new ArgumentException("Incorrect type for " + property.Name);
        }
        _ = settings.Copy(); // Validate relationships before any request can be sent.
        return settings;
    }

    internal static Request Parse(string[] args)
    {
        if (args.Length < 2 || args[0] != "--phoenix-control") throw new ArgumentException("Expected --phoenix-control <game.exe>.");
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 2; index < args.Length; index += 2)
        {
            if (args[index] is not ("--op" or "--pid" or "--session" or "--settings" or "--output" or "--timeout") ||
                index + 1 == args.Length || !options.TryAdd(args[index], args[index + 1]))
                throw new ArgumentException("Unknown, duplicate or incomplete option.");
        }
        if (!options.TryGetValue("--op", out var name) ||
            !new[] { "status", "set", "reset_history", "screenshot", "screenshot_pair", "save", "validate_model" }.Contains(name))
            throw new ArgumentException("Unsupported operation.");
        var operation = name switch
        {
            "reset_history" => PhoenixOperation.ResetHistory, "validate_model" => PhoenixOperation.ValidateModel,
            "screenshot_pair" => PhoenixOperation.ScreenshotPair,
            _ => Enum.Parse<PhoenixOperation>(name, true)
        };
        if (!options.TryGetValue("--pid", out var pidText) || !int.TryParse(pidText, out var pid) || pid <= 0 ||
            !options.TryGetValue("--output", out var output)) throw new ArgumentException("Explicit --pid and --output are required.");
        var session = options.GetValueOrDefault("--session");
        if (operation != PhoenixOperation.Status && string.IsNullOrWhiteSpace(session))
            throw new ArgumentException("Observe status first and supply its --session before changing the game.");
        var timeout = options.TryGetValue("--timeout", out var seconds) ? int.Parse(seconds, CultureInfo.InvariantCulture) : 10;
        if (timeout is < 1 or > 30) throw new ArgumentException("Timeout must be 1..30 seconds.");
        if (options.ContainsKey("--settings") != (operation == PhoenixOperation.Set))
            throw new ArgumentException("Only set requires --settings.");
        return new(Path.GetFullPath(args[1]), pid, operation,
            operation == PhoenixOperation.Set ? ParseSettings(options["--settings"]) : null,
            session, Path.GetFullPath(output), timeout);
    }

    public static async Task<int> RunAsync(string[] args)
    {
        try
        {
            if (args.Length == 3 && args[1] == "--contract")
            {
                await File.WriteAllTextAsync(Path.GetFullPath(args[2]), JsonSerializer.Serialize(
                    PhoenixControlContract.Settings.Select(x => new { key = x.WireKey,
                        type = x.ValueType.Name, minimum = x.Minimum, maximum = x.Maximum,
                        choices = x.Choices?.Select(c => c.Value) }), new JsonSerializerOptions { WriteIndented = true }));
                return 0;
            }
            var request = Parse(args);
            var client = new PhoenixControlClient(Path.GetDirectoryName(request.Executable)!, request.Executable);
            var observed = await client.SendAsync(PhoenixOperation.Status,
                timeout: TimeSpan.FromSeconds(request.TimeoutSeconds));
            if (observed.Pid != request.Pid || request.Session is not null && observed.Session != request.Session)
                throw new PhoenixSessionChangedException("The selected process/session is no longer current.");
            if (request.Operation == PhoenixOperation.Set)
                PhoenixControlAvailability.FromSnapshot(observed.Snapshot).Validate(request.Settings!);
            var reply = request.Operation == PhoenixOperation.Status ? observed :
                await client.SendAsync(request.Operation, request.Settings, timeout: TimeSpan.FromSeconds(request.TimeoutSeconds),
                    expectedSession: observed.Session);
            var json = JsonSerializer.Serialize(reply, new JsonSerializerOptions
                { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, WriteIndented = true });
            await File.WriteAllTextAsync(request.Output, json);
            return reply.Ok ? 0 : 3;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine("Phoenix control: " + error.Message);
            return 2;
        }
    }
}
