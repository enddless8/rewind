namespace Replay;

internal record CaptureGraph(string Graph, string[] DeviceArgs, string Backend)
{
    // A missing size means native source dimensions, with the same cursor/crop/border semantics.
    public static CaptureGraph Build(string mode, bool hideBorder, int fps, GameWindow? game, Display display,
        int? width = null, int? height = null)
    {
        if (mode is not ("Game" or "Desktop")) throw new ArgumentException("Неизвестный режим захвата.");
        if (mode == "Game" && (game == null || !Native.IsWindow(game.Handle)))
            throw new InvalidOperationException("Выбери запущенную игру.");
        display = CaptureSource.CurrentDisplay(display);
        if (mode == "Desktop" && hideBorder)
        {
            if (width.HasValue && height.HasValue)
            {
                var scaled = DesktopCapture.Build(display, width.Value, height.Value, fps);
                return new(scaled.Graph, scaled.DeviceArgs, "DXGI — без рамки");
            }
            var native = DesktopCapture.BuildSource(display, fps);
            return new(native.Graph + ",hwdownload,format=bgra[v]", native.DeviceArgs, "DXGI — без рамки");
        }
        var target = mode == "Game" ? $"hwnd={(ulong)game!.Handle}" : $"hmonitor={(ulong)display.Handle}";
        var border = mode == "Game" && hideBorder ? 0 : 1;
        var graph = $"gfxcapture={target}:display_border={border}:max_framerate={fps}";
        if (width.HasValue && height.HasValue)
            graph += $":width={width}:height={height}:resize_mode=scale_aspect";
        graph += ":output_fmt=bgra";
        graph += width.HasValue ? $",fps={fps}[v]" : ",hwdownload,format=bgra[v]";
        return new(graph, [], border == 0 ? "WGC — окно без рамки" : "WGC");
    }
}
