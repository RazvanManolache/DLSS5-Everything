using System.Diagnostics;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Dlss5CompatApp;

/// <summary>Keeps newly started, local game bootstraps inside the test lifetime.
/// Store clients outside the installation and processes present before launch are excluded.</summary>
internal sealed class GameLaunchLifetime : IDisposable
{
    readonly string root;
    readonly DateTime startedAfter = DateTime.Now;
    readonly HashSet<int> existing = [];
    readonly Dictionary<int, Process> owned = [];
    readonly ProcessJob job;
    readonly Action<string> log;

    public GameLaunchLifetime(string gameRoot, Action<string> log)
    {
        root = Path.GetFullPath(gameRoot);
        if (LocalPaths.Same(root, Path.GetPathRoot(root)!))
            throw new ArgumentException("A game launch lifetime requires an installation directory.", nameof(gameRoot));
        this.log = log;
        foreach (var process in Process.GetProcesses())
            using (process) existing.Add(process.Id);
        job = new ProcessJob();
    }

    internal static bool IsNewLocalProcess(string root, string? image, DateTime started,
        DateTime launchTime, bool existedBefore)
    {
        if (existedBefore || started < launchTime || string.IsNullOrWhiteSpace(image)) return false;
        var relative = Path.GetRelativePath(root, image);
        return !Path.IsPathRooted(relative) && relative != ".." &&
            !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
            !relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal);
    }

    public void Observe()
    {
        foreach (var process in Process.GetProcesses())
        {
            var keep = false;
            try
            {
                if (existing.Contains(process.Id) || owned.ContainsKey(process.Id) || process.HasExited) continue;
                // Pin the identity before inspecting it; never reopen a recorded PID during cleanup.
                _ = process.SafeHandle;
                if (!IsNewLocalProcess(root, ProcessImagePath(process), process.StartTime, startedAfter, false)) continue;
                job.Assign(process);
                owned.Add(process.Id, process);
                keep = true;
                log("Guarding game bootstrap process " + process.Id + ".");
            }
            catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException or IOException or NotSupportedException)
            { /* Unverifiable or protected processes are never adopted. */ }
            finally { if (!keep) process.Dispose(); }
        }
    }

    internal static string ProcessImagePath(Process process)
    {
        // Module enumeration reads the child's loader state, which may not yet
        // exist just after startup. Query the executable identity from Windows
        // through the same pinned handle used for ownership and cleanup.
        var path = new StringBuilder(32768);
        var length = path.Capacity;
        if (!QueryFullProcessImageName(process.SafeHandle, 0, path, ref length))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        return path.ToString();
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool QueryFullProcessImageName(SafeProcessHandle process, int flags,
        StringBuilder path, ref int length);

    public void Dispose()
    {
        // Job membership captures children, including publisher launchers outside the game folder.
        // Closing the job only affects adopted processes; Steam itself is never adopted.
        job.Dispose();
        foreach (var process in owned.Values) process.Dispose();
        owned.Clear();
    }
}
