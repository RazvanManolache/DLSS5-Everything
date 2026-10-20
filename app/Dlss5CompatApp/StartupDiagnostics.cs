using System.Runtime.InteropServices;
using System.Text;

namespace Dlss5CompatApp;

/// <summary>Read-only diagnostics for an owned process blocked before its addon
/// bridge exists. Never presses buttons, accepts prompts, or changes focus.</summary>
static class StartupDiagnostics
{
    internal static string DescribeExit(int exitCode, string executable)
    {
        var message = "Game exited with code " + exitCode + ".";
        if (unchecked((uint)exitCode) != 0xc0000135u) return message;
        message += " Windows reported a missing dependency (0xC0000135).";
        var directories = new[]
        {
            Path.GetDirectoryName(executable)!,
            Environment.GetFolderPath(PeReader.GetArchitecture(executable) == CpuArch.X86
                ? Environment.SpecialFolder.SystemX86 : Environment.SpecialFolder.System),
            Environment.GetFolderPath(Environment.SpecialFolder.Windows)
        }.Where(Directory.Exists).ToArray();
        // This is an explanation after a real loader failure, never an install gate.
        // Delay imports, API contracts and other Windows loader searches cannot be
        // treated as missing mandatory game files by a simple directory check.
        var unresolved = PeReader.GetImports(executable, includeDelayed: false)
            .Where(name => Path.GetFileName(name) == name && name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) &&
                !name.StartsWith("api-ms-", StringComparison.OrdinalIgnoreCase) &&
                !name.StartsWith("ext-ms-", StringComparison.OrdinalIgnoreCase) &&
                directories.All(directory => !File.Exists(Path.Combine(directory, name))))
            .Take(16).ToArray();
        if (unresolved.Length > 0)
            message += " Direct imports not found beside the executable or in Windows directories: " +
                string.Join(", ", unresolved) + ". Other loader search locations may apply.";
        return message;
    }

    public static string[] ReadGraphicsModules(System.Diagnostics.Process process)
    {
        try
        {
            return process.Modules.Cast<System.Diagnostics.ProcessModule>().Take(1024)
                .Where(module => IsGraphicsModule(module.ModuleName))
                .Select(module => module.FileName).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException)
        { return []; }
    }

    internal static bool IsGraphicsModule(string name) => name.ToLowerInvariant() is
        "dxgi.dll" or "d3d9.dll" or "d3d10.dll" or "d3d10_1.dll" or "d3d11.dll" or "d3d12.dll" or
        "d3d12core.dll" or "opengl32.dll" or "vulkan-1.dll" or "ddraw.dll" or "d3d8.dll" or
        "unityplayer.dll" or "phoenix.addon32" or "phoenix.addon64";

    public static string? ReadDialog(int pid)
    {
        string? result = null;
        EnumWindows((window, context) =>
        {
            GetWindowThreadProcessId(window, out var owner);
            if (owner != pid || !IsWindowVisible(window)) return true;
            var className = new StringBuilder(128);
            GetClassNameW(window, className, className.Capacity);
            if (className.ToString() != "#32770") return true;
            var parts = new List<string>();
            void Read(nint handle)
            {
                var text = new StringBuilder(2048);
                if (SendMessageTimeoutW(handle, 13, (nuint)text.Capacity, text, 2, 100, out _) != 0 && text.Length > 0)
                    parts.Add(text.ToString());
            }
            Read(window);
            EnumChildWindows(window, (child, _) => { Read(child); return parts.Count < 16; }, 0);
            result = string.Join(" | ", parts.Distinct());
            return false;
        }, 0);
        return result;
    }

    delegate bool WindowCallback(nint window, nint parameter);
    [DllImport("user32.dll")] static extern bool EnumWindows(WindowCallback callback, nint parameter);
    [DllImport("user32.dll")] static extern bool EnumChildWindows(nint parent, WindowCallback callback, nint parameter);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(nint window, out int processId);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassNameW(nint window, StringBuilder text, int limit);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern nint SendMessageTimeoutW(nint window, uint message,
        nuint length, StringBuilder text, uint flags, uint timeout, out nuint result);
}
