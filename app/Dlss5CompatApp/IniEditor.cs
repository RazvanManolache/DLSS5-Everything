namespace Dlss5CompatApp;

static class IniEditor
{
    public static void RemoveValue(string path, string section, string key)
    {
        if (!File.Exists(path)) return;
        var lines = File.ReadAllLines(path).ToList();
        var sectionStart = FindSection(lines, section);
        if (sectionStart < 0) return;
        var removed = false;
        for (var i = FindSectionEnd(lines, sectionStart) - 1; i > sectionStart; i--)
        {
            var separator = lines[i].IndexOf('=');
            if (separator < 0 || !lines[i][..separator].Trim().Equals(key, StringComparison.OrdinalIgnoreCase)) continue;
            lines.RemoveAt(i);
            removed = true;
        }
        if (removed) File.WriteAllLines(path, lines);
    }

    public static void SetValue(string path, string section, string key, string value)
    {
        var lines = File.Exists(path) ? File.ReadAllLines(path).ToList() : [];
        var sectionStart = FindSection(lines, section);
        if (sectionStart < 0)
        {
            if (lines.Count > 0 && !string.IsNullOrWhiteSpace(lines[^1])) lines.Add("");
            lines.Add($"[{section}]");
            lines.Add($"{key}={value}");
            File.WriteAllLines(path, lines);
            return;
        }

        var sectionEnd = FindSectionEnd(lines, sectionStart);
        var existing = -1;
        for (var i = sectionStart + 1; i < sectionEnd; i++)
        {
            // Match the same whitespace-tolerant syntax used by GetValue.
            // Leaving an earlier spaced key would make readers disagree about
            // whether the requested renderer, switch or wrapper option won.
            var separator = lines[i].IndexOf('=');
            if (separator < 0 || !lines[i][..separator].Trim().Equals(key, StringComparison.OrdinalIgnoreCase)) continue;
            if (existing < 0) { existing = i; lines[i] = $"{key}={value}"; }
            else { lines.RemoveAt(i--); sectionEnd--; }
        }
        if (existing >= 0)
        {
            File.WriteAllLines(path, lines);
            return;
        }

        lines.Insert(sectionEnd, $"{key}={value}");
        File.WriteAllLines(path, lines);
    }

    public static void AddCsvValue(string path, string section, string key, string value)
    {
        var existing = GetValue(path, section, key);
        if (string.IsNullOrWhiteSpace(existing))
        {
            SetValue(path, section, key, value);
            return;
        }

        var values = existing.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        if (!values.Any(v => v.Equals(value, StringComparison.OrdinalIgnoreCase)))
            values.Add(value);
        SetValue(path, section, key, string.Join(",", values));
    }

    public static void SetCsvDefinition(string path, string section, string key, string definition)
    {
        var name = definition.Split('=', 2)[0].Trim();
        var existing = GetValue(path, section, key);
        if (string.IsNullOrWhiteSpace(existing))
        {
            SetValue(path, section, key, definition);
            return;
        }

        var values = existing.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(v => !v.StartsWith(name + "=", StringComparison.OrdinalIgnoreCase) &&
                        !v.Equals(name, StringComparison.OrdinalIgnoreCase))
            .ToList();
        values.Add(definition);
        SetValue(path, section, key, string.Join(",", values));
    }

    internal static string? GetValue(string path, string section, string key)
    {
        if (!File.Exists(path)) return null;
        var lines = File.ReadAllLines(path).ToList();
        var sectionStart = FindSection(lines, section);
        if (sectionStart < 0) return null;
        var sectionEnd = FindSectionEnd(lines, sectionStart);
        for (var i = sectionStart + 1; i < sectionEnd; i++)
        {
            var separator = lines[i].IndexOf('=');
            if (separator >= 0 && lines[i][..separator].Trim().Equals(key, StringComparison.OrdinalIgnoreCase))
                return lines[i][(separator + 1)..].Trim();
        }

        return null;
    }

    static int FindSection(List<string> lines, string section)
    {
        var header = "[" + section + "]";
        for (var i = 0; i < lines.Count; i++)
        {
            if (lines[i].Trim().Equals(header, StringComparison.OrdinalIgnoreCase))
                return i;
        }

        return -1;
    }

    static int FindSectionEnd(List<string> lines, int sectionStart)
    {
        for (var i = sectionStart + 1; i < lines.Count; i++)
        {
            var trimmed = lines[i].Trim();
            if (trimmed.StartsWith("[", StringComparison.Ordinal) && trimmed.EndsWith("]", StringComparison.Ordinal))
                return i;
        }

        return lines.Count;
    }
}
