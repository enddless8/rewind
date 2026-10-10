using System.Globalization;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using Point = System.Windows.Point;

namespace Replay;

// Position only the text; track/thumb layout and slider input remain unchanged.
public sealed class AudioSliderValuePositionConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length != 7 || values.Take(4).Any(value => value is not double number || !double.IsFinite(number)) ||
            values[4] is not FrameworkElement rail || values[5] is not Track { Thumb: { } } track ||
            values[6] is not FrameworkElement surface) return 0d;
        var labelWidth = (double)values[0];
        if (labelWidth <= 0 || rail.ActualWidth <= 0) return 0d;

        var trackLeft = rail.TranslatePoint(new Point(), surface).X;
        var trackRight = rail.TranslatePoint(new Point(rail.ActualWidth, 0), surface).X;
        // Track arranges its repeat buttons before moving the thumb. Their measured
        // width gives the current center without reading the thumb's previous offset.
        var thumbLeft = (double)values[2];
        if (track.IsDirectionReversed) thumbLeft = track.ActualWidth - thumbLeft - (double)values[3];
        if (track.UseLayoutRounding)
        {
            var dpi = VisualTreeHelper.GetDpi(track).DpiScaleX;
            thumbLeft = Math.Round(thumbLeft * dpi) / dpi;
        }
        var centerInTrack = thumbLeft + (double)values[3] / 2;
        var thumbCenter = track.TranslatePoint(new Point(centerInTrack, 0), surface).X;
        var desiredLeft = thumbCenter - labelWidth / 2;
        return Math.Clamp(desiredLeft, trackLeft, Math.Max(trackLeft, trackRight - labelWidth));
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => targetTypes.Select(_ => System.Windows.Data.Binding.DoNothing).ToArray();
}
