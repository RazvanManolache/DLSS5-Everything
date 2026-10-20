using System.Text.Json;

namespace Dlss5CompatApp.Tests;

public class ScreenMediaTests
{
    [Fact]
    public void MediaDefaultsDoNotEnableSharingOrHdr()
    {
        var settings = new ScreenStudioSettings(); var wire = settings.Worker(1);
        Assert.False(settings.Hdr); Assert.False(settings.Spout);
        Assert.False((bool)wire["hdr"]); Assert.False((bool)wire["spout"]);
    }
    [Fact]
    public void MediaSettingsRejectInvalidNamesAndBounds()
    {
        foreach (var settings in new ScreenStudioSettings[] { new() { PaperWhite = float.NaN }, new() { PaperWhite = 401 },
            new() { SpoutName = "bad\0name" }, new() { SpoutName = "" }, new() { SpoutName = "\u00e9" } })
            Assert.Throws<InvalidDataException>(settings.Validate);
    }
    [Fact]
    public void MediaSettingsRoundTripUiAndWorker()
    {
        Sta(() =>
        {
            using var view = new ScreenStudioView(() => null, () => "", () => UiPalette.Dark, true);
            var settings = new ScreenStudioSettings { Hdr = true, PaperWhite = 240, Spout = true, SpoutName = "Example" };
            view.LoadSettings(settings); Assert.Equal(settings, view.ReadSettings());
            Assert.Equal(240f, settings.Worker(3)["paper_white"]);
        });
    }
    [Fact]
    public void VideoMetadataUsesValidatedRationalRate()
    {
        var result = ScreenMediaConverter.ReadVideoMetadata("{\"streams\":[{\"codec_type\":\"audio\"},{\"codec_type\":\"video\",\"width\":640,\"height\":360,\"avg_frame_rate\":\"30000/1001\"}]}");
        Assert.Equal((640, 360, "30000/1001"), result);
        foreach (var fps in new[] { "0/0", "300/1", "-1/1", "60;evil", "1/0" })
            Assert.Throws<InvalidDataException>(() => ScreenMediaConverter.ReadVideoMetadata(JsonSerializer.Serialize(new { streams = new[] { new { codec_type = "video", width = 640, height = 360, avg_frame_rate = fps } } })));
        Assert.Throws<InvalidDataException>(() => ScreenMediaConverter.ReadVideoMetadata("{\"streams\":[]}"));
    }
    [Fact]
    public void ConversionNeverOverwritesOrAcceptsWrongOutputType()
    {
        using var workspace = new TestWorkspace(); var input = workspace.FilePath("source.png"); var output = workspace.FilePath("output.png"); File.WriteAllText(input, "input");
        new ScreenMediaOptions(input, output).Validate(); File.WriteAllText(output, "original");
        Assert.Throws<IOException>(() => new ScreenMediaOptions(input, output).Validate()); Assert.Equal("original", File.ReadAllText(output));
        Assert.Throws<InvalidDataException>(() => new ScreenMediaOptions(input, workspace.FilePath("bad.mp4")).Validate());
        Assert.Throws<IOException>(() => new ScreenMediaOptions(input, input).Validate());
    }
    [Fact]
    public async Task CancelledMediaProcessDoesNotStart()
    {
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ScreenMediaConverter.RunToolAsync("missing.exe", [], cancellation.Token));
    }
    [Fact]
    public void CompletionPublishedDuringWorkerExitIsNotLost()
    {
        var reads = 0; var status = new ScreenStudioStatus(1,"running","",1,320,240,320,240,10,1,0);
        var observed = ScreenMediaConverter.ObserveWorker(() => ++reads == 1 ? status : status with { State="completed", Converted=3 }, () => false);
        Assert.Equal("completed", observed!.State); Assert.Equal(3, observed.Converted); Assert.Equal(2, reads);
        Assert.Throws<IOException>(() => ScreenMediaConverter.ObserveWorker(() => status, () => false));
        Assert.Throws<IOException>(() => ScreenMediaConverter.ObserveWorker(() => status with { State="failed", Error="failure" }, () => true));
    }
    [Fact]
    public void LegacyRecordingPreferencesAreIgnoredAndNotSentToTheWorker()
    {
        var settings = JsonSerializer.Deserialize<ScreenStudioSettings>("""
            {"Tone":1.5,"RecordingAudio":true,"RecordingFps":0,"RecordingDirectory":"old-folder","RecordingKey":118}
            """)!;
        settings.Validate();
        Assert.Equal(1.5f, settings.Tone);
        var wire = settings.Worker(1);
        Assert.DoesNotContain(wire.Keys, key => key.StartsWith("record", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain("Recording", JsonSerializer.Serialize(settings));
    }
    static void Sta(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() => { try { action(); } catch (Exception ex) { error = ex; } }); thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (error is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
    }
}
