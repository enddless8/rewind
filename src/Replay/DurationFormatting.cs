using System.Globalization;

namespace Replay;

// Shared presentation helpers; never round or write configuration values.
public static class DurationFormatting
{
    public static string HumanSeconds(int seconds)
    {
        seconds = Math.Max(0, seconds);
        if (seconds < 60) return $"{seconds} сек";
        return seconds % 60 == 0 ? $"{seconds / 60} мин" : $"{seconds / 60} мин {seconds % 60} сек";
    }

    public static string Recording(double duration, bool padMinutes = false)
    {
        var seconds = (long)Math.Floor(double.IsFinite(duration) ? Math.Max(0, duration) : 0);
        return seconds < 60 ? $"{seconds} сек" : seconds < 3600
            ? (seconds / 60).ToString(padMinutes ? "00" : "0", CultureInfo.InvariantCulture) + ":" + (seconds % 60).ToString("00", CultureInfo.InvariantCulture)
            : string.Create(CultureInfo.InvariantCulture, $"{seconds / 3600}:{seconds / 60 % 60:00}:{seconds % 60:00}");
    }
}
