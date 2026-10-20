using System.Text.Json;

namespace Dlss5CompatApp.Tests;

public sealed class PhoenixBridgeEvidenceTests
{
    [Theory]
    [InlineData(0, true, "native", true)]
    [InlineData(123, true, "native", false)]
    [InlineData(0, false, "native", false)]
    [InlineData(0, true, "cpu", false)]
    public void InProcessOutputUsesLocalCompletionEvidenceNotAnEmptyHelper(int helperPid, bool enabled,
        string runner, bool complete)
    {
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            execution_mode = "in_process", helper_pid = helperPid, enabled,
            phase = 2, delivered = 10, presented = 10, status = "Same-device GPU",
            applied_settings = new { Backend = 3, ExecutionDevice = "native" },
            helper = new { },
            in_process = new { error = "", execution = new { kind = runner },
                phoenix = new { submitted_frames = 10, status = "Own network output written back" } }
        }));
        var reply = new PhoenixControlResponse("session", 123, "id", true, "observed", null, json.RootElement.Clone(), null);
        Assert.Equal(complete, PhoenixBridgeEvidence.Evaluate(reply, 123).OutputCompleted);
    }

    [Fact]
    public void NativeRecordingCanPermitAPictureWithoutProvingGpuCompletion()
    {
        using var json = JsonDocument.Parse("""
            {"execution":{"kind":"native"},"settings":{"Backend":3},"phoenix":{"submitted_frames":10}}
            """);
        var reply = new PhoenixControlResponse("session", 123, "id", true, "observed", null, json.RootElement.Clone(), null);
        Assert.True(PhoenixBridgeEvidence.NativeFramesRecorded(reply, 123));
        Assert.False(PhoenixBridgeEvidence.NativeFramesRecorded(reply, 124));
        Assert.False(PhoenixBridgeEvidence.Evaluate(reply, 123).OutputCompleted);
    }

    [Fact]
    public void NativePngCaptureRetriesAnUnfinishedWriter()
    {
        using var workspace = new TestWorkspace(); var path = workspace.FilePath("frame.png");
        Assert.False(SmokeTestRunner.CompletePng(path));
        using (var image = new System.Drawing.Bitmap(16, 16)) image.Save(path, System.Drawing.Imaging.ImageFormat.Png);
        Assert.True(SmokeTestRunner.CompletePng(path));
        using (var file = new FileStream(path, FileMode.Open, FileAccess.Write)) file.SetLength(file.Length - 4);
        Assert.False(SmokeTestRunner.CompletePng(path));
    }

    [Theory]
    [InlineData("native", 3, true)]
    [InlineData("cuda", 3, false)]
    [InlineData("vulkan", 3, false)]
    [InlineData("cpu", 3, false)]
    [InlineData("native", 0, false)]
    [InlineData("native", 1, false)]
    public void OnlyExecutedNativePhoenixCanQualifyTheFastInstallation(string runner, int backend, bool expected)
    {
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            phase = 2, delivered = 10, presented = 10, status = "Running",
            applied_settings = new { Backend = backend, ExecutionDevice = "native" },
            helper = new { error = "", execution = new { kind = runner },
                phoenix = new { submitted_frames = 10, status = "Own output written back" },
                nvidia = new { evaluates = 10 } }
        }));
        var reply = new PhoenixControlResponse("session", 123, "id", true, "observed", null, json.RootElement.Clone(), null);
        Assert.Equal(expected, PhoenixBridgeEvidence.Evaluate(reply, 123).OutputCompleted);
    }

    [Theory]
    [InlineData("C:/User/DLSS5Phoenix/sessions/123-abcd/phoenix_graphics_host.exe", 123, 0, true)]
    [InlineData("C:/User/DLSS5Phoenix/sessions/124-abcd/phoenix_graphics_host.exe", 123, 0, false)]
    [InlineData("C:/User/DLSS5Phoenix/sessions/123-abcd/phoenix_graphics_host.exe", 123, -60, false)]
    [InlineData("C:/Other/session/123-abcd/phoenix_graphics_host.exe", 123, 0, false)]
    public void HelperCleanupRequiresThisLaunchAndThePrivateSessionLayout(string path, int ownerPid, int age, bool owned)
    {
        var launch = DateTimeOffset.UtcNow;
        Assert.Equal(owned, SmokeTestRunner.IsOwnedPrivateHelper(path, ownerPid, launch.AddSeconds(age), launch));
    }

    [Theory]
    [InlineData(3, 4, 4, 1, 123, true, false)]
    [InlineData(3, 4, 4, 0, 123, false, false)]
    [InlineData(3, 4, 0, 1, 123, false, false)]
    [InlineData(3, 4, 4, 1, 124, false, false)]
    [InlineData(1, 0, 0, 0, 123, false, false)]
    [InlineData(6, 4, 4, 1, 123, false, true)]
    [InlineData(5, 4, 4, 1, 123, false, true)]
    public void RequiresLiveProcessPresentedFramesAndActualInference(int phase, long delivered, long presented,
        long submitted, int replyPid, bool complete, bool failed)
    {
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            phase, delivered, presented, status = "Graphics bridge",
            applied_settings = new { Backend = 3, ExecutionDevice = "native" },
            helper = new { error = "", execution = new { kind = "native" }, phoenix = new { submitted_frames = submitted,
                status = "Own network output written back to the game DLSS output" } }
        }));
        var response = new PhoenixControlResponse("session", replyPid, "id", true, "observed", null,
            json.RootElement.Clone(), null);
        var evidence = PhoenixBridgeEvidence.Evaluate(response, 123);
        Assert.Equal(complete, evidence.OutputCompleted);
        Assert.Equal(failed, evidence.Failed);
    }
}
