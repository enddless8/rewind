using System.Runtime.InteropServices;

namespace Replay;

internal readonly record struct OverlayMonitor(nint Handle, int Left, int Top, int Right, int Bottom, double Scale);
internal readonly record struct OverlayBounds(int Left, int Top, int Width, int Height, double ContentScale);

internal static class OverlayPlacement
{
    public const double CardWidth = 496, CardHeight = 157, Margin = 24, Gap = 12;
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public int Size; public Rect Monitor, Work; public uint Flags; }
    [DllImport("user32.dll")] private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] private static extern nint MonitorFromWindow(nint hwnd, uint flags);
    [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(nint monitor, int type, out uint x, out uint y);

    public static OverlayMonitor Resolve(nint captureMonitor = default, nint captureWindow = default)
    {
        // A captured window follows its monitor even when it moves while recording.
        var handle = captureWindow != 0 && Native.IsWindow(captureWindow) ? MonitorFromWindow(captureWindow, 2) : captureMonitor;
        if (!TryRead(handle, out var monitor))
        {
            handle = MonitorFromWindow(Native.GetForegroundWindow(), 0);
            if (!TryRead(handle, out monitor)) TryRead(MonitorFromWindow(0, 1), out monitor);
        }
        return monitor;
    }

    private static bool TryRead(nint handle, out OverlayMonitor monitor)
    {
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (handle == 0 || !GetMonitorInfo(handle, ref info)) { monitor = default; return false; }
        var scale = GetDpiForMonitor(handle, 0, out var dpi, out _) == 0 ? dpi / 96d : 1;
        monitor = new(handle, info.Work.Left, info.Work.Top, info.Work.Right, info.Work.Bottom, scale);
        return true;
    }

    public static double FitScale(OverlayMonitor monitor) => Math.Clamp(Math.Min(1,
        Math.Min((monitor.Right - monitor.Left) / monitor.Scale / (CardWidth + Margin * 2),
                 (monitor.Bottom - monitor.Top) / monitor.Scale / (CardHeight + Margin * 2))), .01, 1);

    public static int Capacity(OverlayMonitor monitor)
    {
        var scale = monitor.Scale * FitScale(monitor);
        return Math.Clamp((int)Math.Floor(((monitor.Bottom - monitor.Top) / scale - Margin * 2 + Gap) / (CardHeight + Gap)), 1, 3);
    }

    public static OverlayBounds Bounds(OverlayMonitor monitor, int slot)
        => BoundsAt(monitor, slot * (CardHeight + Gap), CardHeight);

    public static OverlayBounds BoundsAt(OverlayMonitor monitor, double offset, double cardHeight)
    {
        var contentScale = FitScale(monitor);
        var scale = monitor.Scale * contentScale;
        var width = (int)Math.Round(CardWidth * scale);
        var height = (int)Math.Round(cardHeight * scale);
        var margin = (int)Math.Round(Margin * scale);
        return new(Math.Max(monitor.Left, monitor.Right - margin - width),
            Math.Min(monitor.Bottom - height, monitor.Top + margin + (int)Math.Round(offset * scale)),
            width, height, contentScale);
    }
}
