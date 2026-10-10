using System.Windows.Media.Imaging;

namespace Replay;

internal record ScreenshotRequest(string Mode, bool HideBorder, int Fps, GameWindow? Game, Display Display,
    string OutputDirectory, bool GroupByApplication, string? ApplicationName);
public record SavedScreenshot(string Path, int Width, int Height, string CaptureBackend);

internal static class ScreenshotCapture
{
    internal const int TimeoutSeconds = 5;
    internal static List<string> Arguments(CaptureGraph capture, string temporary)
    {
        List<string> args = ["-hide_banner", "-v", "error", "-nostdin"];
        args.AddRange(capture.DeviceArgs);
        args.AddRange(["-filter_complex", capture.Graph, "-map", "[v]", "-an", "-frames:v", "1",
            "-c:v", "png", "-threads", "1", "-pix_fmt", "rgb24", "-f", "image2", "-update", "1", "-n", temporary]);
        return args;
    }
    public static Task<SavedScreenshot> Take(ScreenshotRequest request, CancellationToken cancellationToken = default) => Task.Run(async () =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(request.OutputDirectory) || !Path.IsPathFullyQualified(request.OutputDirectory))
            throw new ArgumentException("Выбери абсолютный путь для сохранения скриншотов.");
        if (request.Mode == "Game" && request.Game != null && Native.IsIconic(request.Game.Handle))
            throw new InvalidOperationException("Приложение свёрнуто. Разверни его и повтори скриншот.");
        var capture = CaptureGraph.Build(request.Mode, request.HideBorder, request.Fps, request.Game, request.Display);
        var folder = ClipFiles.OutputFolder(request.OutputDirectory, request.GroupByApplication, request.ApplicationName);
        Directory.CreateDirectory(folder);
        var temporary = Path.Combine(folder, ".screenshot-" + Guid.NewGuid().ToString("N") + ".partial");
        try
        {
            var result = await MediaTools.Run("ffmpeg", Arguments(capture, temporary), TimeoutSeconds, cancellationToken, bindLifetime: true);
            if (result.Code != 0) throw new IOException($"Не удалось захватить выбранный источник ({capture.Backend}). Подробности в журнале Rewind.", new IOException(result.Error));
            cancellationToken.ThrowIfCancellationRequested();
            int width, height;
            using (var file = File.OpenRead(temporary))
            {
                var decoder = new PngBitmapDecoder(file, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                width = decoder.Frames[0].PixelWidth; height = decoder.Frames[0].PixelHeight;
                if (width <= 0 || height <= 0) throw new IOException("Захват вернул пустое изображение.");
            }
            // The random suffix matches clips; an existing filename is never overwritten.
            var final = Path.Combine(folder, ClipFiles.NewId() + ".png");
            await ClipFiles.PublishFile(temporary, final, cancellationToken);
            return new SavedScreenshot(final, width, height, capture.Backend);
        }
        finally
        {
            // MediaTools already terminated/reaped FFmpeg and closed its pipes.
            await ClipFiles.RetrySharing(() => File.Delete(temporary));
        }
    }, cancellationToken);
}
