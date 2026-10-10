using System.Globalization;

namespace Replay;

internal enum OverlayNotificationKind
{ RewindSaved, RecordingStarted, RecordingSaved, ScreenshotSaved, RewindEnabled, RewindDisabled, MicrophoneEnabled, MicrophoneMuted,
    RewindSaving, CompressionStarted, CompressionCompleted }

internal sealed record OverlayNotification(OverlayNotificationKind Kind, string Title, string Body = "",
    string Metadata = "", string? MediaPath = null, double Duration = 0, string? Emphasis = null,
    Guid? SaveOperationId = null, string? CompressionJobId = null)
{
    public bool HasThumbnail => Kind is OverlayNotificationKind.RewindSaved or OverlayNotificationKind.RecordingSaved or OverlayNotificationKind.ScreenshotSaved;
    public bool IsMicro => Kind is OverlayNotificationKind.CompressionStarted or OverlayNotificationKind.CompressionCompleted;
    public bool IsOngoing => Kind is OverlayNotificationKind.RewindSaving or OverlayNotificationKind.CompressionStarted;
    public double Height => IsMicro ? 61 : OverlayPlacement.CardHeight;
    public TimeSpan Lifetime => TimeSpan.FromSeconds(Kind == OverlayNotificationKind.CompressionCompleted ? 1.5 : HasThumbnail ? 4 : 3);
    public static string FileSize(long bytes)
    {
        var culture = CultureInfo.GetCultureInfo("ru-RU");
        if (bytes < 1024) return Math.Max(0, bytes).ToString(culture) + " Б";
        if (bytes < 1024L * 1024) return (bytes / 1024d).ToString("0", culture) + " КБ";
        if (bytes < 1024L * 1024 * 1024) return (bytes / (1024d * 1024)).ToString("0.#", culture) + " МБ";
        return (bytes / (1024d * 1024 * 1024)).ToString("0.#", culture) + " ГБ";
    }
}
