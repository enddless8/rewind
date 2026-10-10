using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Replay;
public record GameWindow(nint Handle, int ProcessId, string Exe, string Title)
{
    public override string ToString() => $"{Title}  ·  {Exe}";
}
public record Display(nint Handle, string Device, int Width, int Height, bool Primary)
{
    public override string ToString() => $"{Device.Replace("\\\\.\\", "")} · {Width} × {Height}{(Primary ? " · основной" : "")}";
}
internal static class Native
{
    public delegate bool EnumWindow(nint hwnd, nint param);
    public delegate bool EnumMonitor(nint monitor, nint dc, nint rect, nint param);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindow callback, nint param);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")] public static extern bool IsWindow(nint hwnd);
    [DllImport("user32.dll")] public static extern bool IsIconic(nint hwnd);
    [DllImport("user32.dll")] public static extern nint GetForegroundWindow();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(nint hwnd, StringBuilder text, int count);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd, out uint id);
    [DllImport("user32.dll", SetLastError = true)] public static extern bool RegisterHotKey(nint hwnd, int id, uint modifiers, uint key);
    [DllImport("user32.dll", SetLastError = true)] public static extern bool UnregisterHotKey(nint hwnd, int id);
    [DllImport("user32.dll")] private static extern bool EnumDisplayMonitors(nint dc, nint clip, EnumMonitor callback, nint data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct MonitorInfo
    { public int Size; public Rect Monitor, Work; public uint Flags; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Device; }
    public static List<GameWindow> Windows()
    {
        var result = new List<GameWindow>();
        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd)) return true;
            var title = new StringBuilder(512); GetWindowText(hwnd, title, title.Capacity);
            if (title.Length == 0) return true;
            GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == Environment.ProcessId) return true;
            try { using var p = Process.GetProcessById((int)pid); if (p.ProcessName is not ("explorer" or "ApplicationFrameHost")) result.Add(new(hwnd, (int)pid, p.ProcessName, title.ToString())); } catch { }
            return true;
        }, 0);
        return result.OrderBy(x => x.Exe).ToList();
    }
    public static List<Display> Displays()
    {
        var result = new List<Display>();
        EnumDisplayMonitors(0, 0, (h, _, _, _) =>
        {
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>(), Device = "" };
            if (GetMonitorInfo(h, ref info)) result.Add(new(h, info.Device, info.Monitor.Right-info.Monitor.Left, info.Monitor.Bottom-info.Monitor.Top, (info.Flags & 1) != 0));
            return true;
        }, 0);
        return result;
    }
    public static int? DiscordProcess()
    {
        // Chromium audio subprocesses belong to the oldest/root Discord process.
        var candidates = new List<(int Id, DateTime Start)>();
        foreach (var p in Process.GetProcesses()) using (p)
            try { if (p.ProcessName.Equals("Discord", StringComparison.OrdinalIgnoreCase) || p.ProcessName.Equals("DiscordPTB", StringComparison.OrdinalIgnoreCase) || p.ProcessName.Equals("DiscordCanary", StringComparison.OrdinalIgnoreCase)) candidates.Add((p.Id, p.StartTime)); } catch { }
        return candidates.OrderBy(x => x.Start).Select(x => (int?)x.Id).FirstOrDefault();
    }
    public static double Clock => (double)Stopwatch.GetTimestamp() / Stopwatch.Frequency;
}
