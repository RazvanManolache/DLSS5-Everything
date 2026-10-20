using System.Text.Json;

namespace Dlss5CompatApp;

internal sealed record ScreenStudioSettings
{
    public bool Enabled { get; init; } = true;
    public int Iterations { get; init; } = 1;
    public int Fps { get; init; } = 60;
    public float Scale { get; init; } = 1;
    public int Comparison { get; init; }
    public float Divider { get; init; } = .5f;
    public float DifferenceGain { get; init; } = 4;
    public bool Residual { get; init; } = true;
    public float ResidualStrength { get; init; } = 1;
    public bool Cursor { get; init; } = true;
    public float Tone { get; init; } = 1;
    public float Structure { get; init; } = 1;
    public float Skin { get; init; } = -1;
    public bool AutoMask { get; init; }
    public bool Lens { get; init; }
    public float Fov { get; init; } = 110;
    public float Vignette { get; init; }
    public float Aberration { get; init; }
    public float Noise { get; init; }
    public bool Hdr { get; init; }
    public float PaperWhite { get; init; } = 203;
    public bool Spout { get; init; }
    public bool VirtualCamera { get; init; }
    public string SpoutName { get; init; } = "DLSS5 Everything";
    public string ScreenshotDirectory { get; init; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "DLSS5 Everything");
    public string ScreenshotFormat { get; init; } = "png";
    public Keys StopKey { get; init; } = Keys.End;
    public Keys ToggleKey { get; init; } = Keys.F8;
    public Keys CompareKey { get; init; } = Keys.F9;
    public Keys ScreenshotKey { get; init; } = Keys.F10;
    public void Validate()
    {
        if (Iterations is < 1 or > 10 || Fps is < 0 or > 240 || Comparison is < 0 or > 3 ||
            !Between(Scale, .25f, 1) || !Between(Divider, 0, 1) || !Between(DifferenceGain, 1, 64) ||
            !Between(ResidualStrength, 0, 2) || !Between(Tone, 0, 2) || !Between(Structure, 0, 2) || !Between(Skin, -1, 2) ||
            !Between(Fov, 30, 160) || !Between(Vignette, 0, 1) || !Between(Aberration, 0, 1) || !Between(Noise, 0, 1))
            throw new InvalidDataException("Invalid Screen Studio settings.");
        if (ScreenshotFormat is not ("png" or "jpeg")) throw new InvalidDataException("Choose PNG or JPEG screenshots.");
        if (!Between(PaperWhite, 80, 400))
            throw new InvalidDataException("Invalid HDR settings.");
        if (VirtualCamera && Hdr) throw new InvalidDataException("Virtual webcam output requires SDR. Turn off HDR transport.");
        if (string.IsNullOrWhiteSpace(SpoutName) || SpoutName.Length > 100 || SpoutName.Any(c => c < 32 || c > 126))
            throw new InvalidDataException("Spout names must contain 1..100 printable ASCII characters.");
        _ = LocalPaths.Normalize(ScreenshotDirectory);
        var keys = new[] { StopKey, ToggleKey, CompareKey, ScreenshotKey };
        if (keys.Distinct().Count() != keys.Length || keys.Any(key => !AllowedHotkeys.Contains(key)))
            throw new InvalidDataException("Screen Studio hotkeys must be distinct function keys, End, Home, or Pause.");
    }
    static bool Between(float value, float min, float max) => float.IsFinite(value) && value >= min && value <= max;
    public static IReadOnlyList<Keys> AllowedHotkeys { get; } =
        [Keys.End, Keys.Home, Keys.Pause, Keys.F1, Keys.F2, Keys.F3, Keys.F4, Keys.F5, Keys.F6, Keys.F7, Keys.F8, Keys.F9, Keys.F10, Keys.F11, Keys.F12];

    public PhoenixSettings Phoenix() => new()
    {
        Enabled = true,
        OverrideStrengths = true,
        Advanced = true,
        LocalTone = Tone,
        LocalStructure = Structure,
        SkinStructure = Skin,
        AutoMask = AutoMask,
        Comparison = 1,
        SourceA = 2,
        SourceB = 2,
        ComparisonLabels = false,
        ComparisonLines = false
    };

    public Dictionary<string, object> Worker(long revision, int screenshot = 0)
    {
        Validate();
        if (revision < 0 || screenshot < 0) throw new ArgumentOutOfRangeException(nameof(revision));
        return new()
        {
            ["schema"] = 1,
            ["revision"] = revision,
            ["enabled"] = Enabled,
            ["iterations"] = Iterations,
            ["fps"] = Fps,
            ["scale"] = Scale,
            ["comparison"] = Comparison,
            ["divider"] = Divider,
            ["gain"] = DifferenceGain,
            ["residual"] = Residual,
            ["residual_strength"] = ResidualStrength,
            ["lens"] = Lens,
            ["fov"] = Fov,
            ["vignette"] = Vignette,
            ["aberration"] = Aberration,
            ["noise"] = Noise,
            ["hdr"] = Hdr,
            ["paper_white"] = PaperWhite,
            ["spout"] = Spout,
            ["virtual_camera"] = VirtualCamera,
            ["spout_name"] = SpoutName,
            ["screenshot"] = screenshot
        };
    }

    public PhoenixControlSettings LiveControls() => new PhoenixControlSettings()
        .Set(PhoenixSetting.OverrideStrengths, true).Set(PhoenixSetting.Advanced, true)
        .Set(PhoenixSetting.Tone, Tone).Set(PhoenixSetting.Structure, Structure)
        .Set(PhoenixSetting.Skin, Skin).Set(PhoenixSetting.AutoMask, AutoMask);
}

internal sealed class ScreenStudioPreferences
{
    public sealed record CameraChoice(string DeviceId, int Width, int Height, int Fps, bool DeviceReported = false);
    public int Schema { get; set; } = 1;
    public ScreenStudioSettings Settings { get; set; } = new();
    public CameraChoice? Camera { get; set; }
    public Dictionary<string, ScreenStudioSettings> Presets { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public static string DefaultPath => Path.Combine(LocalData.DirectoryPath, "screen-studio.json");
    public static ScreenStudioPreferences Load(string? path = null)
    {
        path ??= DefaultPath;
        if (!File.Exists(path)) return new();
        var value = JsonSerializer.Deserialize<ScreenStudioPreferences>(LocalData.ReadText(path, 128 * 1024), LocalData.Json)
            ?? throw new InvalidDataException("Screen Studio preferences are empty.");
        value.Validate(); return value;
    }
    public void Validate()
    {
        if (Schema != 1 || Settings is null || Presets is null || Presets.Count > 40)
            throw new InvalidDataException("Unsupported Screen Studio preferences.");
        Settings.Validate();
        if (Camera is { } camera)
            CameraDevices.Validate(new("camera", 0, 0, "Saved camera") { DeviceId = camera.DeviceId,
                CaptureWidth = camera.Width, CaptureHeight = camera.Height, CaptureFps = camera.Fps }, new(), virtualInstalled: true);
        foreach (var (name, preset) in Presets)
        {
            if (!ValidName(name) || preset is null) throw new InvalidDataException("Invalid Screen Studio preset.");
            preset.Validate();
        }
    }
    public static bool ValidName(string name) => !string.IsNullOrWhiteSpace(name) && name.Length <= 60 &&
        name == name.Trim() && !name.Any(char.IsControl);
    public void Save(string? path = null)
    {
        Validate(); path ??= DefaultPath;
        using var gate = LocalData.Lock(path);
        LocalData.WriteAtomic(path, JsonSerializer.Serialize(this, LocalData.Json));
    }
}
