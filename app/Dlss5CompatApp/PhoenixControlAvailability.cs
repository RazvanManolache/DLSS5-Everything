using System.Text.Json;

namespace Dlss5CompatApp;

// Backbuffer capture is a color-only direct feed. It cannot acquire engine
// motion/depth merely because a vendor upscaler DLL is installed.
internal readonly record struct PhoenixControlAvailability(bool ColorOnlyDirectFeed,
    IReadOnlyDictionary<string, string>? Devices = null)
{
    public static PhoenixControlAvailability FromSnapshot(JsonElement snapshot)
    {
        Dictionary<string,string>? devices=null;
        foreach (var candidate in new[] { snapshot,
            snapshot.TryGetProperty("helper",out var helper) ? helper : default,
            snapshot.TryGetProperty("in_process",out var local) ? local : default })
            if (candidate.ValueKind==JsonValueKind.Object && candidate.TryGetProperty("execution_devices",out var entries) && entries.ValueKind==JsonValueKind.Array)
                foreach (var entry in entries.EnumerateArray())
                    if (entry.TryGetProperty("id",out var id) && id.ValueKind==JsonValueKind.String && PhoenixSettingDefinition.ValidDevice(id.GetString()!))
                    {
                        devices ??= new(StringComparer.Ordinal);
                        devices[id.GetString()!]=entry.TryGetProperty("label",out var label) ? label.GetString() ?? id.GetString()! : id.GetString()!;
                    }
        return new(snapshot.TryGetProperty("history_policy", out var policy) && policy.ValueKind == JsonValueKind.String &&
            policy.GetString() == "spatial_reset_no_engine_motion_vectors", devices);
    }

    public bool Allows(PhoenixSetting key, object value) => key==PhoenixSetting.ExecutionDevice && Devices is not null
        ? Devices.ContainsKey((string)value) : !ColorOnlyDirectFeed || key switch
    {
        PhoenixSetting.Comparison => Convert.ToInt32(value) != 0,
        PhoenixSetting.BudgetMode => Convert.ToInt32(value) == 0,
        PhoenixSetting.Upscaler => Convert.ToInt32(value) == 0,
        PhoenixSetting.HookStage => Convert.ToInt32(value) is 0 or 3,
        _ => true
    };

    public string Reason(PhoenixSetting key) => key switch
    {
        PhoenixSetting.Comparison => "Direct capture supports pair comparison and difference views; three-way comparison is unavailable.",
        PhoenixSetting.BudgetMode => "Direct capture uses a companion GPU-time budget. Its hidden-window FPS is not the game's presentation FPS.",
        PhoenixSetting.Upscaler => "Direct capture has no engine depth/motion guides. Only spatial upscaling is available; installing a vendor DLL does not supply those guides.",
        PhoenixSetting.HookStage => "Final-frame capture supports Off and Present. Render and Upscaled require an observed game DLSS feature.",
        PhoenixSetting.ExecutionDevice => "The selected runner is unavailable in the active runtime package or on this machine. Choose an advertised execution device.",
        _ => "Unavailable on this capture route."
    };

    public void Validate(PhoenixControlSettings settings)
    {
        foreach (var pair in settings.Copy())
        {
            var key = PhoenixControlContract.Settings.Single(item => item.WireKey == pair.Key).Key;
            if (!Allows(key, pair.Value)) throw new InvalidOperationException(Reason(key));
        }
    }
}
