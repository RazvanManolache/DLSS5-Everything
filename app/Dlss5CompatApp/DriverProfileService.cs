using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Dlss5CompatApp;

internal interface IDriverProfile : IDisposable
{
    uint? Read(uint setting);
    void Write(uint setting, uint? value);
    void Commit();
}

internal sealed class NvidiaGameProfile : IDriverProfile
{
    readonly NvDrs api;
    readonly nint profile;
    internal NvDrs.ProfileDetails Details => api.DescribeProfile(profile);
    internal NvidiaGameProfile(string executable)
    {
        executable = Path.GetFullPath(executable);
        if (!File.Exists(executable) || !Path.GetExtension(executable).Equals(".exe", StringComparison.OrdinalIgnoreCase))
            throw new FileNotFoundException("Choose an existing game executable.", executable);
        api = NvDrs.Open();
        try
        {
            profile = api.GetOrCreateProfileForExe(executable, "Phoenix: " + executable);
            if (profile == api.BaseProfile()) throw new InvalidOperationException("Global driver profiles cannot be edited here.");
        }
        catch { api.Dispose(); throw; }
    }
    public uint? Read(uint setting) => api.Get(profile, setting);
    public void Write(uint setting, uint? value) { if (value is { } number) api.Set(profile, setting, number); else api.Delete(profile, setting); }
    public void Commit() => api.Save();
    public void Dispose() => api.Dispose();
}

internal sealed record DriverSettingChange(uint? Original, uint? Written);
internal sealed record DriverProfileJournal(int Schema, string Executable,
    Dictionary<uint, DriverSettingChange> Settings, string State = "pending");

internal static class DriverProfileService
{
    internal static readonly IReadOnlyDictionary<uint, string> Allowed = new Dictionary<uint, string>
    {
        [0x10E41DF3] = "DLSS SR preset", [0x10E41DF7] = "DLSS RR preset", [0x10E41DF1] = "DLSS FG preset",
        [0x10E41E01] = "DLSS SR override", [0x10E41E02] = "DLSS RR override", [0x10E41E03] = "DLSS FG override",
        [0x10835002] = "Driver frame limit", [0x1057EB71] = "Power management",
        [0xB0D384C0] = "Smooth Motion", [0x00DD48FB] = "RTX HDR"
    };
    internal static string JournalPath(string executable, string? root = null)
    {
        var identity = Path.GetFullPath(executable).ToUpperInvariant();
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant();
        return Path.Combine(root ?? Path.Combine(LocalData.DirectoryPath, "DriverProfiles"), digest + ".json");
    }
    static DriverProfileJournal ReadJournal(string path, string executable)
    {
        if (!File.Exists(path)) return new(1, Path.GetFullPath(executable), []);
        var journal = JsonSerializer.Deserialize<DriverProfileJournal>(LocalData.ReadText(path, 65536), LocalData.Json)
            ?? throw new InvalidDataException("Invalid driver restoration record.");
        if (journal.Schema != 1 || !Path.GetFullPath(journal.Executable).Equals(Path.GetFullPath(executable), StringComparison.OrdinalIgnoreCase)
            || journal.Settings is null || journal.Settings.Count > Allowed.Count || journal.Settings.Keys.Any(k => !Allowed.ContainsKey(k)))
            throw new InvalidDataException("Driver restoration record does not match this game.");
        return journal;
    }
    internal static void Apply(string executable, IReadOnlyDictionary<uint, uint?> settings,
        Func<string, IDriverProfile>? open = null, string? journalRoot = null)
    {
        if (settings.Count == 0) return;
        if (settings.Keys.Any(k => !Allowed.ContainsKey(k))) throw new InvalidDataException("Unsupported driver setting.");
        var path = JournalPath(executable, journalRoot);
        var previous = ReadJournal(path, executable);
        using var profile = (open ?? (exe => new NvidiaGameProfile(exe)))(executable);
        var changes = new Dictionary<uint, DriverSettingChange>(previous.Settings);
        foreach (var (setting, value) in settings)
        {
            var current = profile.Read(setting);
            var exists = changes.TryGetValue(setting, out var prior);
            if (exists && current != prior!.Written)
                throw new InvalidOperationException("Another application changed " + Allowed[setting] + "; restore or review it first.");
            changes[setting] = new(exists ? prior!.Original : current, value);
        }
        var pending = new DriverProfileJournal(1, Path.GetFullPath(executable), changes);
        LocalData.WriteAtomic(path, JsonSerializer.Serialize(pending, LocalData.Json));
        foreach (var (setting, value) in settings) profile.Write(setting, value);
        profile.Commit();
        LocalData.WriteAtomic(path, JsonSerializer.Serialize(pending with { State = "applied" }, LocalData.Json));
    }
    internal static IReadOnlyList<string> Restore(string executable, Func<string, IDriverProfile>? open = null,
        string? journalRoot = null)
    {
        var path = JournalPath(executable, journalRoot);
        if (!File.Exists(path)) return [];
        var journal = ReadJournal(path, executable);
        using var profile = (open ?? (exe => new NvidiaGameProfile(exe)))(executable);
        var remaining = new Dictionary<uint, DriverSettingChange>(journal.Settings);
        var conflicts = new List<string>();
        foreach (var (setting, change) in journal.Settings)
        {
            if (profile.Read(setting) != change.Written) { conflicts.Add(Allowed[setting]); continue; }
            profile.Write(setting, change.Original); remaining.Remove(setting);
        }
        profile.Commit();
        LocalData.WriteAtomic(path, JsonSerializer.Serialize(journal with { Settings = remaining, State = "restored" }, LocalData.Json));
        return conflicts;
    }
}
