using System.Globalization;
using System.Reflection;
using System.Windows.Data;

namespace Replay;

public static class ApplicationInfo
{
    public static string VersionLabel { get; } = "Rewind v" +
        (typeof(ApplicationInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion.Split('+')[0] ?? typeof(ApplicationInfo).Assembly.GetName().Version?.ToString(3));
}

// UI adapters only. The existing text fields remain the input to ReadSettings().
public sealed class RewindDurationPositionConverter : IValueConverter
{
    private static readonly double[] Seconds = [15, 30, 60, 120, 300, 600];
    private static readonly double[] Positions = [0, 1, 2, 3, 4, 5];
    public static IReadOnlyList<double> LandmarkPositions { get; } = Array.AsReadOnly(Positions);
    public static double PositionFromSeconds(int seconds) => Map(seconds, Seconds, Positions);
    public static int SecondsFromPosition(double position) =>
        (int)Math.Round(Map(position, Positions, Seconds), MidpointRounding.AwayFromZero);
    private static double Map(double value, double[] from, double[] to)
    {
        value = Math.Clamp(value, from[0], from[^1]);
        for (var i = 1; i < from.Length; i++)
            if (value <= from[i]) return to[i - 1] + (value - from[i - 1]) / (from[i] - from[i - 1]) * (to[i] - to[i - 1]);
        return to[^1];
    }
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        double.TryParse(value?.ToString(), out var seconds) ? Map(seconds, Seconds, Positions) : 0d;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        SecondsFromPosition((double)value).ToString(CultureInfo.InvariantCulture);
}

public sealed class RewindDurationTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (!int.TryParse(value?.ToString(), out var seconds)) return "";
        return DurationFormatting.HumanSeconds(seconds);
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => System.Windows.Data.Binding.DoNothing;
}
