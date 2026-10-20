using System.Runtime.InteropServices;
using System.Text;

namespace Dlss5CompatApp;

internal sealed record ScreenCaptureSource(string Kind, ulong Handle, int ProcessId, string Name)
{
    public string DeviceId { get; init; } = "";
    public int CaptureWidth { get; init; } = 1280;
    public int CaptureHeight { get; init; } = 720;
    public int CaptureFps { get; init; } = 30;
    public override string ToString() => Name;
    public Dictionary<string, object> Wire(bool cursor, int parentPid) => new()
    {
        ["schema"] = 1,
        ["kind"] = Kind,
        ["target"] = Handle,
        ["target_pid"] = ProcessId,
        ["parent_pid"] = parentPid,
        ["cursor"] = cursor,
        ["device_id"] = DeviceId,
        ["capture_width"] = CaptureWidth,
        ["capture_height"] = CaptureHeight,
        ["capture_fps"] = CaptureFps
    };
}

internal static class ScreenCaptureSources
{
    public static bool IsForeground(ScreenCaptureSource source) => source.Kind == "window" &&
        GetForegroundWindow() == unchecked((nint)source.Handle);

    public static bool TryActivate(ScreenCaptureSource source)
    {
        if (source.Kind != "window" || source.Handle == 0 || source.ProcessId <= 0) return false;
        var window = unchecked((nint)source.Handle);
        GetWindowThreadProcessId(window, out var pid);
        if (pid != source.ProcessId || !IsWindowVisible(window)) return false;
        if (IsIconic(window)) ShowWindow(window, 9);
        return SetForegroundWindow(window);
    }

    public static IReadOnlyList<ScreenCaptureSource> Enumerate()
    {
        var result = new List<ScreenCaptureSource>();
        EnumDisplayMonitors(0, 0, (nint monitor, nint hdc, ref Rect bounds, nint data) =>
        {
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>(), Device = "" };
            if (GetMonitorInfo(monitor, ref info)) result.Add(new("monitor", (ulong)monitor, 0,
                $"Display {result.Count + 1} - {info.Monitor.Right - info.Monitor.Left} x {info.Monitor.Bottom - info.Monitor.Top}" +
                ((info.Flags & 1) != 0 ? " (primary)" : "")));
            return true;
        }, 0);
        var windows = new List<ScreenCaptureSource>();
        EnumWindows((window, _) =>
        {
            if (!IsWindowVisible(window) || GetWindow(window, 4) != 0) return true;
            GetWindowThreadProcessId(window, out var pid);
            if (pid == Environment.ProcessId || pid == 0) return true;
            var windowClass = new StringBuilder(128); GetClassName(window, windowClass, windowClass.Capacity);
            if (windowClass.ToString() == "DLSS5ScreenStudio") return true;
            var title = new StringBuilder(260); GetWindowText(window, title, title.Capacity);
            var name = title.ToString().Trim();
            if (name.Length == 0 || name == "Program Manager") return true;
            windows.Add(new("window", (ulong)window, (int)pid, name)); return true;
        }, 0);
        result.AddRange(windows.OrderBy(source => source.Name, StringComparer.CurrentCultureIgnoreCase));
        return result;
    }
    [StructLayout(LayoutKind.Sequential)] struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct MonitorInfo
    {
        public int Size; public Rect Monitor, Work; public int Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Device;
    }
    delegate bool MonitorCallback(nint monitor, nint hdc, ref Rect bounds, nint data);
    delegate bool WindowCallback(nint window, nint data);
    [DllImport("user32.dll")] static extern bool EnumDisplayMonitors(nint hdc, nint clip, MonitorCallback callback, nint data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] static extern bool EnumWindows(WindowCallback callback, nint data);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] static extern bool IsIconic(nint window);
    [DllImport("user32.dll")] static extern bool ShowWindow(nint window, int command);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(nint window);
    [DllImport("user32.dll")] static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] static extern nint GetWindow(nint window, uint command);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(nint window, out uint process);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(nint window, StringBuilder text, int length);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(nint window, StringBuilder text, int length);
}
