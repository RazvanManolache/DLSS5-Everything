using System.Text.Json;

namespace Dlss5CompatApp;

/// <summary>Runtime evidence from a fresh request to the launched game, not old log counters.</summary>
static class PhoenixBridgeEvidence
{
    // This only permits taking a picture. Native GPU completion still requires
    // the PID-bound production summary, including its completed/own-launch counters.
    internal static bool NativeFramesRecorded(PhoenixControlResponse reply, int launchedPid)
    {
        try
        {
            return reply.Ok && reply.Pid == launchedPid &&
                reply.Snapshot.GetProperty("execution").GetProperty("kind").GetString() == "native" &&
                reply.Snapshot.GetProperty("settings").GetProperty("Backend").GetInt32() == 3 &&
                reply.Snapshot.GetProperty("phoenix").GetProperty("submitted_frames").GetInt64() > 0;
        }
        catch (Exception error) when (error is InvalidOperationException or KeyNotFoundException or FormatException)
        { return false; }
    }

    public static SmokeTestRunner.PhoenixSmokeEvidence Evaluate(PhoenixControlResponse reply, int launchedPid)
    {
        if (!reply.Ok || reply.Pid != launchedPid)
            return new(false, false, "No successful bridge reply from the launched process.");
        var snapshot = reply.Snapshot;
        var executionMode = snapshot.TryGetProperty("execution_mode", out var mode) ? mode.GetString() : "helper";
        var inProcess = executionMode is "in_process" or "in_process_x64_library" or "in_process_x86_driver";
        if (executionMode is not ("helper" or "in_process" or "in_process_x64_library" or "in_process_x86_driver"))
            return new(false, false, "Unknown graphics execution route.");
        if (!snapshot.TryGetProperty(inProcess ? "in_process" : "helper", out var helper))
            return new(false, false, "Direct game input: completion summary is required.");
        try
        {
            var status = snapshot.GetProperty("status").GetString() ?? "";
            var phase = snapshot.GetProperty("phase").GetInt32();
            var delivered = snapshot.GetProperty("delivered").GetInt64();
            var presented = snapshot.GetProperty("presented").GetInt64();
            var disabled = snapshot.TryGetProperty("enabled", out var enabled) && !enabled.GetBoolean();
            if (inProcess && (!snapshot.TryGetProperty("helper_pid", out var helperPid) || helperPid.GetInt32() != 0))
                return new(false, true, "Inconsistent in-process execution identity.");
            if (inProcess && snapshot.TryGetProperty("inference_pid", out var inferencePid) && inferencePid.GetInt32() != launchedPid)
                return new(false, true, "Inference belongs to another process.");
            if (snapshot.TryGetProperty("selection_revision", out var selection))
                if (!snapshot.TryGetProperty("presented_revision", out var presentation) || selection.GetInt64() != presentation.GetInt64())
                    return new(false, false, "Waiting for output from the current settings revision.");
            if (phase == 5 || phase == 6 && !disabled || helper.TryGetProperty("error", out var error) && !string.IsNullOrEmpty(error.GetString()))
                return new(false, true, "Graphics bridge failed: " + status);
            if (disabled)
                return new(false, false, "Phoenix DLSS5 is disabled; retained counters are not fresh output.");
            var rendered = helper.TryGetProperty("phoenix", out var phoenix) &&
                phoenix.TryGetProperty("submitted_frames", out var frames) && frames.GetInt64() > 0 &&
                phoenix.TryGetProperty("status", out var outputStatus) &&
                (outputStatus.GetString() ?? "").Contains("written back", StringComparison.OrdinalIgnoreCase);
            var native = helper.TryGetProperty("execution", out var execution) &&
                execution.TryGetProperty("kind", out var kind) && kind.GetString() == "native" &&
                snapshot.TryGetProperty("applied_settings", out var settings) &&
                settings.TryGetProperty("Backend", out var backend) && backend.GetInt32() == 3 &&
                settings.TryGetProperty("ExecutionDevice", out var device) && device.GetString() == "native";
            // Vendor-reference or portable-runner output cannot qualify a fast
            // Phoenix installation. Check the runner that executed, not a menu choice.
            return new(delivered >= 2 && presented >= 2 && rendered && native, false,
                $"Live {(inProcess ? "in-process" : "helper")} graphics bridge: delivered={delivered}, presented={presented}; " +
                (native ? "native RTX inference; " : "fast native RTX inference unverified; ") + status);
        }
        catch (Exception error) when (error is InvalidOperationException or KeyNotFoundException or FormatException)
        { return new(false, false, "Incomplete graphics bridge status: " + error.Message); }
    }
}
