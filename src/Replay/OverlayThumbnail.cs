using System.Windows.Media.Imaging;

namespace Replay;

internal static class OverlayThumbnail
{
    public static async Task<BitmapSource?> Load(OverlayNotification notification, CancellationToken cancellation)
    {
        string? temporary = null;
        try
        {
            var path = notification.MediaPath;
            if (path == null || !File.Exists(path)) return null;
            if (notification.Kind != OverlayNotificationKind.ScreenshotSaved)
            {
                // No persistent cache. Bound to our process and cancellable during Exit.
                temporary = Path.Combine(Path.GetTempPath(), "Rewind-thumbnail-" + Guid.NewGuid().ToString("N") + ".png");
                var position = Math.Max(0, notification.Duration - Math.Min(1, notification.Duration / 2));
                var result = await MediaTools.Run("ffmpeg", ["-hide_banner", "-loglevel", "error", "-ss",
                    position.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture), "-i", path,
                    "-frames:v", "1", "-vf", "scale=512:-2", "-threads", "1", "-y", temporary], 8, cancellation, bindLifetime: true);
                if (result.Code != 0 || !File.Exists(temporary)) return null;
                path = temporary;
            }
            cancellation.ThrowIfCancellationRequested();
            using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            var image = new BitmapImage();
            image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad; image.DecodePixelWidth = 512;
            image.StreamSource = input; image.EndInit(); image.Freeze();
            return image;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { return null; }
        catch (Exception ex) { ClipFiles.Log("overlay-thumbnail", ex, notification.MediaPath); return null; }
        finally
        {
            if (temporary != null)
            {
                try { File.Delete(temporary); }
                catch (Exception ex) { ClipFiles.Log("overlay-thumbnail-cleanup", ex, temporary); }
            }
        }
    }
}
