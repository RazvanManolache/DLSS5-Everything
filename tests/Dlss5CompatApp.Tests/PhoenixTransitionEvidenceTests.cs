using System.Text.Json;

namespace Dlss5CompatApp.Tests;

public class PhoenixTransitionEvidenceTests
{
    static PhoenixControlResponse Reply(string route, int inferencePid = 123, int revision = 4, int displayed = 4)
    {
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            execution_mode = route, helper_pid = 0, inference_pid = inferencePid, enabled = true,
            selection_revision = revision, presented_revision = displayed,
            phase = 2, delivered = 20, presented = 50, status = "Running",
            applied_settings = new { Backend = 3, ExecutionDevice = "native" }, helper = new { },
            in_process = new { error = "", execution = new { kind = "native" },
                phoenix = new { submitted_frames = 20, status = "Own network output written back" } }
        }));
        return new("123-created", 123, "reply", true, "observed", null, json.RootElement.Clone(), null);
    }

    [Theory]
    [InlineData("in_process")]
    [InlineData("in_process_x64_library")]
    [InlineData("in_process_x86_driver")]
    public void NewExecutionRoutesRequireFreshLocalOutput(string route)
    {
        Assert.True(PhoenixBridgeEvidence.Evaluate(Reply(route), 123).OutputCompleted);
        Assert.False(PhoenixBridgeEvidence.Evaluate(Reply(route, inferencePid: 999), 123).OutputCompleted);
        Assert.False(PhoenixBridgeEvidence.Evaluate(Reply(route, displayed: 3), 123).OutputCompleted);
        Assert.False(PhoenixBridgeEvidence.Evaluate(Reply(route), 999).OutputCompleted);
    }

    [Fact]
    public void UnknownExecutionCannotInheritAnotherRoutesCompletion() =>
        Assert.False(PhoenixBridgeEvidence.Evaluate(Reply("future_route"), 123).OutputCompleted);
}
