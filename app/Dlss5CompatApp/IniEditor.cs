namespace Dlss5CompatApp;

static class IniEditor
{
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
        for (var i = sectionStart + 1; i < sectionEnd; i++)
        {
            if (!lines[i].StartsWith(key + "=", StringComparison.OrdinalIgnoreCase)) continue;
            lines[i] = $"{key}={value}";
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

    static string? GetValue(string path, string section, string key)
    {
        if (!File.Exists(path)) return null;
        var lines = File.ReadAllLines(path).ToList();
        var sectionStart = FindSection(lines, section);
        if (sectionStart < 0) return null;
        var sectionEnd = FindSectionEnd(lines, sectionStart);
        for (var i = sectionStart + 1; i < sectionEnd; i++)
        {
            if (lines[i].StartsWith(key + "=", StringComparison.OrdinalIgnoreCase))
                return lines[i][(key.Length + 1)..];
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
