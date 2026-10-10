using System.Runtime.InteropServices;

namespace Replay;
internal static class DesktopCapture
{
    [DllImport("dxgi.dll")] private static extern int CreateDXGIFactory1(ref Guid iid, out nint factory);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int Enumerate(nint self, uint index, out nint item);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int Describe(nint self, out OutputDescription description);
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct OutputDescription
    { [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Device; public Rect Rect; public int Attached, Rotation; public nint Monitor; }
    private static T Method<T>(nint instance, int slot) where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(instance), slot * nint.Size));
    public static (int Adapter, int Output) Find(Display display)
    {
        var iid = new Guid("770aae78-f26f-4dba-a829-253c83d1b387");
        Marshal.ThrowExceptionForHR(CreateDXGIFactory1(ref iid, out var factory));
        try
        {
            for (uint a = 0; Method<Enumerate>(factory, 12)(factory, a, out var adapter) == 0; a++)
            {
                try
                {
                    for (uint o = 0; Method<Enumerate>(adapter, 7)(adapter, o, out var output) == 0; o++)
                    {
                        try
                        {
                            Marshal.ThrowExceptionForHR(Method<Describe>(output, 7)(output, out var info));
                            if (info.Attached != 0 && info.Monitor == display.Handle) return ((int)a, (int)o);
                        }
                        finally { Marshal.Release(output); }
                    }
                }
                finally { Marshal.Release(adapter); }
            }
        }
        finally { Marshal.Release(factory); }
        throw new InvalidOperationException("Выбранный монитор недоступен для DXGI. Выключи режим без рамки или выбери другой монитор.");
    }
    public static (string Graph, string[] DeviceArgs) BuildSource(Display display, int fps)
    {
        var (adapter, output) = Find(display);
        var graph = $"ddagrab=output_idx={output}:framerate={fps}:draw_mouse=1:output_fmt=bgra";
        return (graph, ["-init_hw_device", $"d3d11va=capture:{adapter}", "-filter_hw_device", "capture"]);
    }
    public static (string Graph, string[] DeviceArgs) Build(Display display, int width, int height, int fps)
    {
        var source = BuildSource(display, fps);
        var graph = source.Graph;
        // Current FFmpeg scale_d3d11 fails texture allocation on this driver. Resize
        // within the isolated encoder process; at native size textures stay on GPU.
        if (display.Width != width || display.Height != height)
            // The D3D11 NVENC session cannot allocate planar YUV420P input buffers.
            // NV12 keeps the resized CPU frames compatible with that session.
            graph += $",hwdownload,format=bgra,scale={width}:{height}:force_original_aspect_ratio=decrease:force_divisible_by=2,pad={width}:{height}:(ow-iw)/2:(oh-ih)/2,format=nv12";
        return (graph + $",setpts=PTS-STARTPTS,fps={fps}[v]", source.DeviceArgs);
    }
}
