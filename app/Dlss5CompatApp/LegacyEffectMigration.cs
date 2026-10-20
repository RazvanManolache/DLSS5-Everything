namespace Dlss5CompatApp;

sealed record LegacyPresetChange(string Source, string Destination, string Ini);

static class LegacyEffectMigration
{
    internal static bool IsLegacyTechnique(string value) =>
        value.Split('@').Last().Trim().Equals("DLSS5_Feed.fx", StringComparison.OrdinalIgnoreCase);

    // Only the superseded feeder effect is disabled; other user effects and
    // their tuning survive. Shared presets are copied into this game first.
    public static IReadOnlyList<LegacyPresetChange> Find(GameCandidate game)
    {
        var directory = Path.GetDirectoryName(game.ExePath)!;
        var result = new List<LegacyPresetChange>();
        foreach (var name in new[] { "ReShade.ini", "ReShadeVR.ini" })
        {
            var ini = Path.Combine(directory, name);
            var preset = IniEditor.GetValue(ini, "GENERAL", "PresetPath");
            var path = Path.GetFullPath(string.IsNullOrWhiteSpace(preset) ? "ReShadePreset.ini" : preset, directory);
            if (!File.Exists(path)) continue;
            LocalPaths.RequireNoLinks(path);
            if (new FileInfo(path).Length > 4 * 1024 * 1024)
                throw new InvalidDataException("ReShade preset exceeds the migration size limit: " + path);
            var legacy = new[] { "Techniques", "TechniqueSorting" }.Any(key =>
                (IniEditor.GetValue(path, "GENERAL", key) ?? "").Split(',').Any(IsLegacyTechnique));
            if (!legacy) continue;
            var destination = LocalPaths.IsInside(path, game.Root) ? path :
                Path.Combine(directory, "Phoenix DLSS5." + Path.GetFileNameWithoutExtension(name) + "Preset.ini");
            result.Add(new(path, destination, ini));
        }
        return result;
    }

    public static void Filter(string preset)
    {
        foreach (var key in new[] { "Techniques", "TechniqueSorting" })
        {
            var value = IniEditor.GetValue(preset, "GENERAL", key);
            if (value is null) continue;
            IniEditor.SetValue(preset, "GENERAL", key, string.Join(',', value.Split(',').Where(item => !IsLegacyTechnique(item))));
        }
    }
}
