using System.Diagnostics;

namespace Dlss5CompatApp.Tests;

public sealed class ProcessLifetimeTests
{
    [Fact]
    public async Task GuardClosesAChildEvenAfterItsParentExits()
    {
        using var workspace = new TestWorkspace();
        var identity = workspace.FilePath("child.pid");
        using var parent = Process.Start(new ProcessStartInfo("cmd.exe", "/q")
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
        })!;
        var guard = new ProcessJob(); Process? child = null;
        try
        {
            guard.Assign(parent);
            // Start the child only after assigning the parent, so it inherits the job.
            await parent.StandardInput.WriteLineAsync("powershell.exe -NoProfile -NonInteractive -Command \"$PID | Set-Content '" +
                identity + "'; Start-Sleep -Seconds 120\"");
            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (!File.Exists(identity) && DateTime.UtcNow < deadline) await Task.Delay(50);
            Assert.True(File.Exists(identity), "The guarded child did not start.");
            child = Process.GetProcessById(int.Parse(File.ReadAllText(identity).Trim()));
            _ = child.SafeHandle;
            parent.Kill();
            await parent.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(child.HasExited);
            guard.Dispose();
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            guard.Dispose();
            if (!parent.HasExited) parent.Kill(entireProcessTree: true);
            if (child is not null) { if (!child.HasExited) child.Kill(); child.Dispose(); }
        }
    }
}
